using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>Index element width for <see cref="CommandList.BindIndexBuffer"/>.</summary>
public enum IndexFormat
{
    UInt16,
    UInt32,
}

/// <summary>
/// Thin recording surface over a command buffer, handed to the per-frame draw callback. It exposes pass
/// composition (BeginRendering/EndRendering), image layout transitions and viewport/scissor so a caller in
/// the Rendering layer can build multi-pass frames; the frame loop keeps only the swapchain acquire/present
/// transitions and frame synchronization. The exposed API grows as milestones need it.
/// </summary>
public readonly unsafe struct CommandList
{
    private readonly GraphicsDevice _device;
    private readonly CommandBuffer _buffer;

    internal CommandList(GraphicsDevice device, CommandBuffer buffer)
    {
        _device = device;
        _buffer = buffer;
    }

    public void BindPipeline(GraphicsPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        _device.Api.CmdBindPipeline(_buffer, PipelineBindPoint.Graphics, pipeline.Handle);
    }

    public void BindVertexBuffer(GpuBuffer buffer, ulong offsetBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var handle = buffer.Handle;
        _device.Api.CmdBindVertexBuffers(_buffer, 0, 1, &handle, &offsetBytes);
    }

    public void BindIndexBuffer(GpuBuffer buffer, IndexFormat format, ulong offsetBytes = 0)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var indexType = format == IndexFormat.UInt16 ? IndexType.Uint16 : IndexType.Uint32;
        _device.Api.CmdBindIndexBuffer(_buffer, buffer.Handle, offsetBytes, indexType);
    }

    public void BindDescriptorSet(GraphicsPipeline pipeline, uint setIndex, DescriptorSetHandle set)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var vkSet = set.Set;
        _device.Api.CmdBindDescriptorSets(
            _buffer, PipelineBindPoint.Graphics, pipeline.Layout, setIndex, 1, &vkSet, 0, null);
    }

    /// <summary>Pushes <paramref name="value"/> as push-constant data. The pipeline must declare
    /// a matching <see cref="PushConstantRange"/> (same stages, offset and size).</summary>
    public void PushConstants<T>(GraphicsPipeline pipeline, ShaderStages stages, in T value, uint offsetBytes = 0)
        where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        fixed (T* p = &value)
        {
            _device.Api.CmdPushConstants(
                _buffer, pipeline.Layout, DescriptorSetLayout.ToVkStages(stages),
                offsetBytes, (uint)Unsafe.SizeOf<T>(), p);
        }
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
        => _device.Api.CmdDraw(_buffer, vertexCount, instanceCount, firstVertex, firstInstance);

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
        => _device.Api.CmdDrawIndexed(_buffer, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

    /// <summary>
    /// Opens a dynamic-rendering scope for <paramref name="attachments"/> (color + optional depth). Routes
    /// through the device dispatch so it works on a Vulkan 1.3 core device and a 1.2 + KHR_dynamic_rendering
    /// device alike. Pair with <see cref="EndRendering"/>; issue <see cref="SetViewportScissor"/> before drawing.
    /// </summary>
    public void BeginRendering(in RenderingAttachments attachments)
    {
        var renderingInfo = new RenderingInfo
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(default, new Extent2D(attachments.Width, attachments.Height)),
            LayerCount = 1,
        };

        // Kept in scope until CmdBeginRendering returns; PColorAttachments points at it only when present.
        // A depth-only pass (shadow map) leaves Color null → ColorAttachmentCount stays 0, PColorAttachments null.
        var colorAttachment = default(RenderingAttachmentInfo);
        if (attachments.Color is { } color)
        {
            var (r, g, b, a) = color.ClearColor;
            colorAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = color.Target.View,
                ImageLayout = ImageLayout.ColorAttachmentOptimal,
                LoadOp = ToVkLoadOp(color.LoadOp),
                StoreOp = AttachmentStoreOp.Store,
                ClearValue = new ClearValue { Color = new ClearColorValue(r, g, b, a) },
            };
            renderingInfo.ColorAttachmentCount = 1;
            renderingInfo.PColorAttachments = &colorAttachment;
        }

        // Kept in scope until CmdBeginRendering returns; PDepthAttachment points at it only when present.
        var depthAttachment = default(RenderingAttachmentInfo);
        if (attachments.Depth is { } depth)
        {
            depthAttachment = new RenderingAttachmentInfo
            {
                SType = StructureType.RenderingAttachmentInfo,
                ImageView = depth.Target.View,
                ImageLayout = ImageLayout.DepthAttachmentOptimal,
                LoadOp = ToVkLoadOp(depth.LoadOp),
                // Scene depth is per-frame scratch (DontCare); a shadow map is sampled after the
                // pass and must be stored.
                StoreOp = depth.Store ? AttachmentStoreOp.Store : AttachmentStoreOp.DontCare,
                ClearValue = new ClearValue { DepthStencil = new ClearDepthStencilValue(depth.ClearDepth, 0) },
            };
            renderingInfo.PDepthAttachment = &depthAttachment;
        }

        _device.CmdBeginRendering(_buffer, &renderingInfo);
    }

    /// <summary>Closes the scope opened by <see cref="BeginRendering"/>.</summary>
    public void EndRendering() => _device.CmdEndRendering(_buffer);

    /// <summary>
    /// Sets a full-target viewport and scissor of <paramref name="width"/>×<paramref name="height"/>. No
    /// viewport Y-flip: the Vulkan clip-space convention (Y down, Z [0,1]) is handled in the projection
    /// matrix, so the viewport stays top-left origin with depth [0,1].
    /// </summary>
    public void SetViewportScissor(uint width, uint height)
    {
        var viewport = new Viewport { X = 0, Y = 0, Width = width, Height = height, MinDepth = 0, MaxDepth = 1 };
        var scissor = new Rect2D(default, new Extent2D(width, height));
        _device.Api.CmdSetViewport(_buffer, 0, 1, &viewport);
        _device.Api.CmdSetScissor(_buffer, 0, 1, &scissor);
    }

    /// <summary>
    /// Transitions <paramref name="image"/> from one <see cref="ImageLayoutState"/> to another with a
    /// synchronization2 barrier. The (stage, access) pair for each state reproduces exactly the combinations
    /// the frame loop used before this API existed (color attach, depth attach, present) plus shader-read and
    /// transfer states.
    /// </summary>
    public void TransitionImage(GpuImage image, ImageLayoutState from, ImageLayoutState to)
    {
        ArgumentNullException.ThrowIfNull(image);
        TransitionImage(image.Handle, image.Aspect, from, to);
    }

    /// <summary>Transitions a <see cref="RenderTargetView"/> (used by the frame loop for swapchain images).</summary>
    internal void TransitionImage(RenderTargetView target, ImageLayoutState from, ImageLayoutState to)
        => TransitionImage(target.Image, target.Aspect, from, to);

    private void TransitionImage(Image image, ImageAspectFlags aspect, ImageLayoutState from, ImageLayoutState to)
    {
        var (oldLayout, srcStage, srcAccess) = MapState(from, aspect);
        var (newLayout, dstStage, dstAccess) = MapState(to, aspect);

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
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _device.CmdPipelineBarrier2(_buffer, &dependency);
    }

    private static AttachmentLoadOp ToVkLoadOp(AttachmentLoadAction action) => action switch
    {
        AttachmentLoadAction.Clear => AttachmentLoadOp.Clear,
        AttachmentLoadAction.Load => AttachmentLoadOp.Load,
        AttachmentLoadAction.DontCare => AttachmentLoadOp.DontCare,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, null),
    };

    // Maps an engine layout state to its (Vulkan layout, pipeline stage, access) triple. For an Undefined
    // source the access is none and the stage is aspect-aware: fragment-test stages for a depth image (so the
    // clear serializes against the previous frame's depth work on the shared depth target), top-of-pipe for a
    // color image. Both reproduce the exact barriers the frame loop issued before this abstraction.
    private static (ImageLayout Layout, PipelineStageFlags2 Stage, AccessFlags2 Access) MapState(
        ImageLayoutState state, ImageAspectFlags aspect) => state switch
    {
        ImageLayoutState.Undefined => (
            ImageLayout.Undefined,
            (aspect & ImageAspectFlags.DepthBit) != 0
                ? PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit
                : PipelineStageFlags2.TopOfPipeBit,
            AccessFlags2.None),
        ImageLayoutState.ColorAttachment => (
            ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentWriteBit),
        ImageLayoutState.DepthAttachment => (
            ImageLayout.DepthAttachmentOptimal,
            PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit,
            AccessFlags2.DepthStencilAttachmentWriteBit),
        ImageLayoutState.ShaderReadOnly => (
            ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.ShaderReadBit),
        ImageLayoutState.TransferSrc => (
            ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferReadBit),
        ImageLayoutState.TransferDst => (
            ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.TransferBit,
            AccessFlags2.TransferWriteBit),
        ImageLayoutState.PresentSrc => (
            ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.BottomOfPipeBit,
            AccessFlags2.None),
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
    };
}
