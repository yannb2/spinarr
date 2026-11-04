namespace Spinarr.Disc;

/// <summary>One track of the disc. Addresses are frames from the start of the disc (LBA).</summary>
public sealed class TocTrack
{
    public required int Number { get; init; }
    public bool Audio { get; init; } = true;

    /// <summary>Session 1 unless the track is in a later one (the data track of a CD-Extra).</summary>
    public int Session { get; init; } = 1;

    /// <summary>Index 00: where the pre-gap starts, when the track has one.</summary>
    public int? Index0 { get; internal set; }

    /// <summary>Index 01: where the track starts.</summary>
    public int Start { get; internal set; }

    /// <summary>Index 02 and up, rare: places inside the track.</summary>
    public List<int> MoreIndexes { get; } = [];

    public string? Isrc { get; internal set; }
    public bool PreEmphasis { get; internal set; }
    public Dictionary<string, string> CdText { get; } = [];

    public int Pregap => Index0 is { } zero ? Start - zero : 0;

    public override string ToString() => $"Track {Number:00}";
}

/// <summary>A disc's table of contents, as cdrdao reads it.</summary>
public sealed class Toc
{
    // From cdrecord: the first extra session sits 11400 frames after the end of the one before
    // (lead-out, lead-in and the pre-gap of its first track); later ones 6900.
    private const int FirstSessionGap = 11400;
    private const int LaterSessionGap = 6900;

    public List<TocTrack> Tracks { get; } = [];

    /// <summary>The lead-out: one past the last frame of the last track.</summary>
    public int Leadout { get; set; }

    /// <summary>The disc's barcode (UPC/EAN), when the disc carries it.</summary>
    public string? Catalog { get; set; }

    public Dictionary<string, string> CdText { get; } = [];

    public TocTrack Track(int number) => Tracks[number - 1];

    public IReadOnlyList<TocTrack> AudioTracks => Tracks.Where(t => t.Audio).ToList();

    public bool HasDataTracks => Tracks.Any(t => !t.Audio);

    public int TrackStart(int number) => Track(number).Start;

    /// <summary>The last frame of a track (inclusive): the frame before the next track's index 01, so a pre-gap belongs to the track before it.</summary>
    public int TrackEnd(int number)
    {
        if (number >= Tracks.Count)
        {
            return Leadout - 1;
        }

        var end = Tracks[number].Start - 1;
        var current = Tracks[number - 1];
        var next = Tracks[number];
        if (next.Session > current.Session)
        {
            end -= next.Session > 2 ? LaterSessionGap : FirstSessionGap;
        }

        return end;
    }

    public int TrackLength(int number) => TrackEnd(number) - TrackStart(number) + 1;

    /// <summary>Hidden audio before track 1 (HTOA): track 1's pre-gap, when the disc starts with one.</summary>
    public (int Start, int End)? Htoa =>
        Tracks.Count > 0 && Tracks[0].Index0 is { } zero && Tracks[0].Start > zero
            ? (zero, Tracks[0].Start - 1)
            : null;

    /// <summary>The length of the audio tracks in frames, not counting the HTOA.</summary>
    public int AudioLength
    {
        get
        {
            var lastAudio = Tracks.Last(t => t.Audio).Number;
            return TrackEnd(lastAudio) + 1 - TrackStart(1);
        }
    }
}
