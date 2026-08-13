using System.Numerics;
using System.Diagnostics;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Agapanthe.Engine;

/// <summary>
/// The default frame assembly (P3-M2, decision D1/D3.c): it registers the engine's own systems into a
/// <see cref="SystemScheduler"/> in the one correct order and hands back a cached render delegate. This is where the
/// frame invariant — propagate transforms, aggregate bounds, fit the light, cull, draw — finally LIVES, instead of
/// being a sequence of statements in an application's closure that nothing protected (spec §1).
/// <para>
/// <b>It owns nothing.</b> The <see cref="GameWorld"/>, the <see cref="Renderer"/>, the <see cref="ResourceRegistry"/>
/// and the render lists are borrowed references; their lifetime — and the 0-leak teardown order — stays with the
/// application. The orchestrator holds only the scheduler, the per-frame scratch it computes (the scene bounds), and
/// its render delegate.
/// </para>
/// <para>
/// <b>Tick and render are separate on purpose (D1.a).</b> <see cref="Tick"/> runs Input → Simulation →
/// PostSimulation (each closed by the structural barrier) and must run EVERY frame; <see cref="RenderDelegate"/> runs
/// the Render stage and is handed to <c>FrameRenderer.DrawFrame</c>, which skips it when the swapchain is out of date.
/// Driving the simulation from inside that callback would freeze it on every window resize.
/// </para>
/// </summary>
public sealed class FrameOrchestrator
{
    private readonly GameWorld _world;
    private readonly Renderer _renderer;
    private readonly ResourceRegistry _registry;
    private readonly Camera _camera;
    private readonly RenderList _render;

    // The persistent scene-candidate set (P3-M6): the World maintains it (structural rebuild vs incremental patch)
    // from CollectRenderLists. Owned here; the GPU scene cull switches to consuming it in AW-007 (until then the
    // _render list still drives DrawScene). Not owned by the Renderer, which stays a borrowed reference.
    private readonly SceneCandidateSet _persistent = new();

    // CSM per-frame state (P3-M5), allocated once: the four cascade matrices, their split depths, and the frusta
    // derived from them. The frusta cross into DrawScene, where the GPU shadow cull tests the persistent candidates
    // against them (P3-M6 — the per-cascade CPU caster lists are gone).
    private const int CascadeCount = 4;
    private readonly Matrix4x4[] _cascades = new Matrix4x4[CascadeCount];
    private readonly float[] _splits = new float[CascadeCount];
    private readonly Frustum[] _cascadeFrusta = new Frustum[CascadeCount];
    // The per-cascade near-side view-depth cut plane (P3-M7 W3): appended to each cascade's shadow-cull planes so the
    // far cascades stop swallowing the near field (raster 4× → ~1×). Cascade 0's is an all-keeping tautology.
    private readonly Vector4[] _cascadeNearCutPlanes = new Vector4[CascadeCount];

    private readonly SystemScheduler _scheduler;

    // Cached once (F1.i): FrameRenderer.DrawFrame takes an Action, and building it per frame would be one managed
    // allocation per frame — invisible to unit tests, fatal to the 0-alloc gate.
    private readonly Action<CommandList, FrameContext, SwapchainTarget> _renderDelegate;

    // Per-frame scratch the orchestrator computes: the scene extent, produced by the PostSimulation aggregation and
    // consumed by the Render-stage light fit. Not owned state — recomputed from the world every tick.
    private Double3Bounds _sceneBounds = Double3Bounds.Empty;
    private float _dt;

    // Frame self-measurement (UI-2): opened in Tick, closed in EndFrame.
    private long _frameAllocStart;
    private long _frameTimestampStart;
    private int _bracketThreadId;
    private bool _measurementOpen;

    private FrameOrchestrator(
        GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera, RenderList render)
    {
        _world = world;
        _renderer = renderer;
        _registry = registry;
        _camera = camera;
        _render = render;

        // The structural barrier the scheduler runs at the end of every stage IS the world's deferred-change flush
        // (P3-M2 D2): a system enqueues spawns/despawns, the barrier applies them before the next stage iterates.
        _scheduler = new SystemScheduler(_world.FlushStructuralChanges);

        _renderDelegate = (cmd, frame, target) =>
        {
            var ctx = new RenderContext(new TickContext(_dt, _scheduler.FrameIndex), cmd, frame, target);
            _scheduler.Render(in ctx);
        };
    }

    /// <summary>
    /// Builds the orchestrator with the engine's default systems registered: PostSimulation propagates transforms
    /// then aggregates world bounds; Render fits the light, culls, and draws. The application adds its own systems
    /// (input, gameplay, a bench spinner) with <see cref="Add(Stage, ISystem)"/> BEFORE the first <see cref="Tick"/>.
    /// </summary>
    public static FrameOrchestrator CreateDefault(
        GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera, RenderList render)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(render);

