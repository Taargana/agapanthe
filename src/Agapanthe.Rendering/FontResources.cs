using Agapanthe.Assets.Font;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// A baked font's GPU side: the SDF atlas as an image plus the sampler it is read through (UI-1). Owned by the
/// <see cref="Renderer"/>, disposed with it.
/// <para>
/// It deliberately does <b>not</b> go through <see cref="ResourceRegistry"/>: that registry is typed on
/// <c>ModelAsset</c> and hands out positional handles, so routing a font through it would add to the asset-identity
/// debt for no benefit. This follows the <c>IblGenerator</c>/<c>IblMaps</c> shape instead — a small owned bundle.
/// </para>
/// </summary>
internal sealed class FontResources : IDisposable
{
    private bool _disposed;

    public FontResources(GraphicsDevice device, GpuUploader uploader, FontAsset font)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(font);

        Font = font;

        // R8Unorm and EXACTLY ONE MIP LEVEL. Both matter:
        //   - the atlas is a distance field, i.e. data, so it must not be sRGB-decoded on sample;
        //   - mips would average distances from unrelated glyphs and smear the field into mush. The whole point of
        //     an SDF is that ONE level stays sharp at every size, so mipping is not merely unnecessary, it is wrong.
        Atlas = new GpuImage(
            device,
            (uint)font.AtlasWidth,
            (uint)font.AtlasHeight,
            PixelFormat.R8Unorm,
            ImageUsage.Sampled | ImageUsage.TransferDst,
            mipLevels: 1);

        try
        {
            uploader.Upload<byte>(Atlas, font.AtlasPixels);

            // Linear filtering IS the antialiasing: interpolating the distance field between texels is what gives a
            // smooth edge at any scale. ClampToEdge so a glyph at the atlas border cannot wrap and sample another.
            // No mips, no anisotropy — there is one level and the quads are axis-aligned.
            Sampler = new Sampler(device, new SamplerDesc(
                Filter: SamplerFilter.Linear,
                MipFilter: SamplerFilter.Nearest,
                AddressMode: SamplerAddressMode.ClampToEdge));
        }
        catch
        {
            Atlas.Dispose();
            throw;
        }
    }

    /// <summary>The CPU-side metrics, needed to lay text out.</summary>
    public FontAsset Font { get; }

    public GpuImage Atlas { get; }

    public Sampler Sampler { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Sampler.Dispose();
        Atlas.Dispose();
    }
}
