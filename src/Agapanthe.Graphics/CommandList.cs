using Silk.NET.Vulkan;

namespace Agapanthe.Graphics;

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

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
        => _device.Api.CmdDraw(_buffer, vertexCount, instanceCount, firstVertex, firstInstance);
}
