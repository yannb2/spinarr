using System.Globalization;
using System.Text.RegularExpressions;

namespace Spinarr.Drives;

/// <summary>
/// Reads what whipper prints. Its log lines look like "INFO:whipper.command.offset:trying read offset 6..."
/// (Python's basic log format: level, module, message); results such as the offset come on stdout.
/// </summary>
public static partial class WhipperOutput
{
    public sealed record Line(string Level, string Message);

    /// <summary>The level and message of a log line, or the line as it is for plain output.</summary>
    public static Line Split(string line)
    {
        var m = LogLine().Match(line);
        return m.Success ? new Line(m.Groups[1].Value, m.Groups[2].Value) : new Line("", line.Trim());
    }

    /// <summary>The offset whipper settled on ("Read offset of device is: 667.").</summary>
    public static int? FoundOffset(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            var m = OffsetFound().Match(line);
            if (m.Success)
            {
                return int.Parse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            }
        }

        return null;
    }

    /// <summary>The offset being tried right now, for the progress line.</summary>
    public static int? TryingOffset(string line)
    {
        var m = TryingOffsetLine().Match(line);
        return m.Success ? int.Parse(m.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The drive analyze answer: can cdparanoia defeat the drive's audio cache?</summary>
    public static bool? DefeatsCache(IEnumerable<string> lines)
    {
        foreach (var line in lines)
        {
            if (line.Contains("cannot defeat the audio cache", StringComparison.Ordinal))
            {
                return false;
            }

            if (line.Contains("can defeat the audio cache", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return null;
    }

    /// <summary>
    /// What went wrong, in words for the Drives page: the first CRITICAL or ERROR message, or a
    /// warning that ends the task (no AccurateRip entry for this disc).
    /// </summary>
    public static string? Problem(IEnumerable<string> lines)
    {
        string? warning = null;
        foreach (var line in lines.Select(Split))
        {
            if (line.Level is "CRITICAL" or "ERROR")
            {
                return Explain(line.Message);
            }

            if (line.Level == "WARNING" && line.Message.StartsWith("AccurateRip entry not found", StringComparison.Ordinal))
            {
                warning ??= Explain(line.Message);
            }
        }

        return warning;
    }

    private static string Explain(string message)
    {
        if (message.StartsWith("AccurateRip entry not found", StringComparison.Ordinal))
        {
            return "This disc is not in the AccurateRip database, so it can't show the offset. Try a better-known CD.";
        }

        if (message.Contains("needs a CD with at least 3 tracks", StringComparison.Ordinal))
        {
            return "Finding the offset needs a CD with at least 3 tracks.";
        }

        if (message.Contains("is there a CD in it", StringComparison.Ordinal))
        {
            return "The drive could not be read: is there an audio CD in it?";
        }

        if (message.StartsWith("no matching offset found", StringComparison.Ordinal))
        {
            return "No offset matched AccurateRip with this disc. Try another, well-known CD.";
        }

        return message;
    }

    [GeneratedRegex(@"^(DEBUG|INFO|WARNING|ERROR|CRITICAL):[\w.]+:(.*)$")]
    private static partial Regex LogLine();

    [GeneratedRegex(@"Read offset of device is: (-?\d+)")]
    private static partial Regex OffsetFound();

    [GeneratedRegex(@"trying read offset (-?\d+)")]
    private static partial Regex TryingOffsetLine();
}
