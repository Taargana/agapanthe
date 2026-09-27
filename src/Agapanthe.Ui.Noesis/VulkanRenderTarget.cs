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

    /// <summary>Whether this target has been drawn into at least once (tracks the one-shot
    /// Undefined→ColorAttachment layout transition — see <see cref="VulkanRenderDevice.DrawBatch"/>).</summary>
    internal bool EverRendered { get; set; }

    /// <summary>Set by <see cref="RequestClear"/>, consumed (reset to false) by the next
    /// <see cref="VulkanRenderDevice.DrawBatch"/> call. Separate from <see cref="EverRendered"/>: that flag is
    /// permanent (drives the one-time layout transition), this one is per-frame (drives the Clear-vs-Load
    /// decision) — a caller (e.g. <c>NoesisUiHost.Tick</c>) calls <see cref="RequestClear"/> once per frame so the
    /// FIRST batch of that frame clears instead of accumulating over the previous frame's content.</summary>
    internal bool PendingClear { get; set; }

    /// <summary>Arms <see cref="PendingClear"/> for this target's next <see cref="VulkanRenderDevice.DrawBatch"/>
    /// call — call once per frame, before rendering, so transparent/animated content does not accumulate across
    /// frames (a bug found live: the vertical slice's demo never surfaced this because its content is solid
    /// opaque red).</summary>
    internal void RequestClear() => PendingClear = true;

    private readonly VulkanTexture _texture;

    public VulkanRenderTarget(GpuImage image)
    {
        Image = image;
        _texture = new VulkanTexture(image);
    }

    public override global::Noesis.Texture Texture => _texture;
}
