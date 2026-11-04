using System.Globalization;
using System.Text;
using Spinarr.Disc;
using Spinarr.MusicBrainz;

namespace Spinarr.Rip;

/// <summary>Folder and file names, and the tags written into each FLAC.</summary>
public static class RipNaming
{
    /// <summary>"{Artist}/{Album} ({Year})", the layout Lidarr imports from.</summary>
    public static string AlbumFolder(MbRelease? release, DiscIdentity disc)
    {
        if (release == null)
        {
            return Path.Combine("Unknown Artist", Safe($"Unknown disc {disc.Freedb}"));
        }

        var album = release.Year.Length > 0 ? $"{release.Title} ({release.Year})" : release.Title;
        return Path.Combine(Safe(release.Artist), Safe(album));
    }

    /// <summary>"01 - Title.flac", or "2-01 - Title.flac" on a release with several discs; track 0 is the hidden audio before track 1.</summary>
    public static string TrackFile(MbRelease? release, MbMedium? medium, int number, string title)
    {
        var prefix = release != null && medium != null && release.Media.Count > 1 ? $"{medium.Position}-" : "";
        return Safe($"{prefix}{number:00} - {title}") + ".flac";
    }

    /// <summary>The log and cue sheet: named after the album, with the disc number on a set.</summary>
    public static string DiscFileBase(MbRelease? release, MbMedium? medium, DiscIdentity disc)
    {
        var album = release == null ? $"Unknown disc {disc.Freedb}"
            : release.Year.Length > 0 ? $"{release.Title} ({release.Year})" : release.Title;
        if (release != null && medium != null && release.Media.Count > 1)
        {
            album += $" (Disc {medium.Position})";
        }

        return Safe(album);
    }

    /// <summary>A name that works on every file system and over SMB: no / \ : * ? " &lt; &gt; |, no trailing dot or space.</summary>
    public static string Safe(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            builder.Append(c switch
            {
                '/' or '\\' => "-",
                ':' => " -",
                '*' or '?' or '"' or '<' or '>' or '|' => "",
                _ when char.IsControl(c) => "",
                _ => c.ToString()
            });
        }

        var safe = builder.ToString().Replace("  ", " ").Trim().TrimEnd('.', ' ');
        return safe.Length == 0 ? "_" : safe.Length > 180 ? safe[..180].TrimEnd() : safe;
    }

    /// <summary>
    /// Vorbis comments with the names Picard and Lidarr use: the MusicBrainz IDs tie the files to
    /// the exact release, so Lidarr files them without guessing.
    /// </summary>
    public static IReadOnlyList<(string Key, string Value)> Tags(RipRequest request, RipTrack track, Toc toc, string version)
    {
        var tags = new List<(string, string)>();

        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                tags.Add((key, value.Trim()));
            }
        }

        var release = request.Release;
        var medium = request.Medium;
        var mbTrack = medium?.Tracks.FirstOrDefault(t => t.Position == track.AudioIndex);

        Add("TITLE", track.Title);
        Add("ARTIST", track.Artist);
        Add("ALBUM", release?.Title ?? toc.CdText.GetValueOrDefault("TITLE"));
        Add("ALBUMARTIST", release?.Artist ?? toc.CdText.GetValueOrDefault("PERFORMER"));
        Add("TRACKNUMBER", track.Number.ToString(CultureInfo.InvariantCulture));
        Add("TRACKTOTAL", toc.AudioTracks.Count.ToString(CultureInfo.InvariantCulture));
        Add("TOTALTRACKS", toc.AudioTracks.Count.ToString(CultureInfo.InvariantCulture));
        if (release != null && medium != null)
        {
            Add("DISCNUMBER", medium.Position.ToString(CultureInfo.InvariantCulture));
            Add("DISCTOTAL", release.Media.Count.ToString(CultureInfo.InvariantCulture));
            Add("TOTALDISCS", release.Media.Count.ToString(CultureInfo.InvariantCulture));
            Add("DISCSUBTITLE", medium.Title);
            Add("MEDIA", medium.Format);
            Add("DATE", release.Date);
            Add("ORIGINALDATE", release.OriginalDate);
            Add("RELEASECOUNTRY", release.Country);
            Add("RELEASESTATUS", release.Status.ToLowerInvariant());
            Add("RELEASETYPE", string.Join("; ", new[] { release.PrimaryType }.Concat(release.SecondaryTypes)
                .Where(t => t.Length > 0).Select(t => t.ToLowerInvariant())));
            Add("BARCODE", release.Barcode);
            foreach (var label in release.Labels.Select(l => l.Name).Where(n => n.Length > 0).Distinct())
            {
                Add("LABEL", label);
            }

            foreach (var catalog in release.Labels.Select(l => l.CatalogNumber).Where(n => n.Length > 0).Distinct())
            {
                Add("CATALOGNUMBER", catalog);
            }

            Add("MUSICBRAINZ_ALBUMID", release.Id);
            Add("MUSICBRAINZ_RELEASEGROUPID", release.ReleaseGroupId);
            foreach (var id in release.ArtistIds)
            {
                Add("MUSICBRAINZ_ALBUMARTISTID", id);
            }

            if (mbTrack != null)
            {
                Add("MUSICBRAINZ_TRACKID", mbTrack.RecordingId);
                Add("MUSICBRAINZ_RELEASETRACKID", mbTrack.TrackId);
                foreach (var id in mbTrack.ArtistIds)
                {
                    Add("MUSICBRAINZ_ARTISTID", id);
                }
            }
        }

        Add("ISRC", track.Isrc ?? mbTrack?.Isrcs.FirstOrDefault());
        Add("MUSICBRAINZ_DISCID", request.Disc.MusicBrainz);
        // Marks Spinarr's own rips: Lidarr's re-encode script can skip them (level and gain already done).
        Add("RIPPER", $"Spinarr {version}");
        Add("SPINARR_FLAC_LEVEL", request.FlacLevel.ToString(CultureInfo.InvariantCulture));
        if (track.AccurateRip?.Best is { } best)
        {
            Add("ACCURATERIPRESULT", $"accurate (confidence {best.Confidence}, AR {best.Version})");
        }

        return tags;
    }
}
