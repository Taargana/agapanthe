using System.Numerics;
using Agapanthe.Assets.Font;
using Agapanthe.Core;
using Agapanthe.Rendering;
using Agapanthe.Ui;

namespace Agapanthe.Engine.Render;


/// <summary>
/// The engine's debug overlay: frame time, per-frame managed allocation, draw counts, and two graphs (UI-2).
/// <para>
/// <b>It lives in the engine, not in the sample.</b> The same information used to be hardcoded in the Sandbox's
/// title-bar HUD, which meant every application would have rewritten it. Here any app enables it in one line.
/// </para>
/// <para>
/// <b>The allocation readout is the point.</b> "Zero managed allocation per frame in steady state" is a blocking
/// project gate that was previously only ever checked in a bench run; drawn every frame, a regression becomes
/// visible the moment it is introduced, in whatever scene happens to be open.
/// </para>
/// <para>
/// Runs in <see cref="Stage.PostSimulation"/>, after the tick stages have done their work and after
/// <see cref="UiRenderSystem"/> cleared the draw list in <see cref="Stage.Input"/>.
/// </para>
/// </summary>
public sealed class DebugOverlaySystem : ISystem
{
    // Text and panel geometry, in framebuffer pixels.
    private const float Margin = 12f;
    private const float LineHeight = 17f;
    private const float TextSize = 13f;
    private const float PanelWidth = 300f;
    private const float GraphHeight = 34f;

    private const uint TextColour = 0xE8E8E8FFu;
    private const uint LabelColour = 0x9AA0AAFFu;
    private const uint PanelColour = 0x0C0F14C8u; // translucent: the scene stays readable underneath
    private const uint FrameGraphColour = 0x5AC8FAFFu;
    private const uint AllocGraphGoodColour = 0x4CD97BFFu; // green while the 0-alloc gate holds
    private const uint AllocGraphBadColour = 0xFF6B4AFFu; // red the moment it does not

    /// <summary>Frame-time budget the graph scales to at minimum, so a smooth 60 Hz reads flat instead of being
    /// auto-scaled into fake spikes.</summary>
    private const float FrameBudgetMs = 1000f / 60f;

    private readonly UiDrawList _drawList;
    private readonly FontAsset _font;
    private readonly Renderer _renderer;
    private readonly RenderList _renderList;
    private readonly FrameStats _stats;
    private readonly float[] _graphScratch;

    public DebugOverlaySystem(
        UiDrawList drawList, FontAsset font, Renderer renderer, RenderList renderList, FrameStats stats)
    {
        ArgumentNullException.ThrowIfNull(drawList);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(renderList);
        ArgumentNullException.ThrowIfNull(stats);

        _drawList = drawList;
        _font = font;
        _renderer = renderer;
        _renderList = renderList;
        _stats = stats;
        // Sized from the HISTORY, not from a pixel width: the two happen to be compatible today (300 >= 240), but
        // raising FrameStats.DefaultCapacity would then silently truncate the graph. Allocated ONCE — a per-frame
        // stackalloc of this size would be a per-frame memset, and this system exists to keep per-frame cost honest.
        _graphScratch = new float[stats.FrameTimeMs.Capacity];
    }

    /// <summary>The metrics history. Owned by the SimulationHost (which records it every frame whether or not this
    /// overlay exists), read here only to display it.
    /// <para>
    /// Taken as a <see cref="FrameStats"/> rather than as the concrete orchestrator (MP-0a audit): the data belongs
    /// to the simulation half, and depending on the render-side type to reach it was what left this system
    /// untestable in UI-2.
    /// </para></summary>
    public FrameStats Stats => _stats;

    /// <summary>Whether the overlay draws. Statistics keep being recorded either way — see <see cref="Execute"/>.</summary>
    public bool Visible { get; set; } = true;

    /// <summary>Flips <see cref="Visible"/>. Bind it to a key.</summary>
    public void Toggle() => Visible = !Visible;

