using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Engine.Render;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0a W1 — the render-stage neutrality guard: <b>running the Render stage must not alter simulation state.</b>
/// <para>
/// This is the invariant the whole engine cap rests on ("same simulation code everywhere, only authority changes").
/// A dedicated server runs Input → Simulation → PostSimulation and stops; a client runs the Render stage as well.
/// If that extra stage moves the simulation by so much as one bit, client and server diverge, and no other test in
/// this suite would notice.
/// </para>
/// <para>
/// <b>It passes today, and the code says why</b> — this is a ratchet, not an experiment. Collection does not ADD
/// <c>InstanceSlot</c>: <c>GameWorld.cs:807</c> is <c>entity.Set(...)</c>, an in-place write; every creation path
/// adds the component at spawn (<c>GameWorld.cs:214</c>, <c>GameWorld.Physics.cs:105</c>) and the load path re-adds
/// it (<c>WorldSerialization.cs:219</c>); and the gather query REQUIRES it (<c>GameWorld.cs:52</c>), so an entity
/// lacking it is skipped rather than migrated to a new archetype. No archetype churn, therefore no change in Arch
/// chunk iteration order, therefore no divergence.
/// </para>
/// <para>
/// What it guards is the future: the day a render-stage system writes a component, or enqueues a structural command
/// whose materialisation the headless host would never perform, this test goes red — at the commit that introduces
/// it rather than months later in a multiplayer desync.
/// </para>
/// <para>
/// GPU-free by construction: the three parameters of <see cref="GameWorld.CollectRenderLists"/> all live in
/// <c>Agapanthe.Core</c>, and the <see cref="RenderContext"/> handles are never dereferenced (see
/// <see cref="RenderBarrierTests"/>).
/// </para>
/// </summary>
[Collection("World")]
public sealed class RenderStageNeutralityTests
{
    private const float Dt = 1f / 60f;
    private const int Ticks = 40;

    // A real ground plane and real gravity: the bodies must actually collide, so the broadphase, the contact-pair
    // sort and the impulse resolution all participate. A test that only integrates free-fall would prove nothing
    // about iteration order, which is the entire mechanism under suspicion.
    private static readonly PhysicsSettings Settings =
        new(new Vector3(0, -9.81f, 0), groundY: 0f, fixedDt: Dt);

    /// <summary>
    /// The shared starting state, as a VS-1 snapshot. Both runs load the SAME bytes, so any difference in the
    /// output is caused by what happened during the run and by nothing else.
    /// </summary>
    private static byte[] BuildFixture()
    {
        using var world = new GameWorld();

        // Bodies that will collide with each other and with the ground.
        for (var i = 0; i < 6; i++)
        {
            var spec = new ImportedEntitySpec(
                new MeshHandle(0, 1), new MaterialHandle(0, 1),
                new Double3(i * 0.8, 3 + (i * 2.1), 0), Matrix4x4.Identity, Vector3.Zero, 1f, (uint)i);
            world.SpawnBody(in spec, new Vector3(0.1f * i, 0f, 0f), inverseMass: 1f, restitution: 0.4f, radius: 1f);
        }

        // A parent chain, so PropagateTransforms participates rather than iterating an empty query.
        var root = world.Spawn(new Double3(50, 10, 0), Quaternion.Identity, 1f);
        var mid = world.Spawn(
            new Double3(1, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.5f), 1f, root);
        world.Spawn(new Double3(0, 1, 0), Quaternion.Identity, 2f, mid);

        using var stream = new MemoryStream();
        world.Save(stream); // Save flushes the structural queue first
        return stream.ToArray();
    }

    /// <summary>
    /// One run. <paramref name="withRenderStage"/> selects headless (tick stages only) or windowed (tick stages
    /// plus the Render stage). Both drive the REAL <see cref="SystemScheduler"/>, so the barrier count — three per
    /// tick, four when a frame is drawn — is the production one rather than a re-implementation.
    /// </summary>
    private static byte[] Run(byte[] fixture, bool withRenderStage, out int candidatesSeen)
    {
        using var world = new GameWorld();
        using (var input = new MemoryStream(fixture))
        {
            world.Load(input);
        }

        var scheduler = new SystemScheduler(world.FlushStructuralChanges);
        scheduler.Add(Stage.Input, new ChurnSystem(world));
        scheduler.Add(Stage.Simulation, new PhysicsSystem(world, Settings));
        scheduler.Add(Stage.PostSimulation, new LambdaSystem(world.PropagateTransforms));

        var renderList = new RenderList();
        var candidates = new SceneCandidateSet();
        var collect = new CollectSystem(world, renderList, candidates, MakeView());
        var renderScheduler = new RenderSystemScheduler(world.FlushStructuralChanges);
        renderScheduler.Add(collect);

        for (var i = 0; i < Ticks; i++)
        {
            scheduler.Tick(Dt);
            if (withRenderStage)
            {
                // Exactly what FrameOrchestrator's render delegate builds (FrameOrchestrator.cs:84): the LAST tick
                // actually executed, Math.Max(0L, TickIndex - 1) (MP-0c off-by-one fix — SimulationHost.CurrentTick).
                // The three GPU handles are inert here: no test system dereferences them.
                renderScheduler.Render(
                    new RenderContext(new TickContext(Dt, Math.Max(0L, scheduler.TickIndex - 1)), default, null!, default));
            }
        }

        candidatesSeen = collect.LastCount;

        using var output = new MemoryStream();
        world.Save(output);
        return output.ToArray();
    }

