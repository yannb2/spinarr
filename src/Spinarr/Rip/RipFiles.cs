using System.Globalization;
using System.Text;
using Spinarr.Disc;

namespace Spinarr.Rip;

/// <summary>The cue sheet and the rip log, in EAC's layouts.</summary>
public static class RipFiles
{
    /// <summary>
    /// A cue sheet for one file per track with each pre-gap at the end of the track before it: EAC's
    /// "Multiple WAV files with gaps (Noncompliant)", which plays back the disc exactly.
    /// </summary>
    public static string CueSheet(RipJob job, string version)
    {
        var toc = job.Toc!;
        var release = job.Request.Release;
        var lines = new List<string>();
        if (release?.Year is { Length: > 0 } year)
        {
            lines.Add($"REM DATE {year}");
        }

        lines.Add($"REM DISCID {job.Request.Disc.Freedb.ToUpperInvariant()}");
        lines.Add($"REM COMMENT \"Spinarr {version}\"");
        if (toc.Catalog != null)
        {
            lines.Add($"CATALOG {toc.Catalog}");
        }

        lines.Add($"PERFORMER {Quote(release?.Artist ?? toc.CdText.GetValueOrDefault("PERFORMER") ?? "Unknown Artist")}");
        lines.Add($"TITLE {Quote(release?.Title ?? toc.CdText.GetValueOrDefault("TITLE") ?? "Unknown disc")}");

        var htoa = job.Tracks.FirstOrDefault(t => t.Number == 0 && t.State == TrackState.Done);
        var tracks = job.Tracks.Where(t => t.Number > 0).ToList();
        string? previousFile = null;
        var previousStart = 0;
        foreach (var track in tracks)
        {
            var disc = toc.Track(track.Number);
            var gapInPrevious = disc.Index0 != null && track.Number > 1 && previousFile != null;
            if (track.Number == 1 && htoa != null)
            {
                lines.Add($"FILE {Quote(htoa.FileName)} WAVE");
            }
            else if (!gapInPrevious)
            {
                lines.Add($"FILE {Quote(track.FileName)} WAVE");
            }

            lines.Add($"  TRACK {track.Number:00} AUDIO");
            lines.Add($"    TITLE {Quote(track.Title)}");
            lines.Add($"    PERFORMER {Quote(track.Artist)}");
            if (track.Isrc != null)
            {
                lines.Add($"    ISRC {track.Isrc}");
            }

            if (track.PreEmphasis)
            {
                lines.Add("    FLAGS PRE");
            }

            if (track.Number == 1 && disc.Pregap > 0)
            {
                if (htoa != null)
                {
                    lines.Add("    INDEX 00 00:00:00");
                    lines.Add($"FILE {Quote(track.FileName)} WAVE");
                }
                else
                {
                    lines.Add($"    PREGAP {Cd.Msf(disc.Pregap)}");
                }
            }
            else if (gapInPrevious)
            {
                lines.Add($"    INDEX 00 {Cd.Msf(disc.Index0!.Value - previousStart)}");
                lines.Add($"FILE {Quote(track.FileName)} WAVE");
            }

            lines.Add("    INDEX 01 00:00:00");
            var number = 2;
            foreach (var index in disc.MoreIndexes)
            {
                lines.Add($"    INDEX {number++:00} {Cd.Msf(index - disc.Start)}");
            }

            previousFile = track.FileName;
            previousStart = disc.Start;
        }

        return string.Join("\r\n", lines) + "\r\n";
    }

