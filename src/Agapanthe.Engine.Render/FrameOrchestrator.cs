using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Graphics;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Agapanthe.Engine.Render;

/// <summary>
/// The default frame assembly (P3-M2, decision D1/D3.c): it registers the engine's own systems in the one correct
/// order and hands back a cached render delegate. This is where the frame invariant — propagate transforms, fit the
/// cascades, cull, draw — finally LIVES, instead of being a sequence of statements in an application's closure that
/// nothing protected (spec §1).
/// <para>
/// <b>It is the RENDER half of the frame (MP-0a).</b> The simulation half — the world, the tick schedule, the frame
/// measurement — lives in <see cref="SimulationHost"/>, which this type composes and delegates to. A dedicated
/// server runs the host alone; only a client needs what is left here. The tick-side members below
/// (<see cref="Tick"/>, <see cref="EndFrame"/>, <see cref="Stats"/>, <see cref="Add(Stage, ISystem)"/>…) are kept as
/// forwarding members so applications do not have to know which half owns what.
/// </para>
/// <para>
/// <b>It owns nothing.</b> The <see cref="GameWorld"/>, the <see cref="Renderer"/>, the <see cref="ResourceRegistry"/>
/// and the render lists are borrowed references; their lifetime — and the 0-leak teardown order — stays with the
/// application. The orchestrator holds only the simulation host, the per-frame render scratch, and its render
/// delegate.
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

    // The simulation half. Composed, not inherited from and not merged into: everything it does is runnable with no
    // GPU, and keeping that literally true in the type graph is the point of the split.
    private readonly SimulationHost _simulation;

    // Decouples simulation speed from frame rate (MP-0c): Tick hands it the wall-clock delta and it drives
    // _simulation.Tick a whole number of fixed steps. Lives in Agapanthe.Engine (not here) so a headless real-time
    // server can reuse it without a FrameOrchestrator.
    private readonly FixedTimestepAccumulator _accumulator;

    // The render half's own schedule. It takes the world's structural barrier DIRECTLY rather than reaching through
    // the simulation host: the headless half must not have to know that a render scheduler exists at all.
    private readonly RenderSystemScheduler _renderScheduler;

    // Cached once (F1.i): FrameRenderer.DrawFrame takes an Action, and building it per frame would be one managed
    // allocation per frame — invisible to unit tests, fatal to the 0-alloc gate.
    private readonly Action<CommandList, FrameContext, SwapchainTarget> _renderDelegate;

    private FrameOrchestrator(
        SimulationHost simulation, GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera,
        RenderList render, float fixedTickDeltaSeconds, float maxWallClockDeltaSeconds)
    {
        _world = world;
        _renderer = renderer;
        _registry = registry;
        _camera = camera;
        _render = render;
        _simulation = simulation;
        _accumulator = new FixedTimestepAccumulator(fixedTickDeltaSeconds, maxWallClockDeltaSeconds);
        _renderScheduler = new RenderSystemScheduler(world.FlushStructuralChanges);

        _renderDelegate = (cmd, frame, target) =>
        {
            var ctx = new RenderContext(_simulation.CurrentTick, cmd, frame, target);
            _renderScheduler.Render(in ctx);
        };
    }

    /// <summary>
    /// Builds the orchestrator with the engine's default systems registered: PostSimulation propagates transforms
    /// (from <see cref="SimulationHost.CreateDefault"/>); Render fits the cascades, culls, and draws. The
    /// application adds its own systems (input, gameplay, a bench spinner) with <see cref="Add(Stage, ISystem)"/>
    /// BEFORE the first <see cref="Tick"/>.
    /// </summary>
    /// <param name="fixedTickDeltaSeconds">The fixed simulation step, seconds (MP-0c) — a PERIOD, not a rate.
    /// Default 1/60.</param>
    /// <param name="maxWallClockDeltaSeconds">The accumulator's input clamp, seconds — the anti-spiral-of-death
    /// ceiling. Default 250 ms.</param>
    public static FrameOrchestrator CreateDefault(
        GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera, RenderList render,
        float fixedTickDeltaSeconds = 1f / 60f, float maxWallClockDeltaSeconds = 0.25f)
        => CreateDefault(
            SimulationHost.CreateDefault(world), world, renderer, registry, camera, render,
            fixedTickDeltaSeconds, maxWallClockDeltaSeconds);

    /// <summary>
    /// Attaches presentation to an <b>existing</b> <see cref="SimulationHost"/>.
    /// <para>
    /// This is the overload that matches the engine cap. A simulation exists; a client may attach a presentation to
    /// it — not the other way round. The convenience overload above builds a host because a windowed application
    /// usually has no reason to hold one first, but a composition root that decides between "server only",
    /// "client + server" and "client only" must be able to build and configure the host itself, then hand it over.
    /// </para>
    /// </summary>
    /// <param name="fixedTickDeltaSeconds">The fixed simulation step, seconds (MP-0c) — a PERIOD, not a rate.
    /// Default 1/60.</param>
    /// <param name="maxWallClockDeltaSeconds">The accumulator's input clamp, seconds — the anti-spiral-of-death
    /// ceiling. Default 250 ms.</param>
    public static FrameOrchestrator CreateDefault(
        SimulationHost simulation, GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera,
        RenderList render, float fixedTickDeltaSeconds = 1f / 60f, float maxWallClockDeltaSeconds = 0.25f)
    {
        ArgumentNullException.ThrowIfNull(simulation);
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(render);

        var o = new FrameOrchestrator(
            simulation, world, renderer, registry, camera, render, fixedTickDeltaSeconds, maxWallClockDeltaSeconds);
        o._renderScheduler.Add(new SceneViewSystem(o));
        return o;
    }

    /// <summary>
    /// The simulation half: register systems on it, read its metrics.
    /// <para>
    /// <b>Do not drive the frame from here.</b> Calling <c>Simulation.Tick</c> or <c>Simulation.BeginFrame</c>
    /// alongside this type's <see cref="Tick"/> would double-tick the world or leave a measurement bracket open.
    /// The frame is driven through the orchestrator; everything else goes through this property.
    /// </para>
    /// </summary>
    public SimulationHost Simulation => _simulation;

    /// <summary>Registers an application simulation system (Input / Simulation / PostSimulation). Forwards to
    /// <see cref="SimulationHost.Add"/>: registration order is execution order, frozen at first tick.</summary>
    public void Add(Stage stage, ISystem system) => _simulation.Add(stage, system);

    /// <summary>The fixed simulation step the accumulator issues, seconds (MP-0c). In capture mode the host feeds
    /// this value as the wall-clock delta so a run is reproducible tick-for-tick.</summary>
    public float FixedTickDeltaSeconds => _accumulator.FixedDeltaSeconds;

    /// <summary>How many simulation ticks the last <see cref="Tick"/> ran (forwards
    /// <see cref="SimulationHost.LastFrameTickCount"/>): 1 in steady state, &gt; 1 while catching up, 0 for a frame
    /// faster than one step.</summary>
    public int LastFrameTickCount => _simulation.LastFrameTickCount;

    /// <summary>Registers an application render system (Render stage). Stays on this side: the simulation half must
    /// not learn that render systems exist.</summary>
    public void Add(IRenderSystem system) => _renderScheduler.Add(system);

    /// <summary>
    /// Advances the simulation for one rendered frame (D1.a). Call this ONCE per frame, OUTSIDE the render callback,
    /// then pass <see cref="RenderDelegate"/> to <c>FrameRenderer.DrawFrame</c>.
    /// <para>
    /// <paramref name="wallClockDeltaSeconds"/> is a wall-clock delta, not "the dt this tick uses": the
    /// <see cref="FixedTimestepAccumulator"/> consumes it and runs Input → Simulation → PostSimulation a whole
    /// number of fixed steps (0, 1, or several while catching up — MP-0c). <see cref="SimulationHost.BeginFrame"/>
    /// stays out of that loop so the frame's self-measurement records one sample per frame, not one per tick.
    /// </para>
    /// </summary>
    public void Tick(float wallClockDeltaSeconds)
        => _accumulator.AdvanceFrame(_simulation, wallClockDeltaSeconds);

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
    public void EndFrame() => _simulation.EndFrame();

    /// <summary>The Render-stage callback, allocated once. Hand it to <c>FrameRenderer.DrawFrame</c>; it is a no-op
    /// on a frame the renderer skips.</summary>
    public Action<CommandList, FrameContext, SwapchainTarget> RenderDelegate => _renderDelegate;

    // PropagateSystem moved to SimulationHost (MP-0a): recomputing world transforms from the Parent chain is
    // simulation, not rendering — a headless server needs it as much as a client does.
    //
    // AggregateBoundsSystem is GONE, not moved. It wrote _sceneBounds every frame and NOTHING read it: the comment
    // claiming the Render-stage light fit consumed it predates P3-M5, since when SceneViewSystem fits each cascade
    // to its own camera-frustum slice and never consults global bounds. It was an O(n) per frame with no observable
    // effect. GameWorld.AggregateBounds() stays — it is pure, tested (WorldSystemsTests, LifecycleTests), and a
    // future consumer (interest management, world-space UI) will want it; only the per-frame caller is removed.

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
