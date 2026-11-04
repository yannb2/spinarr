using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Spinarr.Options;

namespace Spinarr.Tools;

/// <summary>Where a tool's newest version is read.</summary>
public enum UpstreamKind
{
    /// <summary>Comes with the base image's packages: it moves with the image, not with a pin.</summary>
    None,

    /// <summary>A web page; the highest version the regex finds.</summary>
    Page,

    /// <summary>The newest commit on a branch of a GitHub repository (owner/name@branch): the whipper fork.</summary>
    GitHubCommit
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

    /// <summary>The page URL, or owner/name@branch for a commit.</summary>
    public string? UpstreamSource { get; init; }

    public Regex? UpstreamPattern { get; init; }
    public string? ReleaseNotes { get; init; }

    /// <summary>The version the Dockerfile installed (SPINARR_PIN_WHIPPER...).</summary>
    public string? Pinned => Environment.GetEnvironmentVariable(PinVariable);

    public string PinVariable => $"SPINARR_PIN_{Id.ToUpperInvariant()}";
}

public static partial class ToolCatalog
{
    public const string WhipperRepository = "yannb2/whipper@develop";

    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new()
        {
            Id = "whipper", Name = "whipper (fork)",
            Purpose = "Rips the CD: secure reads, AccurateRip check, FLAC level, offsets, the rip log.",
            Command = t => t.Whipper, VersionArguments = ["-v"],
            VersionPattern = WhipperVersion(),
            Upstream = UpstreamKind.GitHubCommit,
            UpstreamSource = WhipperRepository,
            ReleaseNotes = "https://github.com/yannb2/whipper/commits/develop"
        },
        new()
        {
            Id = "cd_paranoia", Name = "cd-paranoia",
            Purpose = "Reads the audio from the disc for whipper, with error correction.",
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
            Purpose = "Talks to the drive: table of contents, drive name and firmware.",
            VersionFile = "/usr/local/lib/pkgconfig/libcdio.pc",
            VersionPattern = PkgConfigVersion(),
            Upstream = UpstreamKind.Page,
            UpstreamSource = "https://ftp.gnu.org/gnu/libcdio/",
            UpstreamPattern = LibcdioUpstream(),
            ReleaseNotes = "https://github.com/libcdio/libcdio/blob/master/NEWS.md"
        },
        new()
        {
            Id = "cdrdao", Name = "cdrdao",
            Purpose = "Reads the disc's table of contents, pre-gaps and hidden track for whipper.",
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

    /// <summary>The commit id in GitHub's answer for a branch (repos/o/n/commits/branch), shortened to 7.</summary>
    public static string? CommitSha(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("sha", out var sha)
                   && sha.GetString() is { Length: >= 7 } value
                ? value[..7]
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The API address for the newest commit of "owner/name@branch".</summary>
    public static string CommitUrl(string source)
    {
        var (repository, branch) = source.Split('@') is [var r, var b] ? (r, b) : (source, "main");
        return $"https://api.github.com/repos/{repository}/commits/{branch}";
    }

    /// <summary>
    /// True when upstream has something newer than what the image holds. A commit is newer when it
    /// is a different commit: the pin is the full id, upstream the first 7 characters.
    /// </summary>
    public static bool IsNewer(ToolDefinition tool, string? latest, string? current)
    {
        if (latest == null || current == null)
        {
            return false;
        }

        return tool.Upstream == UpstreamKind.GitHubCommit
            ? !current.StartsWith(latest, StringComparison.OrdinalIgnoreCase)
            : VersionComparer.Instance.Compare(latest, current) > 0;
    }

    // "whipper 0.10.1.dev12+g71251a0", or whatever version the image build gave it.
    [GeneratedRegex(@"whipper (\S+)")]
    private static partial Regex WhipperVersion();

    // "cd-paranoia 10.2+2.0.1 libcdio 2.1.0 ..." or "This is cd-paranoia version 10.2+2.0.1".
    [GeneratedRegex(@"(\d+\.\d+\+\d+\.\d+\.\d+)")]
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
