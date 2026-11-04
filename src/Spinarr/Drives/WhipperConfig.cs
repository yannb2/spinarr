using System.Globalization;
using System.Text;

namespace Spinarr.Drives;

/// <summary>What whipper.conf holds for one drive.</summary>
public sealed record DriveSettings(string Vendor, string Model, string Release, int? ReadOffset, bool? DefeatsCache);

/// <summary>
/// Reads and writes the drive sections of whipper.conf, the same file whipper itself uses, so a
/// rip and the Drives page always agree. A section looks like:
/// <code>
/// [drive:HL-DT-ST%3ADVDRAM%20GH40L%3A1.04]
/// vendor = HL-DT-ST
/// model = DVDRAM GH40L
/// release = 1.04
/// read_offset = 667
/// defeats_cache = True
/// </code>
/// Writing changes only those lines and leaves everything else in the file as it was.
/// </summary>
public static class WhipperConfig
{
    public static IReadOnlyList<DriveSettings> Read(string path)
    {
        return File.Exists(path) ? Parse(File.ReadAllText(path)) : [];
    }

    public static IReadOnlyList<DriveSettings> Parse(string text)
    {
        var drives = new List<DriveSettings>();
        foreach (var (name, values) in Sections(text))
        {
            if (!name.StartsWith("drive:", StringComparison.Ordinal))
            {
                continue;
            }

            drives.Add(new DriveSettings(
                values.GetValueOrDefault("vendor", ""),
                values.GetValueOrDefault("model", ""),
                values.GetValueOrDefault("release", ""),
                int.TryParse(values.GetValueOrDefault("read_offset"), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offset)
                    ? offset
                    : null,
                Boolean(values.GetValueOrDefault("defeats_cache"))));
        }

        return drives;
    }

    /// <summary>The settings of this drive, matched like whipper does: vendor, model and firmware, spaces trimmed.</summary>
    public static DriveSettings? Find(IEnumerable<DriveSettings> settings, OpticalDrive drive)
    {
        return settings.FirstOrDefault(s => s.Vendor.Trim() == drive.Vendor.Trim()
                                            && s.Model.Trim() == drive.Model.Trim()
                                            && s.Release.Trim() == drive.Release.Trim());
    }

    /// <summary>
    /// Sets the read offset and/or the cache answer of a drive (null leaves it alone), adding the
    /// drive's section when it has none yet.
    /// </summary>
    public static string SetDrive(string text, OpticalDrive drive, int? readOffset, bool? defeatsCache)
    {
        var values = new List<(string Key, string Value)>();
        if (readOffset is { } offset)
        {
            values.Add(("read_offset", offset.ToString(CultureInfo.InvariantCulture)));
        }

        if (defeatsCache is { } defeats)
        {
            // Python's str(True), what whipper writes itself.
            values.Add(("defeats_cache", defeats ? "True" : "False"));
        }

        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var (start, end) = FindSection(lines, drive);
        if (start < 0)
        {
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
            {
                lines.Add("");
            }

            lines.Add($"[{SectionName(drive.Vendor, drive.Model, drive.Release)}]");
            lines.Add($"vendor = {drive.Vendor.Trim()}");
            lines.Add($"model = {drive.Model.Trim()}");
            lines.Add($"release = {drive.Release.Trim()}");
            lines.AddRange(values.Select(v => $"{v.Key} = {v.Value}"));
            lines.Add("");
            return string.Join('\n', lines) + "\n";
        }

        foreach (var (key, value) in values)
        {
            var at = lines.FindIndex(start + 1, end - start - 1, l => KeyOf(l) == key);
            if (at >= 0)
            {
                lines[at] = $"{key} = {value}";
            }
            else
            {
                // After the section's last line with something on it.
                var last = end - 1;
                while (last > start && lines[last].Trim().Length == 0)
                {
                    last--;
                }

                lines.Insert(last + 1, $"{key} = {value}");
                end++;
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>
    /// The section name whipper gives a new drive: "drive:" + Python's urllib quote of
    /// "vendor:model:release" (letters, digits, "_.-~/" kept, everything else %XX in UTF-8).
    /// </summary>
    public static string SectionName(string vendor, string model, string release)
    {
        var raw = $"{vendor.Trim()}:{model.Trim()}:{release.Trim()}";
        var builder = new StringBuilder("drive:");
        foreach (var b in Encoding.UTF8.GetBytes(raw))
        {
            var c = (char)b;
            if (b < 0x80 && (char.IsAsciiLetterOrDigit(c) || "_.-~/".Contains(c)))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static (int Start, int End) FindSection(List<string> lines, OpticalDrive drive)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (HeaderOf(lines[i]) is not { } name || !name.StartsWith("drive:", StringComparison.Ordinal))
            {
                continue;
            }

            var end = lines.FindIndex(i + 1, l => HeaderOf(l) != null);
            end = end < 0 ? lines.Count : end;
            var values = lines.Skip(i + 1).Take(end - i - 1)
                .Select(l => (Key: KeyOf(l), Value: ValueOf(l)))
                .Where(p => p.Key != null)
                .GroupBy(p => p.Key!)
                .ToDictionary(g => g.Key, g => g.Last().Value);
            var settings = new DriveSettings(values.GetValueOrDefault("vendor", ""), values.GetValueOrDefault("model", ""),
                values.GetValueOrDefault("release", ""), null, null);
            if (Find([settings], drive) != null)
            {
                return (i, end);
            }
        }

        return (-1, -1);
    }

    private static IEnumerable<(string Name, Dictionary<string, string> Values)> Sections(string text)
    {
        string? name = null;
        var values = new Dictionary<string, string>();
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (HeaderOf(line) is { } header)
            {
                if (name != null)
                {
                    yield return (name, values);
                }

                (name, values) = (header, new Dictionary<string, string>());
            }
            else if (name != null && KeyOf(line) is { } key)
            {
                values[key] = ValueOf(line);
            }
        }

        if (name != null)
        {
            yield return (name, values);
        }
    }

    private static string? HeaderOf(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']' ? trimmed[1..^1] : null;
    }

    /// <summary>The key of a "key = value" (or "key: value") line, lower case like ConfigParser; null for comments.</summary>
    private static string? KeyOf(string line)
    {
        if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or ';' or '[')
        {
            return null;
        }

        var at = line.IndexOfAny(['=', ':']);
        return at > 0 ? line[..at].Trim().ToLowerInvariant() : null;
    }

    private static string ValueOf(string line)
    {
        var at = line.IndexOfAny(['=', ':']);
        return at > 0 ? line[(at + 1)..].Trim() : "";
    }

    private static bool? Boolean(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "1" or "yes" or "true" or "on" => true,
        "0" or "no" or "false" or "off" => false,
        _ => null
    };
}
