using System.Globalization;
using Spinarr.AccurateRip;
using Spinarr.Audio;
using Spinarr.Disc;
using Spinarr.Rip;
using Microsoft.Extensions.Options;
using Spinarr.Options;

namespace Spinarr.Drives;

public enum DriveTaskKind
{
    /// <summary>Reads track 1 of a disc AccurateRip knows and tries the known drive offsets on it.</summary>
    FindOffset,

    /// <summary>cd-paranoia -A: can the drive's audio cache be defeated?</summary>
    AnalyzeCache
}

public enum DriveTaskState
{
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>One drive setup run, with what it did, line by line.</summary>
public sealed class DriveTask
{
    private const int KeptLines = 2000;
    private readonly List<string> _lines = [];

    public required string Device { get; init; }
    public required DriveTaskKind Kind { get; init; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; internal set; }
    public DriveTaskState State { get; internal set; } = DriveTaskState.Running;

    /// <summary>One sentence for the drive card: the result, or what went wrong.</summary>
    public string? Summary { get; internal set; }

    /// <summary>What it is doing now ("Trying offset +48").</summary>
    public string? Progress { get; internal set; }

    internal CancellationTokenSource Cancel { get; } = new();

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_lines)
            {
                return _lines.ToList();
            }
        }
    }

    internal void Add(string line)
    {
        lock (_lines)
        {
            _lines.Add(line);
            if (_lines.Count > KeptLines)
            {
                _lines.RemoveRange(0, _lines.Count - KeptLines);
            }
        }
    }

    public string Title => Kind == DriveTaskKind.FindOffset ? "Find the read offset" : "Check the audio cache";
}

