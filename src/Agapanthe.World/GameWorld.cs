using System.Diagnostics;
using System.Linq;
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
/// <remarks>
/// <b>Threading contract: construct and drive a GameWorld from ONE thread</b> — enforced by
/// <see cref="AssertOwnerThread"/> in Debug, free in Release.
/// <para>
/// The component-type-id race is closed by construction: Arch assigns those ids in process-global state on first
/// touch, without a lock, and that touch used to happen lazily at <c>World.Create</c> — so two worlds built
/// concurrently could end up with mismatched chunk arrays (a component read back as all-zero, reproducible).
/// <see cref="ComponentRegistry"/> now forces every id to be assigned inside its own lock before any world
/// exists. Arch's world creation itself (a static world table) remains unguarded, which is why the contract
/// above still stands and the tests still serialize their world-touching classes.
/// </para>
/// <para>Parallel iteration WITHIN one world (a future job system) is a separate question, unaffected by this.</para>
/// </remarks>
public sealed class GameWorld : IDisposable
{
    private readonly ArchWorld _world;

    // Reused across PropagateTransforms calls for on-stack cycle detection, so the steady-state propagation
    // allocates nothing (Clear keeps capacity). Depth is tiny (hierarchy chains), a linear scan is cheapest.
    private readonly List<Entity> _walkStack = new(16);

    // Built once: constructing a QueryDescription per frame would defeat the zero-alloc goal.
    private static readonly QueryDescription PropagateDesc = new QueryDescription().WithAll<LocalTransform, WorldTransform>();
    private static readonly QueryDescription BoundsDesc = new QueryDescription().WithAll<Bounds>();
    private static readonly QueryDescription DrawableDesc = new QueryDescription().WithAll<WorldTransform, MeshRef, RenderOrder>();

    // The thread that built this world. Arch's world creation and entity storage are not thread-safe, so a world
    // is owned by one thread; the guard below turns that contract from a comment into a checked invariant
    // (audit M2: "a contract that is not guarded is a contract that will be violated at the first job system").
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    private bool _disposed;