    [Fact]
    public void RenderStage_DoesNotAlterSimulationState()
    {
        var fixture = BuildFixture();

        var headless = Run(fixture, withRenderStage: false, out _);
        var windowed = Run(fixture, withRenderStage: true, out _);

        Assert.Equal(headless, windowed);
    }

    // Guards the guard. If the fixture ever stopped simulating — a settled scene, a physics regression, a churn
    // system that no longer fires — the parity assertion above would compare two identical no-ops and pass
    // vacuously. That is the failure mode of every "nothing changed" test, and it is worth one assertion.
    [Fact]
    public void TheFixtureActuallySimulates()
    {
        var fixture = BuildFixture();

        var after = Run(fixture, withRenderStage: false, out _);

        Assert.NotEqual(fixture, after);

        // Byte inequality alone would also hold if physics were dead and only the churn spawn/despawn moved: assert
        // that a body under these exact settings actually FALLS, so a broken integrator cannot leave this guard
        // looking healthy. Same spec and settings as BuildFixture uses.
        using var world = new GameWorld();
        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1),
            new Double3(0, 50, 0), Matrix4x4.Identity, Vector3.Zero, 1f, 0);
        var probe = world.SpawnBody(in spec, Vector3.Zero, inverseMass: 1f, restitution: 0.4f, radius: 1f);

        for (var i = 0; i < Ticks; i++)
        {
            world.StepPhysics(in Settings);
        }

        Assert.True(
            world.GetWorldPosition(probe).Y < 50.0 - 1.0,
            "A body did not fall under the fixture's physics settings — the parity comparison would be vacuous.");
    }

    // Same reasoning on the other side: parity is meaningless if the render stage did no work. This pins that
    // CollectRenderLists really gathered the drawables it is supposed to stamp slots on.
    [Fact]
    public void TheRenderStageActuallyCollects()
    {
        var fixture = BuildFixture();

        Run(fixture, withRenderStage: true, out var candidates);

        Assert.True(candidates > 0, "CollectRenderLists gathered nothing — the parity comparison would be vacuous.");
    }

    private sealed class LambdaSystem(Action body) : ISystem
    {
        public void Execute(in TickContext ctx) => body();
    }

    // Structural churn on a fixed tick schedule, so both runs perform the SAME spawns and despawns and therefore
    // allocate the same GlobalIds. Without churn the structural barrier would early-out on every stage and the
    // lifecycle path would go untested.
    private sealed class ChurnSystem(GameWorld world) : ISystem
    {
        private EntityRef _spawned;

        public void Execute(in TickContext ctx)
        {
            switch (ctx.TickIndex)
            {
                case 5:
                    var spec = new ImportedEntitySpec(
                        new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(2, 14, 0),
                        Matrix4x4.Identity, Vector3.Zero, 1f, 99);
                    _spawned = world.SpawnBodyDeferred(
                        in spec, Vector3.Zero, inverseMass: 1f, restitution: 0.4f, radius: 1f);
                    break;
                case 20:
                    world.Despawn(_spawned);
                    break;
            }
        }
    }

    private sealed class CollectSystem(GameWorld world, RenderList list, SceneCandidateSet set, RenderView view)
        : IRenderSystem
    {
        public int LastCount { get; private set; }

        public void Render(in RenderContext ctx)
        {
            world.CollectRenderLists(list, set, in view);
            LastCount = set.Count;
        }
    }

    private static RenderView MakeView()
    {
        var eye = new Double3(0, 6, 40);
        var origin = RenderView.Snap(eye);
        var eyeRelative = (eye - origin).ToVector3(Double3.Zero);
        var view = MathHelpers.LookAt(eyeRelative, eyeRelative - Vector3.UnitZ, Vector3.UnitY);
        const float fovY = 1.0f;
        const float aspect = 16f / 9f;
        const float near = 0.1f;
        const float far = 10_000f;
        var projection = MathHelpers.PerspectiveVulkanReversed(fovY, aspect, near, far);
        return new RenderView(origin, eyeRelative, in view, projection, fovY, aspect, near, far);
    }
}
