using Spinarr.Drives;

namespace Spinarr.Tests;

[TestClass]
public class WhipperConfigTests
{
    private static readonly OpticalDrive Gh40L = new("/dev/sr0", "HL-DT-ST", "DVDRAM GH40L", "1.04", []);

    private const string Existing = """
                                    [main]
                                    path_filter_fat = True

                                    [whipper.cd.rip]
                                    track_template = %%A/%%d (%%y)/%%N-%%t - %%n
                                    flac_level = 8

                                    [drive:HL-DT-ST%3ADVDRAM%20GH40L%3A1.04]
                                    vendor = HL-DT-ST
                                    model = DVDRAM GH40L
                                    release = 1.04
                                    read_offset = 667

                                    """;

    [TestMethod]
    public void NamesSectionsLikePythonsQuote()
    {
        // urllib.parse.quote('HL-DT-ST:DVDRAM GH40L:1.04')
        Assert.AreEqual("drive:HL-DT-ST%3ADVDRAM%20GH40L%3A1.04", WhipperConfig.SectionName("HL-DT-ST", "DVDRAM GH40L", "1.04"));
        Assert.AreEqual("drive:a/b%3Ac%25d%3Ae", WhipperConfig.SectionName("a/b", "c%d", "e"));
        Assert.AreEqual("drive:%C3%A9%3Ax%3Ay", WhipperConfig.SectionName("é", "x", "y"));
    }

    [TestMethod]
    public void ReadsTheDriveSections()
    {
        var drives = WhipperConfig.Parse(Existing);

        Assert.HasCount(1, drives);
        var found = WhipperConfig.Find(drives, Gh40L);
        Assert.IsNotNull(found);
        Assert.AreEqual(667, found.ReadOffset);
        Assert.IsNull(found.DefeatsCache);
        Assert.IsNull(WhipperConfig.Find(drives, Gh40L with { Release = "1.05" }));
    }

    [TestMethod]
    public void ReadsNegativeOffsetsAndPythonBooleans()
    {
        var drives = WhipperConfig.Parse("[drive:x]\nvendor = PIONEER\nmodel = BD-RW BDR-XD07\nrelease = 1.00\nread_offset = -1164\ndefeats_cache = False\n");

        Assert.AreEqual(-1164, drives[0].ReadOffset);
        Assert.IsFalse(drives[0].DefeatsCache);
    }

    [TestMethod]
    public void UpdatesAnExistingSectionAndKeepsTheRest()
    {
        var text = WhipperConfig.SetDrive(Existing, Gh40L, 6, true);

        StringAssert.Contains(text, "read_offset = 6\n");
        StringAssert.Contains(text, "release = 1.04\nread_offset = 6\ndefeats_cache = True\n");
        StringAssert.Contains(text, "track_template = %%A/%%d (%%y)/%%N-%%t - %%n\nflac_level = 8\n");
        Assert.DoesNotContain("667", text);
        var drive = WhipperConfig.Find(WhipperConfig.Parse(text), Gh40L)!;
        Assert.AreEqual(6, drive.ReadOffset);
        Assert.IsTrue(drive.DefeatsCache);
    }

    [TestMethod]
    public void LeavesAValueAloneWhenNotGiven()
    {
        var text = WhipperConfig.SetDrive(Existing, Gh40L, null, false);

        var drive = WhipperConfig.Find(WhipperConfig.Parse(text), Gh40L)!;
        Assert.AreEqual(667, drive.ReadOffset);
        Assert.IsFalse(drive.DefeatsCache);
    }

    [TestMethod]
    public void AddsASectionForANewDrive()
    {
        var bluRay = new OpticalDrive("/dev/sr1", "HL-DT-ST", "BD-RE  WH16NS60", "1.02", []);

        var text = WhipperConfig.SetDrive(Existing, bluRay, 6, null);

        StringAssert.Contains(text, "\n[drive:HL-DT-ST%3ABD-RE%20%20WH16NS60%3A1.02]\nvendor = HL-DT-ST\nmodel = BD-RE  WH16NS60\nrelease = 1.02\nread_offset = 6\n");
        Assert.AreEqual(667, WhipperConfig.Find(WhipperConfig.Parse(text), Gh40L)!.ReadOffset);
        Assert.AreEqual(6, WhipperConfig.Find(WhipperConfig.Parse(text), bluRay)!.ReadOffset);
    }

    [TestMethod]
    public void StartsAnEmptyFile()
    {
        var text = WhipperConfig.SetDrive("", Gh40L, 667, null);

        Assert.AreEqual("[drive:HL-DT-ST%3ADVDRAM%20GH40L%3A1.04]\nvendor = HL-DT-ST\nmodel = DVDRAM GH40L\nrelease = 1.04\nread_offset = 667\n\n", text);
    }

    [TestMethod]
    public void InsertsANewKeyAfterTheLastValueNotAfterBlankLines()
    {
        var text = WhipperConfig.SetDrive(Existing + "\n[main2]\nx = 1\n", Gh40L, null, true);

        StringAssert.Contains(text, "read_offset = 667\ndefeats_cache = True\n\n[main2]");
    }
}
