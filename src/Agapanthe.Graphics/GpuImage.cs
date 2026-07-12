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
/// Disposal is <b>deferred</b> (<see cref="Dispose"/> → device DeletionQueue): the destroy runs once the
/// frame that used the image leaves flight — including swapchain-sized attachments, which are recreated
/// behind a <c>vkDeviceWaitIdle</c> and drained by the queue like everything else.
/// <c>VkDeviceMemory</c> is <i>not</i> tracked here — the allocator counts it per backing
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
    // Additional single-mip/layer-subrange views (storage-image write targets, M7) created on demand by
    // CreateMipView and OWNED here: the default view above spans the whole resource, but a compute kernel
    // writing one mip (or one cube face) of a cubemap needs a narrow view. Each is registered separately in
    // the ResourceTracker as "VkImageView" and destroyed with the image — never in the 4-slot main payload
    // (which is full: image, view, block id, packed offset), but each enqueued on its own deferred entry.
    private readonly List<ImageView> _extraViews = new();
    private bool _disposed;

    /// <summary>
    /// Creates a 2D image of <paramref name="width"/>×<paramref name="height"/> in
    /// <paramref name="format"/> with <paramref name="mipLevels"/> mip levels, device-local memory, and
    /// a view spanning <c>[0, mipLevels)</c>. <paramref name="usage"/> drives the Vulkan usage flags and
    /// the view aspect. Memory comes from the device allocator (render targets get a dedicated block when
    /// large enough — decided by the free-list size threshold, nothing special here).
    /// </summary>
    /// <param name="arrayLayers">Number of array layers (default 1). <see cref="ImageViewKind.Cube"/>
    /// requires exactly 6; <see cref="ImageViewKind.Color2D"/> requires exactly 1.</param>
    /// <param name="viewKind">Shape of the default view (and, for <see cref="ImageViewKind.Cube"/>, adds
    /// <c>CUBE_COMPATIBLE</c> to the image). The default view spans every mip and every layer.</param>
    /// <exception cref="GraphicsException"><paramref name="usage"/> is empty; a dimension/mip/layer count is
    /// zero; a cube image does not have 6 layers; or a 2D view is asked for a layered image.</exception>
    public GpuImage(
        GraphicsDevice device, uint width, uint height, PixelFormat format, ImageUsage usage,
        uint mipLevels = 1, uint arrayLayers = 1, ImageViewKind viewKind = ImageViewKind.Color2D)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (usage == ImageUsage.None)
        {
            throw new GraphicsException("ImageUsage must specify at least one usage.");
        }

        if (width == 0 || height == 0 || mipLevels == 0 || arrayLayers == 0)
        {
            throw new GraphicsException("GpuImage width, height, mipLevels and arrayLayers must all be non-zero.");
        }

        // A cube view needs the 6 faces; a plain 2D view cannot address extra layers. Array2D is the only
        // kind free to pick any layer count. Fail loudly rather than mint a view the driver rejects.
        if (viewKind == ImageViewKind.Cube && arrayLayers != 6)
        {
            throw new GraphicsException($"ImageViewKind.Cube requires exactly 6 array layers, got {arrayLayers}.");
        }

        if (viewKind == ImageViewKind.Color2D && arrayLayers != 1)
        {
            throw new GraphicsException(
                $"ImageViewKind.Color2D requires arrayLayers == 1, got {arrayLayers}; use Array2D for a layered image.");
        }

        _device = device;
        Width = width;
        Height = height;
        MipLevels = mipLevels;
        ArrayLayers = arrayLayers;
        Format = format;
        Usage = usage;
        // DepthAttachment implies the depth aspect; every other usage is a color image.
        _aspect = (usage & ImageUsage.DepthAttachment) != 0 ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit;
        var viewType = viewKind switch
        {
            ImageViewKind.Cube => ImageViewType.TypeCube,
            ImageViewKind.Array2D => ImageViewType.Type2DArray,
            _ => ImageViewType.Type2D,
        };
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
                ArrayLayers = arrayLayers,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = ToVkUsage(usage),
                // CUBE_COMPATIBLE lets a Type2DArray/TypeCube view address the 6 faces (core Vulkan; needs
                // no imageCubeArray feature since a single-cube view is used, not a cube-array).
                Flags = viewKind == ImageViewKind.Cube ? ImageCreateFlags.CreateCubeCompatibleBit : ImageCreateFlags.None,
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
                ViewType = viewType,
                Format = format.ToVk(),
                SubresourceRange = new ImageSubresourceRange(_aspect, 0, mipLevels, 0, arrayLayers),
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

    /// <summary>Number of array layers (6 for a cubemap, 1 for a plain 2D image); the default view covers
    /// <c>[0, ArrayLayers)</c>.</summary>
    public uint ArrayLayers { get; }

    /// <summary>The pixel format the image and its view were created with.</summary>
    public PixelFormat Format { get; }

    /// <summary>The composable usage the image was created with (drives usage flags and view aspect).</summary>
    public ImageUsage Usage { get; }

    /// <summary>Full mip chain length for an image of size <paramref name="width"/>×<paramref name="height"/>:
    /// <c>floor(log2(max(w, h))) + 1</c>.</summary>
    public static uint FullMipChain(uint width, uint height)
        => (uint)(BitOperations.Log2(Math.Max(Math.Max(width, height), 1u)) + 1);

    /// <summary>
    /// Dimensions of mip <paramref name="level"/> for a base image of
    /// <paramref name="width"/>×<paramref name="height"/>: each axis is halved per level and floored to a
    /// minimum of 1 (<c>max(1, dim &gt;&gt; level)</c>). This is the extent a buffer→image copy or a blit
    /// target uses at that level, and it matches the Vulkan mip-size rule. Pure/testable (M3-07).
    /// </summary>
    public static (uint Width, uint Height) MipSize(uint width, uint height, uint level)
        => (Math.Max(1u, width >> (int)level), Math.Max(1u, height >> (int)level));

    internal Image Handle => _image;
    internal ImageView View => _view;

    /// <summary>The view/subresource aspect (Depth or Color), derived from <see cref="Usage"/>.</summary>
    internal ImageAspectFlags Aspect => _aspect;

    /// <summary>
    /// Creates and <b>retains</b> an extra view over a single mip and a contiguous layer range — the write
    /// target a compute kernel binds as a storage image (M7: one mip / one face of an IBL cubemap). The view
    /// is <c>Type2DArray</c> when it spans more than one layer, else <c>Type2D</c>, aspect Color (extra views
    /// are storage color targets; depth stays a plain 2D image). Ownership stays with this <see cref="GpuImage"/>:
    /// the view lives until the image is disposed — do not destroy the returned handle yourself.
    /// </summary>
    /// <param name="mip">Mip level in <c>[0, MipLevels)</c>.</param>
    /// <param name="baseLayer">First layer in <c>[0, ArrayLayers)</c>.</param>
    /// <param name="layerCount">Layer count; 0 (default) means all remaining layers from
    /// <paramref name="baseLayer"/>.</param>
    /// <exception cref="GraphicsException">The mip or layer range is out of bounds.</exception>
    public ImageMipView CreateMipView(uint mip, uint baseLayer = 0, uint layerCount = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (mip >= MipLevels)
        {
            throw new GraphicsException($"CreateMipView mip {mip} is out of range [0, {MipLevels}).");
        }

        if (baseLayer >= ArrayLayers)
        {
            throw new GraphicsException($"CreateMipView baseLayer {baseLayer} is out of range [0, {ArrayLayers}).");
        }

        var resolved = layerCount == 0 ? ArrayLayers - baseLayer : layerCount;
        if (baseLayer + resolved > ArrayLayers)
        {
            throw new GraphicsException(
                $"CreateMipView layer range [{baseLayer}, {baseLayer + resolved}) exceeds ArrayLayers {ArrayLayers}.");
        }

        var vk = _device.Api;
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _image,
            ViewType = resolved > 1 ? ImageViewType.Type2DArray : ImageViewType.Type2D,
            Format = Format.ToVk(),
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, mip, 1, baseLayer, resolved),
        };
        ImageView view;
        VkCheck.ThrowIfFailed(vk.CreateImageView(_device.Device, &viewInfo, null, &view), "vkCreateImageView");
        _extraViews.Add(view);
        ResourceTracker.Register("VkImageView");
        return new ImageMipView(view);
    }

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

        // Extra views (CreateMipView) don't fit the full 4-slot main payload below, so each is enqueued on
        // its OWN non-capturing entry first: Handle0 = the view alone, destructor = cached static
        // DestroyViewDelegate (DestroyImageView + Unregister). No packing, no registry, correct deferral.
        foreach (var extra in _extraViews)
        {
            _device.EnqueueDestroy(DestroyViewDelegate, new DeletionPayload(extra.Handle));
        }

        _extraViews.Clear();

        // Non-capturing deferred destroy (spec §3.2.5, zero managed allocation): the raw handles plus
        // the routing data needed to return the suballocation travel by value in the payload, and the
        // destructor is a cached static delegate. An image needs FIVE values (image, view, block id,
        // memory-type index, offset) but the payload has four ulong slots, so memory-type index and
        // offset are packed into the single Offset slot by PackOffsetAndType (40-bit offset / 24-bit
        // type — see the helper for the guards). Payload layout: Handle0 = VkImage, Handle1 =
        // VkImageView, Handle2 = VkDeviceMemory (block id), Offset = packed(offset, memTypeIndex).
        var payload = new DeletionPayload(
            _image.Handle,
            _view.Handle,
            _hasAllocation ? _allocation.DeviceMemory.Handle : 0,
            _hasAllocation ? PackOffsetAndType(_allocation.Offset, _allocation.MemoryTypeIndex) : 0);
        _image = default;
        _view = default;
        _hasAllocation = false;

        _device.EnqueueDestroy(DestroyDelegate, in payload);
        GC.SuppressFinalize(this);
    }


    // Allocated once per type: passing this reference on the deferred path costs no allocation.
    private static readonly Action<GraphicsDevice, DeletionPayload> DestroyDelegate = DestroyDeferred;

    // Cached static destructor for a single extra view (CreateMipView) on the deferred path: Handle0 is the
    // lone VkImageView, nothing else. Allocated once, so enqueuing an extra view costs no allocation.
    private static readonly Action<GraphicsDevice, DeletionPayload> DestroyViewDelegate = DestroyDeferredView;

    private static void DestroyDeferredView(GraphicsDevice device, DeletionPayload payload)
    {
        var view = new ImageView(payload.Handle0);
        if (view.Handle != 0)
        {
            device.Api.DestroyImageView(device.Device, view, null);
            ResourceTracker.Unregister("VkImageView");
        }
    }

    /// <summary>
    /// Deferred destructor for the non-capturing DeletionQueue path (<see cref="Dispose"/>): rebuilds the
    /// handles and the <see cref="GpuAllocation"/> from the value-type payload, then destroys view → image
    /// and returns the suballocation. The free-list identifies a region by (memory-type index, block id,
    /// offset) only — <c>Size</c>/<c>MappedPointer</c> are irrelevant and left at zero. The allocator's
    /// blocks outlive any deferred free, so replaying this after the frame leaves flight is safe.
    /// </summary>
    private static void DestroyDeferred(GraphicsDevice device, DeletionPayload payload)
    {
        var vk = device.Api;
        var deviceHandle = device.Device;

        var view = new ImageView(payload.Handle1);
        if (view.Handle != 0)
        {
            vk.DestroyImageView(deviceHandle, view, null);
            ResourceTracker.Unregister("VkImageView");
        }

        var image = new Image(payload.Handle0);
        if (image.Handle != 0)
        {
            vk.DestroyImage(deviceHandle, image, null);
            ResourceTracker.Unregister("VkImage");
        }

        // Non-zero block id ⇒ a suballocation was bound (a null VkDeviceMemory handle is never valid).
        if (payload.Handle2 != 0)
        {
            var block = new MemoryBlock(payload.Handle2, nint.Zero);
            var sub = new Suballocation(block, UnpackOffset(payload.Offset), 0);
            // Domain is not consulted by Free; any value is fine.
            var allocation = new GpuAllocation(sub, UnpackMemoryType(payload.Offset), MemoryDomain.DeviceLocal);
            device.Allocator.Free(in allocation);
        }
    }

    // Payload packing (spec §3.2.5): the suballocation offset and its memory-type index share the single
    // 64-bit Offset slot. Suballocation offsets sit inside 64–256 MiB blocks (≪ 2^40), and Vulkan caps
    // memory types at VK_MAX_MEMORY_TYPES = 32 (≪ 2^24), so a 40/24 split is comfortable with headroom.
    private const int OffsetBits = 40;
    private const ulong OffsetMask = (1UL << OffsetBits) - 1;
    private const uint MaxMemoryTypeIndex = (1u << (64 - OffsetBits)) - 1; // 24-bit ceiling

    /// <summary>Packs a suballocation <paramref name="offset"/> (low 40 bits) and
    /// <paramref name="memoryTypeIndex"/> (high 24 bits) into one 64-bit value for the deletion payload.</summary>
    /// <exception cref="GraphicsException">The offset needs ≥ 40 bits or the memory-type index ≥ 24 bits
    /// (would silently corrupt the free-list routing).</exception>
    internal static ulong PackOffsetAndType(ulong offset, uint memoryTypeIndex)
    {
        if (offset > OffsetMask)
        {
            throw new GraphicsException(
                $"Suballocation offset {offset} exceeds the {OffsetBits}-bit deletion-payload packing limit.");
        }

        if (memoryTypeIndex > MaxMemoryTypeIndex)
        {
            throw new GraphicsException(
                $"Memory-type index {memoryTypeIndex} exceeds the {64 - OffsetBits}-bit deletion-payload packing limit.");
        }

        return ((ulong)memoryTypeIndex << OffsetBits) | offset;
    }

    /// <summary>Extracts the suballocation offset from a value packed by <see cref="PackOffsetAndType"/>.</summary>
    internal static ulong UnpackOffset(ulong packed) => packed & OffsetMask;

    /// <summary>Extracts the memory-type index from a value packed by <see cref="PackOffsetAndType"/>.</summary>
    internal static uint UnpackMemoryType(ulong packed) => (uint)(packed >> OffsetBits);

    /// <summary>
    /// Single destruction routine shared by the ctor rollback and the immediate path. Order: view → image
    /// → free the suballocation. The allocator's blocks outlive any deferred free, so replaying this after
    /// the frame leaves flight is safe.
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

        if ((usage & ImageUsage.Storage) != 0)
        {
            flags |= ImageUsageFlags.StorageBit;
        }

        return flags;
    }
}

