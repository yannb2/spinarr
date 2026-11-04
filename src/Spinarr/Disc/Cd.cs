using System.Globalization;

namespace Spinarr.Disc;

/// <summary>Audio CD units: a frame (sector) is 1/75 s, 588 stereo samples, 2352 bytes.</summary>
public static class Cd
{
    public const int FramesPerSecond = 75;
    public const int SamplesPerFrame = 588;
    public const int BytesPerFrame = 2352;
    public const int BytesPerSample = 4;

    /// <summary>The 2-second lead-in every TOC address is counted after (LBA 0 = MSF 00:02:00).</summary>
    public const int LeadIn = 150;

    /// <summary>"MM:SS:FF" (or a plain frame count) to frames.</summary>
    public static int MsfToFrames(string msf)
    {
        var parts = msf.Trim().Split(':');
        if (parts.Length == 1)
        {
            return int.Parse(parts[0], CultureInfo.InvariantCulture);
        }

        return (int.Parse(parts[0], CultureInfo.InvariantCulture) * 60 + int.Parse(parts[1], CultureInfo.InvariantCulture))
               * FramesPerSecond + int.Parse(parts[2], CultureInfo.InvariantCulture);
    }

    /// <summary>"MM:SS:FF", as cue sheets write it.</summary>
    public static string Msf(int frames) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{frames / FramesPerSecond / 60:00}:{frames / FramesPerSecond % 60:00}:{frames % FramesPerSecond:00}");

    /// <summary>"HH:MM:SS.FF", the way cd-paranoia reads a position inside a track.</summary>
    public static string Hmsf(int frames) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{frames / FramesPerSecond / 3600:00}:{frames / FramesPerSecond / 60 % 60:00}:{frames / FramesPerSecond % 60:00}.{frames % FramesPerSecond:00}");

    /// <summary>"M:SS.FF", the way EAC's TOC table writes a start and a length.</summary>
    public static string EacTime(int frames) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{frames / FramesPerSecond / 60}:{frames / FramesPerSecond % 60:00}.{frames % FramesPerSecond:00}");

    /// <summary>"H:MM:SS.FF", EAC's pre-gap length.</summary>
    public static string EacGap(int frames) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{frames / FramesPerSecond / 3600}:{frames / FramesPerSecond / 60 % 60:00}:{frames / FramesPerSecond % 60:00}.{frames % FramesPerSecond:00}");

    /// <summary>"4:05", a track length for the page.</summary>
    public static string Length(int frames)
    {
        var seconds = (int)Math.Round(frames / (double)FramesPerSecond);
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }
}
