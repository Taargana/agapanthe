namespace Agapanthe.Tests;

/// <summary>ImGui debug-overlay spec, Verification item 1a: the mandatory spike gate before anything else
/// in the milestone. If this fails, nothing downstream (Vulkan backend, ImGuiDebugSystem) should proceed.</summary>
public class ImGuiContextProbeTests
{
    [Fact]
    public void RunSmokeFrame_ProducesNonEmptyDrawData()
    {
        var vertexCount = Agapanthe.DebugUi.ImGuiContextProbe.RunSmokeFrame();

        Assert.True(vertexCount > 0, "ImGui smoke frame produced no vertices — the binding is not functional.");
    }
}
