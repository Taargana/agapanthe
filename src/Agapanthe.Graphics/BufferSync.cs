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

    /// <summary>A transfer/copy writes the buffer (P3-M7: staging → device-local per-frame copy).</summary>
    TransferWrite,

    /// <summary>A transfer/copy reads the buffer as the copy source (P3-M7).</summary>
    TransferRead,
}

/// <summary>
/// One region of a <see cref="CommandList.CopyBuffer"/> (P3-M7): copy <see cref="Size"/> bytes from
/// <see cref="SourceOffset"/> in the source buffer to <see cref="DestinationOffset"/> in the destination. A full
/// rewrite is one region spanning the whole payload; a dirty-slot replay is one region per changed slot.
/// </summary>
public readonly record struct BufferCopyRegion(ulong SourceOffset, ulong DestinationOffset, ulong Size);
