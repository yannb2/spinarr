using Spinarr.AccurateRip;
using Spinarr.Audio;
using Spinarr.Disc;
using Spinarr.Drives;
using Spinarr.MusicBrainz;
using Spinarr.Rip;

namespace Spinarr.Tests;

/// <summary>MusicBrainz answers, names, tags, and the log and cue sheet a rip leaves.</summary>
[TestClass]
public class RipTests
{
    private static readonly OpticalDrive Drive = new("/dev/sr0", "HL-DT-ST", "DVDRAM GH40L", "1.04", []);

    private static MbRelease Ladyhawke() =>
        MusicBrainzClient.ParseReleases(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "musicbrainz", "discid.json"))).Single();

    [TestMethod]
    public void ReadsAMusicBrainzDiscIdAnswer()
    {
        var release = Ladyhawke();

        Assert.AreEqual("Ladyhawke", release.Title);
        Assert.AreEqual("Ladyhawke", release.Artist);
        Assert.AreEqual("2008", release.Year);
        Assert.AreEqual("Modular Recordings", release.Labels[0].Name);
        Assert.AreEqual("1773809", release.Labels[0].CatalogNumber);
        Assert.AreEqual("Album", release.PrimaryType);
        var medium = release.MediumFor("KnpGsLhvH.lPrNc1PBL21lb9Bg4-", 12);
        Assert.IsNotNull(medium);
        Assert.AreEqual("Magic", medium.Tracks[0].Title);
        Assert.AreEqual("GBUM70810800", medium.Tracks[0].Isrcs[0]);
        Assert.AreEqual("b1b1b1b1-0000-0000-0000-000000000001", medium.Tracks[0].RecordingId);
        // A track credit is joined the way MusicBrainz shows it, and its length falls back to the recording's.
        Assert.AreEqual("Ladyhawke feat. Guest", medium.Tracks[1].Artist);
        Assert.HasCount(2, medium.Tracks[1].ArtistIds);
        Assert.AreEqual(215000, medium.Tracks[1].LengthMs);
    }

    [TestMethod]
    public void NamesFoldersAndFilesForLidarr()
    {
        var release = Ladyhawke();
        var disc = new DiscIdentity("KnpGsLhvH.lPrNc1PBL21lb9Bg4-", "a50b6c0c", "0", "0", 12);

        Assert.AreEqual(Path.Combine("Ladyhawke", "Ladyhawke (2008)"), RipNaming.AlbumFolder(release, disc));
        Assert.AreEqual(Path.Combine("Unknown Artist", "Unknown disc a50b6c0c"), RipNaming.AlbumFolder(null, disc));
        Assert.AreEqual("01 - Magic.flac", RipNaming.TrackFile(release, release.Media[0], 1, "Magic"));
        Assert.AreEqual("03 - What - Why.flac", RipNaming.TrackFile(null, null, 3, "What: Why?"));
        Assert.AreEqual("AC-DC", RipNaming.Safe("AC/DC"));
        Assert.AreEqual("Ladyhawke (2008)", RipNaming.DiscFileBase(release, release.Media[0], disc));
    }

    [TestMethod]
    public void TagsCarryTheMusicBrainzIds()
    {
        var release = Ladyhawke();
        var toc = CdrdaoToc.Read(DiscTests.Fixture("ladyhawke.toc"));
        var request = new RipRequest(Drive, DiscIds.Of(toc), release, release.Media[0], 8, 5);
        var track = new RipTrack
        {
            Number = 1, AudioIndex = 1, Title = "Magic", Artist = "Ladyhawke", FirstFrame = 0, LastFrame = 100,
            FileName = "01 - Magic.flac"
        };

        var tags = RipNaming.Tags(request, track, toc, "0.2.0").ToLookup(t => t.Key, t => t.Value);

        Assert.AreEqual("Magic", tags["TITLE"].Single());
        Assert.AreEqual("0b9d4b47-2d1f-4dcb-8b58-cb4c3f0f1d0a", tags["MUSICBRAINZ_ALBUMID"].Single());
        Assert.AreEqual("b1b1b1b1-0000-0000-0000-000000000001", tags["MUSICBRAINZ_TRACKID"].Single());
        Assert.AreEqual("a1a1a1a1-0000-0000-0000-000000000001", tags["MUSICBRAINZ_RELEASETRACKID"].Single());
        Assert.AreEqual("GBUM70810800", tags["ISRC"].Single());
        Assert.AreEqual("Spinarr 0.2.0", tags["RIPPER"].Single());
        Assert.AreEqual("album", tags["RELEASETYPE"].Single());
    }

    [TestMethod]
    public void WritesAnEacStyleLogAndACueSheet()
    {
        var toc = CdrdaoToc.Read(DiscTests.Fixture("cure.toc"));
        var disc = DiscIds.Of(toc);
        var job = new RipJob { Id = "x", Request = new RipRequest(Drive, disc, null, null, 8, 5), WorkFolder = "/tmp/x" };
        job.Toc = toc;
        job.ReadOffset = 6;
        job.DefeatsCache = true;
        job.AccurateRipResponses = [];
        foreach (var t in toc.AudioTracks)
        {
            var digest = new TrackDigest(0x1234ABCD, 1, 2, 16384, 588);
            job.Tracks.Add(new RipTrack
            {
                Number = t.Number, AudioIndex = t.Number, Title = $"Track {t.Number:00}", Artist = "Unknown Artist",
                FirstFrame = toc.TrackStart(t.Number), LastFrame = toc.TrackEnd(t.Number), Pregap = t.Pregap, Isrc = t.Isrc,
                FileName = $"{t.Number:00} - Track {t.Number:00}.flac", State = TrackState.Done, Test = digest, Copy = digest,
                Quality = 1, Speed = 8.2, AccurateRip = new AccurateRipMatch(1, 2, null, null, 0, null)
            });
        }

        var log = RipFiles.Log(job, "0.2.0", "10.2+2.0.2", new DateTime(2026, 11, 4, 21, 5, 0));

        StringAssert.StartsWith(log, "Spinarr V0.2.0 from 4. November 2026\r\n");
        StringAssert.Contains(log, "Read offset correction                      : 6\r\n");
        StringAssert.Contains(log, "Gap handling                                : Appended to previous track");
        StringAssert.Contains(log, "     Pre-gap length  0:00:01.04");
        StringAssert.Contains(log, "     Test CRC 1234ABCD\r\n     Copy CRC 1234ABCD\r\n     Track not present in AccurateRip database\r\n     Copy OK");
        StringAssert.Contains(log, "None of the tracks are present in the AccurateRip database");
        StringAssert.Contains(log, "No errors occurred");
        Assert.DoesNotContain("Log checksum", log, "No EAC checksum: the log is Spinarr's, not EAC's.");
        StringAssert.EndsWith(log, "End of status report\r\n");

        var cue = RipFiles.CueSheet(job, "0.2.0");

        StringAssert.Contains(cue, "CATALOG 0602517642256\r\n");
        StringAssert.Contains(cue, "FILE \"01 - Track 01.flac\" WAVE\r\n  TRACK 01 AUDIO");
        // Track 2's pre-gap is at the end of track 1's file: INDEX 00 there, then its own file.
        StringAssert.Contains(cue, "  TRACK 02 AUDIO\r\n    TITLE \"Track 02\"\r\n    PERFORMER \"Unknown Artist\"\r\n    ISRC ");
        StringAssert.Contains(cue, "    INDEX 00 06:16:45\r\nFILE \"02 - Track 02.flac\" WAVE\r\n    INDEX 01 00:00:00");
    }
}
