#if !MASTER
using System.Numerics;
using Hexa.NET.ImGui;

namespace Agapanthe.DebugUi;

/// <summary>
/// Spike (ImGui debug-overlay spec, Verification item 1a): proves the binding itself works under this
/// project's constraints — no Vulkan, no window, just the C# wrapper + native cimgui. Exercised by
/// <c>Agapanthe.Tests</c>; not part of the runtime render path (see <see cref="ImGuiDebugSystem"/> for that).
/// </summary>
internal static class ImGuiContextProbe
{
    /// <summary>Creates a context, runs one frame with a single <c>Text</c> call, and returns the resulting
    /// <see cref="ImDrawData"/>'s total vertex count — non-zero proves layout actually happened. Always
    /// destroys the context it created, even on throw.</summary>
    internal static unsafe int RunSmokeFrame()
    {
        var context = ImGui.CreateContext();
        ImGui.SetCurrentContext(context);
        try
        {
            var io = ImGui.GetIO();
            // Real bug found live: a stray imgui.ini left in the process's working directory by an earlier
            // Sandbox run (ImGui's own default persistence, CWD-relative) made this SAME code silently produce
            // 0 vertices when run under `dotnet test` (whose working directory picked up that file) — reproduced
            // and confirmed in isolation. This spike must be hermetic regardless of what is on disk; ini
            // persistence itself is explicitly out of scope for this milestone (spec's Deferred section).
            io.IniFilename = null;
            io.DisplaySize = new Vector2(1280f, 720f);
            io.DeltaTime = 1f / 60f;

            // Real finding, spec D2's flagged risk confirmed: this Hexa.NET.ImGui version wraps Dear ImGui
            // 1.92+, which reworked font-atlas handling entirely — ImFontAtlasPtr has no GetTexDataAsRGBA32/
            // Build anymore (confirmed by reflection: only Add*/Clear*/Remove* methods remain). Texture data
            // now flows through ImDrawData.Textures (an ImVector<ImTextureDataPtr>) each frame; a real backend
            // must declare it handles this dynamically via BackendFlags. AW-008/009 (the real Vulkan backend)
            // will consume ImDrawData.Textures for real; this spike only needs the assert to not fire.
            io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;

            ImGui.NewFrame();
            ImGui.Text("smoke");
            ImGui.Render();

            var drawData = ImGui.GetDrawData();
            return drawData.TotalVtxCount;
        }
        finally
        {
            ImGui.DestroyContext(context);
        }
    }
}
#endif
