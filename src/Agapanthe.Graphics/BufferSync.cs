namespace Agapanthe.Graphics;

/// <summary>
/// A buffer synchronization scope for <see cref="CommandList.BufferBarrier"/> (P3-M4): a (pipeline stage,
/// access) pair, named by intent rather than by raw Vulkan flags so callers in the Rendering layer never
/// touch <c>PipelineStageFlags2</c>/<c>AccessFlags2</c> (the mono-backend seam, CLAUDE.md). The set grows as
/// milestones need it — these are the states the GPU-driven cull requires.
/// </summary>
public enum BufferSync
{
    /// <summary>A compute shader writes the buffer (cull output: instance transforms, indirect args).</summary>
    ComputeWrite,

    /// <summary>A compute shader reads the buffer (cull input: candidates).</summary>
    ComputeRead,

    /// <summary>The draw-indirect stage reads the buffer as command arguments (<c>vkCmdDrawIndexedIndirect</c>).</summary>
    IndirectRead,

    /// <summary>The vertex shader reads the buffer as a storage buffer (per-instance transforms).</summary>
    VertexStorageRead,
}
