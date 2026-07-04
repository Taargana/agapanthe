using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Graphics.Memory;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// A device-local image with a view covering all its mip levels, backed by a suballocation from the
/// device <see cref="GpuAllocator"/> (spec §3.5). Usage is composable (<see cref="ImageUsage"/>) so the
/// same type serves depth attachments, color targets and sampled textures; the view aspect is derived
/// (Depth for <see cref="ImageUsage.DepthAttachment"/>, Color otherwise).
/// <para>
/// Disposal is <b>deferred by default</b> (<see cref="Dispose"/> → device DeletionQueue): the destroy
/// runs once the frame that used the image leaves flight. Swapchain-sized attachments (the depth image)
/// are recreated only after <c>vkDeviceWaitIdle</c>, so they use <see cref="DestroyImmediately"/> to skip
/// the queue. <c>VkDeviceMemory</c> is <i>not</i> tracked here — the allocator counts it per backing
/// block; this type only registers its <c>VkImage</c> and <c>VkImageView</c>.
/// </para>
/// </summary>
public sealed unsafe class GpuImage : IDisposable
{
    private readonly GraphicsDevice _device;
    private Image _image;
    private ImageView _view;
    private GpuAllocation _allocation;
    private bool _hasAllocation;
    private readonly ImageAspectFlags _aspect;
    private bool _disposed;

    /// <summary>
    /// Creates a 2D image of <paramref name="width"/>×<paramref name="height"/> in
    /// <paramref name="format"/> with <paramref name="mipLevels"/> mip levels, device-local memory, and
    /// a view spanning <c>[0, mipLevels)</c>. <paramref name="usage"/> drives the Vulkan usage flags and
    /// the view aspect. Memory comes from the device allocator (render targets get a dedicated block when
    /// large enough — decided by the free-list size threshold, nothing special here).
    /// </summary>
    /// <exception cref="GraphicsException"><paramref name="usage"/> is empty, or a dimension/mip count is zero.</exception>
    public GpuImage(GraphicsDevice device, uint width, uint height, PixelFormat format, ImageUsage usage, uint mipLevels = 1)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (usage == ImageUsage.None)
        {
            throw new GraphicsException("ImageUsage must specify at least one usage.");
        }

        if (width == 0 || height == 0 || mipLevels == 0)
        {
            throw new GraphicsException("GpuImage width, height and mipLevels must all be non-zero.");
        }

        _device = device;
        Width = width;
        Height = height;
        MipLevels = mipLevels;
        Format = format;
        Usage = usage;
        // DepthAttachment implies the depth aspect; every other usage is a color image.
        _aspect = (usage & ImageUsage.DepthAttachment) != 0 ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
        var vk = device.Api;

        try
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = format.ToVk(),
                Extent = new Extent3D(width, height, 1),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ToVkUsage(usage),
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Image image;
            VkCheck.ThrowIfFailed(vk.CreateImage(device.Device, &imageInfo, null, &image), "vkCreateImage");
            _image = image;
            ResourceTracker.Register("VkImage");

