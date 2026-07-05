using Agapanthe.Core;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Agapanthe.Graphics;

/// <summary>
/// Synchronous GPU→CPU image readback, for debug captures and (later) automated visual checks.
/// One-shot: creates a transient command pool + host-visible buffer, copies the image, waits,
/// returns the bytes and destroys everything. Not a hot-path facility — every call stalls the
/// queue. The image must have been created with <see cref="ImageUsage.TransferSrc"/>.
/// </summary>
public static unsafe class GpuReadback
{
    /// <summary>
    /// Reads back mip 0 of <paramref name="image"/> as tightly-packed texel bytes
    /// (row-major, no padding). <paramref name="currentLayout"/> is the layout the image is
    /// known to be in; it is restored before returning. <paramref name="bytesPerTexel"/> must
    /// match the image format (e.g. 8 for Rgba16Sfloat, 4 for Rgba8*).
    /// </summary>
    public static byte[] ReadImage(
        GraphicsDevice device, GpuImage image, ImageLayoutState currentLayout, int bytesPerTexel)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(image);

        var vk = device.Api;
        var byteCount = (ulong)(image.Width * image.Height * (uint)bytesPerTexel);

        // Host-visible readback buffer (raw: BufferUsage has no TransferDst-only shape, same
        // reasoning as GpuUploader's raw staging buffer).
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = byteCount,
            Usage = BufferUsageFlags.TransferDstBit,
            SharingMode = SharingMode.Exclusive,
        };
        Buffer buffer;
        VkCheck.ThrowIfFailed(vk.CreateBuffer(device.Device, &bufferInfo, null, &buffer), "vkCreateBuffer");
        ResourceTracker.Register("VkBuffer");

        vk.GetBufferMemoryRequirements(device.Device, buffer, out var requirements);
        var allocation = device.Allocator.Allocate(
            new Memory.MemoryRequirementsInfo(requirements.Size, requirements.Alignment, requirements.MemoryTypeBits),
            Memory.MemoryDomain.HostVisible);

        CommandPool pool = default;
        Fence fence = default;
        try
        {
            VkCheck.ThrowIfFailed(
                vk.BindBufferMemory(device.Device, buffer, allocation.DeviceMemory, allocation.Offset),
                "vkBindBufferMemory");

            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                QueueFamilyIndex = device.GraphicsQueueFamily,
                Flags = CommandPoolCreateFlags.TransientBit,
            };
            VkCheck.ThrowIfFailed(vk.CreateCommandPool(device.Device, &poolInfo, null, &pool), "vkCreateCommandPool");
            ResourceTracker.Register("VkCommandPool");

            var allocInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = pool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer cmd;
            VkCheck.ThrowIfFailed(vk.AllocateCommandBuffers(device.Device, &allocInfo, &cmd), "vkAllocateCommandBuffers");

            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            VkCheck.ThrowIfFailed(vk.BeginCommandBuffer(cmd, &beginInfo), "vkBeginCommandBuffer");

            var list = new CommandList(device, cmd);
            list.TransitionImage(image, currentLayout, ImageLayoutState.TransferSrc);

            var region = new BufferImageCopy
            {
                BufferOffset = 0,
                BufferRowLength = 0,   // tightly packed
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(image.Aspect, 0, 0, 1),
                ImageOffset = default,
                ImageExtent = new Extent3D(image.Width, image.Height, 1),
            };
            vk.CmdCopyImageToBuffer(cmd, image.Handle, ImageLayout.TransferSrcOptimal, buffer, 1, &region);

            list.TransitionImage(image, ImageLayoutState.TransferSrc, currentLayout);
            VkCheck.ThrowIfFailed(vk.EndCommandBuffer(cmd), "vkEndCommandBuffer");

            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VkCheck.ThrowIfFailed(vk.CreateFence(device.Device, &fenceInfo, null, &fence), "vkCreateFence");
            ResourceTracker.Register("VkFence");

            var submit = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                CommandBufferCount = 1,
                PCommandBuffers = &cmd,
            };
            VkCheck.ThrowIfFailed(vk.QueueSubmit(device.GraphicsQueue, 1, &submit, fence), "vkQueueSubmit");
            VkCheck.ThrowIfFailed(vk.WaitForFences(device.Device, 1, in fence, true, ulong.MaxValue), "vkWaitForFences");

            var result = new byte[byteCount];
            new ReadOnlySpan<byte>((void*)allocation.MappedPointer, (int)byteCount).CopyTo(result);
            return result;
        }
        finally
        {
            if (fence.Handle != 0)
            {
                vk.DestroyFence(device.Device, fence, null);
                ResourceTracker.Unregister("VkFence");
            }

            if (pool.Handle != 0)
            {
                vk.DestroyCommandPool(device.Device, pool, null);
                ResourceTracker.Unregister("VkCommandPool");
            }

            vk.DestroyBuffer(device.Device, buffer, null);
            ResourceTracker.Unregister("VkBuffer");
            device.Allocator.Free(in allocation);
        }
    }
}
