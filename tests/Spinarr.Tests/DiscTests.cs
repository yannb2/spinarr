using Spinarr.Disc;

namespace Spinarr.Tests;

/// <summary>The TOC parser and disc IDs, on whipper's real .toc files and the values its tests expect.</summary>
[TestClass]
public class DiscTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "whipper", name);

    private static Toc Read(string name) => CdrdaoToc.Read(Fixture(name));

    [TestMethod]
    public void ReadsTrackStartsAndPregaps()
    {
        var toc = Read("cure.toc");

        Assert.HasCount(13, toc.Tracks);
        Assert.AreEqual(0, toc.TrackStart(1));
        Assert.AreEqual(28245, toc.Track(2).Index0);
        Assert.AreEqual(28324, toc.TrackStart(2));
        Assert.AreEqual(79, toc.Track(2).Pregap);
        Assert.AreEqual(46110, toc.TrackStart(3));
        Assert.AreEqual(66767, toc.TrackStart(4));
        Assert.AreEqual("0602517642256", toc.Catalog);
        Assert.AreEqual("USUM70839873", toc.Track(1).Isrc);
        // A pre-gap belongs to the track before it: track 1 ends right before track 2's index 01.
        Assert.AreEqual(28323, toc.TrackEnd(1));
    }

    [TestMethod]
    public void FindsHiddenAudioBeforeTrackOne()
    {
        var toc = Read("bloc.toc");

        Assert.AreEqual(0, toc.Track(1).Index0);
        Assert.AreEqual(15220, toc.TrackStart(1));
        Assert.AreEqual((0, 15219), toc.Htoa);
        Assert.AreEqual(84070, toc.Track(5).Index0);
        Assert.AreEqual(84142, toc.TrackStart(5));
        Assert.IsNull(Read("cure.toc").Htoa);
    }

    [TestMethod]
    public void ComputesTheFreedbAndAccurateRipIds()
    {
        var bloc = DiscIds.Of(Read("bloc.toc"));
        Assert.AreEqual("ad0be00d", bloc.Freedb);
        Assert.AreEqual("e/d/2/dBAR-013-001af2de-0105994e-ad0be00d.bin", bloc.AccurateRipPath);

        Assert.AreEqual("3/c/4/dBAR-013-0019d4c3-00fe8924-b90c650d.bin", DiscIds.Of(Read("cure.toc")).AccurateRipPath);
        Assert.AreEqual("810b7b0b", DiscIds.Freedb(Read("totbl.fast.toc")));
    }

    [TestMethod]
    public void LeavesALastDataTrackOutOfTheMusicBrainzId()
    {
        var toc = Read("ladyhawke.toc");

        Assert.IsFalse(toc.Tracks[^1].Audio);
        Assert.HasCount(12, toc.AudioTracks);
        Assert.AreEqual("c60af50d", DiscIds.Freedb(toc));
        Assert.AreEqual("KnpGsLhvH.lPrNc1PBL21lb9Bg4-", DiscIds.MusicBrainz(toc));
        Assert.AreEqual(
            "https://musicbrainz.org/cdtoc/attach?toc=1+12+195856+150+15687+31841+51016+66616+81352+99559+116070+133243+149997+161710+177832&tracks=12&id=KnpGsLhvH.lPrNc1PBL21lb9Bg4-",
            DiscIds.MusicBrainzSubmitUrl(toc));
    }

    [TestMethod]
    public void ReadsCdTextOfTheDiscAndOfEachTrack()
    {
        var breeders = Read("breeders.toc");
        Assert.AreEqual("THE BREEDERS", breeders.CdText["PERFORMER"]);
        Assert.AreEqual("MOUNTAIN BATTLES", breeders.CdText["TITLE"]);
        Assert.AreEqual("OVERGLAZED", breeders.Track(1).CdText["TITLE"]);
        // PERFORMER "" is empty: not kept.
        Assert.IsFalse(breeders.Track(1).CdText.ContainsKey("PERFORMER"));

        var diorama = Read("diorama_utf8.toc");
        Assert.HasCount(8, diorama.Tracks);
        Assert.AreEqual("MØL", diorama.CdText["PERFORMER"]);
        Assert.AreEqual("Fraktur", diorama.Track(1).CdText["TITLE"]);

        // An older cdrdao wrote Latin-1 letters as octal escapes.
        Assert.AreEqual("José González", Read("jose.toc").Track(1).CdText["PERFORMER"]);
    }

    [TestMethod]
    public void ReadsASingleTrackWithASilentPregap()
    {
        var toc = Read("strokes-someday.toc");

        Assert.HasCount(1, toc.Tracks);
        Assert.AreEqual(0, toc.Track(1).Index0);
        Assert.AreEqual(1, toc.TrackStart(1));
    }

    [TestMethod]
    public void WritesTimesTheWayEachToolReadsThem()
    {
        Assert.AreEqual(28324, Cd.MsfToFrames("06:17:49"));
        Assert.AreEqual("06:17:49", Cd.Msf(28324));
        Assert.AreEqual("00:06:17.49", Cd.Hmsf(28324));
        Assert.AreEqual("6:17.49", Cd.EacTime(28324));
        Assert.AreEqual("0:00:01.04", Cd.EacGap(79));
    }
}
