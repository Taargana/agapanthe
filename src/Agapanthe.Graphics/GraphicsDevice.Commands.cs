using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// Dispatch helpers for dynamic rendering and synchronization2 commands. On MoltenVK
/// (and any device declaring Vulkan 1.3) these route to the core entry points; on a
/// 1.2 device they route to the KHR extension objects. Callers stay backend-agnostic.
/// </summary>
public sealed unsafe partial class GraphicsDevice
{
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

    internal void QueueSubmit2(Queue queue, SubmitInfo2* submit, Fence fence)
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