    /// <summary>
    /// Debug-only guard: a GameWorld must be driven from the thread that created it. <see cref="ConditionalAttribute"/>
    /// compiles the call away entirely in Release, so the zero-alloc hot path pays nothing for it.
    /// </summary>
    [Conditional("DEBUG")]
    private void AssertOwnerThread([CallerMemberName] string caller = "")
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
        {
            throw new InvalidOperationException(
                $"GameWorld.{caller} was called from thread {Environment.CurrentManagedThreadId}, but the world is " +
                $"owned by thread {_ownerThreadId}. A world is single-threaded (Arch's world creation and entity " +
                "storage are not thread-safe).");
        }
    }

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
        AssertOwnerThread();
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
        AssertOwnerThread();

        // Imported-shape entities (GlobalId, WorldTransform, MeshRef, Bounds, RenderOrder).
        for (var i = 0; i < 8; i++)
        {
            SpawnImported(new ImportedEntitySpec(
                new MeshHandle(i, 1), new MaterialHandle(0, 1), Matrix4x4.Identity,
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
        using var cb = new CommandBuffer();
        var deferred = cb.Create([Component<WorldTransform>.ComponentType]);
        cb.Set(deferred, new WorldTransform { Value = Matrix4x4.Identity });
        cb.Add(deferred, new RenderOrder { Value = 99 });
        cb.Playback(_world);

        // Hierarchy + the M2 systems, so the probe proves the exact chunk-iteration / entity-walk paths W2 uses
        // survive AOT (audit W1 M2 gate), not only Create/Add/Remove.
        var root = SpawnLocalRoot(new Double3(1, 0, 0), Quaternion.Identity, 1f);
        SpawnLocalChild(root, new Double3(0, 2, 0), Quaternion.Identity, 1f);
        PropagateTransforms();
        _ = AggregateBounds();

        // ALL THREE systems, so the probe covers exactly what the game runs per frame (audit M2, minor 1):
        // CollectRenderLists is the only user of GetSpan<MeshRef>() / GetSpan<RenderOrder>(), which the smoke
        // would otherwise leave untested under AOT. It sees the 8 imported drawables — not the CommandBuffer
        // entity above, which has no MeshRef.
        var render = new RenderList();
        CollectRenderLists(render, new RenderList());
        if (render.Count != 8)
        {
            throw new InvalidOperationException(
                $"AOT smoke: CollectRenderLists produced {render.Count} drawables, expected 8.");
        }

        // Chunk-iteration query (the path the systems use) touching several component arrays. Counts the 8
        // imported entities plus the deferred CommandBuffer one (WorldTransform + RenderOrder, no MeshRef).
        var count = 0;
        foreach (ref var chunk in _world.Query(new QueryDescription().WithAll<WorldTransform, RenderOrder>()))
        {
            count += chunk.Count;
        }

        return count;
    }

    // --- Systems (spec §3.5) ---------------------------------------------------------------------------------

    /// <summary>
    /// System 1: computes each hierarchical entity's <see cref="WorldTransform"/> from its
    /// <see cref="LocalTransform"/> chain (spec §3.5). Imported drawables have no <see cref="LocalTransform"/>,
    /// so their baked matrix is never touched (byte-identical guarantee). Throws <see cref="WorldHierarchyException"/>
    /// on a parent cycle. Zero-alloc in steady state (reused <see cref="_walkStack"/>, chunk iteration, no lambda).
    /// </summary>
    public void PropagateTransforms()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        foreach (ref var chunk in _world.Query(in PropagateDesc))
        {
            var entities = chunk.Entities;
            var worlds = chunk.GetSpan<WorldTransform>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                worlds[i].Value = ComputeWorldMatrix(entities[i]);
            }
        }
    }

    /// <summary>
    /// System 2: unions every entity's world-space <see cref="Bounds"/> into the scene extent (spec §3.5). This
    /// replaces <c>Scene.BoundsMin/Max/Center/Diagonal</c>. Zero-alloc (chunk iteration). Returns
    /// <see cref="Double3Bounds.Empty"/> if there are no bounded entities (the caller guards degenerate extents).
    /// </summary>
    public Double3Bounds AggregateBounds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        var acc = Double3Bounds.Empty;
        foreach (ref var chunk in _world.Query(in BoundsDesc))
        {
            var bounds = chunk.GetSpan<Bounds>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                ref var b = ref bounds[i];
                acc = Double3Bounds.Union(acc, new Double3Bounds(b.Min, b.Max));
            }
        }

        return acc;
    }

    /// <summary>
    /// Builds the two render lists (spec §3.5, systems 3-4 in PASSTHROUGH form for M2): every drawable entity
    /// goes into both lists — no culling yet (that is M4). Both lists are sorted by <see cref="RenderOrder"/>, so
    /// the draw order is deterministic even though Arch iterates by archetype/chunk (spec §6 condition b).
    /// Zero-alloc: the lists are reused (Clear keeps capacity), iteration is chunk-based, the sort uses a struct
    /// comparer.
    /// <para>The shadow-caster list is a separate list on purpose: in M4 it is culled against the LIGHT volume,
    /// never the camera frustum, or off-screen casters would pop their shadows in and out (spec §3.5).</para>
    /// </summary>
    public void CollectRenderLists(RenderList render, RenderList shadowCasters)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(shadowCasters);

        render.Clear();
        shadowCasters.Clear();

        foreach (ref var chunk in _world.Query(in DrawableDesc))
        {
            var worlds = chunk.GetSpan<WorldTransform>();
            var meshes = chunk.GetSpan<MeshRef>();
            var orders = chunk.GetSpan<RenderOrder>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                var item = new RenderItem(worlds[i].Value, meshes[i].Mesh, meshes[i].Material, orders[i].Value);
                render.Add(item);
                shadowCasters.Add(item); // passthrough in M2: every drawable casts
            }
        }

        render.SortByKey();
        shadowCasters.SortByKey();
    }

    // world(e) = local(e) · local(parent) · … · local(root) (row-vector convention). Walks the Parent chain,
    // detecting cycles on the reused stack. A chain node without a LocalTransform ends the walk (it is a world
    // anchor). In M2 position is narrowed to absolute float (camera-relative subtraction is M3).
    private Matrix4x4 ComputeWorldMatrix(Entity entity)
    {
        _walkStack.Clear();
        var m = Matrix4x4.Identity;
        var node = entity;
        while (node.Has<LocalTransform>())
        {
            for (var i = 0; i < _walkStack.Count; i++)
            {
                if (_walkStack[i] == node)
                {
                    throw new WorldHierarchyException(BuildCyclePath(node));
                }
            }

            _walkStack.Add(node);
            m *= LocalMatrix(node.Get<LocalTransform>());

            if (!node.Has<Parent>())
            {
                break;
            }

            node = node.Get<Parent>().Value;
        }

        return m;
    }

    private static Matrix4x4 LocalMatrix(in LocalTransform lt)
        => Matrix4x4.CreateScale(lt.Scale)
         * Matrix4x4.CreateFromQuaternion(lt.Rotation)
         * Matrix4x4.CreateTranslation(lt.Position.ToVector3(Double3.Zero));

    private string BuildCyclePath(Entity repeated)
    {
        var ids = string.Join(" -> ", _walkStack.Select(e => e.Id));
        return $"Cycle in entity hierarchy: {ids} -> {repeated.Id}";
    }

    // --- Test helpers (internal) : build hierarchies the public API does not yet expose (M2 W2). They take and
    // return EntityRef (an opaque wrapper), NOT Arch's Entity — the test assembly cannot compile against Arch
    // (PrivateAssets=compile), and this keeps the ECS type out of even the internal surface. ---

    internal EntityRef SpawnLocalRoot(Double3 position, Quaternion rotation, float scale)
        => new(_world.Create(new LocalTransform { Position = position, Rotation = rotation, Scale = scale },
                             new WorldTransform { Value = Matrix4x4.Identity }));

    internal EntityRef SpawnLocalChild(EntityRef parent, Double3 position, Quaternion rotation, float scale)
        => new(_world.Create(new LocalTransform { Position = position, Rotation = rotation, Scale = scale },
                             new WorldTransform { Value = Matrix4x4.Identity },
                             new Parent { Value = parent.Value }));

    internal void SetParent(EntityRef child, EntityRef parent)
    {
        if (child.Value.Has<Parent>())
        {
            child.Value.Set(new Parent { Value = parent.Value });
        }
        else
        {
            child.Value.Add(new Parent { Value = parent.Value });
        }
    }

    internal Matrix4x4 GetWorld(EntityRef entity) => entity.Value.Get<WorldTransform>().Value;

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
