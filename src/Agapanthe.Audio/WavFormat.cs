using System.Buffers.Binary;

namespace Agapanthe.Audio;

/// <summary>
/// A minimal PCM `.wav` (RIFF/WAVE) reader/writer — pure, zero OpenAL dependency, fully testable without any
/// audio device (Audio-1 spec: the piece of this domain that's testable headless, mirroring
/// <c>AgModelFormat</c>/<c>AgSceneFormat</c>'s own "reader is pure" discipline). Supports exactly the PCM subset
/// Audio-1 needs (8/16/24/32-bit integer PCM, any channel count/sample rate) — no compressed formats, no
/// extensible `fmt` chunk, no non-PCM audio formats. Both <see cref="Read"/> and <see cref="Write"/> are public:
/// unlike the cooked asset formats, there is no offline cook step here (spec D4) — the same code path both
/// decodes real content and produces the synthesized test/demo tone (spec D6).
/// </summary>
public static class WavFormat
{
    public readonly record struct WavData(int SampleRate, short Channels, short BitsPerSample, byte[] Pcm);

    private const int RiffHeaderBytes = 12; // "RIFF" | chunkSize u32 | "WAVE"
    private const int ChunkHeaderBytes = 8; // chunkId (4) | chunkSize u32
    private const int FmtChunkBytes = 16; // audioFormat u16 | channels u16 | sampleRate u32 | byteRate u32 | blockAlign u16 | bitsPerSample u16
    private const long MaxDataBytes = 512 * 1024 * 1024; // a generous ceiling against a forged chunk size

    /// <summary>
    /// Reads a PCM WAV stream. Every structural expectation — magic bytes, chunk ids, a PCM (not compressed)
    /// format, a declared data length that actually fits in the stream — is checked before it is trusted; a
    /// malformed stream throws <see cref="AudioException"/>, never returns a partially-decoded result.
    /// </summary>
    public static WavData Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        Span<byte> header = stackalloc byte[RiffHeaderBytes];
        ReadExact(stream, header, "RIFF header");

        if (!header[..4].SequenceEqual("RIFF"u8) || !header[8..12].SequenceEqual("WAVE"u8))
        {
            throw new AudioException("Not a RIFF/WAVE stream (bad magic bytes).");
        }

        short? channels = null;
        int? sampleRate = null;
        short? bitsPerSample = null;
        byte[]? pcm = null;

        Span<byte> chunkHeader = stackalloc byte[ChunkHeaderBytes];
        Span<byte> fmt = stackalloc byte[FmtChunkBytes]; // hoisted out of the loop below (CA2014)
        while (pcm is null)
        {
            var read = stream.Read(chunkHeader);
            if (read == 0)
            {
                break; // clean EOF between chunks — handled below by the "found everything" check
            }

            if (read != ChunkHeaderBytes)
            {
                throw new AudioException("Truncated chunk header.");
            }

            var chunkId = chunkHeader[..4];
            var chunkSize = BinaryPrimitives.ReadUInt32LittleEndian(chunkHeader[4..8]);

            if (chunkId.SequenceEqual("fmt "u8))
            {
                if (chunkSize < FmtChunkBytes)
                {
                    throw new AudioException($"'fmt ' chunk too small ({chunkSize} bytes).");
                }

                ReadExact(stream, fmt, "'fmt ' chunk");
                SkipPadding(stream, chunkSize - FmtChunkBytes, "'fmt ' chunk trailer");

                var audioFormat = BinaryPrimitives.ReadUInt16LittleEndian(fmt[0..2]);
                if (audioFormat != 1)
                {
                    throw new AudioException($"Unsupported WAV audio format {audioFormat} — only PCM (1) is supported.");
                }

                channels = BinaryPrimitives.ReadInt16LittleEndian(fmt[2..4]);
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(fmt[4..8]);
                bitsPerSample = BinaryPrimitives.ReadInt16LittleEndian(fmt[14..16]);

                if (channels <= 0)
                {
                    throw new AudioException($"Invalid channel count {channels}.");
                }

                if (sampleRate <= 0)
                {
                    throw new AudioException($"Invalid sample rate {sampleRate}.");
                }

                if (bitsPerSample is not (8 or 16 or 24 or 32))
                {
                    throw new AudioException($"Unsupported bits-per-sample {bitsPerSample} — only 8/16/24/32 are supported.");
                }
            }
            else if (chunkId.SequenceEqual("data"u8))
            {
                if (chunkSize > MaxDataBytes)
                {
                    throw new AudioException($"'data' chunk claims {chunkSize} bytes, exceeding the {MaxDataBytes} byte ceiling.");
                }

                // Bound against what the stream actually has left BEFORE allocating (audit finding, csharp-
                // lowlevel — a 44-byte forged file with a chunkSize near uint.MaxValue previously allocated up to
                // 512 MiB before ReadExact ever proved the bytes existed). Same posture as Contenu-2/3b's
                // ReadCount hardening.
                if (stream.CanSeek && chunkSize > (ulong)Math.Max(0, stream.Length - stream.Position))
                {
                    throw new AudioException(
                        $"'data' chunk claims {chunkSize} bytes, exceeding the {stream.Length - stream.Position} bytes actually remaining in the stream.");
                }

                pcm = new byte[chunkSize];
                ReadExact(stream, pcm, "'data' chunk");
                SkipPadding(stream, chunkSize % 2, "'data' chunk pad byte"); // RIFF chunks are word-aligned
            }
            else
            {
                // long, not uint: chunkSize + (chunkSize % 2) wrapped to 0 at chunkSize == uint.MaxValue in the
                // original uint arithmetic, turning a forged chunk into a silent no-op skip instead of a loud
                // failure (audit finding, csharp-lowlevel).
                SkipPadding(stream, (long)chunkSize + (chunkSize % 2), $"unknown chunk '{System.Text.Encoding.ASCII.GetString(chunkId)}'");
            }
        }

