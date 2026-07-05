using System.Runtime.InteropServices;
using Agapanthe.Core;
using Agapanthe.Graphics.Memory;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Agapanthe.Graphics;

/// <summary>
/// Explicit, <b>synchronous</b> staging upload path for device-local resources (spec §3.2/§3.6,
/// architect decision session 2: an upload is always a visible, explicit call — never a hidden submit
/// buried inside <c>Write&lt;T&gt;</c>). Copies CPU data into a device-local <see cref="GpuBuffer"/> or
/// a sampled <see cref="GpuImage"/> through a transient host-visible staging buffer and a one-shot
/// command buffer, then blocks on a fence before returning.
/// <para>
/// <b>Synchronous by design (M3).</b> Every <c>Upload</c> submits to the graphics queue and waits on a
/// fence, so on return the GPU has fully consumed the staging buffer and it is destroyed immediately (no
/// DeletionQueue deferral needed — nothing is in flight). This stalls the calling thread; an async path
/// on a dedicated transfer queue is deferred (board "Deferred Work" → M8). Uploads happen at load time,
/// off the per-frame hot path, so the stall and the per-upload staging allocation are acceptable and are
/// not optimised here.
/// </para>
/// <para>
/// <b>Staging in raw Vulkan on purpose.</b> A staging buffer needs <c>TRANSFER_SRC</c> usage, which the
/// public <see cref="BufferUsage"/> enum does not expose (and M3-06 may not extend it). Rather than widen
/// that surface, the staging <c>VkBuffer</c> is created directly here with
/// <c>VK_BUFFER_USAGE_TRANSFER_SRC_BIT</c> and backed by a host-visible suballocation from the device
/// <see cref="GpuAllocator"/> — fully encapsulated, never surfaced to callers.
/// </para>
/// Owns a transient command pool, one reused command buffer and one reused fence; dispose it once uploads
/// are done. Not thread-safe (phase-1 single-threaded).
/// </summary>
public sealed unsafe class GpuUploader : IDisposable
{
    private readonly GraphicsDevice _device;
    private CommandPool _commandPool;
    private CommandBuffer _commandBuffer;
    private Fence _fence;
    private bool _disposed;

    /// <summary>
    /// Creates an uploader bound to <paramref name="device"/>: a transient command pool on the graphics
    /// queue family (uploads run there), one primary command buffer and one unsignaled fence, all reused
    /// across uploads.
    /// </summary>
    public GpuUploader(GraphicsDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        var vk = device.Api;

        try
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                // Transient: buffers are short-lived one-shots. ResetCommandBuffer: the single buffer is
                // reset and re-recorded per upload.
                Flags = CommandPoolCreateFlags.TransientBit | CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = device.GraphicsQueueFamily,
            };
            CommandPool pool;
            VkCheck.ThrowIfFailed(vk.CreateCommandPool(device.Device, &poolInfo, null, &pool), "vkCreateCommandPool");
            _commandPool = pool;
            ResourceTracker.Register("VkCommandPool");

            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer cmd;
            VkCheck.ThrowIfFailed(vk.AllocateCommandBuffers(device.Device, &allocInfo, &cmd), "vkAllocateCommandBuffers");
            _commandBuffer = cmd;

