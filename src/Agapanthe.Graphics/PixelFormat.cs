using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// Engine-facing pixel/vertex-attribute formats. Keeps the Vulkan <see cref="Format"/>
/// enum out of public signatures (spec §3.1) while letting other modules choose and
/// compare formats (swapchain color, depth, HDR targets, vertex attributes).
/// </summary>
public enum PixelFormat
{
    Undefined = 0,

    // Color / texture
    Bgra8Srgb,
    Rgba8Srgb,
    Rgba8Unorm,

    // HDR render target (M5)
    Rgba16Sfloat,

    // Two-channel half-float (M7 IBL BRDF integration LUT: RG = scale/bias for the split-sum approximation)
    Rg16Sfloat,

    // Depth
    D32Sfloat,

    // Vertex attribute layouts
    R32G32Sfloat,
    R32G32B32Sfloat,
    R32G32B32A32Sfloat,
}

internal static class PixelFormatExtensions
{
    public static Format ToVk(this PixelFormat format) => format switch
    {
        PixelFormat.Bgra8Srgb => Format.B8G8R8A8Srgb,
        PixelFormat.Rgba8Srgb => Format.R8G8B8A8Srgb,
        PixelFormat.Rgba8Unorm => Format.R8G8B8A8Unorm,
        PixelFormat.Rgba16Sfloat => Format.R16G16B16A16Sfloat,
        PixelFormat.Rg16Sfloat => Format.R16G16Sfloat,
        PixelFormat.D32Sfloat => Format.D32Sfloat,
        PixelFormat.R32G32Sfloat => Format.R32G32Sfloat,
        PixelFormat.R32G32B32Sfloat => Format.R32G32B32Sfloat,
        PixelFormat.R32G32B32A32Sfloat => Format.R32G32B32A32Sfloat,
        PixelFormat.Undefined => Format.Undefined,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unmapped pixel format."),
    };

    public static PixelFormat FromVk(Format format) => format switch
    {
        Format.B8G8R8A8Srgb => PixelFormat.Bgra8Srgb,
        Format.R8G8B8A8Srgb => PixelFormat.Rgba8Srgb,
        Format.R8G8B8A8Unorm => PixelFormat.Rgba8Unorm,
        Format.R16G16B16A16Sfloat => PixelFormat.Rgba16Sfloat,
        Format.R16G16Sfloat => PixelFormat.Rg16Sfloat,
        Format.D32Sfloat => PixelFormat.D32Sfloat,
        Format.R32G32Sfloat => PixelFormat.R32G32Sfloat,
        Format.R32G32B32Sfloat => PixelFormat.R32G32B32Sfloat,
        Format.R32G32B32A32Sfloat => PixelFormat.R32G32B32A32Sfloat,
        _ => PixelFormat.Undefined,
    };
}
