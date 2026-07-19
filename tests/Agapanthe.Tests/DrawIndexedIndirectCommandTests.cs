using System.Runtime.InteropServices;
using Agapanthe.Graphics;

namespace Agapanthe.Tests;

/// <summary>
/// Guards the <see cref="DrawIndexedIndirectCommand"/> memory layout (P3-M4): it is written straight into a
/// GPU indirect-args buffer and read by <c>vkCmdDrawIndexedIndirect</c> as <c>VkDrawIndexedIndirectCommand</c>,
/// so its size and field offsets must match that Vulkan struct exactly. A silent reordering here would corrupt
/// every indirect draw with no validation error.
/// </summary>
public sealed class DrawIndexedIndirectCommandTests
{
    [Fact]
    public void Layout_MatchesVkDrawIndexedIndirectCommand()
    {
        Assert.Equal(20, Marshal.SizeOf<DrawIndexedIndirectCommand>());
        Assert.Equal(0, (int)Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.IndexCount)));
        Assert.Equal(4, (int)Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.InstanceCount)));
        Assert.Equal(8, (int)Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.FirstIndex)));
        Assert.Equal(12, (int)Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.VertexOffset)));
        Assert.Equal(16, (int)Marshal.OffsetOf<DrawIndexedIndirectCommand>(nameof(DrawIndexedIndirectCommand.FirstInstance)));
    }
}
