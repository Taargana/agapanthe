using System.Numerics;
using Agapanthe.Core;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0b W2: proves <see cref="ContactPairKey"/> (W1) makes contact resolution order depend on the RELATIVE order
/// of <see cref="GlobalId"/>s within a pair, never on where the id block starts. Two tests, deliberately paired
/// (spec §Testing strategy): <see cref="SpawnOrder_ChangesFinalPositions"/> proves the three-body scene actually IS
/// order-sensitive (without it, the invariance test below would pass by construction and prove nothing);
/// <see cref="IdOffset_DoesNotChangeFinalPositions"/> proves a uniform shift of the whole id block — the case a
/// sparse, host-assigned <see cref="GlobalIdRange"/> produces — leaves that order, and therefore the outcome,
/// unchanged.
/// </summary>
[Collection("World")]
public sealed class ContactResolutionOrderTests
{
    // Distinct mass, distinct restitution, asymmetric layout, distinct initial velocity per body — an
    // interchangeable (symmetric) configuration would make both tests below tautological (spec: "the configuration
    // is specified as deliberately asymmetric, and that asymmetry is proven, not declared").
    private readonly record struct BodyDef(Double3 Position, Vector3 Velocity, float InverseMass, float Restitution);

    private static readonly BodyDef[] Bodies =
    [
        new(new Double3(0, 0, 0), new Vector3(0.6f, -0.3f, 0.1f), InverseMass: 1.0f, Restitution: 0.2f),
        new(new Double3(1.5, 0, 0), new Vector3(-0.4f, 0.5f, -0.2f), InverseMass: 0.5f, Restitution: 0.5f),
        new(new Double3(0, 1.5, 0), new Vector3(0.1f, -0.6f, 0.4f), InverseMass: 2.0f, Restitution: 0.8f),
    ];

    private const float Radius = 1.2f; // every pairwise distance above (1.5, 1.5, ~2.12) < 2*Radius: all 3 pairs overlap

    private static readonly PhysicsSettings Settings = new(new Vector3(0, -1f, 0), groundY: -1_000f, fixedDt: 1f / 60f);

    // Spawns the fixed BodyDef set through `world` in the order named by `spawnOrder` (a permutation of 0,1,2),
    // steps N times, and returns each body's final position INDEXED BY ITS ORIGINAL BodyDef INDEX (not spawn
    // order), so callers can compare "the same physical body" across differently-ordered runs.
    private static Double3[] RunScene(GameWorld world, int[] spawnOrder, int steps)
    {
        var refs = new EntityRef[Bodies.Length];
        foreach (var bodyIndex in spawnOrder)
        {
            var def = Bodies[bodyIndex];
            var spec = new ImportedEntitySpec(
                new MeshHandle(0, 1), new MaterialHandle(0, 1), def.Position, Matrix4x4.Identity, Vector3.Zero, 1f, 0);
            refs[bodyIndex] = world.SpawnBody(in spec, def.Velocity, def.InverseMass, def.Restitution, Radius);
        }

        for (var i = 0; i < steps; i++)
        {
            world.StepPhysics(in Settings);
        }

        var result = new Double3[Bodies.Length];
        for (var i = 0; i < Bodies.Length; i++)
        {
            result[i] = world.GetWorldPosition(refs[i]);
        }

        return result;
    }

    // Proves the scene is order-sensitive at all: spawning the SAME bodies in a different order (hence different
    // GlobalIds, hence a different (min,max) resolution order) must change at least one final position. Without
    // this, IdOffset_DoesNotChangeFinalPositions below would be vacuous — a symmetric scene passes it either way.
    [Fact]
    public void SpawnOrder_ChangesFinalPositions()
    {
        using var worldForward = new GameWorld();
        var forward = RunScene(worldForward, [0, 1, 2], steps: 30);

        using var worldReversed = new GameWorld();
        var reversed = RunScene(worldReversed, [2, 1, 0], steps: 30);

        Assert.True(
            forward[0] != reversed[0] || forward[1] != reversed[1] || forward[2] != reversed[2],
            "Spawning the same asymmetric bodies in a different order produced identical outcomes — the scene is " +
            "not order-sensitive, so it cannot prove anything about resolution order.");
    }

    // The headline W2 test: two worlds spawn the SAME three bodies in the SAME order, one from GlobalIdRange.Default
    // (ids 1,2,3), one from a range whose block starts far into u64-space (ids 2^32-1, 2^32, 2^32+1 — the sparse
    // triple W1's tests already used). A uniform offset preserves each pair's (min,max) ORDER even though it changes
    // every id's VALUE, so ContactPairKey must resolve both scenes identically. Exact equality, not a tolerance: a
    // correct key makes the two runs bit-for-bit the same float trajectory.
    [Fact]
    public void IdOffset_DoesNotChangeFinalPositions()
    {
        using var worldLowIds = new GameWorld(GlobalIdRange.Default);
        var lowIds = RunScene(worldLowIds, [0, 1, 2], steps: 30);

        using var worldHighIds = new GameWorld(new GlobalIdRange((1UL << 32) - 1, ulong.MaxValue));
        var highIds = RunScene(worldHighIds, [0, 1, 2], steps: 30);

        Assert.Equal(lowIds[0], highIds[0]);
        Assert.Equal(lowIds[1], highIds[1]);
        Assert.Equal(lowIds[2], highIds[2]);
    }
}
