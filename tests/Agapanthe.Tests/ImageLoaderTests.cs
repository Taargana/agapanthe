using Agapanthe.Assets;
using Agapanthe.Assets.Model;

namespace Agapanthe.Tests;

public sealed class ImageLoaderTests
{
    // CesiumLogoFlat.png is a 256x256 8-bit paletted PNG shipped as a Khronos sample texture.
    private const int LogoWidth = 256;
    private const int LogoHeight = 256;

    private static string FixturePath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void Load_DecodesFixtureToRgba8_WithExpectedDimensions()
    {
        ImageAsset image = ImageLoader.Load(FixturePath("CesiumLogoFlat.png"), isSrgb: true);

        Assert.Equal(LogoWidth, image.Width);
        Assert.Equal(LogoHeight, image.Height);
        // Forced 4 channels: the buffer is exactly W * H * 4 bytes, tightly packed.
        Assert.Equal(image.Width * image.Height * 4, image.Rgba8Pixels.Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Load_RoundTripsIsSrgbFlag(bool isSrgb)
    {
        ImageAsset image = ImageLoader.Load(FixturePath("CesiumLogoFlat.png"), isSrgb);

        // The flag is carried through verbatim; the pixel bytes never change with it.
        Assert.Equal(isSrgb, image.IsSrgb);
    }

    [Fact]
    public void LoadFromBytes_MatchesLoad_ForSameBytes()
    {
        string path = FixturePath("CesiumLogoFlat.png");
        byte[] bytes = File.ReadAllBytes(path);

        ImageAsset fromFile = ImageLoader.Load(path, isSrgb: false);
        ImageAsset fromBytes = ImageLoader.LoadFromBytes(bytes, isSrgb: false, "inline.png");

        Assert.Equal(fromFile.Width, fromBytes.Width);
        Assert.Equal(fromFile.Height, fromBytes.Height);
        Assert.Equal(fromFile.IsSrgb, fromBytes.IsSrgb);
        Assert.Equal(fromFile.Rgba8Pixels, fromBytes.Rgba8Pixels);
    }

    [Fact]
    public void LoadFromBytes_HonorsSpanSlice()
    {
        // Simulate an image embedded in a larger buffer (e.g. a GLB chunk): decoding a slice
        // must produce the same result as decoding the standalone file.
        byte[] file = File.ReadAllBytes(FixturePath("CesiumLogoFlat.png"));
        byte[] padded = new byte[file.Length + 32];
        file.CopyTo(padded, 8);
        ReadOnlySpan<byte> slice = padded.AsSpan(8, file.Length);

        ImageAsset image = ImageLoader.LoadFromBytes(slice, isSrgb: true, "glb#image");

        Assert.Equal(LogoWidth, image.Width);
        Assert.Equal(LogoHeight, image.Height);
    }

    [Fact]
    public void Load_MissingFile_ThrowsAssetExceptionWithPath()
    {
        string missing = FixturePath("does-not-exist.png");

        var ex = Assert.Throws<AssetException>(() => ImageLoader.Load(missing, isSrgb: true));
        Assert.Equal(missing, ex.AssetPath);
        Assert.Contains("not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromBytes_CorruptData_ThrowsAssetExceptionWithSourceName()
    {
        // A valid PNG signature followed by garbage: passes the sniff, fails to decode.
        byte[] corrupt = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xDE, 0xAD, 0xBE, 0xEF];

        var ex = Assert.Throws<AssetException>(
            () => ImageLoader.LoadFromBytes(corrupt, isSrgb: false, "corrupt.png"));
        Assert.Equal("corrupt.png", ex.AssetPath);
        Assert.Contains("decode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadFromBytes_EmptyData_ThrowsAssetException()
    {
        var ex = Assert.Throws<AssetException>(
            () => ImageLoader.LoadFromBytes(ReadOnlySpan<byte>.Empty, isSrgb: false, "empty.png"));
        Assert.Equal("empty.png", ex.AssetPath);
    }
}
