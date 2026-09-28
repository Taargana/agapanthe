using Silk.NET.Vulkan;
using VkQueryPool = Silk.NET.Vulkan.QueryPool;

namespace Agapanthe.Graphics;

/// <summary>
/// Dispatch helpers for dynamic rendering and synchronization2 commands. On MoltenVK
/// (and any device declaring Vulkan 1.3) these route to the core entry points; on a
/// 1.2 device they route to the KHR extension objects. Callers stay backend-agnostic.
/// </summary>
public sealed unsafe partial class GraphicsDevice
{
    /// <summary>Finds a memory type index matching <paramref name="typeBits"/> and all <paramref name="required"/> flags.</summary>
    internal uint FindMemoryType(uint typeBits, MemoryPropertyFlags required)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memProps);
        for (var i = 0u; i < memProps.MemoryTypeCount; i++)
        {
            var suitable = (typeBits & (1u << (int)i)) != 0;
            var hasProps = (memProps.MemoryTypes[(int)i].PropertyFlags & required) == required;
            if (suitable && hasProps)
            {
                return i;
            }
        }

        throw new GraphicsException($"No memory type with {required}.");
    }

    internal void CmdBeginRendering(CommandBuffer cmd, RenderingInfo* info)
    {
        if (HasVulkan13Core)
        {
            _vk.CmdBeginRendering(cmd, info);
        }
        else
        {
            KhrDynamicRendering!.CmdBeginRendering(cmd, info);
        }
    }

    internal void CmdEndRendering(CommandBuffer cmd)
    {
        if (HasVulkan13Core)
        {
            _vk.CmdEndRendering(cmd);
        }
        else
        {
            KhrDynamicRendering!.CmdEndRendering(cmd);
        }
    }

    internal void CmdPipelineBarrier2(CommandBuffer cmd, DependencyInfo* info)
    {
        if (HasVulkan13Core)
        {
            _vk.CmdPipelineBarrier2(cmd, info);
        }
        else
        {
            KhrSynchronization2!.CmdPipelineBarrier2(cmd, info);
        }
    }

    internal void CmdWriteTimestamp2(CommandBuffer cmd, PipelineStageFlags2 stage, VkQueryPool pool, uint query)
    {
        if (HasVulkan13Core)
        {
            _vk.CmdWriteTimestamp2(cmd, stage, pool, query);
        }
        else
        {
            KhrSynchronization2!.CmdWriteTimestamp2(cmd, stage, pool, query);
        }
    }

    /// <summary>
    /// Submits via synchronization2. GraphicsDevice thread-safety spec D4a: guarded by
    /// <see cref="AssertCallerThread"/> (owner or the sanctioned loader thread — a loader legitimately
    /// submits, e.g. via <see cref="GpuUploader"/>) and takes <see cref="_queueLock"/>, the same lock
    /// <see cref="WaitIdle"/> takes, so a concurrent <c>vkDeviceWaitIdle</c> and submit are serialized
    /// rather than racing (the Vulkan spec's own external-synchronization requirement on both).
    /// </summary>
    internal void QueueSubmit2(Queue queue, SubmitInfo2* submit, Fence fence)
    {
        AssertCallerThread();
        lock (_queueLock)
        {
            if (HasVulkan13Core)
            {
                VkCheck.ThrowIfFailed(_vk.QueueSubmit2(queue, 1, submit, fence), "vkQueueSubmit2");
            }
            else
            {
                VkCheck.ThrowIfFailed(KhrSynchronization2!.QueueSubmit2(queue, 1, submit, fence), "vkQueueSubmit2KHR");
            }
        }
    }

    /// <summary>
    /// Legacy (non-sync2) submit — GraphicsDevice thread-safety spec D4b, replaces
    /// <see cref="GpuReadback"/>'s previously-raw, unlocked <c>vkQueueSubmit</c> call. Same guard and lock
    /// as <see cref="QueueSubmit2"/>, same error label as the call site it replaces.
    /// </summary>
    internal void QueueSubmit(Queue queue, SubmitInfo* submit, Fence fence)
    {
        AssertCallerThread();
        lock (_queueLock)
        {
            VkCheck.ThrowIfFailed(_vk.QueueSubmit(queue, 1, submit, fence), "vkQueueSubmit");
        }
    }

    /// <summary>
    /// GraphicsDevice thread-safety spec D5: routes <c>vkQueuePresentKHR</c> through the same
    /// <see cref="_queueLock"/> as submit. Guarded by <see cref="AssertOwnerThreadStrict"/>, not
    /// <see cref="AssertCallerThread"/> — a loader thread never presents, so a loader calling this is a bug
    /// worth catching, not a path to permit. <b>Returns the raw <see cref="Result"/> instead of throwing</b>
    /// (round-1 fix) — <c>Swapchain.Present</c> must keep inspecting
    /// <see cref="Result.ErrorOutOfDateKhr"/>/<see cref="Result.SuboptimalKhr"/> itself before treating
    /// anything else as a hard failure.
    /// </summary>
    internal Result QueuePresent(Queue queue, PresentInfoKHR* info)
    {
        AssertOwnerThreadStrict();
        lock (_queueLock)
        {
            return KhrSwapchain.QueuePresent(queue, info);
        }
    }
}
