using System.Buffers.Binary;
using Spinarr.Disc;

namespace Spinarr.Audio;

/// <summary>What a pass over a track's audio gives: EAC's CRC, the AccurateRip checksums, the peak.</summary>
public sealed record TrackDigest(uint Crc, uint ArV1, uint ArV2, int Peak, long Samples)
{
    /// <summary>The peak as EAC writes it: 98.8 (%), full scale = 100.</summary>
    public double PeakPercent => Math.Round(Peak * 100.0 / 32768, 1);
}

/// <summary>
/// Reads a track's 16-bit stereo audio once and computes everything checked about it. Fed in
/// pieces, so a long track never has to sit in memory.
/// </summary>
public sealed class TrackDigester
{
    // AccurateRip leaves the first 5 frames of the first track and the last 5 of the last track out:
    // drives with different offsets can't all read them.
    private const long SkippedSamples = 5 * Cd.SamplesPerFrame;

    private readonly long _checkFrom;
    private readonly long _checkTo;
    private readonly bool _accurateRip;
    private uint _crc = 0xFFFFFFFF;
    private uint _sumLow;
    private uint _sumHigh;
    private long _position;
    private int _peak;

    /// <param name="trackNumber">The track's number among the disc's audio tracks (1-based), or 0 for audio AccurateRip doesn't know (HTOA).</param>
    /// <param name="audioTracks">How many audio tracks the disc has.</param>
    /// <param name="samples">The track's length in samples (frames × 588).</param>
    public TrackDigester(int trackNumber, int audioTracks, long samples)
    {
        _accurateRip = trackNumber > 0;
        // The same bounds as the reference code (accuraterip-checksum.c): positions count from 1.
        _checkFrom = trackNumber == 1 ? SkippedSamples : 0;
        _checkTo = trackNumber == audioTracks ? samples - SkippedSamples : samples;
    }

    /// <summary>Adds audio: whole stereo samples, 4 bytes each, little-endian.</summary>
    public void Append(ReadOnlySpan<byte> pcm)
    {
        _crc = Crc32.Update(_crc, pcm);
        var count = pcm.Length / Cd.BytesPerSample;
        for (var i = 0; i < count; i++)
        {
            var word = BinaryPrimitives.ReadUInt32LittleEndian(pcm.Slice(i * 4, 4));
            var position = ++_position;
            if (_accurateRip && position >= _checkFrom && position <= _checkTo)
            {
                var product = (ulong)word * (ulong)position;
                _sumHigh += (uint)(product >> 32);
                _sumLow += (uint)product;
            }

            var left = Math.Abs((int)(short)(word & 0xFFFF));
            var right = Math.Abs((int)(short)(word >> 16));
            if (left > _peak)
            {
                _peak = left;
            }

            if (right > _peak)
            {
                _peak = right;
            }
        }
    }

    public TrackDigest Result() => new(~_crc, _sumLow, _sumLow + _sumHigh, _peak, _position);

    /// <summary>AccurateRip v1 and v2 for audio already in memory, as stereo sample words.</summary>
    public static (uint V1, uint V2) AccurateRip(ReadOnlySpan<uint> samples, int trackNumber, int audioTracks)
    {
        long checkFrom = trackNumber == 1 ? SkippedSamples : 0;
        long checkTo = trackNumber == audioTracks ? samples.Length - SkippedSamples : samples.Length;
        uint low = 0, high = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var position = i + 1L;
            if (position >= checkFrom && position <= checkTo)
            {
                var product = (ulong)samples[i] * (ulong)position;
                high += (uint)(product >> 32);
                low += (uint)product;
            }
        }

        return (low, low + high);
    }
}

/// <summary>CRC-32 (the zlib one), as EAC's test and copy CRCs use it.</summary>
public static class Crc32
{
    private static readonly uint[] Table = Build();

    /// <summary>Continues a CRC: start from 0xFFFFFFFF and invert the end result.</summary>
    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (var b in data)
        {
            crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    public static uint Of(ReadOnlySpan<byte> data) => ~Update(0xFFFFFFFF, data);

    private static uint[] Build()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var c = i;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[i] = c;
        }

        return table;
    }
}
