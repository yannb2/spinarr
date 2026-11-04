using System.Text.Json;
using Microsoft.Extensions.Options;
using Spinarr.Options;

namespace Spinarr.Drives;

/// <summary>
/// Each drive's read offset and cache answer, in /config/drives.json, by vendor, model and
/// firmware (a firmware update can change a drive's behaviour). The first time, the drive
/// sections of an old whipper.conf in /config are taken over, so nothing found before is lost.
/// </summary>
public sealed class DriveSettingsStore(IOptions<SpinarrOptions> options, ILogger<DriveSettingsStore> logger)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly SpinarrOptions _options = options.Value;
    private readonly Lock _lock = new();
    private List<DriveSettings>? _drives;

    public string Path => System.IO.Path.Combine(_options.ConfigFolder, "drives.json");

    public IReadOnlyList<DriveSettings> All()
    {
        lock (_lock)
        {
            return (_drives ??= Load()).ToList();
        }
    }

    public DriveSettings? Find(OpticalDrive drive) => WhipperConfig.Find(All(), drive);

    /// <summary>Sets the read offset and/or the cache answer (null leaves it as it was).</summary>
    public void Save(OpticalDrive drive, int? readOffset, bool? defeatsCache)
    {
        lock (_lock)
        {
            var drives = _drives ??= Load();
            var old = WhipperConfig.Find(drives, drive);
            var updated = new DriveSettings(drive.Vendor.Trim(), drive.Model.Trim(), drive.Release.Trim(),
                readOffset ?? old?.ReadOffset, defeatsCache ?? old?.DefeatsCache);
            if (old != null)
            {
                drives.Remove(old);
            }

            drives.Add(updated);
            Directory.CreateDirectory(_options.ConfigFolder);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(drives, Json));
            File.Move(temporary, Path, overwrite: true);
        }
    }

    private List<DriveSettings> Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize<List<DriveSettings>>(File.ReadAllText(Path)) ?? [];
            }

            var whipper = System.IO.Path.Combine(_options.ConfigFolder, "whipper", "whipper.conf");
            var imported = WhipperConfig.Read(whipper).ToList();
            if (imported.Count > 0)
            {
                logger.LogInformation("Took over {Count} drive(s) from {Path}", imported.Count, whipper);
            }

            return imported;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Could not read the drive settings in {Path}: {Message}", Path, e.Message);
            return [];
        }
    }
}