/// <summary>
/// Shape of a <see cref="GpuImage"/>'s default view (and, for <see cref="Cube"/>, of the image itself).
/// The 2D path (<see cref="Color2D"/>) is the default and matches every image created before M7.
/// </summary>
public enum ImageViewKind
{
    /// <summary>A plain single-layer 2D view (<c>VK_IMAGE_VIEW_TYPE_2D</c>). Requires <c>arrayLayers == 1</c>.</summary>
    Color2D,

    /// <summary>A cubemap view (<c>VK_IMAGE_VIEW_TYPE_CUBE</c>); adds <c>CUBE_COMPATIBLE</c> and requires
    /// <c>arrayLayers == 6</c>.</summary>
    Cube,

    /// <summary>A 2D-array view (<c>VK_IMAGE_VIEW_TYPE_2D_ARRAY</c>) over all layers.</summary>
    Array2D,
}

/// <summary>
/// A lightweight handle to an extra image view owned by a <see cref="GpuImage"/> (created via
/// <see cref="GpuImage.CreateMipView"/>), covering one mip and a layer subrange. Bind it as a storage-image
/// write target through the <c>WriteStorageImage(..., ImageMipView)</c> overloads. The wrapped
/// <c>VkImageView</c> is owned and destroyed by the parent image — this struct never owns it.
/// </summary>
public readonly struct ImageMipView
{
    internal ImageMipView(ImageView view) => Handle = view;

    internal ImageView Handle { get; }
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

    /// <summary>Readable and writable from shaders without a sampler (compute storage image, layout General;
    /// M7 IBL cubemap generation).</summary>
    Storage = 1 << 5,
}