    /// <summary>
    /// The rip log in EAC's layout and wording, so it reads like an EAC log and tools that read EAC
    /// logs can parse it. The header names Spinarr, and there is no EAC log checksum: that would sign it as EAC's.
    /// </summary>
    public static string Log(RipJob job, string version, string paranoiaVersion, DateTime now)
    {
        var toc = job.Toc!;
        var request = job.Request;
        var release = request.Release;
        var text = new StringBuilder();

        void Line(string line = "") => text.Append(line).Append("\r\n");

        var english = CultureInfo.GetCultureInfo("en-US");
        Line($"Spinarr V{version} from {now.ToString("d. MMMM yyyy", english)}");
        Line();
        Line($"Spinarr extraction logfile from {now.ToString("d. MMMM yyyy, H:mm", english)}");
        Line();
        Line(release != null ? $"{release.Artist} / {release.Title}" : "Unknown Artist / Unknown disc");
        Line();
        Line($"Used drive  : {request.Drive.Vendor} {request.Drive.Model}   Firmware: {request.Drive.Release}   Device: {request.Drive.Device}");
        Line();
        Line("Read mode               : Secure");
        Line("Utilize accurate stream : Yes");
        Line($"Defeat audio cache      : {(job.DefeatsCache == true ? "Yes" : "No")}");
        Line("Make use of C2 pointers : No");
        Line();
        Line($"Read offset correction                      : {job.ReadOffset ?? 0}");
        Line("Overread into Lead-In and Lead-Out          : No");
        Line("Fill up missing offset samples with silence : Yes");
        Line("Delete leading and trailing silent blocks   : No");
        Line("Null samples used in CRC calculations       : Yes");
        Line($"Used interface                              : cd-paranoia {paranoiaVersion}");
        Line("Gap handling                                : Appended to previous track");
        Line();
        Line("Used output format              : FLAC");
        Line($"Command line compressor         : flac");
        Line($"Additional command line options : -{request.FlacLevel} --verify");
        Line();
        Line();
        Line("TOC of the extracted CD");
        Line();
        Line("     Track |   Start  |  Length  | Start sector | End sector ");
        Line("    ---------------------------------------------------------");
        foreach (var track in toc.Tracks)
        {
            Line(string.Create(CultureInfo.InvariantCulture,
                $"      {track.Number,3}  | {Cd.EacTime(track.Start),8} | {Cd.EacTime(toc.TrackLength(track.Number)),8} |  {track.Start,10}  |  {toc.TrackEnd(track.Number),9}   "));
        }

        Line();
        if (toc.Catalog != null)
        {
            Line($"Media Catalog Number (MCN) : {toc.Catalog}");
            Line();
        }

        foreach (var track in job.Tracks)
        {
            Line();
            Line(track.Number == 0 ? "Track  0 (hidden audio before track 1)" : $"Track {track.Number,2}");
            Line();
            Line($"     Filename {track.FileName}");
            Line();
            if (track.Pregap > 0)
            {
                Line($"     Pre-gap length  {Cd.EacGap(track.Pregap)}");
                Line();
            }

            if (track.State == TrackState.Silent)
            {
                Line("     Digital silence: not kept");
                continue;
            }

            if (track.Copy == null)
            {
                Line($"     Copy failed: {track.Problem}");
                continue;
            }

            Line(string.Create(CultureInfo.InvariantCulture, $"     Peak level {track.Copy.PeakPercent:0.0} %"));
            Line(string.Create(CultureInfo.InvariantCulture, $"     Extraction speed {track.Speed:0.0} X"));
            Line(string.Create(CultureInfo.InvariantCulture, $"     Track quality {track.Quality * 100:0.0} %"));
            if (track.Test != null)
            {
                Line($"     Test CRC {track.Test.Crc:X8}");
            }

            Line($"     Copy CRC {track.Copy.Crc:X8}");
            if (track.Number > 0)
            {
                Line($"     {AccurateRipLine(job, track)}");
            }

            Line(track.CrcsMatch ? "     Copy OK" : "     Copy finished");
        }

        Line();
        var verified = job.Tracks.Where(t => t.Number > 0).ToList();
        if (job.AccurateRipResponses is { Count: > 0 })
        {
            var accurate = verified.Count(t => t.AccurateRip?.Accurate == true);
            Line();
            if (accurate == verified.Count)
            {
                Line("All tracks accurately ripped");
            }
            else
            {
                Line($"{accurate} track(s) accurately ripped");
                Line($"{verified.Count - accurate} track(s) not present in the AccurateRip database or not accurately ripped");
                Line();
                Line("Some tracks could not be verified as accurate");
            }
        }
        else
        {
            Line();
            Line(job.AccurateRipReached ? "None of the tracks are present in the AccurateRip database" : "AccurateRip could not be reached");
        }

        Line();
        var errors = job.Tracks.Any(t => t.State == TrackState.Error || !t.CrcsMatch && t.State != TrackState.Silent);
        Line(errors ? "There were errors" : "No errors occurred");
        Line();
        Line("End of status report");
        return text.ToString();
    }

    private static string AccurateRipLine(RipJob job, RipTrack track)
    {
        if (!job.AccurateRipReached)
        {
            return "AccurateRip could not be reached";
        }

        var match = track.AccurateRip;
        if (job.AccurateRipResponses is not { Count: > 0 } || match == null || !match.InDatabase)
        {
            return "Track not present in AccurateRip database";
        }

        if (match.Best is { } best)
        {
            return $"Accurately ripped (confidence {best.Confidence})  [{best.Checksum:X8}]  (AR {best.Version})";
        }

        return $"Cannot be verified as accurate (confidence {match.MaxConfidence})  [{match.V2:X8}], AccurateRip returned [{match.MaxConfidenceChecksum:X8}]  (AR v2)";
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "'")}\"";
}