            // Device-local memory via the allocator. The free-list picks a dedicated block for oversized
            // requests (large render targets) on its own; VkDeviceMemory is counted per block there.
            vk.GetImageMemoryRequirements(device.Device, _image, out var requirements);
            var reqInfo = new MemoryRequirementsInfo(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits);
            _allocation = device.Allocator.Allocate(in reqInfo, MemoryDomain.DeviceLocal);
            _hasAllocation = true;
            VkCheck.ThrowIfFailed(
                vk.BindImageMemory(device.Device, _image, _allocation.DeviceMemory, _allocation.Offset),
                "vkBindImageMemory");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _image,
                ViewType = ImageViewType.Type2D,
                Format = format.ToVk(),
                SubresourceRange = new ImageSubresourceRange(_aspect, 0, mipLevels, 0, 1),
            };
            ImageView view;
            VkCheck.ThrowIfFailed(vk.CreateImageView(device.Device, &viewInfo, null, &view), "vkCreateImageView");
            _view = view;
            ResourceTracker.Register("VkImageView");
        }
        catch
        {
            DestroyHandles(_device, _image, _view, in _allocation, _hasAllocation);
            _image = default;
            _view = default;
            _hasAllocation = false;
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~GpuImage()
    {
        // Only report when a native handle was actually acquired; ctor argument-validation
        // exceptions reach the finalizer with nothing registered (audit M2, finding 1). The
        // allocation cannot outlive its image handle, so checking the handles covers it too.
        if (_image.Handle != 0 || _view.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GpuImage));
        }
    }

    /// <summary>Image width in texels.</summary>
    public uint Width { get; }

    /// <summary>Image height in texels.</summary>
    public uint Height { get; }

    /// <summary>Number of mip levels; the view covers <c>[0, MipLevels)</c>.</summary>
    public uint MipLevels { get; }

    /// <summary>The pixel format the image and its view were created with.</summary>
    public PixelFormat Format { get; }

    /// <summary>The composable usage the image was created with (drives usage flags and view aspect).</summary>
    public ImageUsage Usage { get; }

    /// <summary>Full mip chain length for an image of size <paramref name="width"/>×<paramref name="height"/>:
    /// <c>floor(log2(max(w, h))) + 1</c>.</summary>
    public static uint FullMipChain(uint width, uint height)
        => (uint)(BitOperations.Log2(Math.Max(Math.Max(width, height), 1u)) + 1);

    internal Image Handle => _image;
    internal ImageView View => _view;

    /// <summary>The view/subresource aspect (Depth or Color), derived from <see cref="Usage"/>.</summary>
    internal ImageAspectFlags Aspect => _aspect;

    /// <summary>
    /// Deferred disposal (default, spec §3.2.1): the image, view and its suballocation are released once
    /// the frame that used them leaves flight, so a resource freed mid-loop is never destroyed while a
    /// frame in flight may still reference it.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // TODO-M3-06: this deferred path uses the closure-allocating legacy DeletionQueue overload,
        // because a GpuAllocation (block reference + offset + size + type) does not fit the non-capturing
        // 4×ulong DeletionPayload and no shared suballocation registry exists yet to carry it. The
        // zero-alloc non-capturing migration lands at the M3-06 (W4) integration. In M3-05 no image is
        // freed per frame — the only live image (depth) uses DestroyImmediately after WaitIdle — so this
        // closure path is not on any hot loop.
        var device = _device;
        var image = _image;
        var view = _view;
        var allocation = _allocation;
        var hasAllocation = _hasAllocation;
        _image = default;
        _view = default;
        _hasAllocation = false;

        device.EnqueueDestroy(() => DestroyHandles(device, image, view, in allocation, hasAllocation));
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Destroys the image, view and suballocation <b>synchronously</b>. Only valid when the GPU can no
    /// longer reference them (after <c>vkDeviceWaitIdle</c>) — used for swapchain-sized attachments the
    /// FrameRenderer recreates behind a device wait.
    /// </summary>
    internal void DestroyImmediately()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DestroyHandles(_device, _image, _view, in _allocation, _hasAllocation);
        _image = default;
        _view = default;
        _hasAllocation = false;
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Single destruction routine shared by the ctor rollback, the immediate path and the deferred
    /// closure. Order: view → image → free the suballocation. The allocator's blocks outlive any
    /// deferred free, so replaying this after the frame leaves flight is safe.
    /// </summary>
    private static void DestroyHandles(
        GraphicsDevice device, Image image, ImageView view, in GpuAllocation allocation, bool hasAllocation)
    {
        var vk = device.Api;
        var deviceHandle = device.Device;

        if (view.Handle != 0)
        {
            vk.DestroyImageView(deviceHandle, view, null);
            ResourceTracker.Unregister("VkImageView");
        }

        if (image.Handle != 0)
        {
            vk.DestroyImage(deviceHandle, image, null);
            ResourceTracker.Unregister("VkImage");
        }

        if (hasAllocation)
        {
            // VkDeviceMemory itself is owned/counted by the allocator's backing blocks, not here.
            device.Allocator.Free(in allocation);
        }
    }

    private static ImageUsageFlags ToVkUsage(ImageUsage usage)
    {
        var flags = ImageUsageFlags.None;
        if ((usage & ImageUsage.Sampled) != 0)
        {
            flags |= ImageUsageFlags.SampledBit;
        }

        if ((usage & ImageUsage.ColorAttachment) != 0)
        {
            flags |= ImageUsageFlags.ColorAttachmentBit;
        }

        if ((usage & ImageUsage.DepthAttachment) != 0)
        {
            flags |= ImageUsageFlags.DepthStencilAttachmentBit;
        }

        if ((usage & ImageUsage.TransferSrc) != 0)
        {
            flags |= ImageUsageFlags.TransferSrcBit;
        }

        if ((usage & ImageUsage.TransferDst) != 0)
        {
            flags |= ImageUsageFlags.TransferDstBit;
        }

        return flags;
    }
}

/// <summary>
/// Composable usage for a <see cref="GpuImage"/> (drives <c>VkImageUsageFlags</c> and the view aspect).
/// Combine with <c>|</c>, e.g. <c>Sampled | TransferDst</c> for a texture uploaded via staging, or
/// <c>Sampled | TransferDst | TransferSrc</c> when mips are generated by blits.
/// </summary>
[Flags]
public enum ImageUsage
{
    /// <summary>No usage (invalid on its own).</summary>
    None = 0,

    /// <summary>Readable from shaders through a sampler (textures).</summary>
    Sampled = 1 << 0,

    /// <summary>Usable as a color render target; view aspect stays Color.</summary>
    ColorAttachment = 1 << 1,

    /// <summary>Usable as a depth attachment; view aspect becomes Depth.</summary>
    DepthAttachment = 1 << 2,

    /// <summary>Valid source of a transfer/blit (e.g. mip N → N+1).</summary>
    TransferSrc = 1 << 3,

    /// <summary>Valid destination of a transfer/copy (staging upload, blit target).</summary>
    TransferDst = 1 << 4,
}
