using Spinarr.AccurateRip;
using Spinarr.Audio;
using Spinarr.Disc;
using Spinarr.Drives;
using Spinarr.MusicBrainz;

namespace Spinarr.Rip;

public enum RipState
{
    Running,
    Done,
    Failed,
    Cancelled
}

public enum TrackState
{
    Waiting,
    Reading,
    Verifying,
    Encoding,
    Done,
    Silent,
    Error
}

/// <summary>What the person chose on the disc page.</summary>
public sealed record RipRequest(
    OpticalDrive Drive,
    DiscIdentity Disc,
    MbRelease? Release,
    MbMedium? Medium,
    int FlacLevel,
    int Retries);

/// <summary>A rip that ended, as /config/rips.json keeps it.</summary>
public sealed record RipRecord(
    string Id,
    string Title,
    string Drive,
    string DiscId,
    string? ReleaseId,
    DateTime StartedAt,
    DateTime EndedAt,
    RipState State,
    string Summary,
    string? Destination,
    string? LogFile,
    int Tracks,
    int Accurate,
    int Errors,
    int FlacLevel);

/// <summary>One track of a rip, as it goes: the page shows these, the log is written from them.</summary>
public sealed class RipTrack
{
    /// <summary>The track's number on the disc; 0 for the hidden audio before track 1.</summary>
    public required int Number { get; init; }

    /// <summary>Its place among the audio tracks for AccurateRip (1-based); 0 for the HTOA.</summary>
    public required int AudioIndex { get; init; }

    public required string Title { get; init; }
    public required string Artist { get; init; }
    public required int FirstFrame { get; init; }
    public required int LastFrame { get; init; }
    public required string FileName { get; set; }

    public int Frames => LastFrame - FirstFrame + 1;
    public int Pregap { get; init; }
    public string? Isrc { get; init; }
    public bool PreEmphasis { get; init; }

    public TrackState State { get; internal set; } = TrackState.Waiting;
    public int Attempts { get; internal set; }
    public TrackDigest? Test { get; internal set; }
    public TrackDigest? Copy { get; internal set; }
    public double Quality { get; internal set; }
    public double Speed { get; internal set; }
    public AccurateRipMatch? AccurateRip { get; internal set; }
    public string? Problem { get; internal set; }

    /// <summary>0 to 1 for the track: the test read is the first half, the copy read the second.</summary>
    public double Fraction { get; internal set; }

    public bool CrcsMatch => Test != null && Copy != null && Test.Crc == Copy.Crc;
}

/// <summary>A rip from the drive: started from the disc page, followed live, finished with a log and a cue sheet.</summary>
public sealed class RipJob
{
    private const int KeptLines = 3000;
    private readonly List<string> _lines = [];

    public required string Id { get; init; }
    public required RipRequest Request { get; init; }
    public required string WorkFolder { get; init; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;
    public DateTime? EndedAt { get; internal set; }
    public RipState State { get; internal set; } = RipState.Running;
    public string? Summary { get; internal set; }

    /// <summary>What it is doing now, in a few words.</summary>
    public string Step { get; internal set; } = "Starting";

    public Toc? Toc { get; internal set; }
    public int? ReadOffset { get; internal set; }
    public bool? DefeatsCache { get; internal set; }
    public IReadOnlyList<AccurateRipResponse>? AccurateRipResponses { get; internal set; }
    public bool AccurateRipReached { get; internal set; } = true;
    public List<RipTrack> Tracks { get; } = [];

    /// <summary>The album folder the files went to, once done.</summary>
    public string? Destination { get; internal set; }

    public string? LogFile { get; internal set; }

    internal CancellationTokenSource Cancel { get; } = new();

    public double Fraction
    {
        get
        {
            var tracks = Tracks.ToList();
            if (tracks.Count == 0)
            {
                return 0;
            }

            var frames = tracks.Sum(t => (double)t.Frames);
            return tracks.Sum(t => t.Frames * (t.State is TrackState.Done or TrackState.Silent or TrackState.Error ? 1 : t.Fraction)) / frames;
        }
    }

    public RipTrack? Current => Tracks.FirstOrDefault(t => t.State is TrackState.Reading or TrackState.Verifying or TrackState.Encoding);

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
            _lines.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_lines.Count > KeptLines)
            {
                _lines.RemoveRange(0, _lines.Count - KeptLines);
            }
        }
    }

    public string Title => Request.Release is { } r ? $"{r.Artist} - {r.Title}" : "Unknown disc";
}
