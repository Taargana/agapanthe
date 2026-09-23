using Agapanthe.Audio;

namespace Agapanthe.Tests;

/// <summary>
/// Audio-1 — <see cref="WavFormat"/> in isolation: pure, zero OpenAL dependency, fully testable without any
/// audio device (spec D6: the same synthesized-tone generator backs both these tests and the interactive demo).
/// </summary>
public sealed class WavFormatTests
{
    [Fact]
    public void WriteThenRead_RoundTripsExactly()
    {
        var original = WavFormat.SineTone();

        using var stream = new MemoryStream();
        WavFormat.Write(stream, original);
        stream.Position = 0;
        var decoded = WavFormat.Read(stream);

        Assert.Equal(original.SampleRate, decoded.SampleRate);
        Assert.Equal(original.Channels, decoded.Channels);
        Assert.Equal(original.BitsPerSample, decoded.BitsPerSample);
        Assert.Equal(original.Pcm, decoded.Pcm);
    }

    [Fact]
    public void WriteThenRead_RoundTripsStereo24Bit()
    {
        // Confirms the reader/writer isn't secretly mono/16-bit-only despite the demo only using that shape.
        var pcm = new byte[3 * 2 * 6]; // 6 stereo frames, 24-bit (3 bytes/sample)
        new Random(42).NextBytes(pcm);
        var original = new WavFormat.WavData(48_000, Channels: 2, BitsPerSample: 24, pcm);

        using var stream = new MemoryStream();
        WavFormat.Write(stream, original);
        stream.Position = 0;
        var decoded = WavFormat.Read(stream);

        Assert.Equal(original.SampleRate, decoded.SampleRate);
        Assert.Equal(original.Channels, decoded.Channels);
        Assert.Equal(original.BitsPerSample, decoded.BitsPerSample);
        Assert.Equal(original.Pcm, decoded.Pcm);
    }

    [Fact]
    public void Read_RejectsBadMagicBytes()
    {
        var bytes = new byte[12];
        "JUNK"u8.CopyTo(bytes);
        "WAVE"u8.CopyTo(bytes.AsSpan(8));

        using var stream = new MemoryStream(bytes);
        Assert.Throws<AudioException>(() => WavFormat.Read(stream));
    }

    [Fact]
    public void Read_RejectsATruncatedHeader()
    {
        using var stream = new MemoryStream(new byte[4]); // shorter than the 12-byte RIFF header
        Assert.Throws<AudioException>(() => WavFormat.Read(stream));
    }

    [Fact]
    public void Read_RejectsADataChunkLongerThanTheStreamActuallyHas()
    {
        using var stream = new MemoryStream();
        WavFormat.Write(stream, WavFormat.SineTone(seconds: 0.01));
        var bytes = stream.ToArray();

        // Corrupt the 'data' chunk's declared length (the 4 bytes right after its "data" tag) to claim far more
        // than the stream actually holds.
        var dataTagIndex = FindTag(bytes, "data"u8);
        var forged = (byte[])bytes.Clone();
        BitConverter.GetBytes(0x7FFF_FFFF).CopyTo(forged, dataTagIndex + 4);

        using var forgedStream = new MemoryStream(forged);
        Assert.Throws<AudioException>(() => WavFormat.Read(forgedStream));
    }

    private static int FindTag(byte[] bytes, ReadOnlySpan<byte> tag)
    {
        for (var i = 0; i <= bytes.Length - tag.Length; i++)
        {
            if (bytes.AsSpan(i, tag.Length).SequenceEqual(tag))
            {
                return i;
            }
        }

        throw new InvalidOperationException("Tag not found in test fixture bytes.");
    }

    [Fact]
    public void Read_RejectsANonPcmAudioFormat()
    {
        using var stream = new MemoryStream();
        WavFormat.Write(stream, WavFormat.SineTone(seconds: 0.01));
        var bytes = stream.ToArray();

        // The 'fmt ' chunk's audioFormat field sits right after "RIFF"(4)+size(4)+"WAVE"(4)+"fmt "(4)+size(4).
        var audioFormatOffset = 12 + 8;
        var forged = (byte[])bytes.Clone();
        BitConverter.GetBytes((ushort)3).CopyTo(forged, audioFormatOffset); // 3 = IEEE float, not PCM

        using var forgedStream = new MemoryStream(forged);
        Assert.Throws<AudioException>(() => WavFormat.Read(forgedStream));
    }
}
