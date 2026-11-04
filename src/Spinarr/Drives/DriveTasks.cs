using Microsoft.Extensions.Options;
using Spinarr.Options;
using Spinarr.Tools;

namespace Spinarr.Drives;

public enum DriveTaskKind
{
    /// <summary>whipper offset find: rips the first track at many offsets until AccurateRip agrees.</summary>
    FindOffset,

    /// <summary>whipper drive analyze: can cdparanoia defeat the drive's audio cache?</summary>
    AnalyzeCache
}

public enum DriveTaskState
{
    Running,
    Done,
    Failed,
    Cancelled
}

/// <summary>One whipper run on a drive, with what it printed.</summary>
public sealed class DriveTask
{
    private const int KeptLines = 2000;
    private readonly List<string> _lines = [];

    public required string Device { get; init; }
    public required DriveTaskKind Kind { get; init; }
    public required string CommandLine { get; init; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; internal set; }
    public DriveTaskState State { get; internal set; } = DriveTaskState.Running;

    /// <summary>One sentence for the drive card: the result, or what went wrong.</summary>
    public string? Summary { get; internal set; }

    /// <summary>What it is doing now ("Trying offset 48").</summary>
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

/// <summary>
/// Runs whipper's drive setup commands, one at a time per drive. Whipper saves each result in
/// whipper.conf itself; the Drives page reads it back from there.
/// </summary>
public sealed class DriveTasks(IOptions<SpinarrOptions> options, ILogger<DriveTasks> logger)
{
    private readonly SpinarrOptions _options = options.Value;
    private readonly Dictionary<string, DriveTask> _tasks = [];

    /// <summary>Raised with the device whenever its task prints a line or ends.</summary>
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

    /// <summary>Starts a task, unless one already runs on this drive. Returns null when it started.</summary>
    public string? Start(OpticalDrive drive, DriveTaskKind kind)
    {
        var arguments = Arguments(kind, drive.Device);
        var spec = new ProcessSpec(_options.Tools.Whipper, arguments) { Environment = _options.WhipperEnvironment };
        var task = new DriveTask { Device = drive.Device, Kind = kind, CommandLine = spec.CommandLine };
        lock (_tasks)
        {
            if (_tasks.TryGetValue(drive.Device, out var running) && running.State == DriveTaskState.Running)
            {
                return $"{running.Title} is still running on this drive.";
            }

            _tasks[drive.Device] = task;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(_options.WhipperConfigPath)!);
        Directory.CreateDirectory(_options.CacheFolder);
        task.Add($"$ {spec.CommandLine}");
        _ = Task.Run(() => RunAsync(task, spec));
        Changed?.Invoke(drive.Device);
        return null;
    }

    public void Cancel(string device) => Get(device)?.Cancel.Cancel();

    internal static IReadOnlyList<string> Arguments(DriveTaskKind kind, string device) => kind switch
    {
        DriveTaskKind.FindOffset => ["offset", "find", "-d", device],
        _ => ["drive", "analyze", "-d", device]
    };

    private async Task RunAsync(DriveTask task, ProcessSpec spec)
    {
        // Finding the offset rips track 1 again for every offset tried: it can take an hour on a slow drive.
        var timeout = task.Kind == DriveTaskKind.FindOffset ? TimeSpan.FromHours(3) : TimeSpan.FromMinutes(20);
        try
        {
            var result = await ProcessRunner.RunAsync(spec, timeout, line =>
            {
                task.Add(line);
                if (WhipperOutput.TryingOffset(line) is { } offset)
                {
                    task.Progress = $"Trying offset {offset:+0;-0;0}";
                }

                Changed?.Invoke(task.Device);
            }, task.Cancel.Token);

            var lines = task.Lines;
            (task.State, task.Summary) = Finish(task.Kind, result, lines);
        }
        catch (OperationCanceledException)
        {
            (task.State, task.Summary) = (DriveTaskState.Cancelled, "Stopped.");
        }
        catch (Exception e)
        {
            logger.LogError(e, "Drive task on {Device} failed", task.Device);
            (task.State, task.Summary) = (DriveTaskState.Failed, e.Message);
        }

        task.Progress = null;
        task.EndedAt = DateTime.UtcNow;
        task.Add($"[{task.State.ToString().ToLowerInvariant()}] {task.Summary}");
        Changed?.Invoke(task.Device);
    }

    /// <summary>How a finished run went, from whipper's exit code and what it printed.</summary>
    internal static (DriveTaskState State, string Summary) Finish(DriveTaskKind kind, ProcessResult result, IReadOnlyList<string> lines)
    {
        if (result.NotStarted)
        {
            return (DriveTaskState.Failed, $"whipper could not be started: {result.LastLine}");
        }

        if (result.ExitCode == ProcessRunner.TimedOutCode)
        {
            return (DriveTaskState.Failed, result.LastLine);
        }

        if (kind == DriveTaskKind.FindOffset && WhipperOutput.FoundOffset(lines) is { } offset)
        {
            return (DriveTaskState.Done, $"Read offset {offset:+0;-0;0}, saved for this drive.");
        }

        if (kind == DriveTaskKind.AnalyzeCache && WhipperOutput.DefeatsCache(lines) is { } defeats)
        {
            return (DriveTaskState.Done, defeats
                ? "cd-paranoia can defeat this drive's audio cache: saved."
                : "cd-paranoia cannot defeat this drive's audio cache: saved. Rips still work; each track is read twice and compared.");
        }

        return (DriveTaskState.Failed, WhipperOutput.Problem(lines)
                                       ?? (result.Success ? "whipper ended without an answer." : $"whipper stopped: {result.LastLine}"));
    }
}
