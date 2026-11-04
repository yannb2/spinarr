namespace Spinarr.Options;

/// <summary>
/// Settings from appsettings.json, or from environment variables in Docker:
/// <c>Spinarr__WhipperHome=/config</c>, <c>Spinarr__Devices__0=/dev/sr0</c>.
/// </summary>
public class SpinarrOptions
{
    public const string Section = "Spinarr";

    /// <summary>
    /// Whipper keeps its settings in $XDG_CONFIG_HOME/whipper/whipper.conf: the read offset and the
    /// cache result of each drive, found once on the Drives page. Spinarr runs whipper with
    /// XDG_CONFIG_HOME set here, so they live in /config and survive a new image.
    /// </summary>
    public string WhipperHome { get; set; } = "/config";

    /// <summary>Whipper's caches (MusicBrainz and AccurateRip answers): $XDG_CACHE_HOME.</summary>
    public string CacheFolder { get; set; } = "/config/cache";

    /// <summary>
    /// The disc drives to use. Empty: every /dev/sr* the container can see (compose passes them
    /// with <c>devices:</c>).
    /// </summary>
    public List<string> Devices { get; set; } = [];

    public ToolPaths Tools { get; set; } = new();

    public UpdateSettings Updates { get; set; } = new();

    /// <summary>Where the kernel's device information is read. Only changed for tests and development.</summary>
    public string SysRoot { get; set; } = "/sys";

    /// <summary>Where the device nodes are. Only changed for tests and development.</summary>
    public string DevRoot { get; set; } = "/dev";

    public string WhipperConfigPath => Path.Combine(WhipperHome, "whipper", "whipper.conf");

    /// <summary>What whipper needs in its environment, every time it runs.</summary>
    public IReadOnlyDictionary<string, string> WhipperEnvironment => new Dictionary<string, string>
    {
        ["XDG_CONFIG_HOME"] = Path.GetFullPath(WhipperHome),
        ["XDG_CACHE_HOME"] = Path.GetFullPath(CacheFolder),
        // Lines as they happen, not in blocks when the pipe buffer fills.
        ["PYTHONUNBUFFERED"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
        ["LC_ALL"] = "C.UTF-8",
        ["LANG"] = "C.UTF-8"
    };

    public SpinarrOptions WithDefaults()
    {
        Devices = Devices.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct().ToList();
        return this;
    }
}

/// <summary>The tools, by name on the PATH unless set.</summary>
public class ToolPaths
{
    public string Whipper { get; set; } = "whipper";
    public string CdParanoia { get; set; } = "cd-paranoia";
    public string Cdrdao { get; set; } = "cdrdao";
    public string Flac { get; set; } = "flac";
    public string Stat { get; set; } = "stat";
}

public class UpdateSettings
{
    /// <summary>Look for newer tool versions upstream (GitHub, ftp.gnu.org). Nothing is ever installed.</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalHours { get; set; } = 24;
}
