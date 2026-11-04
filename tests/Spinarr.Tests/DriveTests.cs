using Microsoft.Extensions.Logging.Abstractions;
using Spinarr.Drives;
using Spinarr.Options;
using Spinarr.Tools;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Spinarr.Tests;

[TestClass]
public class DriveTests
{
    [TestMethod]
    public void ReadsStatsDeviceNumbersInHex()
    {
        Assert.AreEqual((11, 0), DriveScanner.ParseStat("b 0\n"));
        Assert.AreEqual((11, 17), DriveScanner.ParseStat("b 11"));
        Assert.IsNull(DriveScanner.ParseStat("stat: cannot stat"));
    }

    [TestMethod]
    public void NamesADriveFromItsOwnDeviceNumbers()
    {
        using var root = new TempFolder();
        // The host's sr0 is another drive: the container's /dev/sr0 is the host's 11:1.
        root.Write("sys/dev/block/11:0/device/vendor", "PIONEER \n");
        root.Write("sys/dev/block/11:1/device/vendor", "HL-DT-ST\n");
        root.Write("sys/dev/block/11:1/device/model", "DVDRAM GH40L    \n");
        root.Write("sys/dev/block/11:1/device/rev", "1.04\n");
        root.Write("dev/sr0", "");
        Directory.CreateDirectory(root.Path("dev/disk/by-id"));
        File.CreateSymbolicLink(root.Path("dev/disk/by-id/usb-HL-DT-ST_DVDRAM_GH40L_K1234-0:0"), "../../sr0");
        File.CreateSymbolicLink(root.Path("dev/disk/by-id/usb-Other-0:0"), "../../sr9");
        var scanner = Scanner(root);

        var drive = scanner.Describe(root.Path("dev/sr0"), 11, 1);

        Assert.AreEqual("HL-DT-ST", drive.Vendor);
        Assert.AreEqual("DVDRAM GH40L", drive.Model);
        Assert.AreEqual("1.04", drive.Release);
        Assert.AreEqual("HL-DT-ST DVDRAM GH40L", drive.DisplayName);
        Assert.IsTrue(drive.Identified);
        CollectionAssert.AreEqual(new[] { "usb-HL-DT-ST_DVDRAM_GH40L_K1234-0:0" }, drive.Links.ToArray());
    }

    [TestMethod]
    public void AnUnknownDriveIsNamedByItsDevice()
    {
        using var root = new TempFolder();

        var drive = Scanner(root).Describe("/dev/sr3", 11, 3);

        Assert.AreEqual("/dev/sr3", drive.DisplayName);
        Assert.IsFalse(drive.Identified);
    }

    [TestMethod]
    public void ListsSrDevicesInNumberOrder()
    {
        using var root = new TempFolder();
        foreach (var name in new[] { "sr10", "sr1", "sr0", "sr", "srx", "sda" })
        {
            root.Write($"dev/{name}", "");
        }

        var devices = Scanner(root).Devices().Select(Path.GetFileName).ToArray();

        CollectionAssert.AreEqual(new[] { "sr0", "sr1", "sr10" }, devices);
    }

    [TestMethod]
    public void ConfiguredDevicesWin()
    {
        using var root = new TempFolder();
        root.Write("dev/sr0", "");
        var options = new SpinarrOptions { Devices = ["/dev/cdrom", " /dev/cdrom ", ""], DevRoot = root.Path("dev") }.WithDefaults();

        var devices = new DriveScanner(MsOptions.Create(options), NullLogger<DriveScanner>.Instance).Devices();

        CollectionAssert.AreEqual(new[] { "/dev/cdrom" }, devices.ToArray());
    }

    [TestMethod]
    public void MapsTheDriverAnswers()
    {
        Assert.AreEqual(DiscState.NoDisc, CdromDevice.FromCodes(1, -1));
        Assert.AreEqual(DiscState.TrayOpen, CdromDevice.FromCodes(2, -1));
        Assert.AreEqual(DiscState.NotReady, CdromDevice.FromCodes(3, -1));
        Assert.AreEqual(DiscState.AudioCd, CdromDevice.FromCodes(4, 100));
        Assert.AreEqual(DiscState.DataDisc, CdromDevice.FromCodes(4, 101));
        Assert.AreEqual(DiscState.MixedCd, CdromDevice.FromCodes(4, 105));
        Assert.AreEqual(DiscState.Unknown, CdromDevice.FromCodes(0, -1));
        Assert.AreEqual(DiscState.Unknown, CdromDevice.FromCodes(-1, -1));
    }

    [TestMethod]
    public void AMissingDeviceIsUnknownNotAnError()
    {
        Assert.AreEqual(DiscState.Unknown, CdromDevice.State("/dev/does-not-exist"));
        Assert.IsNotNull(CdromDevice.Eject("/dev/does-not-exist"));
    }

    [TestMethod]
    public void KeepsDriveSettingsAndTakesOverAnOldWhipperConf()
    {
        using var root = new TempFolder();
        root.Write("config/whipper/whipper.conf",
            "[drive:HL-DT-ST%3ADVDRAM%20GH40L%3A1.04]\nvendor = HL-DT-ST\nmodel = DVDRAM GH40L\nrelease = 1.04\nread_offset = 667\n");
        var options = MsOptions.Create(new SpinarrOptions { ConfigFolder = root.Path("config") });
        var drive = new OpticalDrive("/dev/sr0", "HL-DT-ST", "DVDRAM GH40L", "1.04", []);

        var store = new DriveSettingsStore(options, NullLogger<DriveSettingsStore>.Instance);
        Assert.AreEqual(667, store.Find(drive)?.ReadOffset);
        Assert.IsNull(store.Find(drive)?.DefeatsCache);

        store.Save(drive, null, true);
        var reloaded = new DriveSettingsStore(options, NullLogger<DriveSettingsStore>.Instance);
        Assert.AreEqual(667, reloaded.Find(drive)?.ReadOffset);
        Assert.IsTrue(reloaded.Find(drive)?.DefeatsCache);
        Assert.IsTrue(File.Exists(root.Path("config/drives.json")));
    }

    [TestMethod]
    public void TriesTheCommonOffsetsFirstAndEachOnce()
    {
        Assert.AreEqual(6, DriveTasks.KnownOffsets[0]);
        Assert.AreEqual(667, DriveTasks.KnownOffsets[1]);
        Assert.AreEqual(DriveTasks.KnownOffsets.Count, DriveTasks.KnownOffsets.Distinct().Count());
    }

    private static DriveScanner Scanner(TempFolder root) =>
        new(MsOptions.Create(new SpinarrOptions { SysRoot = root.Path("sys"), DevRoot = root.Path("dev") }), NullLogger<DriveScanner>.Instance);
}

internal sealed class TempFolder : IDisposable
{
    public string Root { get; } = Directory.CreateTempSubdirectory("spinarr-test-").FullName;

    public string Path(string relative) => System.IO.Path.Combine(Root, relative);

    public void Write(string relative, string text)
    {
        var path = Path(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose() => Directory.Delete(Root, true);
}