    public void Execute(in TickContext ctx)
    {
        // Recording is the ORCHESTRATOR's job (it owns the frame bracket and files every frame into Stats), so the
        // history stays complete whether this overlay exists, is hidden, or was never created for lack of a font.
        // What is displayed is therefore the previous frame — one frame of lag, invisible on a graph.
        if (!Visible)
        {
            return;
        }

        var y = Margin;
        var panelHeight = (LineHeight * 3) + (GraphHeight * 2) + (Margin * 3);
        _drawList.AddRect(
            new Vector4(Margin, Margin, Margin + PanelWidth, Margin + panelHeight),
            _font.WhiteTexelUv,
            PanelColour);

        y += Margin * 0.5f;
        var x = Margin * 1.5f;

        // ---- Line 1: frame time -------------------------------------------------------------------------------
        // One DrawText PER LINE, never one multi-line call: the layout truncates past 256 glyphs per call, and a
        // few lines of HUD would sit uncomfortably close to that ceiling.
        Span<char> buffer = stackalloc char[128];
        var line = new TextBuilder(buffer);
        line.Append(Stats.AverageFps, "F0");
        line.Append(" fps   ");
        line.Append(Stats.FrameTimeMs.Last, "F2");
        line.Append(" ms   peak ");
        line.Append(Stats.FrameTimeMs.Max, "F1");
        TextLayout.DrawText(_drawList, line.Written, _font, new Vector2(x, y), TextSize, TextColour);
        y += LineHeight;

        // ---- Line 2: allocation — the gate made visible -------------------------------------------------------
        var lastAlloc = Stats.LastAllocatedBytes; // exact; the float series is for the graph only
        var peakAlloc = (long)Stats.AllocatedBytes.Max;
        line = new TextBuilder(buffer);
        line.Append("alloc ");
        line.Append(lastAlloc);
        line.Append(" B/frame   peak ");
        line.Append(peakAlloc);
        line.Append(" B");
        // Coloured on the CURRENT frame, not on the peak. The peak stays in the window for its whole retention
        // (~4 s), and warm-up growth alone would keep the line red long after the gate had gone back to holding —
        // an indicator that cries wolf gets ignored, which is the one failure mode it cannot afford.
        TextLayout.DrawText(
            _drawList, line.Written, _font, new Vector2(x, y), TextSize,
            lastAlloc > 0 ? AllocGraphBadColour : AllocGraphGoodColour);
        y += LineHeight;

        // ---- Line 3: draw counts ------------------------------------------------------------------------------
        line = new TextBuilder(buffer);
        line.Append("draws ");
        line.Append(_renderer.LastSceneDrawCalls);
        line.Append('+');
        line.Append(_renderer.LastShadowDrawCalls);
        line.Append("   candidates ");
        line.Append(_renderList.Count);
        TextLayout.DrawText(_drawList, line.Written, _font, new Vector2(x, y), TextSize, LabelColour);
        y += LineHeight + (Margin * 0.5f);

        // ---- Graphs -------------------------------------------------------------------------------------------
        var graphWidth = PanelWidth - (Margin * 1.5f) - (Margin * 0.5f);

        var frames = Stats.FrameTimeMs.CopyChronological(_graphScratch);
        Sparkline.Draw(
            _drawList, frames, new Vector4(x, y, x + graphWidth, y + GraphHeight), _font.WhiteTexelUv,
            FrameGraphColour,
            // Floor at the 60 Hz budget so a steady frame rate reads flat rather than being auto-scaled into
            // spikes that are really just noise.
            scaleMax: MathF.Max(Stats.FrameTimeMs.Max, FrameBudgetMs));
        y += GraphHeight + (Margin * 0.5f);

        var allocs = Stats.AllocatedBytes.CopyChronological(_graphScratch);
        Sparkline.Draw(
            _drawList, allocs, new Vector4(x, y, x + graphWidth, y + GraphHeight), _font.WhiteTexelUv,
            AllocGraphBadColour,
            // Scaled on the CURRENT frame, floored by a small budget — NOT on the window's max. Same reasoning as
            // the text colour above: a single 400 KiB warm-up spike stays in the window for ~4 s, and against that
            // scale a real 512 B/frame regression rasterises to 0.04 px, i.e. an empty graph while the gate is
            // broken. When the gate holds every sample is 0 and the graph is honestly empty.
            scaleMax: MathF.Max(Stats.AllocatedBytes.Last * 4f, MathF.Min(Stats.AllocatedBytes.Max, 4096f)));
    }
}
