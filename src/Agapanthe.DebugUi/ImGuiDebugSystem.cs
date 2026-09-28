#if !MASTER
using System.Diagnostics;
using System.Numerics;
using Agapanthe.App;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Engine.Render;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Hexa.NET.ImGui;
using SilkMouseButton = Silk.NET.Input.MouseButton;

namespace Agapanthe.DebugUi;

/// <summary>
/// The engine's native ImGui debug overlay (F3 replacement) — v1 content parity with the deleted
/// <c>DebugOverlaySystem</c> (fps/frame-time, alloc/frame + peak, draws/candidates, per-pass GPU timings) plus
/// one real interactive widget (a "Close" button, D7) to prove the input plumbing end to end.
/// <para>
/// <b>Only <see cref="IRenderSystem"/>, never <see cref="ISystem"/></b> (D-cadence): the ImGui
/// <c>NewFrame</c>→widgets→<c>Render()</c> cycle must run exactly once per actually-rendered frame — confined
/// entirely to <see cref="Render"/>, never split across <c>Stage.PostSimulation</c> ticks.
/// </para>
/// </summary>
public sealed unsafe class ImGuiDebugSystem : IRenderSystem, IDisposable
{
    private readonly Renderer _renderer;
    private readonly RenderList _renderList;
    private readonly FrameStats _stats;
    private readonly IWindow _window;
    private readonly ImGuiVulkanBackend _backend;
    private readonly ImGuiContextPtr _context;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private double _lastElapsedSeconds;
    private bool _disposed;

    public ImGuiDebugSystem(
        GraphicsDevice device, Renderer renderer, RenderList renderList, FrameStats stats, IWindow window,
        bool startVisible)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(renderList);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(window);

        _renderer = renderer;
        _renderList = renderList;
        _stats = stats;
        _window = window;

        _context = ImGui.CreateContext();
        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();
        // ini persistence is explicitly out of scope for this milestone (spec's Deferred section) — disable it
        // outright rather than leave ImGui's own CWD-relative default half-enabled. A real bug found live:
        // this DID write an imgui.ini next to wherever the process ran from, and that stray file later made an
        // unrelated hermetic test (ImGuiContextProbeTests) silently produce 0 vertices when `dotnet test`'s
        // working directory picked it up.
        io.IniFilename = null;
        // ImGui 1.92+ dynamic-texture-update path (AW-007a finding: no classic upfront atlas-upload API exists
        // anymore) — declares that ImGuiVulkanBackend consumes ImDrawData.Textures itself, per frame.
        io.BackendFlags |= ImGuiBackendFlags.RendererHasTextures;

        try
        {
            _backend = new ImGuiVulkanBackend(device, renderer.SwapchainColorFormat);
        }
        catch
        {
            ImGui.DestroyContext(_context);
            throw;
        }

        Visible = startVisible;
    }

    /// <summary>Whether the panel draws. Bind <see cref="Toggle"/> to F3 (patron <c>DebugOverlaySystem</c>).</summary>
    public bool Visible { get; set; }

    public void Toggle() => Visible = !Visible;

    public void Render(in RenderContext ctx)
    {
        if (!Visible)
        {
            return;
        }

        ImGui.SetCurrentContext(_context);
        var io = ImGui.GetIO();

        // Real wall-clock dt (D4, round 2 🟠) — NOT ctx.Tick, which is the fixed simulation step.
        var now = _stopwatch.Elapsed.TotalSeconds;
        io.DeltaTime = _lastElapsedSeconds > 0 ? (float)Math.Max(now - _lastElapsedSeconds, 1e-6) : 1f / 60f;
        _lastElapsedSeconds = now;

        io.DisplaySize = new Vector2(ctx.Target.Width, ctx.Target.Height);

        var mousePos = _window.MousePosition;
        io.AddMousePosEvent(mousePos.X, mousePos.Y);
        io.AddMouseButtonEvent(0, _window.IsMouseButtonDown(SilkMouseButton.Left));
        io.AddMouseButtonEvent(1, _window.IsMouseButtonDown(SilkMouseButton.Right));
        io.AddMouseButtonEvent(2, _window.IsMouseButtonDown(SilkMouseButton.Middle));

        var scroll = _window.ScrollDelta;
        if (scroll != Vector2.Zero)
        {
            io.AddMouseWheelEvent(scroll.X, scroll.Y);
        }

        ImGui.NewFrame();
        DrawPanel();
        ImGui.Render();

        // D6: WantCaptureMouse arbitrates the FPS-look camera capture, via the promoted IWindow member (D5).
        _window.CaptureMouseOnClick = !io.WantCaptureMouse;

        var drawData = ImGui.GetDrawData();
        _backend.UpdateTextures(drawData);
        _backend.Render(ctx.Cmd, ctx.Frame, ctx.Frame.Slot, ctx.Target, drawData);
    }

    private void DrawPanel()
    {
        ImGui.SetNextWindowSize(new Vector2(340f, 200f), ImGuiCond.FirstUseEver);
        ImGui.Begin("Agapanthe Debug");

        ImGui.Text($"{_stats.AverageFps:F0} fps   {_stats.FrameTimeMs.Last:F2} ms   peak {_stats.FrameTimeMs.Max:F1}");

        // Coloured on the CURRENT frame (patron DebugOverlaySystem) — the peak stays in the window for its whole
        // retention, so colouring on it alone would keep the line red long after the 0-alloc gate held again.
        var lastAlloc = _stats.LastAllocatedBytes;
        var peakAlloc = (long)_stats.AllocatedBytes.Max;
        var allocColor = lastAlloc > 0 ? new Vector4(1f, 0.42f, 0.29f, 1f) : new Vector4(0.30f, 0.85f, 0.48f, 1f);
        ImGui.TextColored(allocColor, $"alloc {lastAlloc} B/frame   peak {peakAlloc} B");

        ImGui.Text($"draws {_renderer.LastSceneDrawCalls}+{_renderer.LastShadowDrawCalls}   candidates {_renderList.Count}");

        if (_renderer.SupportsGpuTimestamps)
        {
            var t = _renderer.LastGpuPassTimingsMs;
            ImGui.Text(
                $"gpu  shadow {FormatMs(t.Shadow)}  scene {FormatMs(t.Scene)}  tonemap {FormatMs(t.Tonemap)}  " +
                $"ui {FormatMs(t.Ui)} ms");
        }

        // D7: the one real interactive widget — proves the click-handling path end to end, not just window
        // drag/resize (which ImGui's own chrome already exercises for free).
        if (ImGui.Button("Close"))
        {
            Visible = false;
        }

        ImGui.End();
    }

    // A field absent this frame (region didn't run) must read visibly different from a genuine 0.00 ms sample
    // (patron DebugOverlaySystem.AppendMsOrPlaceholder).
    private static string FormatMs(float? ms) => ms is { } value ? value.ToString("F2") : "--";

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        // Restore the camera-capture default (D5) — otherwise a disposed panel could leave the camera
        // permanently non-capturable if it was hidden mid-hover.
        _window.CaptureMouseOnClick = true;
        _backend.Dispose();
        ImGui.SetCurrentContext(_context);
        ImGui.DestroyContext(_context);
    }
}
#endif
