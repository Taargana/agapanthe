using System.Numerics;
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

    private readonly SystemScheduler _scheduler;

    // Cached once (F1.i): FrameRenderer.DrawFrame takes an Action, and building it per frame would be one managed
    // allocation per frame — invisible to unit tests, fatal to the 0-alloc gate.
    private readonly Action<CommandList, FrameContext, SwapchainTarget> _renderDelegate;

    // Per-frame scratch the orchestrator computes: the scene extent, produced by the PostSimulation aggregation and
    // consumed by the Render-stage light fit. Not owned state — recomputed from the world every tick.
    private Double3Bounds _sceneBounds = Double3Bounds.Empty;
    private float _dt;

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
        _dt = deltaSeconds;
        _scheduler.Tick(deltaSeconds);
    }

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
            o._renderer.ComputeCascades(in view, cascades, splits);

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
                o._persistent, o._cascadeFrusta.AsSpan(0, count), o._registry, in view, in cameraFrustum,
                cascades, splitVec, ctx.Cmd, ctx.Frame, ctx.Target);
        }
    }
}
