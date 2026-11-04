using System.Globalization;
using Microsoft.Extensions.Options;
using Spinarr.Options;
using Spinarr.Tools;

namespace Spinarr.Drives;

/// <summary>A disc drive the container can use.</summary>
/// <param name="Device">The device node inside the container (/dev/sr0).</param>
/// <param name="Vendor">As the drive names itself (HL-DT-ST), the same words whipper reads.</param>
/// <param name="Release">The firmware revision (1.04).</param>
/// <param name="Links">Its stable names in /dev/disk/by-id, when the container has them.</param>
public sealed record OpticalDrive(string Device, string Vendor, string Model, string Release, IReadOnlyList<string> Links)
{
    public string DisplayName => string.Join(' ', new[] { Vendor, Model }.Where(p => p.Length > 0)) is { Length: > 0 } name
        ? name
        : Device;

    /// <summary>True when the kernel told us who made it; whipper needs that to save settings.</summary>
    public bool Identified => Vendor.Length > 0 || Model.Length > 0;
}

/// <summary>
/// Finds the drives: every /dev/sr* (or the configured devices), named from the kernel's
/// information about that device node. The node's own major:minor numbers are used, not its name:
/// compose can pass the host's sr1 in as the container's sr0, and /sys is the host's.
/// </summary>
public sealed class DriveScanner(IOptions<SpinarrOptions> options, ILogger<DriveScanner> logger)
{
    private readonly SpinarrOptions _options = options.Value;

    public async Task<IReadOnlyList<OpticalDrive>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var drives = new List<OpticalDrive>();
        foreach (var device in Devices())
        {
            var numbers = await DeviceNumbersAsync(device, cancellationToken);
            drives.Add(numbers == null
                ? new OpticalDrive(device, "", "", "", Links(device))
                : Describe(device, numbers.Value.Major, numbers.Value.Minor));
        }

        return drives;
    }

    /// <summary>The configured devices, or every sr* node in /dev, in order (sr0, sr1, sr10).</summary>
    internal IReadOnlyList<string> Devices()
    {
        if (_options.Devices.Count > 0)
        {
            return _options.Devices;
        }

        if (!Directory.Exists(_options.DevRoot))
        {
            return [];
        }

        return Directory.GetFiles(_options.DevRoot, "sr*")
            .Where(p => Path.GetFileName(p)[2..].All(char.IsDigit) && Path.GetFileName(p).Length > 2)
            .OrderBy(p => int.Parse(Path.GetFileName(p)[2..], CultureInfo.InvariantCulture))
            .ToList();
    }

    /// <summary>Vendor, model and firmware from /sys/dev/block/MAJOR:MINOR/device.</summary>
    internal OpticalDrive Describe(string device, int major, int minor)
    {
        var folder = Path.Combine(_options.SysRoot, "dev", "block", $"{major}:{minor}", "device");
        return new OpticalDrive(device, Read(folder, "vendor"), Read(folder, "model"), Read(folder, "rev"), Links(device));
    }

    private static string Read(string folder, string name)
    {
        try
        {
            var path = Path.Combine(folder, name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() : "";
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }

    /// <summary>The /dev/disk/by-id names that point at this device.</summary>
    internal IReadOnlyList<string> Links(string device)
    {
        var folder = Path.Combine(_options.DevRoot, "disk", "by-id");
        if (!Directory.Exists(folder))
        {
            return [];
        }

        var target = Path.GetFullPath(device);
        var links = new List<string>();
        foreach (var link in Directory.GetFiles(folder))
        {
            try
            {
                var resolved = File.ResolveLinkTarget(link, returnFinalTarget: true);
                if (resolved != null && Path.GetFullPath(resolved.FullName) == target)
                {
                    links.Add(Path.GetFileName(link));
                }
            }
            catch (IOException)
            {
                // A broken link: skip it.
            }
        }

        return links.Order(StringComparer.Ordinal).ToList();
    }

    private async Task<(int Major, int Minor)?> DeviceNumbersAsync(string device, CancellationToken cancellationToken)
    {
        // %t and %T: the major and minor device numbers, in hex. -L follows a /dev/cdrom link.
        var result = await ProcessRunner.RunAsync(_options.Tools.Stat, ["-L", "-c", "%t %T", device],
            TimeSpan.FromSeconds(10), cancellationToken);
        var numbers = result.Success ? ParseStat(result.Output) : null;
        if (numbers == null)
        {
            logger.LogWarning("Could not read the device numbers of {Device}: {Reason}", device, result.LastLine);
        }

        return numbers;
    }

    /// <summary>"b 0" from stat -c '%t %T' is 11:0.</summary>
    internal static (int Major, int Minor)? ParseStat(string output)
    {
        var parts = output.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
               && int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var major)
               && int.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var minor)
            ? (major, minor)
            : null;
    }
}
