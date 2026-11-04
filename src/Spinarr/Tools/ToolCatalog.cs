using System.Globalization;
using System.Text.RegularExpressions;
using Spinarr.Options;

namespace Spinarr.Tools;

/// <summary>Where a tool's newest version is read.</summary>
public enum UpstreamKind
{
    /// <summary>Comes with the base image's packages: it moves with the image, not with a pin.</summary>
    None,

    /// <summary>A web page; the highest version the regex finds.</summary>
    Page
}

/// <summary>A tool in the image: how to ask its version, which version the image pins, where to look for a newer one.</summary>
public sealed record ToolDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Purpose { get; init; }

    /// <summary>The command to run for the version, or null for a library (see <see cref="VersionFile"/>).</summary>
    public Func<ToolPaths, string>? Command { get; init; }

    public IReadOnlyList<string> VersionArguments { get; init; } = [];

    /// <summary>A library has no command: its version is read from this file (a pkg-config .pc file).</summary>
    public string? VersionFile { get; init; }

    public required Regex VersionPattern { get; init; }
    public UpstreamKind Upstream { get; init; }

    /// <summary>The page the newest version is read from.</summary>
    public string? UpstreamSource { get; init; }

    public Regex? UpstreamPattern { get; init; }
    public string? ReleaseNotes { get; init; }

    /// <summary>The version the Dockerfile installed (SPINARR_PIN_CD_PARANOIA...).</summary>
    public string? Pinned => Environment.GetEnvironmentVariable(PinVariable);

    public string PinVariable => $"SPINARR_PIN_{Id.ToUpperInvariant()}";
}

public static partial class ToolCatalog
{
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new()
        {
            Id = "cd_paranoia", Name = "cd-paranoia",
            Purpose = "Reads the audio from the disc: secure reads with error correction, and the drive cache check.",
            Command = t => t.CdParanoia, VersionArguments = ["--version"],
            VersionPattern = ParanoiaVersion(),
            Upstream = UpstreamKind.Page,
            UpstreamSource = "https://ftp.gnu.org/gnu/libcdio/",
            UpstreamPattern = ParanoiaUpstream(),
            ReleaseNotes = "https://github.com/libcdio/libcdio-paranoia/blob/master/NEWS.md"
        },
        new()
        {
            Id = "libcdio", Name = "libcdio",
            Purpose = "The CD library cd-paranoia is built on.",
            VersionFile = "/usr/lib/pkgconfig/libcdio.pc",
            VersionPattern = PkgConfigVersion(),
            Upstream = UpstreamKind.Page,
            UpstreamSource = "https://ftp.gnu.org/gnu/libcdio/",
            UpstreamPattern = LibcdioUpstream(),
            ReleaseNotes = "https://github.com/libcdio/libcdio/blob/master/NEWS.md"
        },
        new()
        {
            Id = "cdrdao", Name = "cdrdao",
            Purpose = "Reads the disc's table of contents: pre-gaps, hidden track, ISRCs, CD-Text.",
            Command = t => t.Cdrdao,
            VersionPattern = CdrdaoVersion()
        },
        new()
        {
            Id = "flac", Name = "FLAC",
            Purpose = "Encodes and checks the tracks; metaflac writes ReplayGain and tags.",
            Command = t => t.Flac, VersionArguments = ["--version"],
            VersionPattern = FlacVersion()
        }
    ];

    /// <summary>The installed version in a tool's own output (or its .pc file).</summary>
    public static string? InstalledVersion(ToolDefinition tool, string output) =>
        tool.VersionPattern.Match(output) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The highest version the upstream pattern finds on a page.</summary>
    public static string? HighestOnPage(Regex pattern, string page)
    {
        return pattern.Matches(page)
            .Select(m => m.Groups[1].Value)
            .OrderByDescending(v => v, VersionComparer.Instance)
            .FirstOrDefault();
    }

    /// <summary>True when upstream has something newer than what the image holds.</summary>
    public static bool IsNewer(ToolDefinition tool, string? latest, string? current) =>
        latest != null && current != null && VersionComparer.Instance.Compare(latest, current) > 0;

    // "cdparanoia III release 10.2 libcdio 2.1.0 x86_64-pc-linux-gnu": only the paranoia half of the
    // package version (10.2+2.0.1) is printed; a full one is taken when a build prints it.
    [GeneratedRegex(@"(\d+\.\d+\+\d+\.\d+\.\d+|(?<=release )\d+\.\d+)")]
    private static partial Regex ParanoiaVersion();

    [GeneratedRegex(@"libcdio-paranoia-(\d+\.\d+\+\d+\.\d+\.\d+)\.tar\.bz2")]
    private static partial Regex ParanoiaUpstream();

    [GeneratedRegex(@"libcdio-(\d+\.\d+\.\d+)\.tar\.bz2")]
    private static partial Regex LibcdioUpstream();

    [GeneratedRegex(@"(?m)^Version:\s*(\S+)")]
    private static partial Regex PkgConfigVersion();

    [GeneratedRegex(@"Cdrdao version (\d+\.\d+\.\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex CdrdaoVersion();

    [GeneratedRegex(@"flac (\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex FlacVersion();
}

/// <summary>Compares "2.1.0" and "2.1.10", or "10.2+2.0.1" and "10.2+2.0.2", by their numbers, not as text.</summary>
public sealed class VersionComparer : IComparer<string>
{
    public static readonly VersionComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        var a = Parts(x);
        var b = Parts(y);
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var left = i < a.Length ? a[i] : 0;
            var right = i < b.Length ? b[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        return 0;
    }

    private static int[] Parts(string? version) =>
        (version ?? "").Split('.', '-', '+')
        .Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
        .ToArray();
}
