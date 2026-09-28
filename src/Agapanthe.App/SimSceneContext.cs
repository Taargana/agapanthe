using Agapanthe.Assets;
using Agapanthe.Engine;
using Agapanthe.World;

namespace Agapanthe.App;

/// <summary>
/// The <b>headless-safe</b> half of what <see cref="ISceneRecipe.Build"/> receives (Contenu-3a): the world, the
/// simulation schedule, the cooked-content catalog, the raw args and the host options — everything a recipe needs
/// to <b>populate and simulate</b> a world, with no GPU or window. The GPU/presentation half is
/// <see cref="PresentationSceneContext"/> (null when running headless).
/// </summary>
public sealed class SimSceneContext
{
    /// <summary>The entities.</summary>
    public required GameWorld World { get; init; }

    /// <summary>The simulation half — input phase, tick schedule, <c>SimulationSettings</c>. Register simulation
    /// systems via <see cref="AddSystem"/>, not on this directly.</summary>
    public required SimulationHost Simulation { get; init; }

    /// <summary>Cooked content (Contenu-2): <c>Catalog.LoadModel(key)</c> → a decoded <c>ModelAsset</c>. GPU-free,
    /// so a headless populator can read geometry/bounds without a device.</summary>
    public required AssetCatalog Catalog { get; init; }

    /// <summary>The raw process command line — the host does not parse it.</summary>
    public required string[] Args { get; init; }

    /// <summary>The resolved host options (env + programmatic overrides).</summary>
    public required HostOptions Options { get; init; }

    /// <summary>Registers a simulation system on a stage. Headless-reachable — the reason the context is split.
    /// (<c>IRenderSystem</c>s register on <see cref="PresentationSceneContext.Orchestrator"/>.)</summary>
    public void AddSystem(Stage stage, ISystem system) => Simulation.Add(stage, system);

    // --- Deferred snapshot restore (Contenu-3a — the RestoreIfRequested guard-rail) ------------------------------

    private (string Path, SnapshotAllocatorPolicy Policy)? _pending;

    /// <summary>Ask the host to restore a snapshot into this world <b>after</b> <c>Build</c> returns — i.e. after
    /// the recipe has registered every asset the snapshot can reference. Replaces each recipe calling
    /// <c>GameWorld.Load</c> itself in the wrong order. At most one restore per scene. The file is opened only in
    /// <see cref="ApplyPendingRestore"/>, under its own <c>using</c> — the caller never holds a handle.</summary>
    public void RequestRestore(string snapshotPath, SnapshotAllocatorPolicy policy)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        if (_pending is not null)
        {
            throw new InvalidOperationException("A scene may request at most one snapshot restore.");
        }

        _pending = (snapshotPath, policy);
    }

    internal bool HasPendingRestore => _pending is not null;

    internal string? PendingRestorePath => _pending?.Path;

    /// <summary>Applies the pending restore. <paramref name="resolve"/> comes from the presentation side
    /// (<c>ResourceRegistry.ResolveMeshRef</c>); <c>null</c> when headless — the world then keeps its
    /// <c>AssetRef</c> identities with <c>MeshRef</c> left invalid.</summary>
    internal SnapshotLoadResult ApplyPendingRestore(MeshRefResolver? resolve)
    {
        if (_pending is not { } p)
        {
            throw new InvalidOperationException("No pending restore.");
        }

        _pending = null;
        using var stream = File.OpenRead(p.Path);
        return World.Load(stream, p.Policy, resolve);
    }

    // --- Scene switch request (scene management spec, D4) --------------------------------------------------------

    private string? _pendingSceneSwitch;

    /// <summary>
    /// Records a request to switch to <paramref name="sceneName"/>. <b>Only records it</b> — never spawns a
    /// thread, calls <c>GraphicsDevice.SetSanctionedLoaderThread</c>, or blocks. This may legitimately be called
    /// from inside a <c>Tick</c>, which can run on a non-owner thread under Job-1's scheduler; doing real
    /// orchestration here would trip <c>AssertOwnerThreadStrict</c> or block inside a simulation wave. All actual
    /// orchestration runs from <c>AppHost</c>'s <c>window.Rendered</c> poll, which drains this via
    /// <see cref="DrainPendingSceneSwitch"/> — never from here.
    /// </summary>
    public void RequestSceneSwitch(string sceneName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sceneName);
        _pendingSceneSwitch = sceneName;
    }

    /// <summary>True while a scene switch has been requested and not yet drained.</summary>
    internal bool HasPendingSceneSwitch => _pendingSceneSwitch is not null;

    /// <summary>Atomically reads and clears the pending scene-switch request, or <c>null</c> if none is pending
    /// (mirrors <see cref="ApplyPendingRestore"/>'s clear-before-use pattern).</summary>
    internal string? DrainPendingSceneSwitch()
    {
        var token = _pendingSceneSwitch;
        _pendingSceneSwitch = null;
        return token;
    }
}
