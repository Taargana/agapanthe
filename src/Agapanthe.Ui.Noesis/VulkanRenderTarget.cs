using Agapanthe.Graphics;

namespace Agapanthe.Ui.Noesis;

/// <summary>
/// Wraps a <see cref="GpuImage"/> (created with <see cref="ImageUsage.ColorAttachment"/> |
/// <see cref="ImageUsage.Sampled"/>) as a <see cref="global::Noesis.RenderTarget"/>. Verified against
/// the real Noesis/Managed source (Src/Noesis/Core/Src/Core/RenderTarget.cs): the abstract contract is
/// exactly one member, <c>Texture Texture { get; }</c> — the resolve texture Noesis samples back.
/// </summary>
public sealed class VulkanRenderTarget : global::Noesis.RenderTarget
{
    public GpuImage Image { get; }

    /// <summary>Whether this target has been drawn into at least once (tracks the Undefined→ColorAttachment
    /// layout transition and the Clear-vs-Load decision — see <see cref="VulkanRenderDevice.DrawBatch"/>).</summary>
    internal bool EverRendered { get; set; }

    private readonly VulkanTexture _texture;

    public VulkanRenderTarget(GpuImage image)
    {
        Image = image;
        _texture = new VulkanTexture(image);
    }

    public override global::Noesis.Texture Texture => _texture;
}
