using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Spinarr.AccurateRip;
using Spinarr.Audio;
using Spinarr.Disc;
using Spinarr.Drives;
using Spinarr.Options;
using Spinarr.Rip;
using Spinarr.Tools;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Spinarr.Tests;

/// <summary>
/// A whole rip, start to end, with stand-ins for cdrdao, cd-paranoia and flac (small Python scripts):
/// the hidden track, the pre-gaps, the read offset, the CRCs, the files that land and the history.
/// </summary>
[TestClass]
public class RipServiceTests
{
    // Hidden audio (1 s), then three short tracks; track 2 and 3 have a pre-gap.
    private const string TocText = """
        CD_DA

        // Track 1
        TRACK AUDIO
        NO COPY
        NO PRE_EMPHASIS
        TWO_CHANNEL_AUDIO
        FILE "data.wav" 0 00:05:00
        START 00:01:00

        // Track 2
        TRACK AUDIO
        NO COPY
        NO PRE_EMPHASIS
        TWO_CHANNEL_AUDIO
        FILE "data.wav" 00:05:00 00:03:00
        START 00:00:10

        // Track 3
        TRACK AUDIO
        NO COPY
        NO PRE_EMPHASIS
        TWO_CHANNEL_AUDIO
        FILE "data.wav" 00:08:00 00:04:00
        START 00:00:20
        """;

    // cd-paranoia's stand-in: the span is "track[hh:mm:ss.ff]-track[hh:mm:ss.ff]" from each track's start
    // (track 0 = the disc's start), and the disc's sample n is n * 2654435761, so a wrong offset or span shows.
    private const string Paranoia = """
        #!/usr/bin/env python3
        import json, os, re, struct, sys
        starts = json.load(open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "starts.json")))
        span, out = sys.argv[-2], sys.argv[-1]
        def frame(part):
            track, h, m, s, f = re.fullmatch(r"(\d+)\[(\d+):(\d+):(\d+)\.(\d+)\]", part).groups()
            return (0 if track == "0" else starts[track]) + ((int(h) * 60 + int(m)) * 60 + int(s)) * 75 + int(f)
        first, last = (frame(p) for p in span.split("-"))
        samples = (last - first + 1) * 588
        data = struct.pack(f"<{samples}I", *(((first * 588 + i) * 2654435761) & 0xFFFFFFFF for i in range(samples)))
        with open(out, "wb") as w:
            w.write(b"RIFF" + struct.pack("<I", 36 + len(data)) + b"WAVEfmt " + struct.pack("<IHHIIHH", 16, 1, 2, 44100, 176400, 4, 16))
            w.write(b"data" + struct.pack("<I", len(data)) + data)
        sys.stderr.write(f"##: 0 [read] @ {first * 1176}\n##: -2 [wrote] @ {(last + 1) * 1176 - 1}\n")
        """;

    private const string Cdrdao = """
        #!/usr/bin/env python3
        import os, shutil, sys
        shutil.copy(os.path.join(os.path.dirname(os.path.abspath(__file__)), "disc.toc"), sys.argv[-1])
        """;

    // flac's stand-in: copies the WAV, and keeps the tags next to itself to check them.
    private const string Flac = """
        #!/usr/bin/env python3
        import os, shutil, sys
        args = sys.argv[1:]
        out = args[args.index("-o") + 1]
        shutil.copy(args[-1], out)
        tags = os.path.join(os.path.dirname(os.path.abspath(__file__)), os.path.basename(out).removesuffix(".part") + ".tags")
        with open(tags, "w") as t:
            t.write("\n".join(args[i + 1] for i, a in enumerate(args) if a == "-T"))
        """;

    [TestMethod]
    public async Task RipsADiscFromStartToEnd()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/usr/bin/python3"))
        {
            Assert.Inconclusive("python3 is needed for the stand-in tools.");
        }

