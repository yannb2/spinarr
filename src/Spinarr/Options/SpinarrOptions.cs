namespace Spinarr.Options;

/// <summary>
/// Settings from appsettings.json, or from environment variables in Docker:
/// <c>Spinarr__RipFolder=/downloads/rips/music</c>, <c>Spinarr__Devices__0=/dev/sr0</c>.
/// </summary>
public class SpinarrOptions
{
    public const string Section = "Spinarr";

    /// <summary>
    /// Spinarr's own files: drives.json (each drive's read offset and cache answer), the keys
    /// ASP.NET signs its forms with, and the cache.
    /// </summary>
    public string ConfigFolder { get; set; } = "/config";

    /// <summary>MusicBrainz and AccurateRip answers already fetched.</summary>
    public string CacheFolder { get; set; } = "/config/cache";

    /// <summary>
    /// Where rips go: each album in {Artist}/{Album (Year)}, ready for Lidarr, and the work folder
    /// of a rip in progress under .work. Lidarr must see this folder at the same path.
    /// </summary>
    public string RipFolder { get; set; } = "/downloads/rips/music";

    /// <summary>FLAC compression level, 0 to 8: the disc page starts with this one.</summary>
    public int FlacLevel { get; set; } = 8;

    /// <summary>How many times a track is read again when its test and copy CRCs differ.</summary>
    public int Retries { get; set; } = 5;

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

    public string WorkFolder => Path.Combine(RipFolder, ".work");

    public SpinarrOptions WithDefaults()
    {
        Devices = Devices.Where(d => !string.IsNullOrWhiteSpace(d)).Select(d => d.Trim()).Distinct().ToList();
        FlacLevel = Math.Clamp(FlacLevel, 0, 8);
        Retries = Math.Clamp(Retries, 1, 20);
        return this;
    }
}

/// <summary>The tools, by name on the PATH unless set.</summary>
public class ToolPaths
{
    public string CdParanoia { get; set; } = "cd-paranoia";
    public string Cdrdao { get; set; } = "cdrdao";
    public string Flac { get; set; } = "flac";
    public string Stat { get; set; } = "stat";
}

public class UpdateSettings
{
    /// <summary>Look for newer tool versions upstream (ftp.gnu.org). Nothing is ever installed.</summary>
    public bool Enabled { get; set; } = true;

    public int IntervalHours { get; set; } = 24;
}
