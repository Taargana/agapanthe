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
/// Thin recording surface over a command buffer, handed to the per-frame draw callback.
/// The frame loop owns image transitions and BeginRendering/EndRendering; the callback
/// only issues draw work. The exposed API grows as milestones need it.
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
}
