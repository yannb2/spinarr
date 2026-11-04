using System.Buffers.Binary;
using Spinarr.AccurateRip;
using Spinarr.Audio;
using Spinarr.Disc;
using Spinarr.Rip;

namespace Spinarr.Tests;

/// <summary>Checksums, AccurateRip answers, cd-paranoia's output and the offset shift.</summary>
[TestClass]
public class EngineTests
{
    private const int Samples = 588 * 20;

    private static uint[] Words() => Enumerable.Range(0, Samples).Select(i => unchecked((uint)i * 2654435761u)).ToArray();

    private static byte[] Bytes(uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        return bytes;
    }

    // Expected values from a Python port of accuraterip-checksum.c (whipper's) and zlib.crc32.
    [TestMethod]
    [DataRow(1, 3, 0x6d320c58u, 0x6f209c86u)]
    [DataRow(2, 3, 0xe6e2e9b0u, 0xe8f26e54u)]
    [DataRow(3, 3, 0xcb2c8d04u, 0xcc555700u)]
    [DataRow(1, 1, 0x517bafacu, 0x52838532u)]
    public void ComputesAccurateRipLikeTheReferenceCode(int track, int tracks, uint v1, uint v2)
    {
        var words = Words();
        Assert.AreEqual((v1, v2), TrackDigester.AccurateRip(words, track, tracks));

        // Fed in uneven pieces, the streaming digester gives the same.
        var digester = new TrackDigester(track, tracks, Samples);
        var bytes = Bytes(words);
        digester.Append(bytes.AsSpan(0, 4000));
        digester.Append(bytes.AsSpan(4000));
        var digest = digester.Result();
        Assert.AreEqual(v1, digest.ArV1);
        Assert.AreEqual(v2, digest.ArV2);
        Assert.AreEqual(0x7583bca3u, digest.Crc);
        Assert.AreEqual(32768, digest.Peak);
        Assert.AreEqual(100.0, digest.PeakPercent);
    }

    [TestMethod]
    public void ReadsAccurateRipAnswers()
    {
        var responses = AccurateRipDatabase.Parse(File.ReadAllBytes(DiscTests.Fixture("dBAR-002-0000f21c-00027ef8-05021002.bin")));

        Assert.HasCount(2, responses);
        Assert.AreEqual("0000f21c", responses[0].DiscId1);
        Assert.AreEqual("00027ef8", responses[0].DiscId2);
        Assert.AreEqual("05021002", responses[0].FreedbId);
        CollectionAssert.AreEqual(new[] { 12, 20 }, responses[0].Confidences.ToArray());
        CollectionAssert.AreEqual(new[] { 0x284fc705u, 0x9cc1f32eu }, responses[0].Checksums.ToArray());
        CollectionAssert.AreEqual(new[] { 7, 7 }, responses[1].Confidences.ToArray());
        CollectionAssert.AreEqual(new[] { 0xdc77f9abu, 0xdd97d2c3u }, responses[1].Checksums.ToArray());
    }

    [TestMethod]
    public void MatchesV1AndV2AndKeepsTheBestConfidence()
    {
        var responses = AccurateRipDatabase.Parse(File.ReadAllBytes(DiscTests.Fixture("dBAR-002-0000f21c-00027ef8-05021002.bin")));

        var first = AccurateRipDatabase.Match(responses, 0, 0x284fc705, 0xdc77f9ab);
        Assert.IsTrue(first.Accurate);
        Assert.AreEqual(12, first.V1Confidence);
        Assert.AreEqual(7, first.V2Confidence);
        Assert.AreEqual(("v1", 12, 0x284fc705u), first.Best);
        Assert.AreEqual(12, first.MaxConfidence);

        var wrong = AccurateRipDatabase.Match(responses, 1, 0xdeadbeef, 0x01234567);
        Assert.IsFalse(wrong.Accurate);
        Assert.IsTrue(wrong.InDatabase);
        Assert.AreEqual(20, wrong.MaxConfidence);
        Assert.AreEqual(0x9cc1f32eu, wrong.MaxConfidenceChecksum);

        Assert.IsTrue(AccurateRipDatabase.Matches(responses, 1, 0x0, 0xdd97d2c3));
        Assert.IsFalse(AccurateRipDatabase.Matches(responses, 2, 0x284fc705, 0x284fc705));
    }