/// <summary>Runs the drive setup tasks, one at a time per drive, and saves each answer in drives.json.</summary>
public sealed class DriveTasks(
    DiscReader reader,
    AccurateRipClient accurateRip,
    DriveSettingsStore settings,
    IOptions<SpinarrOptions> options,
    ILogger<DriveTasks> logger)
{
    /// <summary>
    /// Read offsets of real drives, most common first (AccurateRip's drive list, as whipper tries
    /// them). Trying one costs no reading: track 1 is read once and every offset checked on it.
    /// </summary>
    public static readonly IReadOnlyList<int> KnownOffsets =
    [
        6, 667, 48, 102, 30, 12, 103, 618, 96, 738, 594, 98, -472, 733, 696, 116, 120, 691, 685, 99, 702, 97, 600,
        676, 690, 1292, 686, 697, -24, 704, 572, 1182, 688, -491, 91, 145, 689, 86, 355, 708, 79, 564, -496, 679,
        -1164, 0, 1160, -436, 684, 694, 1194, 94, 106, 681, 678, 117, 692, 943, 92, 680, 682, 1268, 1279, 1473, -54,
        1263, -582, 674, 687, 1272, 1508, -489, 740, 675, 534, 122, 974, 976, 1303, 111, 108, 1130, 975, 87, 739, 732,
        -589, -495, -494, -12, 961, 935, 699, 668, 234, 1776, 138, 1364, 1336, 1262, 1161, 1127
    ];

    private const int Margin = 1800;

    private readonly SpinarrOptions _options = options.Value;
    private readonly Dictionary<string, DriveTask> _tasks = [];

    /// <summary>Raised with the device whenever its task adds a line or ends.</summary>
    public event Action<string>? Changed;

    public DriveTask? Get(string device)
    {
        lock (_tasks)
        {
            return _tasks.GetValueOrDefault(device);
        }
    }

    public bool AnyRunning
    {
        get
        {
            lock (_tasks)
            {
                return _tasks.Values.Any(t => t.State == DriveTaskState.Running);
            }
        }
    }

    public bool IsRunning(string device) => Get(device)?.State == DriveTaskState.Running;

    /// <summary>Starts a task, unless one already runs on this drive. Returns null when it started.</summary>
    public string? Start(OpticalDrive drive, DriveTaskKind kind)
    {
        var task = new DriveTask { Device = drive.Device, Kind = kind };
        lock (_tasks)
        {
            if (_tasks.TryGetValue(drive.Device, out var running) && running.State == DriveTaskState.Running)
            {
                return $"{running.Title} is still running on this drive.";
            }

            _tasks[drive.Device] = task;
        }

        _ = Task.Run(() => RunAsync(task, drive));
        Changed?.Invoke(drive.Device);
        return null;
    }

    public void Cancel(string device) => Get(device)?.Cancel.Cancel();

    private void Say(DriveTask task, string line)
    {
        task.Add(line);
        Changed?.Invoke(task.Device);
    }

    private async Task RunAsync(DriveTask task, OpticalDrive drive)
    {
        try
        {
            (task.State, task.Summary) = task.Kind == DriveTaskKind.FindOffset
                ? await FindOffsetAsync(task, drive, task.Cancel.Token)
                : await AnalyzeCacheAsync(task, drive, task.Cancel.Token);
        }
        catch (OperationCanceledException)
        {
            (task.State, task.Summary) = (DriveTaskState.Cancelled, "Stopped.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "{Task} on {Device} failed", task.Title, task.Device);
            (task.State, task.Summary) = (DriveTaskState.Failed, e.Message);
        }

        task.Progress = null;
        task.EndedAt = DateTime.UtcNow;
        Say(task, $"[{task.State.ToString().ToLowerInvariant()}] {task.Summary}");
        logger.LogInformation("{Task} on {Drive}: {Summary}", task.Title, drive.DisplayName, task.Summary);
    }

    private async Task<(DriveTaskState, string)> AnalyzeCacheAsync(DriveTask task, OpticalDrive drive, CancellationToken cancellationToken)
    {
        Say(task, $"$ {_options.Tools.CdParanoia} {string.Join(' ', CdParanoia.AnalyzeArguments(drive.Device))}");
        task.Progress = "Reading the disc a few times (a few minutes)";
        var (defeats, output) = await reader.AnalyzeCacheAsync(drive.Device, line => Say(task, line), cancellationToken);
        if (defeats is not { } answer)
        {
            var last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);
            return (DriveTaskState.Failed, $"cd-paranoia gave no answer: {last ?? "no output"}");
        }

        settings.Save(drive, null, answer);
        return (DriveTaskState.Done, answer
            ? "cd-paranoia can defeat this drive's audio cache: saved."
            : "cd-paranoia cannot defeat this drive's audio cache: saved. Rips still work; each track is read twice and compared.");
    }

    private async Task<(DriveTaskState, string)> FindOffsetAsync(DriveTask task, OpticalDrive drive, CancellationToken cancellationToken)
    {
        var work = Directory.CreateTempSubdirectory("spinarr-offset-");
        try
        {
            task.Progress = "Reading the table of contents";
            Say(task, "Reading the table of contents (cdrdao, fast)…");
            var (toc, problem) = await reader.ReadTocAsync(drive.Device, true, Path.Combine(work.FullName, "disc.toc"), null, cancellationToken);
            if (toc == null)
            {
                return (DriveTaskState.Failed, problem ?? "No table of contents.");
            }

            var audio = toc.AudioTracks;
            Say(task, $"{audio.Count} audio track(s), {Cd.Length(toc.AudioLength)}.");
            if (audio.Count < 3)
            {
                return (DriveTaskState.Failed, "This needs a CD with at least 3 tracks: try another well-known disc.");
            }

            var disc = DiscIds.Of(toc);
            Say(task, $"AccurateRip: {AccurateRipDatabase.BaseUrl}{disc.AccurateRipPath}");
            var responses = await accurateRip.FetchAsync(disc, cancellationToken);
            if (responses == null)
            {
                return (DriveTaskState.Failed, "AccurateRip could not be reached. Check the container's internet access and try again.");
            }

            if (responses.Count == 0)
            {
                return (DriveTaskState.Failed, "AccurateRip doesn't know this disc: try a more common one.");
            }

            Say(task, $"AccurateRip has {responses.Count} answer(s) for this disc.");
            var readable = toc.TrackEnd(audio[^1].Number);

            // Track 1 once, with room on both sides for every offset in the list.
            var first = audio[0];
            var trackSamples = (long)toc.TrackLength(first.Number) * Cd.SamplesPerFrame;
            task.Progress = "Reading track 1";
            Say(task, $"Reading track 1 ({Cd.Length(toc.TrackLength(first.Number))}) with ±{Margin} samples around it…");
            var wide = ReadPlan.For(toc.TrackStart(first.Number), toc.TrackEnd(first.Number), 0, readable, Margin);
            var samples = await ReadSamplesAsync(drive.Device, toc, wide, work.FullName, "track01", cancellationToken);

            var tried = 0;
            foreach (var offset in KnownOffsets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                tried++;
                var slice = samples.AsSpan((int)(Margin + offset), (int)trackSamples);
                var (v1, v2) = TrackDigester.AccurateRip(slice, 1, audio.Count);
                if (!AccurateRipDatabase.Matches(responses, 0, v1, v2))
                {
                    continue;
                }

                Say(task, $"Offset {Format.Offset(offset)} matches AccurateRip on track 1 (after {tried} tried). Checking the other tracks…");
                task.Progress = $"Checking offset {Format.Offset(offset)}";
                var confirmed = await ConfirmAsync(task, drive, toc, responses, offset, readable, work.FullName, cancellationToken);
                if (confirmed)
                {
                    settings.Save(drive, offset, null);
                    return (DriveTaskState.Done, $"Read offset {Format.Offset(offset)}, saved for this drive.");
                }

                Say(task, $"Offset {Format.Offset(offset)} did not hold on every track; trying the next ones…");
            }

            return (DriveTaskState.Failed,
                $"None of the {KnownOffsets.Count} known offsets matched AccurateRip. Clean the disc or try another one; or set it by hand from AccurateRip's drive list.");
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>Tracks 2 to the one before last at the offset: each must match AccurateRip too (the last needs reading past the end).</summary>
    private async Task<bool> ConfirmAsync(DriveTask task, OpticalDrive drive, Toc toc, IReadOnlyList<AccurateRipResponse> responses,
        int offset, int readable, string work, CancellationToken cancellationToken)
    {
        var audio = toc.AudioTracks;
        for (var index = 1; index < audio.Count - 1; index++)
        {
            var track = audio[index];
            var plan = ReadPlan.For(toc.TrackStart(track.Number), toc.TrackEnd(track.Number), offset, readable);
            var raw = Path.Combine(work, $"track{index + 1:00}.raw.wav");
            var read = await reader.ReadAsync(drive.Device, toc, plan, raw, null, cancellationToken);
            if (!read.Ok)
            {
                Say(task, $"Track {index + 1}: {read.Problem}");
                return false;
            }

            var digester = new TrackDigester(index + 1, audio.Count, plan.Samples);
            Wav.Correct(raw, plan, digester, null);
            File.Delete(raw);
            var digest = digester.Result();
            var matches = AccurateRipDatabase.Matches(responses, index, digest.ArV1, digest.ArV2);
            Say(task, string.Create(CultureInfo.InvariantCulture,
                $"Track {index + 1}: v1 {digest.ArV1:X8}, v2 {digest.ArV2:X8}: {(matches ? "matches" : "no match")}"));
            if (!matches)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<uint[]> ReadSamplesAsync(string device, Toc toc, ReadPlan plan, string work, string name,
        CancellationToken cancellationToken)
    {
        var raw = Path.Combine(work, $"{name}.raw.wav");
        var read = await reader.ReadAsync(device, toc, plan, raw, null, cancellationToken);
        if (!read.Ok)
        {
            throw new InvalidOperationException(read.Problem);
        }

        var corrected = Path.Combine(work, $"{name}.wav");
        Wav.Correct(raw, plan, new TrackDigester(0, 1, plan.Samples), corrected);
        File.Delete(raw);
        var samples = Wav.ReadSamples(corrected);
        File.Delete(corrected);
        return samples;
    }
}