            // Unsignaled: each upload records → submits → waits → resets it.
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Fence fence;
            VkCheck.ThrowIfFailed(vk.CreateFence(device.Device, &fenceInfo, null, &fence), "vkCreateFence");
            _fence = fence;
            ResourceTracker.Register("VkFence");
        }
        catch
        {
            DestroyResources();
            GC.SuppressFinalize(this);
            throw;
        }
    }

    ~GpuUploader()
    {
        // Only report when a native handle was actually acquired (audit M2, finding 1).
        if (_commandPool.Handle != 0 || _fence.Handle != 0)
        {
            ResourceTracker.ReportFinalizerLeak(nameof(GpuUploader));
        }
    }

    /// <summary>
    /// Uploads <paramref name="data"/> into a device-local <paramref name="destination"/> buffer via a
    /// staging copy. Synchronous: blocks until the copy completes.
    /// </summary>
    /// <exception cref="GraphicsException">
    /// <paramref name="destination"/> is host-visible (use <see cref="GpuBuffer.Write{T}"/> instead), or
    /// <paramref name="data"/> is larger than the buffer.
    /// </exception>
    public void Upload<T>(GpuBuffer destination, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (destination.Domain != MemoryDomain.DeviceLocal)
        {
            throw new GraphicsException(
                "Upload(GpuBuffer) targets device-local memory; a host-visible buffer must be filled with Write<T>.");
        }

        var bytes = MemoryMarshal.AsBytes(data);
        if ((ulong)bytes.Length == 0)
        {
            throw new GraphicsException("Upload(GpuBuffer) called with empty data.");
        }

        if ((ulong)bytes.Length > destination.SizeBytes)
        {
            throw new GraphicsException(
                $"Upload of {bytes.Length} bytes exceeds destination buffer size {destination.SizeBytes}.");
        }

        var vk = _device.Api;
        var staging = CreateStaging(bytes, out var stagingAlloc);
        try
        {
            var cmd = BeginOneShot();
            var region = new BufferCopy { SrcOffset = 0, DstOffset = 0, Size = (ulong)bytes.Length };
            vk.CmdCopyBuffer(cmd, staging, destination.Handle, 1, &region);
            EndSubmitWait(cmd);
        }
        finally
        {
            DestroyStaging(staging, in stagingAlloc);
        }
    }

    /// <summary>
    /// Uploads <paramref name="data"/> (mip 0, tightly packed, one texel = <see cref="GpuImage.Format"/>'s
    /// size) into <paramref name="destination"/>, transitioning <c>Undefined → TransferDst</c>, copying,
    /// then either transitioning straight to <c>ShaderReadOnlyOptimal</c> (when
    /// <see cref="GpuImage.MipLevels"/> == 1) or generating the mip chain by linear blits (M3-07) and
    /// leaving every level in <c>ShaderReadOnlyOptimal</c>. Synchronous.
    /// </summary>
    /// <exception cref="GraphicsException">
    /// The image lacks <see cref="ImageUsage.TransferDst"/> (always required) or, when it has more than one
    /// mip, <see cref="ImageUsage.TransferSrc"/> (required as the blit source); <paramref name="data"/> is
    /// not exactly the packed mip-0 size; the format is not an uploadable color format; or, for a mipped
    /// image, the format's optimal tiling does not support linear blit filtering.
    /// </exception>
    public void Upload<T>(GpuImage destination, ReadOnlySpan<T> data)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(destination);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var mips = destination.MipLevels;
        if ((destination.Usage & ImageUsage.TransferDst) == 0)
        {
            throw new GraphicsException(
                "Upload(GpuImage) requires the image to be created with ImageUsage.TransferDst (the copy destination).");
        }

        if (mips > 1 && (destination.Usage & ImageUsage.TransferSrc) == 0)
        {
            throw new GraphicsException(
                "Upload(GpuImage) with MipLevels > 1 requires ImageUsage.TransferSrc (each mip is blitted from the previous one).");
        }

        var bytesPerTexel = BytesPerTexel(destination.Format);
        var expected = (ulong)destination.Width * destination.Height * bytesPerTexel;
        var bytes = MemoryMarshal.AsBytes(data);
        if ((ulong)bytes.Length != expected)
        {
            throw new GraphicsException(
                $"Upload(GpuImage) expected {expected} bytes for mip 0 ({destination.Width}×{destination.Height} × {bytesPerTexel} B/texel), got {bytes.Length}.");
        }

        // Linear blit support is a per-format device capability; without it the generated mips would be
        // undefined. Fail loudly so the caller decides (drop mips → MipLevels 1, or pick a blittable
        // format) rather than silently producing garbage (board M3-07 AC).
        if (mips > 1)
        {
            RequireLinearBlitSupport(destination.Format);
        }

        var vk = _device.Api;
        var image = destination.Handle;
        var aspect = destination.Aspect;

        var staging = CreateStaging(bytes, out var stagingAlloc);
        try
        {
            var cmd = BeginOneShot();

            // All levels Undefined → TransferDst: mip 0 is the copy target, the rest are blit targets.
            TransitionImage(
                cmd, image, aspect, baseMip: 0, levelCount: mips,
                oldLayout: ImageLayout.Undefined, newLayout: ImageLayout.TransferDstOptimal,
                srcStage: PipelineStageFlags2.TopOfPipeBit, srcAccess: 0,
                dstStage: PipelineStageFlags2.AllTransferBit, dstAccess: AccessFlags2.TransferWriteBit);

            var copy = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,   // 0 = tightly packed to the image extent
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
                ImageOffset = default,
                ImageExtent = new Extent3D(destination.Width, destination.Height, 1),
            };
            vk.CmdCopyBufferToImage(cmd, staging, image, ImageLayout.TransferDstOptimal, 1, &copy);

            if (mips == 1)
            {
                TransitionImage(
                    cmd, image, aspect, baseMip: 0, levelCount: 1,
                    oldLayout: ImageLayout.TransferDstOptimal, newLayout: ImageLayout.ShaderReadOnlyOptimal,
                    srcStage: PipelineStageFlags2.AllTransferBit, srcAccess: AccessFlags2.TransferWriteBit,
                    dstStage: PipelineStageFlags2.FragmentShaderBit, dstAccess: AccessFlags2.ShaderReadBit);
            }
            else
            {
                GenerateMips(cmd, image, aspect, destination.Width, destination.Height, mips);
            }

            EndSubmitWait(cmd);
        }
        finally
        {
            DestroyStaging(staging, in stagingAlloc);
        }
    }

    /// <summary>
    /// Records the mip chain generation (M3-07) for an image whose level 0 already holds the source data
    /// in <c>TransferDstOptimal</c> and whose levels 1..N-1 are also in <c>TransferDstOptimal</c>. Each
    /// level i is produced by a linear blit from level i-1: level i-1 is first flipped to
    /// <c>TransferSrcOptimal</c>, then blitted (half-size, floored to 1). After the loop, two final
    /// barriers move everything to <c>ShaderReadOnlyOptimal</c>: levels 0..N-2 (currently TransferSrc) in
    /// one range, and the last level (still TransferDst) in another.
    /// </summary>
    private void GenerateMips(CommandBuffer cmd, Image image, ImageAspectFlags aspect, uint width, uint height, uint mips)
    {
        var vk = _device.Api;

        for (uint i = 1; i < mips; i++)
        {
            // Level i-1 was written (copy for level 0, or the previous blit): make it a readable source.
            TransitionImage(
                cmd, image, aspect, baseMip: i - 1, levelCount: 1,
                oldLayout: ImageLayout.TransferDstOptimal, newLayout: ImageLayout.TransferSrcOptimal,
                srcStage: PipelineStageFlags2.AllTransferBit, srcAccess: AccessFlags2.TransferWriteBit,
                dstStage: PipelineStageFlags2.AllTransferBit, dstAccess: AccessFlags2.TransferReadBit);

            var (srcW, srcH) = GpuImage.MipSize(width, height, i - 1);
            var (dstW, dstH) = GpuImage.MipSize(width, height, i);

            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(aspect, i - 1, 0, 1),
                DstSubresource = new ImageSubresourceLayers(aspect, i, 0, 1),
            };
            blit.SrcOffsets[0] = new Offset3D(0, 0, 0);
            blit.SrcOffsets[1] = new Offset3D((int)srcW, (int)srcH, 1);
            blit.DstOffsets[0] = new Offset3D(0, 0, 0);
            blit.DstOffsets[1] = new Offset3D((int)dstW, (int)dstH, 1);

            // Core vkCmdBlitImage (Vulkan 1.0) — no need for the blit2 variant. Linear filter downsamples.
            vk.CmdBlitImage(
                cmd, image, ImageLayout.TransferSrcOptimal, image, ImageLayout.TransferDstOptimal,
                1, &blit, Filter.Linear);
        }

        // Levels 0..N-2 ended in TransferSrc (each became a blit source); the last level is still
        // TransferDst (it was only ever a blit target). Two barriers move all to shader-read.
        TransitionImage(
            cmd, image, aspect, baseMip: 0, levelCount: mips - 1,
            oldLayout: ImageLayout.TransferSrcOptimal, newLayout: ImageLayout.ShaderReadOnlyOptimal,
            srcStage: PipelineStageFlags2.AllTransferBit, srcAccess: AccessFlags2.TransferReadBit,
            dstStage: PipelineStageFlags2.FragmentShaderBit, dstAccess: AccessFlags2.ShaderReadBit);
        TransitionImage(
            cmd, image, aspect, baseMip: mips - 1, levelCount: 1,
            oldLayout: ImageLayout.TransferDstOptimal, newLayout: ImageLayout.ShaderReadOnlyOptimal,
            srcStage: PipelineStageFlags2.AllTransferBit, srcAccess: AccessFlags2.TransferWriteBit,
            dstStage: PipelineStageFlags2.FragmentShaderBit, dstAccess: AccessFlags2.ShaderReadBit);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Every upload already waited on its fence, so nothing this uploader submitted is in flight.
        DestroyResources();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Creates a transient host-visible staging <c>VkBuffer</c> (<c>TRANSFER_SRC</c>) sized to
    /// <paramref name="bytes"/>, backed by a host-visible suballocation, and copies the data into its
    /// persistently mapped memory. Raw Vulkan because <see cref="BufferUsage"/> exposes no
    /// <c>TRANSFER_SRC</c> (see the class remarks).
    /// </summary>
    private Buffer CreateStaging(ReadOnlySpan<byte> bytes, out GpuAllocation allocation)
    {
        var vk = _device.Api;
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = (ulong)bytes.Length,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        Buffer buffer;
        VkCheck.ThrowIfFailed(vk.CreateBuffer(_device.Device, &bufferInfo, null, &buffer), "vkCreateBuffer");
        ResourceTracker.Register("VkBuffer");

        try
        {
            vk.GetBufferMemoryRequirements(_device.Device, buffer, out var requirements);
            allocation = _device.Allocator.Allocate(
                new MemoryRequirementsInfo(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits),
                MemoryDomain.HostVisible);
            VkCheck.ThrowIfFailed(
                vk.BindBufferMemory(_device.Device, buffer, allocation.DeviceMemory, allocation.Offset),
                "vkBindBufferMemory");

            var mapped = (void*)allocation.MappedPointer;
            if (mapped is null)
            {
                throw new GraphicsException("Staging allocation is host-visible but not mapped by the memory backend.");
            }

            // MappedPointer already includes this suballocation's offset (allocator contract), so copy at 0.
            bytes.CopyTo(new Span<byte>(mapped, bytes.Length));
            return buffer;
        }
        catch
        {
            vk.DestroyBuffer(_device.Device, buffer, null);
            ResourceTracker.Unregister("VkBuffer");
            throw;
        }
    }

    /// <summary>
    /// Destroys the staging buffer and returns its suballocation immediately. Safe because the fence wait
    /// in <see cref="EndSubmitWait"/> already proved the GPU finished reading it — nothing is in flight, so
    /// no DeletionQueue deferral is needed.
    /// </summary>
    private void DestroyStaging(Buffer buffer, in GpuAllocation allocation)
    {
        var vk = _device.Api;
        vk.DestroyBuffer(_device.Device, buffer, null);
        ResourceTracker.Unregister("VkBuffer");
        _device.Allocator.Free(in allocation);
    }

    private CommandBuffer BeginOneShot()
    {
        var vk = _device.Api;
        var cmd = _commandBuffer;
        VkCheck.ThrowIfFailed(vk.ResetCommandBuffer(cmd, CommandBufferResetFlags.None), "vkResetCommandBuffer");
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VkCheck.ThrowIfFailed(vk.BeginCommandBuffer(cmd, &beginInfo), "vkBeginCommandBuffer");
        return cmd;
    }

    private void EndSubmitWait(CommandBuffer cmd)
    {
        var vk = _device.Api;
        VkCheck.ThrowIfFailed(vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

        var cmdInfo = new CommandBufferSubmitInfo { SType = StructureType.CommandBufferSubmitInfo, CommandBuffer = cmd };
        var submit = new SubmitInfo2
        {
            SType = StructureType.SubmitInfo2,
            CommandBufferInfoCount = 1,
            PCommandBufferInfos = &cmdInfo,
        };
        // synchronization2 submit (spec §3.3): no semaphores — the fence is the only synchronization.
        _device.QueueSubmit2(_device.GraphicsQueue, &submit, _fence);

        var fence = _fence;
        VkCheck.ThrowIfFailed(vk.WaitForFences(_device.Device, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        VkCheck.ThrowIfFailed(vk.ResetFences(_device.Device, 1, &fence), "vkResetFences");
    }

    private void TransitionImage(
        CommandBuffer cmd, Image image, ImageAspectFlags aspect, uint baseMip, uint levelCount,
        ImageLayout oldLayout, ImageLayout newLayout,
        PipelineStageFlags2 srcStage, AccessFlags2 srcAccess,
        PipelineStageFlags2 dstStage, AccessFlags2 dstAccess)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = srcStage,
            SrcAccessMask = srcAccess,
            DstStageMask = dstStage,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, baseMip, levelCount, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _device.CmdPipelineBarrier2(cmd, &dependency);
    }

    private void RequireLinearBlitSupport(PixelFormat format)
    {
        _device.Api.GetPhysicalDeviceFormatProperties(_device.PhysicalDevice, format.ToVk(), out var props);
        const FormatFeatureFlags needed =
            FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit | FormatFeatureFlags.SampledImageFilterLinearBit;
        if ((props.OptimalTilingFeatures & needed) != needed)
        {
            throw new GraphicsException(
                $"Format {format} does not support linear blit on optimal tiling (needs BLIT_SRC | BLIT_DST | " +
                "SAMPLED_IMAGE_FILTER_LINEAR); create the image with MipLevels = 1 or choose a blittable format.");
        }
    }

    private void DestroyResources()
    {
        var vk = _device.Api;
        if (_fence.Handle != 0)
        {
            vk.DestroyFence(_device.Device, _fence, null);
            _fence = default;
            ResourceTracker.Unregister("VkFence");
        }

        if (_commandPool.Handle != 0)
        {
            // Frees the allocated command buffer with the pool.
            vk.DestroyCommandPool(_device.Device, _commandPool, null);
            _commandPool = default;
            _commandBuffer = default;
            ResourceTracker.Unregister("VkCommandPool");
        }
    }

    /// <summary>Bytes per texel for the uploadable color formats.</summary>
    /// <exception cref="GraphicsException">The format is not a color format that can be uploaded as texels.</exception>
    private static ulong BytesPerTexel(PixelFormat format) => format switch
    {
        PixelFormat.Rgba8Srgb or PixelFormat.Rgba8Unorm or PixelFormat.Bgra8Srgb => 4,
        PixelFormat.Rgba16Sfloat => 8,
        PixelFormat.R32G32B32A32Sfloat => 16,
        _ => throw new GraphicsException($"Format {format} is not an uploadable color format for image staging."),
    };
}
