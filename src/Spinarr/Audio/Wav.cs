using System.Buffers.Binary;
using System.Text;
using Spinarr.Rip;

namespace Spinarr.Audio;

/// <summary>Plain WAV files of CD audio: 44.1 kHz, 16-bit, stereo.</summary>
public static class Wav
{
    public const int HeaderSize = 44;

    /// <summary>The canonical 44-byte header for <paramref name="dataBytes"/> of CD audio.</summary>
    public static byte[] Header(long dataBytes)
    {
        var header = new byte[HeaderSize];
        var span = header.AsSpan();
        Encoding.ASCII.GetBytes("RIFF").CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)(36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 1); // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 2); // stereo
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], 44100);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], 44100 * 4);
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], 4);
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 16);
        Encoding.ASCII.GetBytes("data").CopyTo(span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)dataBytes);
        return header;
    }

    /// <summary>Where the audio of a WAV file starts and how long it is, from its chunks.</summary>
    public static (long Offset, long Length) DataChunk(Stream stream)
    {
        Span<byte> head = stackalloc byte[12];
        stream.Position = 0;
        stream.ReadExactly(head);
        if (!head[..4].SequenceEqual("RIFF"u8) || !head[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("not a WAV file");
        }

        Span<byte> chunk = stackalloc byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            stream.ReadExactly(chunk);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            if (chunk[..4].SequenceEqual("data"u8))
            {
                // cd-paranoia writes the size it planned; trust the file when it is shorter.
                return (stream.Position, Math.Min(size, stream.Length - stream.Position));
            }

            stream.Position += size + (size & 1);
        }

        throw new InvalidDataException("no audio in the WAV file");
    }

    /// <summary>
    /// Takes what cd-paranoia read and writes the track as it really is: shifted by the read
    /// offset, with silence where the drive can't read (before the disc's start, past its end).
    /// Every sample goes through <paramref name="digester"/>; <paramref name="output"/> can be null for a test pass.
    /// </summary>
    public static void Correct(string rawWav, ReadPlan plan, TrackDigester digester, string? output)
    {
        using var input = File.OpenRead(rawWav);
        var (dataOffset, dataLength) = DataChunk(input);
        var expected = (long)plan.Frames * Disc.Cd.BytesPerFrame;
        if (dataLength != expected)
        {
            throw new InvalidDataException($"cd-paranoia read {dataLength} bytes, {expected} were expected");
        }

        using var writer = output == null ? null : new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        writer?.Write(Header(plan.Samples * Disc.Cd.BytesPerSample));

        var buffer = new byte[1 << 16];
        WriteSilence(plan.PadBefore, buffer, digester, writer);

        input.Position = dataOffset + plan.SkipSamples * Disc.Cd.BytesPerSample;
        var remaining = (plan.Samples - plan.PadBefore - plan.PadAfter) * Disc.Cd.BytesPerSample;
        while (remaining > 0)
        {
            // Whole samples each time: the AccurateRip sum works on 4-byte samples.
            var read = (int)Math.Min(buffer.Length, remaining);
            if (input.ReadAtLeast(buffer.AsSpan(0, read), read, throwOnEndOfStream: false) < read)
            {
                throw new InvalidDataException("the read ended early");
            }

            digester.Append(buffer.AsSpan(0, read));
            writer?.Write(buffer, 0, read);
            remaining -= read;
        }

        WriteSilence(plan.PadAfter, buffer, digester, writer);
    }

    /// <summary>All of a WAV file's audio as stereo sample words: for trying many offsets on one read.</summary>
    public static uint[] ReadSamples(string path)
    {
        using var input = File.OpenRead(path);
        var (offset, length) = DataChunk(input);
        var bytes = new byte[length - length % 4];
        input.Position = offset;
        input.ReadExactly(bytes);
        var samples = new uint[bytes.Length / 4];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4, 4));
        }

        return samples;
    }

    private static void WriteSilence(long samples, byte[] buffer, TrackDigester digester, Stream? writer)
    {
        if (samples <= 0)
        {
            return;
        }

        Array.Clear(buffer);
        var remaining = samples * Disc.Cd.BytesPerSample;
        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            digester.Append(buffer.AsSpan(0, count));
            writer?.Write(buffer, 0, count);
            remaining -= count;
        }
    }
}
