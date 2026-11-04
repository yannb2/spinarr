using Spinarr.Tools;

namespace Spinarr.Tests;

[TestClass]
public class ToolCatalogTests
{
    private static ToolDefinition Tool(string id) => ToolCatalog.All.Single(t => t.Id == id);

    [TestMethod]
    public void ReadsInstalledVersions()
    {
        // What the image's cd-paranoia printed in CI.
        Assert.AreEqual("10.2", ToolCatalog.InstalledVersion(Tool("cd_paranoia"),
            "cdparanoia III release 10.2 libcdio 2.1.0 x86_64-pc-linux-gnu\n(C) 2001 Monty <monty@xiph.org> and Xiphophorus"));
        Assert.AreEqual("10.2+2.0.1", ToolCatalog.InstalledVersion(Tool("cd_paranoia"), "cd-paranoia 10.2+2.0.1 (libcdio 2.1.0)"));
        Assert.AreEqual("2.1.0", ToolCatalog.InstalledVersion(Tool("libcdio"), "prefix=/usr/local\nName: libcdio\nVersion: 2.1.0\nLibs: -lcdio"));
        Assert.AreEqual("1.2.5", ToolCatalog.InstalledVersion(Tool("cdrdao"), "Cdrdao version 1.2.5 - (C) Andreas Mueller <andreas@daneb.de>"));
        Assert.AreEqual("1.4.3", ToolCatalog.InstalledVersion(Tool("flac"), "flac 1.4.3"));
    }

    [TestMethod]
    public void FindsTheNewestOnTheGnuListing()
    {
        const string page = "<a href=\"libcdio-2.1.0.tar.bz2\">libcdio-2.1.0.tar.bz2</a> <a href=\"libcdio-2.2.0.tar.bz2.sig\">"
                            + "libcdio-2.2.0.tar.bz2.sig</a> <a>libcdio-paranoia-10.2+2.0.1.tar.bz2</a> <a>libcdio-paranoia-10.2+2.0.2.tar.bz2</a>";

        Assert.AreEqual("2.2.0", ToolCatalog.HighestOnPage(Tool("libcdio").UpstreamPattern!, page));
        Assert.AreEqual("10.2+2.0.2", ToolCatalog.HighestOnPage(Tool("cd_paranoia").UpstreamPattern!, page));
    }

    [TestMethod]
    public void WhipperIsNoLongerInTheImage()
    {
        Assert.IsFalse(ToolCatalog.All.Any(t => t.Id == "whipper"));
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        Assert.DoesNotContain("WHIPPER_COMMIT", dockerfile);
        Assert.DoesNotContain("pip3 install", dockerfile);
    }

    [TestMethod]
    public void VersionsCompareAsNumbers()
    {
        var paranoia = Tool("cd_paranoia");
        Assert.IsTrue(ToolCatalog.IsNewer(paranoia, "10.2+2.0.10", "10.2+2.0.2"));
        Assert.IsFalse(ToolCatalog.IsNewer(paranoia, "10.2+2.0.1", "10.2+2.0.1"));
        Assert.IsTrue(ToolCatalog.IsNewer(Tool("libcdio"), "2.1.10", "2.1.9"));
    }

    [TestMethod]
    public void EveryPinnedToolHasAnArgInTheDockerfile()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        foreach (var tool in ToolCatalog.All.Where(t => t.Upstream != UpstreamKind.None))
        {
            StringAssert.Contains(dockerfile, $"{tool.PinVariable}=", tool.Name);
        }
    }

    internal static string RepoRoot()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder != null && !File.Exists(Path.Combine(folder.FullName, "Spinarr.slnx")))
        {
            folder = folder.Parent;
        }

        return folder?.FullName ?? throw new InvalidOperationException("Spinarr.slnx not found above the test folder");
    }
}
