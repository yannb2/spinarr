using System.Globalization;

namespace Spinarr;

public static class Format
{
    public static string TimeAgo(DateTime utc, DateTime? nowUtc = null)
    {
        var span = (nowUtc ?? DateTime.UtcNow) - utc;
        if (span < TimeSpan.Zero)
        {
            return "just now";
        }

        return span.TotalMinutes switch
        {
            < 1 => "just now",
            < 60 => Plural((int)span.TotalMinutes, "minute"),
            < 60 * 24 => Plural((int)span.TotalHours, "hour"),
            _ => Plural((int)span.TotalDays, "day")
        };
    }

    /// <summary>"12:04", or "3:12:04" past an hour.</summary>
    public static string Duration(TimeSpan span) =>
        span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    /// <summary>An offset the way AccurateRip lists them: +667, -1164, 0.</summary>
    public static string Offset(int offset) => offset.ToString("+0;-0;0", CultureInfo.InvariantCulture);

    private static string Plural(int n, string word) => $"{n} {word}{(n == 1 ? "" : "s")} ago";
}
