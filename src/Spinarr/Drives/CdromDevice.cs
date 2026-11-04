using System.Runtime.InteropServices;

namespace Spinarr.Drives;

public enum DiscState
{
    Unknown,
    NoDisc,
    TrayOpen,
    NotReady,
    AudioCd,
    DataDisc,

    /// <summary>Audio tracks plus a data track (CD-Extra, Enhanced CD).</summary>
    MixedCd
}

/// <summary>
/// Asks the Linux CD-ROM driver about the tray and the disc, and opens the tray: the same calls the
/// eject command makes, without needing it in the image. Opening the device with O_NONBLOCK works
/// with no disc in, and does not spin the drive up.
/// </summary>
public static partial class CdromDevice
{
    private const int ReadOnlyNonBlocking = 0x800; // O_RDONLY | O_NONBLOCK
    private const nuint DriveStatusRequest = 0x5326; // CDROM_DRIVE_STATUS
    private const nuint DiscStatusRequest = 0x5327; // CDROM_DISC_STATUS
    private const nuint EjectRequest = 0x5309; // CDROMEJECT
    private const nuint CloseTrayRequest = 0x5319; // CDROMCLOSETRAY
    private const nuint LockDoorRequest = 0x5329; // CDROM_LOCKDOOR
    private const nint CurrentSlot = int.MaxValue; // CDSL_CURRENT

    public static DiscState State(string device)
    {
        if (!OperatingSystem.IsLinux())
        {
            return DiscState.Unknown;
        }

        var fd = Open(device, ReadOnlyNonBlocking);
        if (fd < 0)
        {
            return DiscState.Unknown;
        }

        try
        {
            var drive = Ioctl(fd, DriveStatusRequest, CurrentSlot);
            var disc = drive == 4 ? Ioctl(fd, DiscStatusRequest, 0) : -1;
            return FromCodes(drive, disc);
        }
        finally
        {
            Close(fd);
        }
    }

    /// <summary>Opens the tray. Returns an error message, or null when it worked.</summary>
    public static string? Eject(string device) => Run(device, fd =>
    {
        // A drive left locked by a crashed read would refuse to open.
        Ioctl(fd, LockDoorRequest, 0);
        return Ioctl(fd, EjectRequest, 0);
    });

    /// <summary>Closes the tray (slot-loading and laptop drives can't). Returns an error message, or null.</summary>
    public static string? CloseTray(string device) => Run(device, fd => Ioctl(fd, CloseTrayRequest, 0));

    /// <summary>The driver's answers: CDS_NO_DISC 1, CDS_TRAY_OPEN 2, CDS_DRIVE_NOT_READY 3, CDS_DISC_OK 4, then CDS_AUDIO 100, CDS_MIXED 105.</summary>
    internal static DiscState FromCodes(int drive, int disc) => drive switch
    {
        1 => DiscState.NoDisc,
        2 => DiscState.TrayOpen,
        3 => DiscState.NotReady,
        4 => disc switch
        {
            100 => DiscState.AudioCd,
            105 => DiscState.MixedCd,
            >= 101 and <= 104 => DiscState.DataDisc,
            _ => DiscState.Unknown
        },
        _ => DiscState.Unknown
    };

    private static string? Run(string device, Func<int, int> call)
    {
        if (!OperatingSystem.IsLinux())
        {
            return "Only works on Linux.";
        }

        var fd = Open(device, ReadOnlyNonBlocking);
        if (fd < 0)
        {
            return $"Could not open {device} (error {Marshal.GetLastPInvokeError()}).";
        }

        try
        {
            return call(fd) < 0 ? $"The drive refused (error {Marshal.GetLastPInvokeError()})." : null;
        }
        finally
        {
            Close(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static partial int Ioctl(int fd, nuint request, nint argument);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
