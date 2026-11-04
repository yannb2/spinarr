using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Spinarr.Disc;

/// <summary>The IDs a disc is looked up by: MusicBrainz, AccurateRip and freedb (CDDB).</summary>
public sealed record DiscIdentity(string MusicBrainz, string Freedb, string AccurateRip1, string AccurateRip2, int AudioTracks)
{
    /// <summary>Where AccurateRip keeps this disc's answers, under http://www.accuraterip.com/accuraterip/.</summary>
    public string AccurateRipPath =>
        $"{AccurateRip1[^1]}/{AccurateRip1[^2]}/{AccurateRip1[^3]}/dBAR-{AudioTracks:000}-{AccurateRip1}-{AccurateRip2}-{Freedb}.bin";
}

public static class DiscIds
{
    public static DiscIdentity Of(Toc toc) =>
        new(MusicBrainz(toc), Freedb(toc), AccurateRip(toc).Id1, AccurateRip(toc).Id2, toc.AudioTracks.Count);

    /// <summary>
    /// The numbers MusicBrainz's disc ID is made of: first track, last audio track, lead-out, then
    /// the start of each audio track, all +150. A data track at the end (CD-Extra) is left out,
    /// and the audio session ends 11400 frames before it.
    /// </summary>
    public static IReadOnlyList<int> MusicBrainzValues(Toc toc)
    {
        var audioCount = toc.AudioTracks.Count;
        var offsets = new List<int>();
        var additional = 0;
        int? leadout = null;
        for (var i = 0; i < toc.Tracks.Count; i++)
        {
            var track = toc.Tracks[i];
            if (!track.Audio)
            {
                if (i < toc.Tracks.Count - 1)
                {
                    additional++;
                }
                else
                {
                    leadout = toc.Tracks[^1].Start - 11400 + Cd.LeadIn;
                }

                continue;
            }

            offsets.Add(track.Start + Cd.LeadIn);
        }

        leadout ??= toc.TrackEnd(audioCount + additional) + 1 + Cd.LeadIn;
        return [1, audioCount, leadout.Value, .. offsets];
    }

    /// <summary>The MusicBrainz disc ID: SHA-1 of the TOC in hex, in MusicBrainz's base64.</summary>
    public static string MusicBrainz(Toc toc)
    {
        var values = MusicBrainzValues(toc);
        var text = new StringBuilder();
        text.Append(CultureInfo.InvariantCulture, $"{values[0]:X2}{values[1]:X2}");
        // 100 slots: the lead-out, then tracks 1 to 99 (unused ones 0).
        for (var i = 0; i < 100; i++)
        {
            var value = i + 2 < values.Count ? values[i + 2] : 0;
            text.Append(CultureInfo.InvariantCulture, $"{value:X8}");
        }

        var hash = SHA1.HashData(Encoding.ASCII.GetBytes(text.ToString()));
        return Convert.ToBase64String(hash).Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }

    /// <summary>The "toc" parameter MusicBrainz takes for a fuzzy lookup or to attach a disc ID: "1 12 195856 150 ...".</summary>
    public static string MusicBrainzToc(Toc toc) => string.Join(' ', MusicBrainzValues(toc));

    /// <summary>The page that adds this disc ID to a MusicBrainz release.</summary>
    public static string MusicBrainzSubmitUrl(Toc toc) =>
        $"https://musicbrainz.org/cdtoc/attach?toc={MusicBrainzToc(toc).Replace(' ', '+')}&tracks={toc.AudioTracks.Count}&id={MusicBrainz(toc)}";

    /// <summary>The freedb (CDDB) disc ID, data tracks included: AccurateRip files its answers under it too.</summary>
    public static string Freedb(Toc toc)
    {
        var digitSum = 0;
        foreach (var track in toc.Tracks)
        {
            var seconds = (track.Start + Cd.LeadIn) / Cd.FramesPerSecond;
            digitSum += seconds.ToString(CultureInfo.InvariantCulture).Sum(c => c - '0');
        }

        var leadout = toc.TrackEnd(toc.Tracks[^1].Number) + 1;
        var length = leadout / Cd.FramesPerSecond - toc.TrackStart(1) / Cd.FramesPerSecond;
        var id = ((uint)(digitSum % 255) << 24) | ((uint)(length & 0xFFFF) << 8) | (uint)(toc.Tracks.Count & 0xFF);
        return id.ToString("x8", CultureInfo.InvariantCulture);
    }

    /// <summary>AccurateRip's two disc IDs: sums of the audio track starts, the lead-out counted in.</summary>
    public static (string Id1, string Id2) AccurateRip(Toc toc)
    {
        uint id1 = 0, id2 = 0;
        foreach (var track in toc.Tracks.Where(t => t.Audio))
        {
            var offset = (uint)track.Start;
            id1 += offset;
            id2 += Math.Max(offset, 1) * (uint)track.Number;
        }

        var leadout = (uint)(toc.TrackEnd(toc.Tracks[^1].Number) + 1);
        id1 += leadout;
        id2 += leadout * (uint)(toc.AudioTracks.Count + 1);
        return (id1.ToString("x8", CultureInfo.InvariantCulture), id2.ToString("x8", CultureInfo.InvariantCulture));
    }
}