        var o = new FrameOrchestrator(world, renderer, registry, camera, render);
        o._scheduler.Add(Stage.PostSimulation, new PropagateSystem(o));
        o._scheduler.Add(Stage.PostSimulation, new AggregateBoundsSystem(o));
        o._scheduler.Add(new SceneViewSystem(o));
        return o;
    }

    /// <summary>Registers an application simulation system (Input / Simulation / PostSimulation). See
    /// <see cref="SystemScheduler.Add(Stage, ISystem)"/>: registration order is execution order, frozen at first tick.</summary>
    public void Add(Stage stage, ISystem system) => _scheduler.Add(stage, system);

    /// <summary>Registers an application render system (Render stage).</summary>
    public void Add(IRenderSystem system) => _scheduler.Add(system);

    /// <summary>Monotonic tick counter (see <see cref="SystemScheduler.FrameIndex"/>).</summary>
    public long FrameIndex => _scheduler.FrameIndex;

    /// <summary>
    /// Runs Input → Simulation → PostSimulation for one frame. Call this ONCE per frame, OUTSIDE the render
    /// callback, then pass <see cref="RenderDelegate"/> to <c>FrameRenderer.DrawFrame</c> (D1.a).
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        // Belt and braces: if the previous frame never called EndFrame, close it here rather than silently dropping
        // its cost. Otherwise a caller who forgets the call gets a profiler frozen on one stale sample.
        if (_measurementOpen)
        {
            EndFrame();
        }

        _dt = deltaSeconds;
        // Open the frame's self-measurement (UI-2). Both reads are allocation-free.
        _frameAllocStart = GC.GetAllocatedBytesForCurrentThread();
        _frameTimestampStart = Stopwatch.GetTimestamp();
        _bracketThreadId = Environment.CurrentManagedThreadId;
        _measurementOpen = true;
        _scheduler.Tick(deltaSeconds);
    }

    /// <summary>
    /// Closes the frame's self-measurement and files it into <see cref="Stats"/>. Call it once per frame, right
    /// after handing <see cref="RenderDelegate"/> to <c>FrameRenderer.DrawFrame</c>.
    /// <para>
    /// <b>Why here and not at the end of the render delegate.</b> That delegate runs INSIDE command-buffer
    /// recording, so closing there would exclude <c>vkEndCommandBuffer</c>, the submit and the present — a blind
    /// spot on exactly the layer where a per-frame allocation is most likely to hide, in the readout whose entire
    /// job is to catch one. Closing here reproduces the bracket the cull-stats bench has always used, and it still
    /// excludes the host's windowing and input pump, which allocates and which the engine does not control.
    /// </para>
    /// <para>
    /// It also covers the frames where <c>DrawFrame</c> returns early (out-of-date swapchain, failed acquire) and
    /// never invokes the delegate at all: those are precisely the frames that recreate the swapchain, the most
    /// allocating path in the engine.
    /// </para>
    /// </summary>
    public void EndFrame()
    {
        if (!_measurementOpen)
        {
            return;
        }

        _measurementOpen = false;

        // GetAllocatedBytesForCurrentThread is a PER-THREAD counter: opening and closing the bracket on two
        // different threads yields an arbitrary delta, and a negative one reads as a perfectly healthy empty graph.
        // The engine is single-threaded today; MP-0 (tick decoupled from the frame) is the milestone that could
        // break this, so the invariant is anchored now while it costs nothing.
        Debug.Assert(
            Environment.CurrentManagedThreadId == _bracketThreadId,
            "Frame measurement must open and close on the same thread — the allocation counter is per-thread.");

        LastFrameAllocatedBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - _frameAllocStart);
        LastFrameMs = (float)Stopwatch.GetElapsedTime(_frameTimestampStart).TotalMilliseconds;
        Stats.Record(LastFrameMs, LastFrameAllocatedBytes);
    }

    /// <summary>
    /// The engine's own frame metrics, recorded every frame whether or not anything displays them.
    /// <para>
    /// Owned HERE rather than by the debug overlay: metrics are an engine concern, and hanging them off the overlay
    /// made them depend on a cooked font being present on disk — no font, no measurements at all.
    /// </para>
    /// </summary>
    public FrameStats Stats { get; } = new();

    /// <summary>
    /// Managed bytes the ENGINE allocated during the last completed frame — tick plus the whole of DrawFrame.
    /// <para>
    /// The bracket matters. Measuring from one tick to the next would also sweep up the host's windowing and input
    /// pump (Silk.NET/GLFW allocate there), which the engine does not control: the readout would sit permanently
    /// non-zero through no fault of the engine, and a permanently red indicator hides the regression it exists to
    /// catch. This bracket is exactly the one the cull-stats bench has always used.
    /// </para>
    /// </summary>
    public long LastFrameAllocatedBytes { get; private set; }

    /// <summary>
    /// Wall-clock duration of the last completed frame, same bracket as <see cref="LastFrameAllocatedBytes"/>.
    /// <b>Not a pure CPU cost</b>: the bracket spans the fence wait and the present, so under vsync this tracks the
    /// display period — which is what makes it the right number for an fps readout, and the wrong one for
    /// attributing a spike to engine code.
    /// </summary>
    public float LastFrameMs { get; private set; }

    /// <summary>The Render-stage callback, allocated once. Hand it to <c>FrameRenderer.DrawFrame</c>; it is a no-op
    /// on a frame the renderer skips.</summary>
    public Action<CommandList, FrameContext, SwapchainTarget> RenderDelegate => _renderDelegate;

    // System 1 (PostSimulation): recompute every hierarchical entity's world transform from its Parent chain.
    private sealed class PropagateSystem(FrameOrchestrator o) : ISystem
    {
        public void Execute(in TickContext ctx) => o._world.PropagateTransforms();
    }

    // System 2 (PostSimulation): re-aggregate the world extent every frame (P3-M1 debt #1). It must run AFTER
    // propagation (bounds derive from world transforms) and BEFORE the light fit, which the stage order guarantees.
    private sealed class AggregateBoundsSystem(FrameOrchestrator o) : ISystem
    {
        public void Execute(in TickContext ctx) => o._sceneBounds = o._world.AggregateBounds();
    }

    // The seam (Render stage): the ONE place that sees GameWorld and Renderer together. It runs the D3.c two-pass
    // shadow cull — wedge cull (pass 1) → fit the light to the caster bounds → compact against the light volume
    // (pass 2) — then draws. Two passes because the fit needs the casters' bounds and the caster cull needs the fit;
    // the wedge depends on neither, so pass 1 uses it alone to break the circularity (P3-M2 D3).
    private sealed class SceneViewSystem(FrameOrchestrator o) : IRenderSystem
    {
        public void Render(in RenderContext ctx)
        {
            // ONE view per frame (spec §3.3): the world narrows every object against view.Origin and the light fit
            // uses the same one. The camera carries no dependency on the systems that ran in Tick, so building the
            // view here rather than in Tick changes no pixel.
            var view = o._camera.CreateView();
            var cameraFrustum = Frustum.FromViewProjection(view.View * view.Projection);

            // Fit the CSM cascades FIRST (P3-M5): each is fitted to its own frustum slice — camera-only, so it needs
            // no caster bounds. That is what retires the P3-M2 two-pass wedge: with the fit independent of the
            // casters, the casters can simply be culled against the finished cascade volumes, in one pass.
            // Honour the renderer's cascade COUNT (audit M1): hard-coding 4 here left the tail matrices at
            // default(Matrix4x4) whenever someone set a smaller count — a zero matrix yields a degenerate frustum
            // that collects everything or nothing, doubles the shadow cost, and (splits[3] = 0) silently disables
            // the distance fade. No crash, no validation error: exactly the kind of trap worth closing.
            var count = Math.Clamp(o._renderer.Cascades.Count, 1, CascadeCount);
            var cascades = o._cascades.AsSpan(0, count);
            var splits = o._splits.AsSpan(0, count);
            var nearCuts = o._cascadeNearCutPlanes.AsSpan(0, count);
            o._renderer.ComputeCascades(in view, cascades, splits, nearCuts);

            for (var c = 0; c < count; c++)
            {
                o._cascadeFrusta[c] = Frustum.FromViewProjection(cascades[c]);
            }

            // The shader always reads a vec4 of splits and treats splits[3] as the shadowed range. With fewer than
            // four cascades the unused lanes repeat the last real split, so the range stays true and the selection
            // loop simply never picks a padded lane (LightsUniforms repeats the matrices the same way).
            var last = splits[count - 1];
            var splitVec = new Vector4(
                splits[0],
                count > 1 ? splits[1] : last,
                count > 2 ? splits[2] : last,
                count > 3 ? splits[3] : last);

            // Collect the scene candidates into the persistent set (structural rebuild vs incremental patch, P3-M6).
            // The shadow casters are no longer bucketed on the CPU: the GPU shadow cull (in DrawScene) tests the same
            // persistent candidates against the cascade frusta (P3-M6 W3 — the P3-M5 per-cascade CPU lists are gone).
            o._world.CollectRenderLists(o._render, o._persistent, in view);

            // Both the camera frustum (scene cull) and the cascade frusta (shadow cull) cross into DrawScene, which
            // runs both GPU culls against the persistent candidate buffer.
            o._renderer.DrawScene(
                o._persistent, o._cascadeFrusta.AsSpan(0, count), nearCuts, o._registry, in view, in cameraFrustum,
                cascades, splitVec, ctx.Cmd, ctx.Frame, ctx.Target);
        }
    }
}
