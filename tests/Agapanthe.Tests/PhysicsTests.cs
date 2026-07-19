using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// GPU-free tests for P3-M3 physics v1 (spec §7): fixed-step integration under gravity, ground half-space
/// collision, and run-to-run determinism. Sphere-sphere collision (W2) is covered separately.
/// </summary>
[Collection("World")]
public sealed class PhysicsTests
{
    // A unit-radius body at a chosen height, no initial velocity. Radius 0 unless a test needs ground contact, so
    // the closed-form integration test is not perturbed by the plane.
    private static EntityRef DropBody(GameWorld world, Double3 position, float radius = 0f, float restitution = 0.3f)
    {
        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, 1f, 0);
        return world.SpawnBody(in spec, Vector3.Zero, inverseMass: 1f, restitution: restitution, radius: radius);
    }

    // Semi-implicit Euler dropped far above any ground reproduces its closed form exactly: with v0 = 0 and a fixed
    // step, after k steps y = y0 + g·dt²·k(k+1)/2 (velocity updated BEFORE position each step).
    [Fact]
    public void Integrate_Gravity_MatchesSemiImplicitClosedForm()
    {
        using var world = new GameWorld();
        var body = DropBody(world, new Double3(0, 1000, 0));
        var settings = new PhysicsSettings(new Vector3(0, -9.81f, 0), groundY: -1_000_000f, fixedDt: 1f / 60f);
        const int steps = 30;

        for (var i = 0; i < steps; i++)
        {
            world.StepPhysics(in settings);
        }

        var dt = 1.0 / 60.0;
        var expectedY = 1000.0 + (-9.81 * dt * dt * steps * (steps + 1) / 2.0);
        Assert.Equal(expectedY, world.GetWorldPosition(body).Y, 3);
        Assert.Equal(-9.81 * dt * steps, (double)world.GetVelocity(body).Y, 3);
    }

    [Fact]
    public void Integrate_ImmovableBody_NeverMoves()
    {
        using var world = new GameWorld();
        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(0, 500, 0),
            Matrix4x4.Identity, Vector3.Zero, 1f, 0);
        var body = world.SpawnBody(in spec, Vector3.Zero, inverseMass: 0f, restitution: 0.5f, radius: 1f);
        var settings = PhysicsSettings.Default(groundY: 0f);

        for (var i = 0; i < 100; i++)
        {
            world.StepPhysics(in settings);
        }

        Assert.Equal(new Double3(0, 500, 0), world.GetWorldPosition(body));
        Assert.Equal(Vector3.Zero, world.GetVelocity(body));
    }

    [Fact]
    public void Ground_Bounce_ReflectsNormalVelocity()
    {
        using var world = new GameWorld();
        // Radius 1, ground at 0: rests at y = 1. Start just above so it hits within a couple of steps with a real
        // downward speed, restitution 0.5 so the bounce is well above the rest clamp.
        var body = DropBody(world, new Double3(0, 1.2, 0), radius: 1f, restitution: 0.5f);
        var settings = new PhysicsSettings(new Vector3(0, -9.81f, 0), groundY: 0f, fixedDt: 1f / 60f);

        // Step until the body has crossed the plane and rebounded (its velocity turns positive).
        var bounced = false;
        for (var i = 0; i < 20 && !bounced; i++)
        {
            world.StepPhysics(in settings);
            if (world.GetVelocity(body).Y > 0f)
            {
                bounced = true;
            }
        }

        Assert.True(bounced, "the body should rebound upward after hitting the ground");
        // Never below the resting height (no tunnelling at these speeds).
        Assert.True(world.GetWorldPosition(body).Y >= 1.0 - 1e-6, "the body must not sink below the ground");
    }

    [Fact]
    public void Ground_ComesToRest_NoJitter()
    {
        using var world = new GameWorld();
        var body = DropBody(world, new Double3(0, 5, 0), radius: 1f, restitution: 0.3f);
        var settings = new PhysicsSettings(new Vector3(0, -9.81f, 0), groundY: 0f, fixedDt: 1f / 60f);

        for (var i = 0; i < 600; i++) // 10 s: plenty to settle
        {
            world.StepPhysics(in settings);
        }

        Assert.Equal(1.0, world.GetWorldPosition(body).Y, 3);       // resting on the plane, radius above it
        Assert.Equal(0.0, (double)world.GetVelocity(body).Y, 3);    // no residual bounce
    }

    // Same scene stepped in two independent worlds must produce bit-identical positions: the determinism the
    // reproducible-capture gate rests on (spec §2).
    [Fact]
    public void Determinism_TwoWorlds_IdenticalTrajectory()
    {
        static Double3 Run()
        {
            using var world = new GameWorld();
            var body = DropBody(world, new Double3(3, 12, -4), radius: 1f, restitution: 0.4f);
            var settings = new PhysicsSettings(new Vector3(0, -9.81f, 0), groundY: 0f, fixedDt: 1f / 60f);
            for (var i = 0; i < 240; i++)
            {
                world.StepPhysics(in settings);
            }

            return world.GetWorldPosition(body);
        }

        Assert.Equal(Run(), Run());
    }

    // --- W2: sphere-sphere collision -----------------------------------------------------------------------------

    private static EntityRef BodyAt(
        GameWorld world, Double3 position, Vector3 velocity, float radius, float invMass = 1f, float restitution = 0f)
    {
        var spec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), position, Matrix4x4.Identity, Vector3.Zero, radius, 0);
        return world.SpawnBody(in spec, velocity, invMass, restitution, radius);
    }

    // Two overlapping, motionless bodies must be pushed apart by the positional correction until they no longer
    // overlap (gravity off, ground far away, so only body-body acts).
    [Fact]
    public void Bodies_Overlapping_ArePushedApart()
    {
        using var world = new GameWorld();
        var a = BodyAt(world, new Double3(0, 0, 0), Vector3.Zero, radius: 1f);
        var b = BodyAt(world, new Double3(0.5, 0, 0), Vector3.Zero, radius: 1f); // overlap 1.5
        var settings = new PhysicsSettings(Vector3.Zero, groundY: -1000f, fixedDt: 1f / 60f);

        for (var i = 0; i < 200; i++)
        {
            world.StepPhysics(in settings);
        }

        var dist = Double3.Distance(world.GetWorldPosition(a), world.GetWorldPosition(b));
        Assert.True(dist >= 2.0 - 0.05, $"overlapping bodies should separate to ~2 m apart, got {dist:F3}");
    }

    // A moving body striking a stationary equal-mass body head-on transfers its motion: the approaching body slows
    // and the struck body moves off along the contact normal (momentum sign correct).
    [Fact]
    public void Bodies_HeadOn_TransfersMomentum()
    {
        using var world = new GameWorld();
        var a = BodyAt(world, new Double3(-3, 0, 0), new Vector3(5, 0, 0), radius: 1f, restitution: 1f);
        var b = BodyAt(world, new Double3(3, 0, 0), Vector3.Zero, radius: 1f, restitution: 1f);
        var settings = new PhysicsSettings(Vector3.Zero, groundY: -1000f, fixedDt: 1f / 60f);

        for (var i = 0; i < 120; i++)
        {
            world.StepPhysics(in settings);
        }

        // b must have been pushed in +x (struck from the left); a must no longer be overtaking it.
        Assert.True(world.GetVelocity(b).X > 0.1f, "the struck body should move off in +x");
        Assert.True(world.GetWorldPosition(b).X > world.GetWorldPosition(a).X, "a must not pass through b");
    }

    // Determinism must hold with body-body contacts too: a small pile stepped in two worlds is bit-identical. The
    // resolution order is sorted by GlobalId, so it cannot depend on Arch's chunk iteration order.
    [Fact]
    public void Determinism_Pile_IsOrderIndependent()
    {
        static Double3 Run()
        {
            using var world = new GameWorld();
            EntityRef last = default;
            // A tight cluster that will interpenetrate and resolve through many mutual contacts.
            for (var i = 0; i < 12; i++)
            {
                var p = new Double3((i % 3) * 0.6, 3 + (i * 0.4), (i / 3) * 0.6);
                last = BodyAt(world, p, Vector3.Zero, radius: 1f, invMass: 1f, restitution: 0.2f);
            }

            var settings = new PhysicsSettings(new Vector3(0, -9.81f, 0), groundY: 0f, fixedDt: 1f / 60f);
            for (var i = 0; i < 300; i++)
            {
                world.StepPhysics(in settings);
            }

            return world.GetWorldPosition(last);
        }

        Assert.Equal(Run(), Run());
    }

    // The hot path — integrate + broadphase + contacts + ground — must not allocate once the scratch has warmed up
    // (the 0 B/frame gate). A cluster so bodies actually contact and the pair buffers are exercised too.
    [Fact]
    public void StepPhysics_IsAllocationFree_AfterWarmup()
    {
        using var world = new GameWorld();
        for (var i = 0; i < 64; i++)
        {
            BodyAt(world, new Double3((i % 8) * 1.5, 3 + ((i / 8) * 1.5), (i % 5) * 1.5), Vector3.Zero, radius: 1f);
        }

        var settings = new PhysicsSettings(new Vector3(0, -9.81f, 0), groundY: 0f, fixedDt: 1f / 60f);
        for (var i = 0; i < 120; i++) // warm up: grow every scratch buffer, settle into contacts
        {
            world.StepPhysics(in settings);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 240; i++)
        {
            world.StepPhysics(in settings);
        }

        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0, $"StepPhysics should allocate nothing after warmup, observed {allocated} bytes.");
    }
}
