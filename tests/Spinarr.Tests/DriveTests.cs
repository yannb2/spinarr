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
    public void RunsWhipperWithTheDevice()
    {
        CollectionAssert.AreEqual(new[] { "offset", "find", "-d", "/dev/sr0" }, DriveTasks.Arguments(DriveTaskKind.FindOffset, "/dev/sr0").ToArray());
        CollectionAssert.AreEqual(new[] { "drive", "analyze", "-d", "/dev/sr1" }, DriveTasks.Arguments(DriveTaskKind.AnalyzeCache, "/dev/sr1").ToArray());
    }

    [TestMethod]
    public void WhipperRunsWithItsSettingsInConfig()
    {
        var environment = new SpinarrOptions { WhipperHome = "/config", CacheFolder = "/config/cache" }.WhipperEnvironment;

        Assert.AreEqual("/config", environment["XDG_CONFIG_HOME"]);
        Assert.AreEqual("/config/cache", environment["XDG_CACHE_HOME"]);
        Assert.AreEqual("1", environment["PYTHONUNBUFFERED"]);
        Assert.AreEqual("/config/whipper/whipper.conf", new SpinarrOptions { WhipperHome = "/config" }.WhipperConfigPath);
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