        using var root = new TempFolder();
        var toc = CdrdaoToc.Parse(TocText);
        root.Write("bin/disc.toc", TocText);
        root.Write("bin/starts.json", JsonSerializer.Serialize(toc.Tracks.ToDictionary(t => t.Number.ToString(), t => t.Start)));
        foreach (var (name, script) in new[] { ("cd-paranoia", Paranoia), ("cdrdao", Cdrdao), ("flac", Flac) })
        {
            root.Write($"bin/{name}", script + "\n");
#pragma warning disable CA1416 // Linux only: checked above.
            File.SetUnixFileMode(root.Path($"bin/{name}"), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#pragma warning restore CA1416
        }

        var options = MsOptions.Create(new SpinarrOptions
        {
            ConfigFolder = root.Path("config"), CacheFolder = root.Path("cache"), RipFolder = root.Path("rips"),
            Tools = new ToolPaths { CdParanoia = root.Path("bin/cd-paranoia"), Cdrdao = root.Path("bin/cdrdao"), Flac = root.Path("bin/flac") }
        }.WithDefaults());
        var drive = new OpticalDrive("/dev/sr0", "HL-DT-ST", "DVDRAM GH40L", "1.04", []);
        var store = new DriveSettingsStore(options, NullLogger<DriveSettingsStore>.Instance);
        const int offset = 6;
        store.Save(drive, offset, true);

        var reader = new DiscReader(options, NullLogger<DiscReader>.Instance);
        var accurateRip = new AccurateRipClient(new NotFoundFactory(), NullLogger<AccurateRipClient>.Instance);
        var tasks = new DriveTasks(reader, accurateRip, store, options, NullLogger<DriveTasks>.Instance);
        var service = new RipService(reader, accurateRip, store, tasks,
            new ToolStatusService(options, NullLogger<ToolStatusService>.Instance), options, NullLogger<RipService>.Instance);

        var (job, problem) = service.Start(new RipRequest(drive, DiscIds.Of(toc), null, null, 5, 2));
        Assert.IsNull(problem);
        Assert.IsNotNull(job);
        for (var waited = 0; job.State == RipState.Running && waited < 600; waited++)
        {
            await Task.Delay(100);
        }

        Assert.AreEqual(RipState.Done, job.State, job.Summary + "\n" + string.Join('\n', job.Lines));
        var album = Path.Combine(root.Path("rips"), "Unknown Artist", $"Unknown disc {job.Request.Disc.Freedb}");
        Assert.AreEqual(album, job.Destination);
        CollectionAssert.AreEquivalent(
            new[] { "00 - Hidden track.flac", "01 - Track 01.flac", "02 - Track 02.flac", "03 - Track 03.flac" },
            Directory.GetFiles(album, "*.flac").Select(Path.GetFileName).ToArray());
        Assert.IsTrue(File.Exists(Path.Combine(album, $"Unknown disc {job.Request.Disc.Freedb}.log")));
        Assert.IsTrue(File.Exists(Path.Combine(album, $"Unknown disc {job.Request.Disc.Freedb}.cue")));
        Assert.IsFalse(Directory.Exists(job.WorkFolder), "The work folder goes once the album is out.");

        // Each file holds exactly its track's samples, shifted by the offset: track 2 runs from its
        // index 01 up to the sample before track 3's index 01 (track 3's pre-gap rides along).
        foreach (var track in job.Tracks)
        {
            var samples = Wav.ReadSamples(Path.Combine(album, track.FileName));
            Assert.HasCount(track.Frames * Cd.SamplesPerFrame, samples, track.FileName);
            var first = (long)track.FirstFrame * Cd.SamplesPerFrame + offset;
            var lastRead = track.Number == 3 ? samples.Length - offset - 1 : samples.Length - 1;
            Assert.AreEqual((uint)(first * 2654435761 & 0xFFFFFFFF), samples[0], track.FileName);
            Assert.AreEqual((uint)((first + lastRead) * 2654435761 & 0xFFFFFFFF), samples[lastRead], track.FileName);
            Assert.IsTrue(track.CrcsMatch);
        }

        // The last track ends at the disc's end: the 6 samples past it are silence, not an overread.
        var last = Wav.ReadSamples(Path.Combine(album, "03 - Track 03.flac"));
        Assert.IsTrue(last[^offset..].All(s => s == 0));

        var tags = File.ReadAllLines(root.Path("bin/02 - Track 02.flac.tags"));
        CollectionAssert.Contains(tags, "TRACKNUMBER=2");
        CollectionAssert.Contains(tags, $"MUSICBRAINZ_DISCID={job.Request.Disc.MusicBrainz}");

        var record = service.History().Single();
        Assert.AreEqual(RipState.Done, record.State);
        Assert.AreEqual(3, record.Tracks);
    }

    private sealed class NotFoundFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new NotFound());

        private sealed class NotFound : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
