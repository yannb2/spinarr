using System.Globalization;
using System.Text.RegularExpressions;
using Spinarr.Disc;

namespace Spinarr.Rip;

/// <summary>
/// What a read covers. cd-paranoia reads whole frames with no offset of its own; Spinarr shifts the
/// audio by the drive's read offset itself (as EAC does), so one read can be checked at any offset.
/// </summary>
public sealed record ReadPlan(int FirstFrame, int LastFrame, long SkipSamples, long Samples, long PadBefore, long PadAfter)
{
    /// <summary>The frames cd-paranoia reads, inclusive.</summary>
    public int Frames => LastFrame - FirstFrame + 1;

    /// <summary>The audio a range of frames should give, read with a drive whose read offset is <paramref name="offset"/> samples.</summary>
    /// <param name="first">The range's first frame.</param>
    /// <param name="last">The range's last frame (inclusive).</param>
    /// <param name="readable">The last frame the drive can read: the end of the last audio track (no reading into the lead-out).</param>
    /// <param name="margin">Extra samples read on each side, for trying other offsets on the same read.</param>
    public static ReadPlan For(int first, int last, int offset, int readable, int margin = 0)
    {
        // A positive offset means the drive hands the audio over early: read that much further on.
        var start = (long)first * Cd.SamplesPerFrame + offset - margin;
        var end = (long)(last + 1) * Cd.SamplesPerFrame + offset + margin;
        var firstFrame = (int)Math.Floor(start / (double)Cd.SamplesPerFrame);
        var lastFrame = (int)Math.Ceiling(end / (double)Cd.SamplesPerFrame) - 1;
        var readFirst = Math.Max(firstFrame, 0);
        var readLast = Math.Min(lastFrame, readable);
        // Before the first frame and past the end there is nothing to read: silence, as EAC fills it.
        var padBefore = Math.Max(0, (long)readFirst * Cd.SamplesPerFrame - start);
        var padAfter = Math.Max(0, end - (long)(readLast + 1) * Cd.SamplesPerFrame);
        var skip = Math.Max(0, start - (long)readFirst * Cd.SamplesPerFrame);
        return new ReadPlan(readFirst, readLast, skip, end - start, padBefore, padAfter);
    }
}

public static partial class CdParanoia
{
    /// <summary>
    /// The span argument for frames <paramref name="first"/> to <paramref name="last"/>:
    /// "track[hh:mm:ss.ff]-track[hh:mm:ss.ff]", each position counted from a track's start,
    /// worked out the way whipper does (track 0 is the hidden audio before track 1).
    /// </summary>
    public static string Span(Toc toc, int first, int last)
    {
        var startTrack = 0;
        var startOffset = first;
        var stopTrack = 0;
        var stopOffset = last;
        for (var number = 1; number <= toc.Tracks.Count; number++)
        {
            if (toc.TrackStart(number) <= first)
            {
                startTrack = number;
                startOffset = first - toc.TrackStart(number);
            }

            if (toc.TrackEnd(number) <= last)
            {
                stopTrack = number;
                stopOffset = last - toc.TrackStart(number);
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"{startTrack}[{Cd.Hmsf(startOffset)}]-{stopTrack}[{Cd.Hmsf(stopOffset)}]");
    }

    /// <summary>The arguments for one read into a WAV file, no offset (Spinarr corrects it).</summary>
    public static IReadOnlyList<string> ReadArguments(string device, Toc toc, ReadPlan plan, string wavPath) =>
        ["--stderr-progress", "--sample-offset=0", "--force-cdrom-device", device,
            Span(toc, plan.FirstFrame, plan.LastFrame), wavPath];

    /// <summary>"cd-paranoia -A" with the drive: its answer on the drive's audio cache.</summary>
    public static IReadOnlyList<string> AnalyzeArguments(string device) => ["-A", "-d", device];

    /// <summary>
    /// What "cd-paranoia -A" decided: true when the cache can be defeated, false when it can't,
    /// null when it gave no answer (no disc, an error).
    /// </summary>
    public static bool? DefeatsCache(string output, bool succeeded)
    {
        if (output.Contains("Drive tests OK with Paranoia.", StringComparison.Ordinal))
        {
            return true;
        }

        if (output.Contains("WARNING! PARANOIA MAY NOT BE", StringComparison.Ordinal)
            || output.Contains("aborting test.", StringComparison.Ordinal))
        {
            return false;
        }

        return succeeded ? false : null;
    }

    [GeneratedRegex(@"^##: (?<code>\S+)\s\[(?<function>[^\]]*)\]\s@\s(?<offset>\d+)")]
    internal static partial Regex ProgressLine();
}

/// <summary>
/// Follows cd-paranoia's --stderr-progress lines ("##: 0 [read] @ 54101880", positions in 16-bit
/// words) for progress and track quality. A port of whipper's parser, so the quality matches its logs.
/// </summary>
public sealed class ParanoiaProgress(int first, int last)
{
    private const int WordsPerFrame = Cd.BytesPerFrame / 2;
    private const int MaxErrors = 100;

    private long _read = first;
    private bool _framesKnown;

    /// <summary>Frames read, counting re-reads: each frame is read at least twice.</summary>
    public long Reads { get; private set; }

    /// <summary>The last frame written to the file.</summary>
    public long Wrote { get; private set; }

    public int Errors { get; private set; }

    public bool TooManyErrors => Errors > MaxErrors;

    /// <summary>0 to 1: how far the read has got.</summary>
    public double Fraction => Math.Clamp((Wrote - first + 1) / (double)(last - first + 1), 0, 1);

    public void Parse(string line)
    {
        if (line.StartsWith("scsi_read error:", StringComparison.Ordinal))
        {
            Errors++;
            return;
        }

        if (CdParanoia.ProgressLine().Match(line) is not { Success: true } m)
        {
            return;
        }

        var words = long.Parse(m.Groups["offset"].Value, CultureInfo.InvariantCulture);
        switch (m.Groups["function"].Value)
        {
            case "read":
                ParseRead(words);
                break;
            case "wrote":
                // Most [wrote] positions are one word short of a frame.
                Wrote = (words + 1) / WordsPerFrame;
                break;
        }
    }

    private void ParseRead(long words)
    {
        if (words % WordsPerFrame != 0)
        {
            return;
        }

        var frame = words / WordsPerFrame;
        _framesKnown = true;
        long markStart, markEnd;
        if (frame > _read)
        {
            markStart = _read;
            markEnd = frame;
        }
        else
        {
            // Went back to verify.
            markStart = frame;
            markEnd = frame;
        }

        // cd-paranoia reads past the end before going back to verify: those frames don't count.
        markEnd = Math.Min(markEnd, last + 1);
        markStart = Math.Min(markStart, last + 1);
        Reads += markEnd - markStart;
        _read = frame;
    }

    /// <summary>EAC's "track quality": 100% when every frame was read only the two times paranoia always does.</summary>
    public double Quality
    {
        get
        {
            if (!_framesKnown || Reads == 0)
            {
                return 0;
            }

            var frames = last - first + 1;
            return Math.Min(frames * 2.0 / Reads, 1.0);
        }
    }
}
