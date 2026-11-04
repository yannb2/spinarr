using System.Text;
using System.Text.RegularExpressions;

namespace Spinarr.Disc;

/// <summary>
/// Reads the .toc file "cdrdao read-toc" writes (format in cdrdao's man page). A port of whipper's
/// TocFile parser, which has years of real discs behind it: the same rules, so the same addresses.
/// </summary>
public static partial class CdrdaoToc
{
    /// <summary>The CD-Text fields kept (others, like SIZE_INFO, are cdrdao's bookkeeping).</summary>
    public static readonly IReadOnlySet<string> CdTextFields = new HashSet<string>
    {
        "ARRANGER", "COMPOSER", "DISCID", "GENRE", "MESSAGE", "ISRC", "PERFORMER", "SIZE_INFO",
        "SONGWRITER", "TITLE", "TOC_INFO", "TOC_INFO2", "UPC_EAN"
    };

    public static Toc Parse(string text)
    {
        var toc = new Toc();
        TocTrack? track = null;
        var inTrack = false;

        // Where the current track starts on the disc, how much of it the lines read so far cover,
        // and its pre-gap (START): index 01 is only known once the track's last line is read.
        var trackOffset = 0;
        var trackLength = 0;
        var pregap = 0;
        var indexNumber = 1;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').TrimEnd();
            var stripped = line.Trim();

            if (CdTextCandidate().Match(line) is { Success: true } cdText)
            {
                var key = cdText.Groups["key"].Value;
                var value = Unescape(cdText.Groups["value"].Value);
                if (CdTextFields.Contains(key))
                {
                    if (!inTrack)
                    {
                        toc.CdText[key] = value;
                    }
                    else if (track != null && (key != "ISRC" || track.Isrc != null))
                    {
                        track.CdText[key] = value;
                    }
                }
            }

            if (Catalog().Match(stripped) is { Success: true } catalog)
            {
                toc.Catalog = catalog.Groups[1].Value;
            }

            if (TrackLine().Match(stripped) is { Success: true } trackLine)
            {
                inTrack = true;
                if (track != null)
                {
                    track.Start = trackOffset + pregap;
                }

                trackOffset += trackLength;
                trackLength = 0;
                pregap = 0;
                indexNumber = 1;
                track = new TocTrack { Number = toc.Tracks.Count + 1, Audio = trackLine.Groups[1].Value == "AUDIO" };
                toc.Tracks.Add(track);
                continue;
            }

            if (track == null)
            {
                continue;
            }

            if (stripped == "PRE_EMPHASIS")
            {
                track.PreEmphasis = true;
            }
            else if (Isrc().Match(stripped) is { Success: true } isrc)
            {
                track.Isrc = isrc.Groups[1].Value;
            }
            else if (Silence().Match(stripped) is { Success: true } silence)
            {
                trackLength += Cd.MsfToFrames(silence.Groups[1].Value);
            }
            else if (Zero().Match(stripped) is { Success: true } zero)
            {
                trackLength += Cd.MsfToFrames(zero.Groups[1].Value);
            }
            else if (FileLine().Match(stripped) is { Success: true } file)
            {
                trackLength += Cd.MsfToFrames(file.Groups["length"].Value);
            }
            else if (DataFile().Match(stripped) is { Success: true } dataFile)
            {
                trackLength += Cd.MsfToFrames(dataFile.Groups["length"].Value);
            }
            else if (Start().Match(stripped) is { Success: true } start)
            {
                track.Index0 = trackOffset;
                pregap = Cd.MsfToFrames(start.Groups[1].Value);
            }
            else if (Index().Match(stripped) is { Success: true } index)
            {
                indexNumber++;
                track.MoreIndexes.Add(trackOffset + Cd.MsfToFrames(index.Groups[1].Value));
            }
        }

        if (track != null)
        {
            track.Start = trackOffset + pregap;
        }

        toc.Leadout = trackOffset + trackLength;
        return toc;
    }

    /// <summary>
    /// cdrdao's string escapes: \" and \\, and \ooo octal bytes. cdrdao 1.2.5+ writes UTF-8 unless
    /// told not to; older ones wrote Latin-1 as octal escapes, read here as Latin-1 characters.
    /// </summary>
    internal static string Unescape(string value) =>
        Escapes().Replace(value, m => m.Groups["octal"].Success
            ? ((char)Convert.ToInt32(m.Groups["octal"].Value, 8)).ToString()
            : m.Value == "\\\"" ? "\"" : "\\");

    /// <summary>Reads a .toc file written by cdrdao: UTF-8, or Latin-1 if it isn't valid UTF-8.</summary>
    public static Toc Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = Encoding.Latin1.GetString(bytes);
        }

        return Parse(text);
    }

    [GeneratedRegex("""(?<key>\w+)\s+"(?<value>(?:\\\\|\\"|[^"])+?)" """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex CdTextCandidate();

    [GeneratedRegex("""\\(?<octal>[0-7]{3})|\\"|\\\\""")]
    private static partial Regex Escapes();

    [GeneratedRegex("""^CATALOG "(\d+)"$""")]
    private static partial Regex Catalog();

    [GeneratedRegex(@"^TRACK\s(.+)$")]
    private static partial Regex TrackLine();

    [GeneratedRegex("""^ISRC "(\w+)"$""")]
    private static partial Regex Isrc();

    [GeneratedRegex(@"^SILENCE\s(.*)$")]
    private static partial Regex Silence();

    [GeneratedRegex(@"^ZERO\s.+\s(.*)$")]
    private static partial Regex Zero();

    [GeneratedRegex("""^FILE\s+"(?<name>.*)"\s+(?<start>\S+)\s(?<length>\S+)$""")]
    private static partial Regex FileLine();

    [GeneratedRegex("""^DATAFILE\s+"(?<name>.*)"\s+(?<length>\S+)""")]
    private static partial Regex DataFile();

    [GeneratedRegex(@"^START\s(.*)$")]
    private static partial Regex Start();

    [GeneratedRegex(@"^INDEX\s(.+)$")]
    private static partial Regex Index();
}
