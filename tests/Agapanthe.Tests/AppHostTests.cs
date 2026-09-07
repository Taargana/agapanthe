using Agapanthe.Engine;
using Agapanthe.Engine.Render;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Agapanthe.App milestone — Wave 1 slice: the fixed-step single definition (<see cref="SimulationSettings"/>)
/// and its readers. The contract / AppHost tests (recipe selection, teardown order, host options) land in W2.
/// </summary>
[Collection("World")]
public sealed class AppHostTests : IDisposable
{
    private readonly List<GameWorld> _worlds = [];

    public void Dispose()
    {
        foreach (var world in _worlds)
        {
            world.Dispose();
        }
    }

    private GameWorld NewWorld()
    {
        var world = new GameWorld();
        _worlds.Add(world);
        return world;
    }

    // ── Test 1 — SimulationSettings ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void SimulationSettings_Default_IsOneSixtieth()
    {
        Assert.Equal(1f / 60f, SimulationSettings.Default.FixedDeltaSeconds);
        Assert.Equal(1f / 30f, new SimulationSettings { FixedDeltaSeconds = 1f / 30f }.FixedDeltaSeconds);
    }

    [Fact]
    public void SimulationSettings_Default_IsAStableSharedInstance()
        => Assert.Same(SimulationSettings.Default, SimulationSettings.Default);

    // ── Test 2 — SimulationHost.Settings ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void SimulationHost_CreateDefault_ExposesTheDefaultSettings()
        => Assert.Same(SimulationSettings.Default, SimulationHost.CreateDefault(NewWorld()).Settings);

    [Fact]
    public void SimulationHost_CreateDefault_WithExplicitSettings_HoldsThatSameReference()
    {
        var custom = new SimulationSettings { FixedDeltaSeconds = 1f / 30f };
        Assert.Same(custom, SimulationHost.CreateDefault(NewWorld(), custom).Settings);
    }

    [Fact]
    public void SimulationHost_CreateDefault_NullSettings_Throws()
        => Assert.Throws<ArgumentNullException>(() => SimulationHost.CreateDefault(NewWorld(), null!));

    // ── Test 3 — FrameOrchestrator reads the single definition ─────────────────────────────────────────────────
    // A real FrameOrchestrator can't be built headless (it needs a GPU Renderer), so the one behavioural line —
    // "the accumulator's step comes from simulation.Settings" — is asserted through the internal seam the private
    // ctor actually calls.

    [Fact]
    public void FrameOrchestrator_ResolveAccumulatorStep_ReadsHostSettings()
    {
        var custom = SimulationHost.CreateDefault(NewWorld(), new SimulationSettings { FixedDeltaSeconds = 1f / 30f });
        Assert.Equal(1f / 30f, FrameOrchestrator.ResolveAccumulatorStep(custom));

        var plain = SimulationHost.CreateDefault(NewWorld());
        Assert.Equal(SimulationSettings.Default.FixedDeltaSeconds, FrameOrchestrator.ResolveAccumulatorStep(plain));
    }

    // ── Test 3b — the convenience overload rejects a negative step LOUDLY ──────────────────────────────────────
    // (before touching the GPU args — ThrowIfNegative is the first statement, so null! is safe here).

    [Fact]
    public void FrameOrchestrator_CreateDefault_NegativeStep_ThrowsBeforeTouchingTheRenderer()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => FrameOrchestrator.CreateDefault(NewWorld(), null!, null!, null!, null!, fixedTickDeltaSeconds: -1f));

    // ── Test 4 — PhysicsSystem.RatesMatch (MP-0c ULP lesson, re-asserted after the refactor) ───────────────────

    [Fact]
    public void PhysicsSystem_RatesMatch_TrueOnlyOnExactEquality()
    {
        Assert.True(PhysicsSystem.RatesMatch(1f / 60f, 1f / 60f));
        Assert.False(PhysicsSystem.RatesMatch(3f / 60f, 1f / 60f)); // 3f/60f != 3f*(1f/60f) by 1 ULP
    }
}
