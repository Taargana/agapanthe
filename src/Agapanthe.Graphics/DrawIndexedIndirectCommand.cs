using System.Runtime.InteropServices;

namespace Agapanthe.Graphics;

/// <summary>
/// One indexed indirect draw command (P3-M4), byte-identical to <c>VkDrawIndexedIndirectCommand</c> so an
/// array of these can be written straight into a host-visible <see cref="BufferUsage.Indirect"/> buffer and
/// consumed by <see cref="CommandList.DrawIndexedIndirect"/>. A compute shader may also produce/patch these
/// on the GPU (it writes <see cref="InstanceCount"/> after a cull — P3-M4 W1).
/// <para>
/// Field order and 20-byte size are load-bearing: they mirror the Vulkan struct exactly
/// (<c>indexCount, instanceCount, firstIndex, vertexOffset, firstInstance</c>). A struct-layout test guards
/// it — a reordering would silently corrupt every indirect draw.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DrawIndexedIndirectCommand
{
    public uint IndexCount;
    public uint InstanceCount;
    public uint FirstIndex;
    public int VertexOffset;
    public uint FirstInstance;

    public DrawIndexedIndirectCommand(uint indexCount, uint instanceCount, uint firstIndex, int vertexOffset, uint firstInstance)
    {
        IndexCount = indexCount;
        InstanceCount = instanceCount;
        FirstIndex = firstIndex;
        VertexOffset = vertexOffset;
        FirstInstance = firstInstance;
    }
}
