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
    private readonly RenderList _shadowCasters;

    private readonly SystemScheduler _scheduler;

    // Cached once (F1.i): FrameRenderer.DrawFrame takes an Action, and building it per frame would be one managed
    // allocation per frame — invisible to unit tests, fatal to the 0-alloc gate.
    private readonly Action<CommandList, FrameContext, SwapchainTarget> _renderDelegate;

    // Per-frame scratch the orchestrator computes: the scene extent, produced by the PostSimulation aggregation and
    // consumed by the Render-stage light fit. Not owned state — recomputed from the world every tick.
    private Double3Bounds _sceneBounds = Double3Bounds.Empty;
    private float _dt;

    private FrameOrchestrator(
        GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera,
        RenderList render, RenderList shadowCasters)
    {
        _world = world;
        _renderer = renderer;
        _registry = registry;
        _camera = camera;
        _render = render;
        _shadowCasters = shadowCasters;

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
        GameWorld world, Renderer renderer, ResourceRegistry registry, Camera camera,
        RenderList render, RenderList shadowCasters)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(shadowCasters);

        var o = new FrameOrchestrator(world, renderer, registry, camera, render, shadowCasters);
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
            var lightDir = o._renderer.Lights.Directional.Direction;
            var cameraFrustum = Frustum.FromViewProjection(view.View * view.Projection);

            // The wedge is bounded a finite distance upstream (D3.a), anchored on the camera frustum's sphere (the
            // same sphere the fit uses), so a far-upstream caster can never enter the caster list and blow out the
            // shadow map's depth range.
            var (anchorCenter, anchorRadius) = o._renderer.ComputeFrustumSphere(in view);
            var wedge = ExtrudedShadowFrustum.FromCameraFrustum(
                in cameraFrustum, lightDir, anchorCenter, anchorRadius, o._renderer.ShadowCasterDistance);

            // Collect: scene CANDIDATES (all drawables, sorted, sphere-carrying — the camera cull is done on the GPU
            // now, P3-M4); wedge cull → shadow casters (superset) + their bounds (still CPU two-pass, P3-M2 D3).
            o._world.CollectRenderLists(
                o._render, o._shadowCasters, in view, in wedge, out var casterBounds);

            // Fit: footprint on the scene bounds, depth range on the caster bounds (D3.b). The wedge list is a
            // superset of the final list, so fitting on its bounds can only grow the depth range — never clip a
            // survivor of pass 2.
            var lightViewProj = o._renderer.ComputeLightViewProj(in view, in o._sceneBounds, in casterBounds);
            var lightFrustum = Frustum.FromViewProjection(lightViewProj);

            // Pass 2: tighten the caster list against the fitted light volume, in place, then sort.
            o._world.CompactShadowCasters(o._shadowCasters, in lightFrustum);

            // The camera frustum crosses into DrawScene, where the compute pass culls the scene candidates against it.
            o._renderer.DrawScene(
                o._render, o._shadowCasters, o._registry, in view, in cameraFrustum, in lightViewProj,
                ctx.Cmd, ctx.Frame, ctx.Target);
        }
    }
}
