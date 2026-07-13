using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Core;
using Arch.Buffer;
using Arch.Core;
using Arch.Core.Extensions;
using ArchWorld = Arch.Core.World;

[assembly: InternalsVisibleTo("Agapanthe.Tests")]
[assembly: InternalsVisibleTo("AotComponentProbe")]

namespace Agapanthe.World;

/// <summary>
/// The world of entities (spec §3.4) and the ONLY sanctioned way to touch the ECS: the Arch <c>World</c>,
/// <c>Entity</c> and <c>CommandBuffer</c> stay private here and never appear in the public API, so the ECS never
/// leaks into Rendering or the Sandbox. The constructor roots every component's chunk array for NativeAOT
/// (spec §6.1) before any entity exists.
/// </summary>
public sealed class GameWorld : IDisposable
{
    private readonly ArchWorld _world;
    private bool _disposed;

    public GameWorld()
    {
        // Root all component T[] chunk arrays for the ILC BEFORE the first entity is created (spec §6.1).
        ComponentRegistry.RootAll();
        _world = ArchWorld.Create();
    }

    /// <summary>
    /// Spawns an imported drawable (spec §3.2 seam). It carries a baked <see cref="WorldTransform"/> (copied
    /// bit-for-bit from the spec — no TRS round-trip, spec §6 condition a), the mesh/material handles, its world
    /// bounds and its stable render order. It deliberately has NO <see cref="LocalTransform"/>, so the
    /// propagation system never recomputes its matrix (byte-identical guarantee).
    /// </summary>
    public void SpawnImported(in ImportedEntitySpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _world.Create(
            new GlobalId { Value = spec.Order },
            new WorldTransform { Value = spec.World },
            new MeshRef { Mesh = spec.Mesh, Material = spec.Material },
            new Bounds { Min = spec.BoundsMin, Max = spec.BoundsMax },
            new RenderOrder { Value = spec.Order });
    }

    /// <summary>
    /// Exercises EVERY component type through the paths that trigger the AOT "missing native code" failure —
    /// create, structural add/remove (archetype moves), a query, and a deferred CommandBuffer change — so the
    /// <see cref="AotComponentProbe"/> (and a JIT unit test) can prove <see cref="ComponentRegistry.RootAll"/> is
    /// sufficient under a real NativeAOT publish. Returns the number of entities the query iterated. Internal:
    /// it exists only to validate rooting, not as a public API.
    /// </summary>
    internal int AotRootingSmoke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Imported-shape entities (GlobalId, WorldTransform, MeshRef, Bounds, RenderOrder).
        for (var i = 0; i < 8; i++)
        {
            SpawnImported(new ImportedEntitySpec(
                new MeshHandle(i), new MaterialHandle(0), Matrix4x4.Identity,
                new Double3(i, 0, 0), new Double3(i + 1, 1, 1), (uint)i));
        }

        // Hierarchical-shape entities (LocalTransform, Parent) — the two components SpawnImported does not touch.
        var parent = _world.Create(new LocalTransform { Scale = 1f });
        var child = _world.Create(new LocalTransform { Scale = 1f });
        child.Add(new Parent { Value = parent });        // structural change -> new archetype
        _ = child.Get<Parent>().Value;                    // read moved component
        if (child.Has<Parent>())
        {
            child.Remove<Parent>();                       // structural change back
        }

        // Deferred structural change through a CommandBuffer (the path P2-M0 flagged as most AOT-fragile).
        var cb = new CommandBuffer();
        var deferred = cb.Create([Component<WorldTransform>.ComponentType]);
        cb.Set(deferred, new WorldTransform { Value = Matrix4x4.Identity });
        cb.Add(deferred, new RenderOrder { Value = 99 });
        cb.Playback(_world);

        // Query touching several component arrays.
        var query = new QueryDescription().WithAll<WorldTransform, RenderOrder>();
        var count = 0;
        _world.Query(in query, (Entity _, ref WorldTransform _, ref RenderOrder _) => count++);
        return count;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _world.Dispose();
    }
}
