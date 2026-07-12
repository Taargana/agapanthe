using Agapanthe.Core;
using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

/// <summary>
/// Builds a <c>VkPipelineLayout</c> from Agapanthe descriptor set layouts and push-constant ranges,
/// shared by <see cref="GraphicsPipeline"/> and <see cref="ComputePipeline"/> (the layout build is
/// identical for both bind points). Kept <see langword="internal"/> so the returned
/// <see cref="PipelineLayout"/> (a Vulkan type) never leaves the assembly (spec: no <c>Vk*</c> type
/// escapes <c>Agapanthe.Graphics</c>). Registers the created layout with the
/// <see cref="ResourceTracker"/>; the owning pipeline is responsible for its destruction/unregister.
/// </summary>
internal static class PipelineLayoutBuilder
{
    /// <summary>
    /// Creates a pipeline layout over <paramref name="setLayouts"/> (in set-index order) and
    /// <paramref name="pushConstants"/>. Both lists may be empty. The set layouts are owned by the caller
    /// and merely referenced. Registers a <c>VkPipelineLayout</c> in the <see cref="ResourceTracker"/> on
    /// success.
    /// </summary>
    internal static unsafe PipelineLayout Create(
        GraphicsDevice device,
        IReadOnlyList<DescriptorSetLayout> setLayouts,
        IReadOnlyList<PushConstantRange> pushConstants)
    {
        var vk = device.Api;

        var vkSetLayouts = stackalloc Silk.NET.Vulkan.DescriptorSetLayout[Math.Max(1, setLayouts.Count)];
        for (var i = 0; i < setLayouts.Count; i++)
        {
            vkSetLayouts[i] = setLayouts[i].Handle;
        }

        var pushRanges = stackalloc Silk.NET.Vulkan.PushConstantRange[Math.Max(1, pushConstants.Count)];
        for (var i = 0; i < pushConstants.Count; i++)
        {
            var range = pushConstants[i];
            pushRanges[i] = new Silk.NET.Vulkan.PushConstantRange
            {
                Offset = range.Offset,
                Size = range.Size,
                StageFlags = DescriptorSetLayout.ToVkStages(range.Stages),
            };
        }

        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = (uint)setLayouts.Count,
            PSetLayouts = setLayouts.Count > 0 ? vkSetLayouts : null,
            PushConstantRangeCount = (uint)pushConstants.Count,
            PPushConstantRanges = pushConstants.Count > 0 ? pushRanges : null,
        };

        PipelineLayout layout;
        VkCheck.ThrowIfFailed(
            vk.CreatePipelineLayout(device.Device, &layoutInfo, null, &layout), "vkCreatePipelineLayout");
        ResourceTracker.Register("VkPipelineLayout");
        return layout;
    }
}
