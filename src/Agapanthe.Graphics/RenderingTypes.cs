using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// Engine-facing image layout states (spec §3.3). Keeps the Vulkan <see cref="ImageLayout"/> enum out of
/// public signatures: callers reason in terms of how an image is used, and <see cref="CommandList"/> maps
/// each state to the exact (layout, pipeline stage, access) triple the transition needs. The Undefined
/// source stage is aspect-aware (fragment-test stages for depth, top-of-pipe for color) so a transition
/// reproduces the same barrier the frame loop issued before this API existed.
/// </summary>
public enum ImageLayoutState
{
    /// <summary>Contents are discarded (valid only as a transition source).</summary>
    Undefined,

    /// <summary>Bound as a color render target (writes at the color-attachment-output stage).</summary>
    ColorAttachment,

    /// <summary>Bound as a depth attachment (writes at the early/late fragment-test stages).</summary>
    DepthAttachment,

    /// <summary>Sampled from a shader through a combined image sampler (fragment stage, read).</summary>
    ShaderReadOnly,

    /// <summary>
    /// Sampled from a <b>compute</b> shader through a combined image sampler (compute stage, read). Same
    /// Vulkan layout as <see cref="ShaderReadOnly"/> (ShaderReadOnlyOptimal), but the barrier's stage/access
    /// scope is compute, so a <c>General→ShaderReadOnlyCompute</c> transition orders a storage-image write in
    /// one IBL kernel before a sampled read in the next one (spec §3.6): the plain <see cref="ShaderReadOnly"/>
    /// maps to the fragment stage and would not synchronize a following compute read.
    /// </summary>
    ShaderReadOnlyCompute,

    /// <summary>
    /// General layout for a storage image read/written by a compute kernel (compute stage, read+write).
    /// Also valid for sampled access, so IBL kernels keep intermediates here across dispatches (spec §3.6).
    /// </summary>
    General,

    /// <summary>Source of a transfer/blit.</summary>
    TransferSrc,

    /// <summary>Destination of a transfer/copy.</summary>
    TransferDst,

    /// <summary>Ready for presentation by the swapchain.</summary>
    PresentSrc,
}

/// <summary>How an attachment's prior contents are treated at <see cref="CommandList.BeginRendering"/>.</summary>
public enum AttachmentLoadAction
{
    /// <summary>Clear to the attachment's clear value before rendering.</summary>
    Clear,

    /// <summary>Preserve and load the existing contents.</summary>
    Load,

    /// <summary>Contents are undefined; skip the load (cheapest).</summary>
    DontCare,
}

/// <summary>
/// A render target reference decoupled from ownership: wraps a view, its image handle and the view aspect
/// so both an owned <see cref="GpuImage"/> and a swapchain image can be attached or transitioned through the
/// same code path. Construct one publicly from a <see cref="GpuImage"/>; the swapchain builds them internally.
/// </summary>
public readonly struct RenderTargetView
{
    internal RenderTargetView(ImageView view, Image image, ImageAspectFlags aspect)
    {
        View = view;
        Image = image;
        Aspect = aspect;
    }

    /// <summary>References the color/depth view of <paramref name="image"/> (aspect derived from its usage).</summary>
    public RenderTargetView(GpuImage image)
        : this(
            (image ?? throw new ArgumentNullException(nameof(image))).View,
            image.Handle,
            image.Aspect)
    {
    }

    internal ImageView View { get; }

    internal Image Image { get; }

    internal ImageAspectFlags Aspect { get; }
}

/// <summary>A color attachment for <see cref="CommandList.BeginRendering"/>: target, load action and clear color.</summary>
public readonly struct ColorAttachmentInfo
{
    /// <summary>The color render target.</summary>
    public RenderTargetView Target { get; init; }

    /// <summary>How the prior contents are treated (default <see cref="AttachmentLoadAction.Clear"/>).</summary>
    public AttachmentLoadAction LoadOp { get; init; }

    /// <summary>Clear color used when <see cref="LoadOp"/> is <see cref="AttachmentLoadAction.Clear"/> (RGBA, linear).</summary>
    public (float R, float G, float B, float A) ClearColor { get; init; }
}

/// <summary>A depth attachment for <see cref="CommandList.BeginRendering"/>: target, load action and clear depth.</summary>
public readonly struct DepthAttachmentInfo
{
    /// <summary>The depth render target.</summary>
    public RenderTargetView Target { get; init; }

    /// <summary>How the prior contents are treated (default <see cref="AttachmentLoadAction.Clear"/>).</summary>
    public AttachmentLoadAction LoadOp { get; init; }

    /// <summary>Clear depth used when <see cref="LoadOp"/> is <see cref="AttachmentLoadAction.Clear"/> ([0,1], Vulkan reversed-free).</summary>
    public float ClearDepth { get; init; }

    /// <summary>
    /// Whether the written depth survives the pass. Default <c>false</c> (per-frame scratch depth,
    /// cheapest); a shadow-map pass sets <c>true</c> because the result is sampled afterwards.
    /// </summary>
    public bool Store { get; init; }
}

/// <summary>
/// The attachment set for one dynamic-rendering scope: an optional color target, an optional depth target,
/// and the render-area extent. Passed by <c>in</c> to <see cref="CommandList.BeginRendering"/>. A depth-only
/// pass (shadow map) leaves <see cref="Color"/> <c>null</c>.
/// </summary>
public readonly struct RenderingAttachments
{
    /// <summary>The color attachment, or <c>null</c> for a depth-only pass (shadow map).</summary>
    public ColorAttachmentInfo? Color { get; init; }

    /// <summary>The depth attachment, or <c>null</c> for a color-only pass.</summary>
    public DepthAttachmentInfo? Depth { get; init; }

    /// <summary>Render-area width in pixels.</summary>
    public uint Width { get; init; }

    /// <summary>Render-area height in pixels.</summary>
    public uint Height { get; init; }
}

/// <summary>
/// The acquired swapchain image handed to the per-frame draw callback: a <see cref="RenderTargetView"/> for
/// the current image plus its extent. The callback opens its own rendering scope against this target; the
/// frame loop owns only the surrounding Undefined→ColorAttachment and ColorAttachment→PresentSrc transitions.
/// </summary>
public readonly struct SwapchainTarget
{
    internal SwapchainTarget(RenderTargetView view, uint width, uint height)
    {
        View = view;
        Width = width;
        Height = height;
    }

    /// <summary>The current swapchain image as a render target.</summary>
    public RenderTargetView View { get; }

    /// <summary>Swapchain image width in pixels.</summary>
    public uint Width { get; }

    /// <summary>Swapchain image height in pixels.</summary>
    public uint Height { get; }
}
