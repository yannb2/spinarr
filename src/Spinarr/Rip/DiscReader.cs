using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Spinarr.Disc;
using Spinarr.Options;
using Spinarr.Tools;

namespace Spinarr.Rip;

/// <summary>How one cd-paranoia read went.</summary>
public sealed record ReadResult(bool Ok, string? Problem, double Quality, double Speed, TimeSpan Duration, int Errors);

/// <summary>Runs cdrdao and cd-paranoia on a drive. Every other step is Spinarr's own C#.</summary>
public sealed partial class DiscReader(IOptions<SpinarrOptions> options, ILogger<DiscReader> logger)
{
    private readonly SpinarrOptions _options = options.Value;

    /// <summary>
    /// Reads the table of contents with cdrdao into <paramref name="tocPath"/>. Fast skips the
    /// pre-gap search (seconds instead of minutes): fine for the disc IDs, not for the rip's cue sheet.
    /// </summary>
    public async Task<(Toc? Toc, string? Problem)> ReadTocAsync(string device, bool fast, string tocPath,
        Action<string>? onLine, CancellationToken cancellationToken)
    {
        File.Delete(tocPath); // cdrdao won't overwrite a file
        Directory.CreateDirectory(Path.GetDirectoryName(tocPath)!);
        List<string> arguments = ["read-toc", .. fast ? new[] { "--fast-toc" } : [], "--device", device, tocPath];
        var spec = new ProcessSpec(_options.Tools.Cdrdao, arguments) { KeepChars = 64 * 1024 };
        logger.LogInformation("Reading the TOC of {Device}: {Command}", device, spec.CommandLine);
        var result = await ProcessRunner.RunAsync(spec, TimeSpan.FromMinutes(fast ? 3 : 20), onLine, cancellationToken);
        if (!File.Exists(tocPath))
        {
            var problem = result.NotStarted ? result.LastLine : CdrdaoProblem(result.Output + "\n" + result.Error);
            logger.LogWarning("cdrdao read no TOC from {Device}: {Problem}", device, problem);
            return (null, problem);
        }

        var toc = CdrdaoToc.Read(tocPath);
        if (toc.Tracks.Count == 0 || toc.AudioTracks.Count == 0)
        {
            return (null, "The disc has no audio tracks.");
        }

        return (toc, null);
    }

    /// <summary>Reads the frames of <paramref name="plan"/> into a WAV file with cd-paranoia (no offset: Spinarr shifts it).</summary>
    public async Task<ReadResult> ReadAsync(string device, Toc toc, ReadPlan plan, string wavPath,
        Action<double>? onProgress, CancellationToken cancellationToken)
    {
        File.Delete(wavPath);
        var progress = new ParanoiaProgress(plan.FirstFrame, plan.LastFrame);
        var spec = new ProcessSpec(_options.Tools.CdParanoia, CdParanoia.ReadArguments(device, toc, plan, wavPath))
        {
            KeepChars = 16 * 1024
        };
        logger.LogDebug("{Command}", spec.CommandLine);
        var lastReport = 0.0;
        using var tooManyErrors = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watch = Stopwatch.StartNew();
        var result = await ProcessRunner.RunAsync(spec, TimeSpan.FromHours(2), line =>
        {
            progress.Parse(line);
            if (progress.TooManyErrors)
            {
                tooManyErrors.Cancel();
            }

            if (progress.Fraction - lastReport >= 0.01)
            {
                lastReport = progress.Fraction;
                onProgress?.Invoke(progress.Fraction);
            }
        }, tooManyErrors.Token).ContinueWith(t => t.IsCanceled && !cancellationToken.IsCancellationRequested
            ? new ProcessResult(1, "", "the drive gave too many read errors")
            : t.GetAwaiter().GetResult(), TaskScheduler.Default);
        watch.Stop();

        var speed = plan.Frames / (double)Cd.FramesPerSecond / Math.Max(watch.Elapsed.TotalSeconds, 0.001);
        if (!result.Success)
        {
            var problem = result.NotStarted ? result.LastLine : $"cd-paranoia stopped: {result.LastLine}";
            return new ReadResult(false, problem, progress.Quality, speed, watch.Elapsed, progress.Errors);
        }

        var expected = (long)plan.Frames * Cd.BytesPerFrame + 44;
        var size = File.Exists(wavPath) ? new FileInfo(wavPath).Length : 0;
        if (size != expected)
        {
            return new ReadResult(false, $"cd-paranoia wrote {size} bytes, {expected} were expected", progress.Quality, speed,
                watch.Elapsed, progress.Errors);
        }

        onProgress?.Invoke(1);
        return new ReadResult(true, null, progress.Quality, speed, watch.Elapsed, progress.Errors);
    }

    /// <summary>"cd-paranoia -A": can the drive's audio cache be defeated? Takes minutes.</summary>
    public async Task<(bool? Defeats, string Output)> AnalyzeCacheAsync(string device, Action<string>? onLine,
        CancellationToken cancellationToken)
    {
        // cd-paranoia -A always writes cdparanoia.log in the folder it runs in.
        var folder = Directory.CreateTempSubdirectory("spinarr-cache-");
        try
        {
            var spec = new ProcessSpec(_options.Tools.CdParanoia, CdParanoia.AnalyzeArguments(device))
            {
                WorkingDirectory = folder.FullName,
                KeepChars = 256 * 1024
            };
            var result = await ProcessRunner.RunAsync(spec, TimeSpan.FromMinutes(30), onLine, cancellationToken);
            var output = result.Output + "\n" + result.Error;
            return (result.NotStarted ? null : CdParanoia.DefeatsCache(output, result.Success), output);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    /// <summary>The line of cdrdao's output that says what went wrong, in plain words when it's a known one.</summary>
    internal static string CdrdaoProblem(string output)
    {
        if (output.Contains("Unit not ready", StringComparison.OrdinalIgnoreCase)
            || output.Contains("medium not present", StringComparison.OrdinalIgnoreCase))
        {
            return "No disc in the drive, or it is still spinning up.";
        }

        if (output.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return "No permission to open the drive: add its group (getent group cdrom) to the container with group_add.";
        }

        var error = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => ErrorLine().IsMatch(l));
        return error ?? output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "cdrdao gave no answer.";
    }

    [GeneratedRegex("^ERROR:", RegexOptions.IgnoreCase)]
    private static partial Regex ErrorLine();
}
