using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Spinarr.AccurateRip;
using Spinarr.Audio;
using Spinarr.Disc;
using Spinarr.Drives;
using Spinarr.Options;
using Spinarr.Tools;

namespace Spinarr.Rip;

/// <summary>
/// Rips a CD: full TOC with cdrdao, each track read twice with cd-paranoia (test and copy) and
/// shifted by the drive's read offset, CRCs compared, AccurateRip checked, then FLAC at the chosen
/// level with the tags. One rip per drive at a time; the album lands in the rip folder for Lidarr.
/// </summary>
public sealed class RipService(
    DiscReader reader,
    AccurateRipClient accurateRip,
    DriveSettingsStore driveSettings,
    DriveTasks driveTasks,
    ToolStatusService tools,
    IOptions<SpinarrOptions> options,
    ILogger<RipService> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SpinarrOptions _options = options.Value;
    private readonly List<RipJob> _jobs = [];
    private readonly Lock _historyLock = new();

    /// <summary>Raised whenever a rip moves on (from the rip's own thread).</summary>
    public event Action<RipJob>? Changed;

    /// <summary>Rips since the app started, newest first.</summary>
    public IReadOnlyList<RipJob> Jobs
    {
        get
        {
            lock (_jobs)
            {
                return _jobs.AsEnumerable().Reverse().ToList();
            }
        }
    }

    public string HistoryPath => Path.Combine(_options.ConfigFolder, "rips.json");

    /// <summary>Every rip that ended, newest first, kept in /config/rips.json across restarts.</summary>
    public IReadOnlyList<RipRecord> History()
    {
        lock (_historyLock)
        {
            return ReadHistory().OrderByDescending(r => r.StartedAt).ToList();
        }
    }

    private List<RipRecord> ReadHistory()
    {
        try
        {
            return File.Exists(HistoryPath) ? JsonSerializer.Deserialize<List<RipRecord>>(File.ReadAllText(HistoryPath)) ?? [] : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Could not read {Path}: {Message}", HistoryPath, e.Message);
            return [];
        }
    }

    private void Remember(RipJob job)
    {
        var tracks = job.Tracks.Where(t => t.Number > 0).ToList();
        var record = new RipRecord(job.Id, job.Title, job.Request.Drive.DisplayName, job.Request.Disc.MusicBrainz,
            job.Request.Release?.Id, job.StartedAt, job.EndedAt ?? DateTime.UtcNow, job.State, job.Summary ?? "",
            job.Destination, job.LogFile, tracks.Count, tracks.Count(t => t.AccurateRip?.Accurate == true),
            tracks.Count(t => t.State == TrackState.Error), job.Request.FlacLevel);
        lock (_historyLock)
        {
            try
            {
                var history = ReadHistory();
                history.Add(record);
                Directory.CreateDirectory(_options.ConfigFolder);
                var temporary = HistoryPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(history, Json));
                File.Move(temporary, HistoryPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("Could not write {Path}: {Message}", HistoryPath, e.Message);
            }
        }
    }

    public RipJob? Running(string device) => Jobs.FirstOrDefault(j => j.State == RipState.Running && j.Request.Drive.Device == device);

    public RipJob? Get(string id) => Jobs.FirstOrDefault(j => j.Id == id);

    public bool AnyRunning => Jobs.Any(j => j.State == RipState.Running);

    /// <summary>Starts a rip. Returns the job, or why it can't start.</summary>
    public (RipJob? Job, string? Problem) Start(RipRequest request)
    {
        if (driveTasks.IsRunning(request.Drive.Device))
        {
            return (null, "A drive setup task is running on this drive.");
        }

        if (driveSettings.Find(request.Drive)?.ReadOffset == null)
        {
            return (null, "This drive has no read offset yet: find it on the Drives page first.");
        }

        var id = $"{DateTime.Now:yyyyMMdd-HHmmss}-{RipNaming.Safe(request.Disc.Freedb)}";
        var job = new RipJob { Id = id, Request = request, WorkFolder = Path.Combine(_options.WorkFolder, id) };
        lock (_jobs)
        {
            if (_jobs.Any(j => j.State == RipState.Running && j.Request.Drive.Device == request.Drive.Device))
            {
                return (null, "A rip is already running on this drive.");
            }

            _jobs.Add(job);
        }

        _ = Task.Run(() => RunAsync(job));
        return (job, null);
    }

    public void Cancel(string id) => Get(id)?.Cancel.Cancel();

    private void Say(RipJob job, string line)
    {
        job.Add(line);
        Changed?.Invoke(job);
    }

    private void Step(RipJob job, string step)
    {
        job.Step = step;
        Say(job, step);
    }

    private async Task RunAsync(RipJob job)
    {
        var cancellationToken = job.Cancel.Token;
        try
        {
            Directory.CreateDirectory(job.WorkFolder);
            await RipAsync(job, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            (job.State, job.Summary) = (RipState.Cancelled, "Stopped. The work folder was removed.");
            TryDelete(job.WorkFolder);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Rip {Id} failed", job.Id);
            (job.State, job.Summary) = (RipState.Failed, e.Message);
        }

        job.EndedAt = DateTime.UtcNow;
        job.Step = job.State.ToString();
        Remember(job);
        Say(job, $"[{job.State.ToString().ToLowerInvariant()}] {job.Summary}");
        logger.LogInformation("Rip of {Title}: {Summary}", job.Title, job.Summary);
    }

    private async Task RipAsync(RipJob job, CancellationToken cancellationToken)
    {
        var request = job.Request;
        var device = request.Drive.Device;
        var settings = driveSettings.Find(request.Drive);
        job.ReadOffset = settings?.ReadOffset ?? 0;
        job.DefeatsCache = settings?.DefeatsCache;
        Say(job, $"Drive {request.Drive.DisplayName}, read offset {Format.Offset(job.ReadOffset.Value)}, FLAC level {request.FlacLevel}.");

        // The full TOC: pre-gaps, ISRCs and the hidden track need cdrdao's slow read (a few minutes).
        Step(job, "Reading the table of contents (pre-gaps, ISRCs: a few minutes)");
        var (toc, problem) = await reader.ReadTocAsync(device, false, Path.Combine(job.WorkFolder, "disc.toc"), null, cancellationToken);
        if (toc == null)
        {
            throw new InvalidOperationException(problem ?? "No table of contents.");
        }

        var disc = DiscIds.Of(toc);
        if (disc.MusicBrainz != request.Disc.MusicBrainz)
        {
            throw new InvalidOperationException("The disc in the drive is not the one picked: open it again on the disc page.");
        }

        job.Toc = toc;
        var audio = toc.AudioTracks;
        BuildTracks(job, toc);
        Say(job, $"{audio.Count} audio track(s){(toc.Htoa != null ? " and hidden audio before track 1" : "")}, MusicBrainz disc ID {disc.MusicBrainz}.");

        Step(job, "Asking AccurateRip");
        var responses = await accurateRip.FetchAsync(disc, cancellationToken);
        job.AccurateRipReached = responses != null;
        job.AccurateRipResponses = responses ?? [];
        Say(job, responses == null ? "AccurateRip could not be reached: the rip goes on, unchecked."
            : responses.Count == 0 ? "AccurateRip doesn't know this disc: the test and copy CRCs are the only check."
            : $"AccurateRip has {responses.Count} answer(s) for this disc.");

        var destination = Path.Combine(_options.RipFolder, RipNaming.AlbumFolder(request.Release, request.Disc));
        var existing = job.Tracks.Select(t => Path.Combine(destination, t.FileName)).FirstOrDefault(File.Exists);
        if (existing != null)
        {
            throw new InvalidOperationException($"Already ripped: {existing} exists. Move or delete it first.");
        }

        var readable = toc.TrackEnd(audio[^1].Number);
        foreach (var track in job.Tracks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await RipTrackAsync(job, track, toc, readable, responses ?? [], cancellationToken);
        }

        Step(job, "Writing the log and cue sheet, moving the album");
        var version = AppInfo.Version;
        Directory.CreateDirectory(destination);
        var fileBase = RipNaming.DiscFileBase(request.Release, request.Medium, request.Disc);
        foreach (var track in job.Tracks.Where(t => t.State is TrackState.Done or TrackState.Error && File.Exists(Path.Combine(job.WorkFolder, t.FileName))))
        {
            File.Move(Path.Combine(job.WorkFolder, track.FileName), Path.Combine(destination, track.FileName));
        }

        await File.WriteAllTextAsync(Path.Combine(destination, fileBase + ".cue"), RipFiles.CueSheet(job, version), cancellationToken);
        var paranoia = tools.Statuses.FirstOrDefault(s => s.Tool.Id == "cd_paranoia")?.Current ?? "";
        job.LogFile = Path.Combine(destination, fileBase + ".log");
        await File.WriteAllTextAsync(job.LogFile, RipFiles.Log(job, version, paranoia, DateTime.Now), cancellationToken);
        File.Copy(Path.Combine(job.WorkFolder, "disc.toc"), Path.Combine(destination, fileBase + ".toc"), overwrite: true);
        job.Destination = destination;
        TryDelete(job.WorkFolder);

        var ripped = job.Tracks.Where(t => t.Number > 0).ToList();
        var accurate = ripped.Count(t => t.AccurateRip?.Accurate == true);
        var errors = ripped.Count(t => t.State == TrackState.Error);
        job.State = errors > 0 ? RipState.Failed : RipState.Done;
        job.Summary = errors > 0
            ? $"{errors} track(s) could not be read the same way twice: see the log. The album is in {destination}."
            : responses is { Count: > 0 }
                ? $"{ripped.Count} tracks, {accurate} accurately ripped (AccurateRip). In {destination}."
                : $"{ripped.Count} tracks, test and copy CRCs match (AccurateRip doesn't know this disc). In {destination}.";
    }

    private void BuildTracks(RipJob job, Toc toc)
    {
        var request = job.Request;
        var audio = toc.AudioTracks;
        if (toc.Htoa is { } htoa)
        {
            job.Tracks.Add(new RipTrack
            {
                Number = 0, AudioIndex = 0, Title = "Hidden track", Artist = request.Release?.Artist ?? "Unknown Artist",
                FirstFrame = htoa.Start, LastFrame = htoa.End,
                FileName = RipNaming.TrackFile(request.Release, request.Medium, 0, "Hidden track")
            });
        }

        for (var index = 0; index < audio.Count; index++)
        {
            var disc = audio[index];
            var mb = request.Medium?.Tracks.FirstOrDefault(t => t.Position == index + 1);
            var title = mb?.Title ?? disc.CdText.GetValueOrDefault("TITLE") ?? $"Track {index + 1:00}";
            job.Tracks.Add(new RipTrack
            {
                Number = disc.Number, AudioIndex = index + 1, Title = title,
                Artist = mb?.Artist ?? disc.CdText.GetValueOrDefault("PERFORMER") ?? request.Release?.Artist ?? "Unknown Artist",
                FirstFrame = toc.TrackStart(disc.Number), LastFrame = toc.TrackEnd(disc.Number),
                Pregap = disc.Pregap, Isrc = disc.Isrc, PreEmphasis = disc.PreEmphasis,
                FileName = RipNaming.TrackFile(request.Release, request.Medium, disc.Number, title)
            });
        }
    }

    private async Task RipTrackAsync(RipJob job, RipTrack track, Toc toc, int readable,
        IReadOnlyList<AccurateRipResponse> responses, CancellationToken cancellationToken)
    {
        var request = job.Request;
        var audioCount = toc.AudioTracks.Count;
        var name = track.Number == 0 ? "hidden track" : $"track {track.Number} of {audioCount}";
        var plan = ReadPlan.For(track.FirstFrame, track.LastFrame, job.ReadOffset ?? 0, readable);
        var raw = Path.Combine(job.WorkFolder, $"read-{track.Number:00}.wav");
        var wav = Path.Combine(job.WorkFolder, $"track-{track.Number:00}.wav");

        for (var attempt = 1; attempt <= request.Retries; attempt++)
        {
            track.Attempts = attempt;
            var again = attempt > 1 ? $" (try {attempt})" : "";

            track.State = TrackState.Reading;
            Step(job, $"Reading {name}{again}: test");
            var test = await reader.ReadAsync(request.Drive.Device, toc, plan, raw,
                f => { track.Fraction = f / 2; Changed?.Invoke(job); }, cancellationToken);
            if (!test.Ok)
            {
                track.Problem = test.Problem;
                Say(job, $"Test read failed: {test.Problem}");
                continue;
            }

            var testDigest = new TrackDigester(track.AudioIndex, audioCount, plan.Samples);
            Wav.Correct(raw, plan, testDigest, null);
            track.Test = testDigest.Result();

            track.State = TrackState.Verifying;
            Step(job, $"Reading {name}{again}: copy");
            var copy = await reader.ReadAsync(request.Drive.Device, toc, plan, raw,
                f => { track.Fraction = 0.5 + f / 2; Changed?.Invoke(job); }, cancellationToken);
            if (!copy.Ok)
            {
                track.Problem = copy.Problem;
                Say(job, $"Copy read failed: {copy.Problem}");
                continue;
            }

            var copyDigest = new TrackDigester(track.AudioIndex, audioCount, plan.Samples);
            Wav.Correct(raw, plan, copyDigest, wav);
            track.Copy = copyDigest.Result();
            track.Quality = Math.Max(test.Quality, copy.Quality);
            track.Speed = copy.Speed;
            File.Delete(raw);

            Say(job, string.Create(CultureInfo.InvariantCulture,
                $"Test CRC {track.Test.Crc:X8}, copy CRC {track.Copy.Crc:X8}, quality {track.Quality * 100:0.0} %, speed {track.Speed:0.0}x"));
            if (track.CrcsMatch)
            {
                track.Problem = null;
                break;
            }

            track.Problem = "The test and copy CRCs differ: the disc reads differently each time.";
            Say(job, track.Problem);
        }

        File.Delete(raw);
        if (track.Copy == null)
        {
            track.State = TrackState.Error;
            Say(job, $"Giving up on {name} after {request.Retries} tries: {track.Problem}");
            return;
        }

        if (track.Number == 0 && track.Copy.Peak == 0)
        {
            track.State = TrackState.Silent;
            File.Delete(wav);
            Say(job, "The hidden track is digital silence: not kept.");
            return;
        }

        if (track.Number > 0)
        {
            track.AccurateRip = AccurateRipDatabase.Match(responses, track.AudioIndex - 1, track.Copy.ArV1, track.Copy.ArV2);
            Say(job, track.AccurateRip.Best is { } best
                ? $"Accurately ripped (confidence {best.Confidence}, AR {best.Version})."
                : track.AccurateRip.InDatabase ? $"Not accurate: AccurateRip has {track.AccurateRip.MaxConfidenceChecksum:X8} (confidence {track.AccurateRip.MaxConfidence})."
                : "Not in AccurateRip.");
        }

        track.State = TrackState.Encoding;
        Step(job, $"Encoding {name} (FLAC level {request.FlacLevel})");
        await EncodeAsync(job, track, wav, toc, cancellationToken);
        File.Delete(wav);
        track.Fraction = 1;
        track.State = track.CrcsMatch ? TrackState.Done : TrackState.Error;
        Changed?.Invoke(job);
    }

    private async Task EncodeAsync(RipJob job, RipTrack track, string wav, Toc toc, CancellationToken cancellationToken)
    {
        var output = Path.Combine(job.WorkFolder, track.FileName);
        var part = output + ".part";
        List<string> arguments = [$"-{job.Request.FlacLevel}", "--verify", "--silent", "-f", "-o", part];
        foreach (var (key, value) in RipNaming.Tags(job.Request, track, toc, AppInfo.Version))
        {
            arguments.Add("-T");
            arguments.Add($"{key}={value}");
        }

        arguments.Add(wav);
        var result = await ProcessRunner.RunAsync(new ProcessSpec(_options.Tools.Flac, arguments), TimeSpan.FromMinutes(30), null,
            cancellationToken);
        if (!result.Success)
        {
            File.Delete(part);
            throw new InvalidOperationException($"flac failed on {track.FileName}: {result.LastLine}");
        }

        File.Move(part, output, overwrite: true);
    }

    private void TryDelete(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Could not remove {Folder}: {Message}", folder, e.Message);
        }
    }
}