    // whipper's own progress fixtures and the track quality its tests expect.
    [TestMethod]
    [DataRow("cdparanoia.progress", 45990, 47719, "99.6")]
    [DataRow("cdparanoia.progress.strokes", 0, 0, "100.0")]
    [DataRow("cdparanoia.progress.error", 0, 10800, "79.6")]
    public void WorksOutTrackQualityFromParanoiasProgress(string file, int first, int last, string quality)
    {
        var progress = new ParanoiaProgress(first, last);
        foreach (var line in File.ReadLines(DiscTests.Fixture(file)))
        {
            progress.Parse(line);
        }

        Assert.AreEqual(quality, (progress.Quality * 100).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
    }

    [TestMethod]
    public void ReadsTheCacheAnswer()
    {
        Assert.IsTrue(CdParanoia.DefeatsCache(File.ReadAllText(DiscTests.Fixture("PX-L890SA.cdparanoia-A.stderr")), true));
        Assert.IsTrue(CdParanoia.DefeatsCache(File.ReadAllText(DiscTests.Fixture("MATSHITA.cdparanoia-A.stderr")), true));
        Assert.IsFalse(CdParanoia.DefeatsCache("\nWARNING! PARANOIA MAY NOT BE\nTRUSTWORTHY WITH THIS DRIVE!", false));
        Assert.IsNull(CdParanoia.DefeatsCache("Unable to open disc.", false));
    }

    [TestMethod]
    public void NamesSpansTheWayWhipperDoes()
    {
        var toc = CdrdaoToc.Read(DiscTests.Fixture("bloc.toc"));

        Assert.AreEqual("1[00:00:00.00]-1[00:04:21.73]", CdParanoia.Span(toc, toc.TrackStart(1), toc.TrackEnd(1)));
        Assert.AreEqual("0[00:00:00.00]-0[00:03:22.69]", CdParanoia.Span(toc, 0, toc.TrackStart(1) - 1));
        Assert.AreEqual("2[00:00:00.00]-2[00:03:40.12]", CdParanoia.Span(toc, toc.TrackStart(2), toc.TrackEnd(2)));
    }

    [TestMethod]
    public void ShiftsAReadByThePositiveOffset()
    {
        // Track at frames 100-199, drive offset +667: read from 667 samples into frame 100.
        var plan = ReadPlan.For(100, 199, 667, readable: 10_000);

        Assert.AreEqual(101, plan.FirstFrame); // 667 = 1 frame and 79 samples
        Assert.AreEqual(201, plan.LastFrame);
        Assert.AreEqual(79L, plan.SkipSamples);
        Assert.AreEqual(100L * 588, plan.Samples);
        Assert.AreEqual(0L, plan.PadBefore);
        Assert.AreEqual(0L, plan.PadAfter);
    }

    [TestMethod]
    public void PadsWithSilenceWhereTheDriveCantRead()
    {
        // Negative offset on the first frames: nothing before frame 0.
        var start = ReadPlan.For(0, 99, -30, readable: 10_000);
        Assert.AreEqual(0, start.FirstFrame);
        Assert.AreEqual(30L, start.PadBefore);
        Assert.AreEqual(0L, start.SkipSamples);
        Assert.AreEqual(99, start.LastFrame);

        // Positive offset on the last track: nothing after the last frame (no overread).
        var end = ReadPlan.For(9_900, 9_999, 667, readable: 9_999);
        Assert.AreEqual(9_999, end.LastFrame);
        Assert.AreEqual(667L, end.PadAfter);
        Assert.AreEqual(100L * 588, end.Samples);
    }

    [TestMethod]
    public void CorrectsAReadIntoTheTrackAndDigestsIt()
    {
        using var folder = new TempFolder();
        // A "disc" of 3 frames whose samples count up: the raw read covers frames 0-2.
        var words = Enumerable.Range(0, 3 * 588).Select(i => (uint)i + 1).ToArray();
        var raw = folder.Path("raw.wav");
        File.WriteAllBytes(raw, [.. Wav.Header(words.Length * 4L), .. Bytes(words)]);
        // Frame 1 at offset +10: samples 598 to 1185 of the disc.
        var plan = new ReadPlan(0, 2, 588 + 10, 588, 0, 0);
        var output = folder.Path("track.wav");

        var digester = new TrackDigester(2, 3, 588);
        Wav.Correct(raw, plan, digester, output);

        var track = Wav.ReadSamples(output);
        Assert.HasCount(588, track);
        Assert.AreEqual(599u, track[0]);
        Assert.AreEqual(1186u, track[^1]);
        Assert.AreEqual(TrackDigester.AccurateRip(track, 2, 3), (digester.Result().ArV1, digester.Result().ArV2));
    }
}
