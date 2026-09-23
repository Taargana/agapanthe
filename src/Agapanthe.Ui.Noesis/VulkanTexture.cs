using Agapanthe.Graphics;

namespace Agapanthe.Ui.Noesis;

/// <summary>
/// Wraps a <see cref="GpuImage"/> as a <see cref="global::Noesis.Texture"/>. Verified against the real
/// Noesis/Managed source (Src/Noesis/Core/Src/Core/Texture.Core.cs): the abstract contract is exactly
/// these 5 read-only properties, nothing else — <c>Texture</c> is a genuine subclassable C# abstract
/// class (not a native-only interface).
/// </summary>
internal sealed class VulkanTexture : global::Noesis.Texture
{
    public GpuImage Image { get; }

    public VulkanTexture(GpuImage image)
    {
        Image = image;
    }

    public override uint Width => Image.Width;

    public override uint Height => Image.Height;

    // The vertical slice never creates a mipped texture (no images/patterns in scope).
    public override bool HasMipMaps => Image.MipLevels > 1;

    // Vulkan's texture-space V origin matches Noesis's expectation (top-left) — no flip needed, unlike
    // the OpenGL backends this comment in the source explicitly calls out.
    public override bool IsInverted => false;

    // Conservative default for a render-target texture in this slice (solid fill only, always opaque
    // background in the current demo) — revisit once transparent content is in scope.
    public override bool HasAlpha => true;
}
