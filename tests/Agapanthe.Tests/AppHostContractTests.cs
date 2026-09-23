using System.Numerics;
using Agapanthe.App;
using Agapanthe.Assets;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.Tests;

/// <summary>
/// Agapanthe.App milestone — Wave 2 slice: the game contract (<see cref="IGame"/> / <see cref="ISceneRecipe"/>),
/// recipe selection, host-option resolution, and the strict-order teardown list. A full <c>AppHost.RunClient</c>
/// cannot run headless (<c>window.Loaded</c> builds a <c>GraphicsDevice</c>), so the windowed capture gate covers
/// the rest.
/// </summary>
public sealed class AppHostContractTests
{
    // Mirrors ModelSceneRecipe: matches its Name, accepts null/empty when it is the default, and (W3a) the
    // "grid:" / "drop:" token families.
    private sealed class FakeRecipe(string name, bool isDefault = false, params string[] familyPrefixes) : ISceneRecipe
    {
        public string Name => name;

        public bool Matches(string? sceneToken)
        {
            if (isDefault && string.IsNullOrEmpty(sceneToken))
            {
                return true;
            }

            if (string.Equals(sceneToken, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return sceneToken is not null
                && familyPrefixes.Any(p => sceneToken.StartsWith(p + ":", StringComparison.OrdinalIgnoreCase));
        }

        public void Build(SimSceneContext sim, PresentationSceneContext? presentation)
            => throw new NotSupportedException("not built in these tests");
    }

    private sealed class FakeGame(string defaultScene, params ISceneRecipe[] scenes) : IGame
    {
        public string Title => "Fake";
        public IReadOnlyList<ISceneRecipe> Scenes { get; } = scenes;
        public string DefaultScene => defaultScene;
        public UniverseId Universe { get; init; } = UniverseId.None;
    }

    // ── Test 6 — IGame.Universe default ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void IGame_Universe_DefaultsToNone()
        => Assert.Equal(UniverseId.None, ((IGame)new FakeGame("model")).Universe);

    // ── Test 7 — SelectRecipe: default → match → throw ─────────────────────────────────────────────────────────

    [Fact]
    public void SelectRecipe_NullOrEmpty_PicksTheDefaultScene()
    {
        var game = new FakeGame("model", new FakeRecipe("planet"), new FakeRecipe("model", isDefault: true, "grid", "drop"));
        Assert.Equal("model", AppHost.SelectRecipe(game, null).Name);
        Assert.Equal("model", AppHost.SelectRecipe(game, "").Name);
    }

    [Fact]
    public void SelectRecipe_FamilyToken_MatchesTheOwningRecipe()
    {
        var game = new FakeGame("model", new FakeRecipe("planet"), new FakeRecipe("model", isDefault: true, "grid", "drop"));
        Assert.Equal("model", AppHost.SelectRecipe(game, "grid:8x8").Name);
    }

    [Fact]
    public void SelectRecipe_NamedToken_MatchesExactly()
    {
        var game = new FakeGame("model", new FakeRecipe("planet"), new FakeRecipe("model", isDefault: true, "grid", "drop"));
        Assert.Equal("planet", AppHost.SelectRecipe(game, "planet").Name);
    }

    [Fact]
    public void SelectRecipe_UnknownToken_Throws()
    {
        var game = new FakeGame("model", new FakeRecipe("planet"), new FakeRecipe("model", isDefault: true, "grid", "drop"));
        Assert.Throws<ArgumentException>(() => AppHost.SelectRecipe(game, "drive"));
    }

    [Fact]
    public void SelectRecipe_DefaultSceneMatchesNoRecipe_Throws()
    {
        var game = new FakeGame("nonexistent", new FakeRecipe("planet"));
        Assert.Throws<InvalidOperationException>(() => AppHost.SelectRecipe(game, null));
    }

    // ── Test 8 — HostOptions.FromEnvironment with an injected accessor (no process-env mutation) ────────────────

    [Fact]
    public void FromEnvironment_ResolvesEveryKnob()
    {
        var env = new Dictionary<string, string?>
        {
            ["AGAPANTHE_SCENE"] = "  planet-drop  ",
            ["AGAPANTHE_UNIVERSE"] = "000000000000000100000000000000a2",
            ["AGAPANTHE_MAX_FRAMES"] = "420",
            ["AGAPANTHE_CAPTURE"] = "hdr.ppm",
            ["AGAPANTHE_SAVE"] = "w.save",
            ["AGAPANTHE_LOAD"] = "r.save",
            ["AGAPANTHE_OVERLAY"] = "0",
            ["AGAPANTHE_CULL_STATS"] = "1",
            ["AGAPANTHE_CULL_VERIFY"] = "1",
            ["AGAPANTHE_SHADER_RELOAD_TEST"] = "1",
        };
        var opts = HostOptions.FromEnvironment(k => env.GetValueOrDefault(k));

        Assert.Equal("planet-drop", opts.Scene); // trimmed
        Assert.Equal(UniverseId.Parse("000000000000000100000000000000a2"), opts.Universe);
        Assert.Equal(420, opts.MaxFrames);
        Assert.Equal("hdr.ppm", opts.CapturePath);
        Assert.Null(opts.CaptureUiPath);
        Assert.Equal("w.save", opts.SavePath);
        Assert.Equal("r.save", opts.LoadPath);
        Assert.False(opts.OverlayVisible);
        Assert.True(opts.CullStats);
        Assert.True(opts.VerifyCull);
        Assert.True(opts.ShaderReloadTest);
    }

    [Fact]
    public void SelectRecipe_UsesHostOptionsScene_OverTheEnvironment()
    {
        // A caller passing options.Scene picks the scene programmatically — no process-env mutation.
        var game = new FakeGame("model", new FakeRecipe("planet"), new FakeRecipe("model", isDefault: true, "grid", "drop"));
        Assert.Equal("planet", AppHost.SelectRecipe(game, "planet").Name);
    }

    [Fact]
    public void FromEnvironment_MalformedUniverse_FallsBackToNull_NoThrow()
    {
        var opts = HostOptions.FromEnvironment(k => k == "AGAPANTHE_UNIVERSE" ? "not-hex" : null);
        Assert.Null(opts.Universe);
        Assert.Equal(-1, opts.MaxFrames);
        Assert.True(opts.OverlayVisible);
    }

    [Fact]
    public void FromEnvironment_EmptyEnvironment_IsAllDefaults()
    {
        var opts = HostOptions.FromEnvironment(_ => null);
        Assert.Null(opts.Scene);
        Assert.Null(opts.Universe);
        Assert.Equal(-1, opts.MaxFrames);
        Assert.Null(opts.CapturePath);
        Assert.Null(opts.CaptureUiPath);
        Assert.Null(opts.SavePath);
        Assert.Null(opts.LoadPath);
        Assert.True(opts.OverlayVisible);
        Assert.False(opts.CullStats);
        Assert.False(opts.VerifyCull);
        Assert.False(opts.ShaderReloadTest);
    }

    // ── UniverseId end-to-end (MP-0b debt: the first host that stamps one) ─────────────────────────────────────

    [Fact]
    public void ResolveUniverse_EnvOverride_BeatsTheGame()
    {
        var gameId = new UniverseId(0xAAAA, 0xBBBB);
        var envId = new UniverseId(0x1111, 0x2222);
        var game = new FakeGame("model") { Universe = gameId };
        Assert.Equal(envId, AppHost.ResolveUniverse(game, new HostOptions { Universe = envId }));
    }

    [Fact]
    public void ResolveUniverse_NoOverride_UsesTheGame()
    {
        var gameId = new UniverseId(0xAAAA, 0xBBBB);
        var game = new FakeGame("model") { Universe = gameId };
        Assert.Equal(gameId, AppHost.ResolveUniverse(game, new HostOptions()));
    }

    [Fact]
    public void ResolveUniverse_BothUnset_IsNone()
        => Assert.Equal(UniverseId.None, AppHost.ResolveUniverse(new FakeGame("model"), new HostOptions()));

    [Fact]
    public void HostStampedWorld_LoadingAnUnidentifiedSnapshot_KeepsItsIdentity_NoThrow()
    {
        var stamped = new UniverseId(0xDEAD, 0xBEEF);

        using var source = new GameWorld();              // UniverseId.None
        source.Spawn(default, System.Numerics.Quaternion.Identity, 1f);
        source.FlushStructuralChanges();
        var buffer = new MemoryStream();
        source.Save(buffer);
        buffer.Position = 0;

        using var target = new GameWorld(GlobalIdRange.Default, stamped);
        var result = target.Load(buffer);

        Assert.Equal(UniverseOutcome.Kept, result.Universe);
        Assert.Equal(stamped, target.Universe);
    }

    [Fact]
    public void HostStampedWorld_LoadingADifferentUniversesSnapshot_Throws()
    {
        using var source = new GameWorld(GlobalIdRange.Default, new UniverseId(1, 2));
        source.Spawn(default, System.Numerics.Quaternion.Identity, 1f);
        source.FlushStructuralChanges();
        var buffer = new MemoryStream();
        source.Save(buffer);
        buffer.Position = 0;

        using var target = new GameWorld(GlobalIdRange.Default, new UniverseId(9, 9));
        Assert.Throws<WorldSerializationException>(() => target.Load(buffer));
    }

    // ── Contenu-3a — the SceneContext split: a recipe can build with no presentation ──────────────────────────

    private sealed class CountingSystem : ISystem
    {
        public int Ticks { get; private set; }

        public void Execute(in TickContext ctx) => Ticks++;
    }

    private sealed class HeadlessFixtureRecipe : ISceneRecipe
    {
        public string Name => "headless-fixture";
        public CountingSystem? System { get; private set; }

        public void Build(SimSceneContext sim, PresentationSceneContext? presentation)
        {
            // Populate a world and register a sim system — no presentation touched.
            for (var i = 0; i < 3; i++)
            {
                sim.World.Spawn(new Double3(i, 0, 0), Quaternion.Identity, 1f);
            }

            sim.World.FlushStructuralChanges();
            System = new CountingSystem();
            sim.AddSystem(Stage.Simulation, System);
        }
    }

    [Fact]
    public void Recipe_BuildsHeadless_WithNullPresentation()
    {
        using var world = new GameWorld();
        var host = SimulationHost.CreateDefault(world);
        var sim = new SimSceneContext
        {
            World = world,
            Simulation = host,
            Catalog = AssetCatalog.Empty,
            Args = [],
            Options = HostOptions.FromEnvironment(_ => null),
        };

        var recipe = new HeadlessFixtureRecipe();
        recipe.Build(sim, presentation: null); // must not throw

        Assert.Equal(3, world.LiveEntityCount);

        host.BeginFrame();
        host.Tick(1f / 60f);
        host.EndFrame();
        Assert.Equal(1, recipe.System!.Ticks); // the registered system ran
    }

    // Contenu-3a D7 hazard (both audits, 🔴): the restore now runs AFTER Build returns, so anything in Build that
    // reads world CONTENT (e.g. LandingChallengeSystem's old constructor seed) sees an empty world on a resume.
    // This pins the ordering: the world is populated by ApplyPendingRestore, not by RequestRestore.
    [Fact]
    public void ApplyPendingRestore_PopulatesTheWorld_RequestAlone_DoesNot()
    {
        using var source = new GameWorld();
        for (var i = 0; i < 4; i++)
        {
            source.Spawn(new Double3(i, 0, 0), Quaternion.Identity, 1f);
        }

        source.FlushStructuralChanges();
        var path = Path.Combine(Path.GetTempPath(), $"aw-restore-{Guid.NewGuid():N}.save");
        try
        {
            using (var fs = File.Create(path))
            {
                source.Save(fs);
            }

            using var world = new GameWorld();
            var sim = new SimSceneContext
            {
                World = world,
                Simulation = SimulationHost.CreateDefault(world),
                Catalog = AssetCatalog.Empty,
                Args = [],
                Options = HostOptions.FromEnvironment(_ => null),
        };

            sim.RequestRestore(path, SnapshotAllocatorPolicy.AdoptFromHeader);
            Assert.Equal(0, world.LiveEntityCount);          // a request alone changes nothing — Build would see this
            Assert.Equal(path, sim.PendingRestorePath);

            var result = sim.ApplyPendingRestore(resolve: null);
            Assert.Equal(4, result.EntityCount);
            Assert.Equal(4, world.LiveEntityCount);          // now the entities exist — this is the post-Build state
            Assert.False(sim.HasPendingRestore);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SimSceneContext_RequestRestore_Twice_Throws()
    {
        using var world = new GameWorld();
        var sim = new SimSceneContext
        {
            World = world,
            Simulation = SimulationHost.CreateDefault(world),
            Catalog = AssetCatalog.Empty,
            Args = [],
            Options = HostOptions.FromEnvironment(_ => null),
        };

        sim.RequestRestore("a.save", SnapshotAllocatorPolicy.AdoptFromHeader);
        Assert.True(sim.HasPendingRestore);
        Assert.Throws<InvalidOperationException>(
            () => sim.RequestRestore("b.save", SnapshotAllocatorPolicy.AdoptFromHeader));
    }

    // ── Test 9 — the strict teardown list ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildTeardown_LabelsAreInStrictM4Order()
    {
        var labels = AppHost.BuildTeardown(default).Select(x => x.Label).ToArray();

        Assert.Equal(
            new[]
            {
                "audioDevice.Dispose+ReportLeaks", "frameRenderer.WaitIdle", "frameRenderer.Dispose", "world.Dispose",
                "registry.Dispose", "renderer.Dispose", "device.DeletionQueue.FlushAll", "swapchain.Dispose",
                "device.Dispose", "ResourceTracker.Report", "window.Dispose",
            },
            labels);

        // The two invariants the order exists for.
        Assert.Equal(labels.Length - 2, Array.IndexOf(labels, "ResourceTracker.Report")); // right before window.Dispose
        Assert.True(Array.IndexOf(labels, "world.Dispose") < Array.IndexOf(labels, "registry.Dispose"));
        Assert.True(Array.IndexOf(labels, "registry.Dispose") < Array.IndexOf(labels, "renderer.Dispose"));
    }

    [Fact]
    public void BuildTeardown_Default_StepsAreNullSafeNoOps()
    {
        foreach (var (_, step) in AppHost.BuildTeardown(default))
        {
            step(); // must not throw with every target null
        }
    }
}