        if (channels is null || sampleRate is null || bitsPerSample is null)
        {
            throw new AudioException("Stream ended before a 'fmt ' chunk was found.");
        }

        if (pcm is null)
        {
            throw new AudioException("Stream ended before a 'data' chunk was found.");
        }

        return new WavData(sampleRate.Value, channels.Value, bitsPerSample.Value, pcm);
    }

    /// <summary>Writes a minimal, canonical PCM WAV stream — exactly the two chunks (`fmt `, `data`) <see cref="Read"/>
    /// requires, nothing extensible. Deterministic: the same <see cref="WavData"/> always yields the same bytes.</summary>
    public static void Write(Stream stream, in WavData data)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (data.Channels <= 0)
        {
            throw new ArgumentException("Channel count must be positive.", nameof(data));
        }

        if (data.SampleRate <= 0)
        {
            throw new ArgumentException("Sample rate must be positive.", nameof(data));
        }

        if (data.BitsPerSample is not (8 or 16 or 24 or 32))
        {
            // Matches Read's accepted set exactly — anything else (e.g. 12-bit) makes blockAlign's integer
            // division silently wrong rather than failing loudly (audit finding, csharp-lowlevel).
            throw new ArgumentException(
                $"Unsupported bits-per-sample {data.BitsPerSample} — only 8/16/24/32 are supported.", nameof(data));
        }

        var blockAlign = (short)(data.Channels * (data.BitsPerSample / 8));
        var byteRate = data.SampleRate * blockAlign;
        var dataLen = (uint)data.Pcm.Length;
        var riffLen = 4u + (uint)(ChunkHeaderBytes + FmtChunkBytes) + (uint)(ChunkHeaderBytes) + dataLen;

        Span<byte> buffer = stackalloc byte[RiffHeaderBytes];
        "RIFF"u8.CopyTo(buffer);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer[4..8], riffLen);
        "WAVE"u8.CopyTo(buffer[8..]);
        stream.Write(buffer);

        Span<byte> fmtChunk = stackalloc byte[ChunkHeaderBytes + FmtChunkBytes];
        "fmt "u8.CopyTo(fmtChunk);
        BinaryPrimitives.WriteUInt32LittleEndian(fmtChunk[4..8], FmtChunkBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(fmtChunk[8..10], 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(fmtChunk[10..12], data.Channels);
        BinaryPrimitives.WriteInt32LittleEndian(fmtChunk[12..16], data.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(fmtChunk[16..20], byteRate);
        BinaryPrimitives.WriteInt16LittleEndian(fmtChunk[20..22], blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(fmtChunk[22..24], data.BitsPerSample);
        stream.Write(fmtChunk);

        Span<byte> dataHeader = stackalloc byte[ChunkHeaderBytes];
        "data"u8.CopyTo(dataHeader);
        BinaryPrimitives.WriteUInt32LittleEndian(dataHeader[4..8], dataLen);
        stream.Write(dataHeader);
        stream.Write(data.Pcm);
    }

    /// <summary>
    /// A synthesized sine tone, mono 16-bit PCM — spec D6's "the same generator backs both the unit tests and
    /// the interactive demo," which an audit found was actually duplicated three ways (<c>AppHost</c>,
    /// <c>WavFormatTests</c>, <c>AudioDeviceTests</c>). This is now the single implementation all three use.
    /// </summary>
    public static WavData SineTone(int sampleRate = 44_100, double frequencyHz = 440, double seconds = 0.15)
    {
        var sampleCount = (int)(sampleRate * seconds);
        var pcm = new byte[sampleCount * 2];
        for (var i = 0; i < sampleCount; i++)
        {
            var t = i / (double)sampleRate;
            var sample = (short)(Math.Sin(2 * Math.PI * frequencyHz * t) * short.MaxValue * 0.5);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2), sample);
        }

        return new WavData(sampleRate, Channels: 1, BitsPerSample: 16, pcm);
    }

    private static void ReadExact(Stream stream, Span<byte> destination, string what)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = stream.Read(destination[total..]);
            if (read == 0)
            {
                throw new AudioException($"Stream ended while reading {what} (expected {destination.Length} bytes, got {total}).");
            }

            total += read;
        }
    }

    private static void SkipPadding(Stream stream, long count, string what)
    {
        if (count <= 0)
        {
            return;
        }

        if (stream.CanSeek)
        {
            var target = stream.Position + count;
            stream.Seek(count, SeekOrigin.Current);
            if (stream.Position != target)
            {
                throw new AudioException($"Stream ended while skipping {what}.");
            }

            return;
        }

        Span<byte> scratch = stackalloc byte[(int)Math.Min(count, 4096)];
        var remaining = count;
        while (remaining > 0)
        {
            var chunk = scratch[..(int)Math.Min(remaining, scratch.Length)];
            ReadExact(stream, chunk, what);
            remaining -= chunk.Length;
        }
    }
}
