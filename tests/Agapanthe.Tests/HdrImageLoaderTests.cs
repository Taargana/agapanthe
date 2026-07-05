using System.Text;
using Agapanthe.Assets;
using Agapanthe.Assets.Model;

namespace Agapanthe.Tests;

public sealed class HdrImageLoaderTests
{
    // studio_small_1k.hdr is a Poly Haven CC0 equirectangular Radiance HDR. Its resolution line
    // reads "-Y 512 +X 1024", i.e. an equirect 2:1 environment map (verified by decoding it).
    private const int HdrWidth = 1024;
    private const int HdrHeight = 512;

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Load_DecodesFixtureToRgbaFloat_WithExpectedDimensions()
    {
        HdrImageAsset image = HdrImageLoader.Load(FixturePath("studio_small_1k.hdr"));

        Assert.Equal(HdrWidth, image.Height * 2); // equirect is 2:1
        Assert.Equal(HdrWidth, image.Width);
        Assert.Equal(HdrHeight, image.Height);
        // Forced 4 channels: the buffer is exactly W * H * 4 floats, tightly packed.
        Assert.Equal(image.Width * image.Height * 4, image.RgbaPixels.Length);
    }

    [Fact]
    public void Load_ContainsTrueHdrValues_Above1()
    {
        // The whole point of HDR: at least one component carries radiance above the [0,1] LDR range.
        HdrImageAsset image = HdrImageLoader.Load(FixturePath("studio_small_1k.hdr"));

        float max = 0f;
        foreach (float v in image.RgbaPixels)
        {
            if (v > max)
            {
                max = v;
            }
        }

        Assert.True(max > 1.0f, $"expected an HDR component > 1.0, but the brightest was {max}");
    }

    [Fact]
    public void LoadFromBytes_MatchesLoad_ForSameBytes()
    {
        string path = FixturePath("studio_small_1k.hdr");
        byte[] bytes = File.ReadAllBytes(path);

        HdrImageAsset fromFile = HdrImageLoader.Load(path);
        HdrImageAsset fromBytes = HdrImageLoader.LoadFromBytes(bytes, "inline.hdr");

        Assert.Equal(fromFile.Width, fromBytes.Width);
        Assert.Equal(fromFile.Height, fromBytes.Height);
        Assert.Equal(fromFile.RgbaPixels, fromBytes.RgbaPixels);
    }

    [Fact]
    public void LoadFromBytes_HonorsSpanSlice()
    {
        // Simulate the HDR bytes embedded in a larger buffer: decoding a slice must match the file.
        byte[] file = File.ReadAllBytes(FixturePath("studio_small_1k.hdr"));
        byte[] padded = new byte[file.Length + 32];
        file.CopyTo(padded, 8);
        ReadOnlySpan<byte> slice = padded.AsSpan(8, file.Length);

        HdrImageAsset image = HdrImageLoader.LoadFromBytes(slice, "embedded.hdr");

        Assert.Equal(HdrWidth, image.Width);
        Assert.Equal(HdrHeight, image.Height);
    }

    [Fact]
    public void Load_MissingFile_ThrowsAssetExceptionWithPath()
    {
        string missing = FixturePath("does-not-exist.hdr");

        var ex = Assert.Throws<AssetException>(() => HdrImageLoader.Load(missing));
        Assert.Equal(missing, ex.AssetPath);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromBytes_CorruptData_ThrowsAssetExceptionWithSourceName()
    {
        // Not a Radiance file at all: random bytes with no valid signature. (A signature-valid
        // header with a truncated body is NOT reliably detectable: stb pads missing scanlines
        // silently — the loader adds a pixel-count check for the cases it can catch.)
        byte[] corrupt = [0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22, 0x33];

        var ex = Assert.Throws<AssetException>(
            () => HdrImageLoader.LoadFromBytes(corrupt, "corrupt.hdr"));
        Assert.Equal("corrupt.hdr", ex.AssetPath);
        Assert.Contains("decode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromBytes_EmptyData_ThrowsAssetException()
    {
        var ex = Assert.Throws<AssetException>(
            () => HdrImageLoader.LoadFromBytes(ReadOnlySpan<byte>.Empty, "empty.hdr"));
        Assert.Equal("empty.hdr", ex.AssetPath);
    }
}
