using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Core;
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
public sealed partial class GameWorld : IDisposable
{
    private readonly ArchWorld _world;

    // Reused across PropagateTransforms calls for on-stack cycle detection, so the steady-state propagation
    // allocates nothing (Clear keeps capacity). Depth is tiny (hierarchy chains), a linear scan is cheapest.
    private readonly List<Entity> _walkStack = new(16);

    // Built once: constructing a QueryDescription per frame would defeat the zero-alloc goal.
    private static readonly QueryDescription PropagateDesc =
        new QueryDescription().WithAll<LocalTransform, WorldTransform, WorldPosition>();
    private static readonly QueryDescription BoundsDesc =
        new QueryDescription().WithAll<WorldTransform, WorldPosition, Bounds>();
    private static readonly QueryDescription DrawableDesc =
        new QueryDescription().WithAll<WorldTransform, WorldPosition, Bounds, MeshRef, RenderOrder>();
    // The shadow-caster set (P3-M5 CSM): every drawable EXCEPT those tagged NoShadowCast (the ground plane), so a
    // large flat receiver does not cast on itself. One iteration buckets these into the per-cascade caster lists.
    private static readonly QueryDescription CasterDesc =
        new QueryDescription().WithAll<WorldTransform, WorldPosition, Bounds, MeshRef, RenderOrder>().WithNone<NoShadowCast>();
    // MeshRef is in the WithAll on purpose: GlobalId is now universal (every entity gets one at spawn, P3-M2 D2),
    // so it can no longer be the discriminant for "is a drawable". Animation targets DRAWABLES — the imported,
    // baked entities — never the hierarchical transform nodes, which the propagation system owns.
    private static readonly QueryDescription AnimateDesc =
        new QueryDescription().WithAll<GlobalId, WorldPosition, WorldTransform, MeshRef>();
    // Cascade despawn (D2.a): the parent link is child->parent only, so destroying a subtree means scanning every
    // Parent-carrying entity to a fixed point. Built once (a per-flush QueryDescription would defeat zero-alloc).
    private static readonly QueryDescription ParentScanDesc =
        new QueryDescription().WithAll<Parent>();

    // The thread that built this world. Arch's world creation and entity storage are not thread-safe, so a world
    // is owned by one thread; the guard below turns that contract from a comment into a checked invariant
    // (audit M2: "a contract that is not guarded is a contract that will be violated at the first job system").
    private readonly int _ownerThreadId = Environment.CurrentManagedThreadId;

    // --- Lifecycle state (P3-M2, decision D2) --------------------------------------------------------------------
    // GameWorld owns its OWN deferred-command queue (NOT Arch's CommandBuffer — the decompiled Arch 2.1.0 buffer
    // invalidates its entity handles at playback, cannot be reset without re-playback, and does not resolve entity
    // refs held in component data; see the spec's D2 correction). Every structural change is enqueued during a stage
    // and applied by FlushStructuralChanges() at the end-of-stage barrier, where NO query is iterating — so the
    // immediate _world.Create/Destroy/Add/Remove below are safe. All collections are reused (Clear keeps capacity):
    // in steady churn the entity count is stable, so the flush allocates nothing.

    // The durable identity that PRECEDES creation: assigned at enqueue, it is what an EntityRef carries. Starts at 1
    // so that default(EntityRef) (id 0) is the "no entity" sentinel. Decoupled from RenderOrder (which is a sort key,
    // not an identity, and need not be unique).
    private ulong _nextGlobalId = 1;

    // GlobalId -> real Arch Entity, for every FLUSHED, live entity. The public EntityRef surface resolves through
    // this; the hot-path systems never touch it (they follow Parent.Value, a raw Entity, directly).
    private readonly Dictionary<ulong, Entity> _live = new();

    // GlobalIds enqueued for spawn but not yet materialised. Keeps IsAlive true for a just-spawned handle before the
    // barrier, without pretending the entity exists (Deref still throws — there is no entity to read yet).
    private readonly HashSet<ulong> _pendingSpawn = new();

    // GlobalIds enqueued for despawn. IsAlive is false the instant an id lands here (the entity is LOGICALLY dead),
    // even though it stays in _live and remains queryable until the barrier applies the destroy.
    private readonly HashSet<ulong> _pendingDead = new();

    private readonly List<StructuralCommand> _commands = new();

    // Reused scratch for the cascade: the set of real entities to destroy this flush (seeded from _pendingDead,
    // grown to a fixed point by the Parent scan). A HashSet, not a list, so the fixed-point membership test is O(1).
    private readonly HashSet<Entity> _destroyScratch = new();

    private enum CommandKind : byte
    {
        SpawnNode,      // a hierarchical transform node (LocalTransform), optionally parented
        SpawnDrawable,  // a baked imported drawable (mirrors SpawnImported, deferred)
        SetParent,      // reparent an existing entity (ParentId == 0 clears the parent)
        Despawn,        // destroy an entity and, cascading, its descendants
    }

    // One fat value struct rather than a class hierarchy: it lives in a reused List, so no per-command allocation and
    // no boxing. ImportedEntitySpec is embedded (the struct is large, but the queue is bounded by one frame's churn).
    private struct StructuralCommand
    {
        public CommandKind Kind;
        public ulong Target;                 // GlobalId this command concerns
        public ulong ParentId;               // SpawnNode: parent (0 = root). SetParent: new parent (0 = clear).
        public LocalTransform Local;         // SpawnNode
        public ImportedEntitySpec Imported;  // SpawnDrawable
    }

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
    /// Spawns an imported drawable (spec §3.2 seam). It carries the baked rotation/scale matrix and the
    /// <see cref="Double3"/> world position of the spec (copied verbatim — no TRS round-trip, spec §6 condition
    /// a), the mesh/material handles, its world bounds and its stable render order. It deliberately has NO
    /// <see cref="LocalTransform"/>, so the propagation system never recomputes its transform.
    /// </summary>
    public void SpawnImported(in ImportedEntitySpec spec, bool castsShadow = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        // Immediate on purpose: SpawnImported is the LOAD-time seam (the Sandbox fills the scene before the loop),
        // never called from inside a running system's query. Runtime spawning goes through the deferred Spawn* API.
        var entity = MaterialiseDrawable(_nextGlobalId++, in spec);

        // castsShadow: false tags the entity NoShadowCast (P3-M5) — it RECEIVES shadows but never casts. Meant for a
        // large flat receiver (the ground plane), whose self-shadowing was a real acne source and whose bounds
        // needlessly inflated every cascade's fit. Added here at spawn, so no archetype move happens later.
        if (!castsShadow)
        {
            entity.Add(new NoShadowCast { Value = 1 });
        }
    }

    // Creates the baked drawable entity NOW and registers it in _live. GlobalId is the unique identity (from the
    // counter); RenderOrder stays the caller's sort order — the two were conflated before P3-M2 and are now split.
    private Entity MaterialiseDrawable(ulong globalId, in ImportedEntitySpec spec)
    {
        AssertNoTranslation(spec.RotationScale);
        var entity = _world.Create(
            new GlobalId { Value = globalId },
            new WorldTransform { Value = spec.RotationScale },
            new WorldPosition { Value = spec.Position },
            new MeshRef { Mesh = spec.Mesh, Material = spec.Material },
            new Bounds { Center = spec.BoundsCenter, Radius = spec.BoundsRadius },
            new RenderOrder { Value = spec.Order });
        _live[globalId] = entity;
        return entity;
    }

    // Creates a hierarchical transform node NOW (LocalTransform + the placeholder world components the propagation
    // system fills) and registers it. The Parent link is wired separately, AFTER all same-batch spawns exist.
    private Entity MaterialiseNode(ulong globalId, in LocalTransform local)
    {
        var entity = _world.Create(
            new GlobalId { Value = globalId },
            local,
            new WorldTransform { Value = Matrix4x4.Identity },
            new WorldPosition { Value = Double3.Zero });
        _live[globalId] = entity;
        return entity;
    }

    // --- Lifecycle API (P3-M2, decision D2) ----------------------------------------------------------------------

    /// <summary>
    /// Queues a hierarchical transform node for creation at the next barrier and returns its stable handle
    /// immediately (the handle is valid at once, even though the entity is created later). Pass a live
    /// <paramref name="parent"/> to attach it, or <c>default</c> for a world root. The node is driven by the
    /// transform-propagation system, exactly like the ones the internal test helpers build.
    /// </summary>
    public EntityRef Spawn(Double3 position, Quaternion rotation, float scale, EntityRef parent = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        var id = _nextGlobalId++;
        _pendingSpawn.Add(id);
        _commands.Add(new StructuralCommand
        {
            Kind = CommandKind.SpawnNode,
            Target = id,
            ParentId = parent.Id,
            Local = new LocalTransform { Position = position, Rotation = rotation, Scale = scale },
        });
        return new EntityRef(id);
    }

    /// <summary>Queues a baked drawable (a <see cref="ImportedEntitySpec"/>) for creation at the next barrier — the
    /// deferred, runtime counterpart of <see cref="SpawnImported"/>. Returns its stable handle immediately.</summary>
    public EntityRef SpawnDeferred(in ImportedEntitySpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        var id = _nextGlobalId++;
        _pendingSpawn.Add(id);
        _commands.Add(new StructuralCommand { Kind = CommandKind.SpawnDrawable, Target = id, Imported = spec });
        return new EntityRef(id);
    }

    /// <summary>
    /// Queues an entity — and, cascading, all its descendants — for destruction at the next barrier. The entity is
    /// LOGICALLY dead at once (<see cref="IsAlive"/> returns false immediately), but stays queryable until the
    /// barrier, so a system reading it later in the same stage sees no corruption. A second Despawn is a no-op.
    /// <para>
    /// The cascade is asymmetric with <see cref="IsAlive"/>: the entity itself flips to not-alive now, but its
    /// descendants are only gathered at the barrier (resolving the subtree on every call would cost a full scan per
    /// call). A system reading a child in the same stage therefore still sees it alive — documented, not a trap.
    /// </para>
    /// </summary>
    public void Despawn(EntityRef entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        if (entity.IsNone)
        {
            return;
        }

        _pendingDead.Add(entity.Id); // HashSet dedups: a double Despawn collapses to one
    }

    /// <summary>
    /// Queues a reparent at the next barrier: <paramref name="child"/> becomes a child of <paramref name="parent"/>
    /// (or a world root if <paramref name="parent"/> is <c>default</c>). Deferred because reparenting moves the
    /// entity between archetypes — doing that mid-query would invalidate the chunks being iterated.
    /// </summary>
    public void SetParent(EntityRef child, EntityRef parent)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        _commands.Add(new StructuralCommand { Kind = CommandKind.SetParent, Target = child.Id, ParentId = parent.Id });
    }

    /// <summary>
    /// True if the handle names an entity that is alive AND not queued for despawn. False the instant
    /// <see cref="Despawn"/> is called (the entity is logically dead), and false for a handle whose entity has
    /// already been destroyed at a past barrier. A just-<see cref="Spawn"/>ed handle is alive immediately.
    /// </summary>
    public bool IsAlive(EntityRef entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        if (entity.IsNone || _pendingDead.Contains(entity.Id))
        {
            return false;
        }

        return _live.ContainsKey(entity.Id) || _pendingSpawn.Contains(entity.Id);
    }

    /// <summary>
    /// Applies all queued structural changes. Called by the scheduler at the end-of-stage barrier — NEVER by a
    /// system in the middle of a query (that is the whole point of deferring). Three ordered passes so that a
    /// hierarchy spawned in one batch wires up correctly: (1) create every spawn, so all handles resolve; (2) wire
    /// the parent links, now that every same-batch parent exists; (3) destroy the despawn set, grown to its full
    /// subtree by a fixed-point scan of the Parent links.
    /// </summary>
    public void FlushStructuralChanges()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        if (_commands.Count == 0 && _pendingDead.Count == 0)
        {
            return; // the common case: nothing structural happened this stage
        }

        // Pass 1 — create every spawn (populate _live) so every handle, including same-batch parents, resolves.
        for (var i = 0; i < _commands.Count; i++)
        {
            var cmd = _commands[i];
            switch (cmd.Kind)
            {
                case CommandKind.SpawnNode:
                    MaterialiseNode(cmd.Target, cmd.Local);
                    break;
                case CommandKind.SpawnDrawable:
                    MaterialiseDrawable(cmd.Target, cmd.Imported);
                    break;
            }
        }

        // Pass 2 — wire parent links. All spawns now exist, so a same-batch parent resolves. A link whose child or
        // parent is not (or no longer) live is silently dropped: reparenting toward a despawned entity is a no-op.
        for (var i = 0; i < _commands.Count; i++)
        {
            var cmd = _commands[i];
            switch (cmd.Kind)
            {
                case CommandKind.SpawnNode when cmd.ParentId != 0:
                    LinkParent(cmd.Target, cmd.ParentId);
                    break;
                case CommandKind.SetParent:
                    if (cmd.ParentId == 0)
                    {
                        ClearParent(cmd.Target);
                    }
                    else
                    {
                        LinkParent(cmd.Target, cmd.ParentId);
                    }

                    break;
            }
        }

        // Pass 3 — destroy the despawn set and its whole subtree.
        ApplyDespawns();

        _commands.Clear();
        _pendingSpawn.Clear();
        _pendingDead.Clear();
    }

    private void LinkParent(ulong childId, ulong parentId)
    {
        if (!_live.TryGetValue(childId, out var child) || !_live.TryGetValue(parentId, out var parent))
        {
            return;
        }

        if (child.Has<Parent>())
        {
            child.Set(new Parent { Value = parent });
        }
        else
        {
            child.Add(new Parent { Value = parent });
        }
    }

    private void ClearParent(ulong childId)
    {
        if (_live.TryGetValue(childId, out var child) && child.Has<Parent>())
        {
            child.Remove<Parent>();
        }
    }

    private void ApplyDespawns()
    {
        if (_pendingDead.Count == 0)
        {
            return;
        }

        _destroyScratch.Clear();
        foreach (var id in _pendingDead)
        {
            if (_live.TryGetValue(id, out var entity))
            {
                _destroyScratch.Add(entity);
            }
        }

        if (_destroyScratch.Count == 0)
        {
            return; // everything was already gone (e.g. a spawn cancelled before it ever materialised)
        }

        // Grow the set to a fixed point: any entity whose Parent is already doomed is doomed too. Re-scanned until a
        // pass adds nothing, so a chain of arbitrary depth is caught. Only runs when a despawn is pending.
        bool grew;
        do
        {
            grew = false;
            foreach (ref var chunk in _world.Query(in ParentScanDesc))
            {
                var entities = chunk.Entities;
                var parents = chunk.GetSpan<Parent>();
                var count = chunk.Count;
                for (var i = 0; i < count; i++)
                {
                    if (_destroyScratch.Contains(parents[i].Value) && _destroyScratch.Add(entities[i]))
                    {
                        grew = true;
                    }
                }
            }
        }
        while (grew);

        foreach (var entity in _destroyScratch)
        {
            if (!_world.IsAlive(entity))
            {
                continue; // a duplicate reference, already destroyed in this loop
            }

            _live.Remove(entity.Get<GlobalId>().Value);
            _world.Destroy(entity);
        }
    }

    /// <summary>
    /// Exercises EVERY component type through the paths that trigger the AOT "missing native code" failure —
    /// create, structural add/remove (archetype moves), a query, and the deferred lifecycle (spawn/despawn/reparent
    /// flushed at a barrier, including the cascade scan) — so the <see cref="AotComponentProbe"/> (and a JIT unit
    /// test) can prove <see cref="ComponentRegistry.RootAll"/> is sufficient under a real NativeAOT publish. Returns
    /// the number of entities the query iterated. Internal: it exists only to validate rooting, not as a public API.
    /// </summary>
    internal int AotRootingSmoke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();

        // Imported-shape entities (GlobalId, WorldTransform, MeshRef, Bounds, RenderOrder).
        for (var i = 0; i < 8; i++)
        {
            SpawnImported(new ImportedEntitySpec(
                new MeshHandle(i, 1), new MaterialHandle(0, 1), new Double3(i, 0, 0), Matrix4x4.Identity,
                Vector3.Zero, 1f, (uint)i));
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

        // The deferred lifecycle (P3-M2 D2), the path this milestone adds — rooted end to end under ILC: enqueue a
        // hierarchy through the public API, flush it (Create + Add<Parent> at the barrier), then despawn the root so
        // the cascade scan (the Parent query + Destroy) runs too. F3: the structural path must not stay unproven.
        var lifeRoot = Spawn(new Double3(2, 0, 0), Quaternion.Identity, 1f);
        Spawn(new Double3(0, 1, 0), Quaternion.Identity, 1f, lifeRoot);
        var lifeGrand = Spawn(new Double3(0, 0, 1), Quaternion.Identity, 1f);
        SetParent(lifeGrand, lifeRoot);
        FlushStructuralChanges();
        Despawn(lifeRoot);                                // cascades to both children at the next barrier
        FlushStructuralChanges();

        // A pair of deferred drawables left ALIVE, so the final WorldTransform+RenderOrder count stays >= 9 (they
        // carry both), and SpawnDeferred + its materialisation are rooted. Placed FAR off-axis (x 500) so the wide
        // frustum below still culls them out of the render list (which must stay exactly 8), while the un-culled raw
        // query at the end still counts them.
        SpawnDeferred(new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(500, 0, 0), Matrix4x4.Identity,
            Vector3.Zero, 1f, 100));
        SpawnDeferred(new ImportedEntitySpec(
            new MeshHandle(1, 1), new MaterialHandle(0, 1), new Double3(501, 0, 0), Matrix4x4.Identity,
            Vector3.Zero, 1f, 101));
        FlushStructuralChanges();

        // Hierarchy + the M2 systems, so the probe proves the exact chunk-iteration / entity-walk paths W2 uses
        // survive AOT (audit W1 M2 gate), not only Create/Add/Remove.
        var root = SpawnLocalRoot(new Double3(1, 0, 0), Quaternion.Identity, 1f);
        SpawnLocalChild(root, new Double3(0, 2, 0), Quaternion.Identity, 1f);
        PropagateTransforms();
        _ = AggregateBounds();

        // Both collection paths, so the probe covers exactly what the game runs per frame (audit M2, minor 1):
        // GetSpan<MeshRef/RenderOrder/Bounds>(), the camera-relative narrow, and the per-cascade caster bucketing
        // would otherwise be untested under AOT. Since P3-M4 the scene is NOT CPU-culled (the camera cull is a GPU
        // compute pass), so the candidate list holds EVERY drawable that exists AT THIS POINT: 8 imported + 2 alive
        // deferred (x 500) = 10. The physics bodies are spawned AFTER this call (they lift the raw count to 12).
        var wide = Frustum.FromViewProjection(
            MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * MathHelpers.OrthographicVulkan(200f, 200f, -100f, 100f));
        var smokeView = new RenderView(
            Double3.Zero, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, 1f, 1f, 0.1f, 1f);
        var render = new RenderList();
        CollectRenderLists(render, in smokeView);
        if (render.Count != 10)
        {
            throw new InvalidOperationException(
                $"AOT smoke: CollectRenderLists produced {render.Count} candidates, expected 10.");
        }

        // The CSM caster path (P3-M5): two cascades, so the ILC roots the span-of-frusta bucketing and the
        // WithNone<NoShadowCast> query. Both frusta are the same wide box here — every caster lands in both.
        Span<Frustum> smokeCascades = stackalloc Frustum[2] { wide, wide };
        var smokeCasters = new[] { new RenderList(), new RenderList() };
        CollectShadowCasters(smokeCascades, smokeCasters, in smokeView);
        // 8, not 10: the two deferred drawables sit at x 500, far outside this frustum, so the per-cascade cull drops
        // them — which is exactly the path being exercised. Both cascades see the same volume, hence the same count.
        if (smokeCasters[0].Count != 8 || smokeCasters[1].Count != 8)
        {
            throw new InvalidOperationException(
                $"AOT smoke: cascades collected {smokeCasters[0].Count}/{smokeCasters[1].Count} casters, expected 8/8.");
        }

        // AnimateDrawables is generic over a struct animator — the M4 direct-write animation path. Instantiate it
        // here with a concrete struct so the ILC compiles that instantiation (a generic method over a struct is
        // the AOT-fragile shape P2-M0 flagged), and confirm the write-in-place actually mutated a component.
        // 10 = the 8 imported + the 2 surviving deferred drawables (AnimateDrawables has no frustum, so the far
        // placement that culls them from the render list does not spare them here — they are still drawables).
        var animator = new SmokeAnimator();
        AnimateDrawables(ref animator);
        if (animator.Count != 10)
        {
            throw new InvalidOperationException(
                $"AOT smoke: AnimateDrawables visited {animator.Count} drawables, expected 10.");
        }

        // Physics (P3-M3): two overlapping bodies + one StepPhysics roots the whole physics path under ILC — the
        // BodyDesc chunk query, the Entity.Set scatter, and the broadphase/sort/impulse of ResolveBodyContacts (a
        // lone body would skip the pair path entirely). Both carry WorldTransform + RenderOrder, so the final count
        // rises to 12; the caller only asserts >= 9.
        var bodySpec = new ImportedEntitySpec(
            new MeshHandle(0, 1), new MaterialHandle(0, 1), new Double3(600, 0, 0), Matrix4x4.Identity,
            Vector3.Zero, 1f, 200);
        SpawnBody(in bodySpec, new Vector3(1, 0, 0), inverseMass: 1f, restitution: 0.3f, radius: 1f);
        SpawnBody(in bodySpec, new Vector3(-1, 0, 0), inverseMass: 1f, restitution: 0.3f, radius: 1f); // overlaps → pair path
        StepPhysics(PhysicsSettings.Default(groundY: -10f));

        // Chunk-iteration query (the path the systems use) touching several component arrays. Counts the 8 imported
        // entities, the 2 surviving deferred drawables, and the 2 physics bodies (all carry WorldTransform +
        // RenderOrder) = 12.
        var count = 0;
        foreach (ref var chunk in _world.Query(new QueryDescription().WithAll<WorldTransform, RenderOrder>()))
        {
            count += chunk.Count;
        }

        return count;
    }

    /// <summary>
    /// Advances every imported drawable by writing its world transform IN PLACE (spec §3.4 deviation, board D5):
    /// the position (double) and rotation/scale of a baked drawable are handed to the animator by <c>ref</c>. No
    /// <see cref="LocalTransform"/> is touched and no component is added or removed, so there is no archetype move
    /// — the write is zero-alloc and does not need a command buffer. This is how a flat (imported) scene animates
    /// without the hierarchy-propagation cost; hierarchical animation stays with <see cref="PropagateTransforms"/>.
    /// <para>
    /// <typeparamref name="TAnimator"/> is a <c>struct</c> constrained to <see cref="IDrawableAnimator"/>: the call
    /// is devirtualized, never boxed, and NativeAOT-safe (the concrete instantiation is reachable from the caller).
    /// </para>
    /// </summary>
    public void AnimateDrawables<TAnimator>(ref TAnimator animator)
        where TAnimator : struct, IDrawableAnimator
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        foreach (ref var chunk in _world.Query(in AnimateDesc))
        {
            var ids = chunk.GetSpan<GlobalId>();
            var positions = chunk.GetSpan<WorldPosition>();
            var worlds = chunk.GetSpan<WorldTransform>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                animator.Animate(ids[i].Value, ref positions[i].Value, ref worlds[i].Value);
                AssertNoTranslation(worlds[i].Value); // the animator must not bake a translation (see the contract)
            }
        }
    }

    // Trivial animator for AotRootingSmoke: mutates the position (keeping the rotation/scale translation zero) and
    // counts visits, so the smoke can both root AnimateDrawables<T> under AOT and assert the write took effect.
    private struct SmokeAnimator : IDrawableAnimator
    {
        public int Count;

        public void Animate(ulong globalId, ref Double3 position, ref Matrix4x4 rotationScale)
        {
            position += new Double3(0.1, 0, 0);
            Count++;
        }
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
            var positions = chunk.GetSpan<WorldPosition>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                ComputeWorld(entities[i], out worlds[i].Value, out positions[i].Value);
            }
        }
    }

    /// <summary>
    /// System 2: transforms every entity's LOCAL bounding sphere to world space and unions the enclosing boxes
    /// into the scene extent (spec §3.5). Zero-alloc (chunk iteration). Returns <see cref="Double3Bounds.Empty"/>
    /// if there are no bounded entities (the caller guards degenerate extents). The extent is derived from the
    /// spheres, so it is a touch looser than a raw vertex AABB — that is the price of a rotation-invariant bound,
    /// and it only over-covers.
    /// </summary>
    public Double3Bounds AggregateBounds()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        var acc = Double3Bounds.Empty;
        foreach (ref var chunk in _world.Query(in BoundsDesc))
        {
            var worlds = chunk.GetSpan<WorldTransform>();
            var positions = chunk.GetSpan<WorldPosition>();
            var bounds = chunk.GetSpan<Bounds>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                WorldSphere(worlds[i].Value, positions[i].Value, bounds[i], out var center, out var radius);
                var r = new Double3(radius, radius, radius);
                acc = Double3Bounds.Union(acc, new Double3Bounds(center - r, center + r));
            }
        }

        return acc;
    }

    /// <summary>
    /// Transforms a local bounding sphere to world space (spec §3.4): the centre through the entity's rotation/
    /// scale then offset by its double-precision world position, and the radius grown by the transform's largest
    /// axis scale so it stays conservative. Kept in <see cref="Double3"/> for the centre — the scene may be
    /// 10 000 km out — and shared by <see cref="AggregateBounds"/> and (M4) the culling loop.
    /// </summary>
    private static void WorldSphere(
        in Matrix4x4 rotationScale, Double3 position, in Bounds local, out Double3 center, out float radius)
    {
        center = position + new Double3(Vector3.Transform(local.Center, rotationScale));
        radius = local.Radius * MathHelpers.MaxStretch(rotationScale);
    }

    /// <summary>
    /// PASS 1 of the frame's culling (spec §3.5, systems 3-4; P3-M2 D3.c). Each drawable's world bounding sphere is
    /// tested against the CAMERA frustum (→ render list) and the BOUNDED WEDGE (→ shadow-caster list). The two are
    /// separate on purpose — a caster off-screen still throws its shadow on-screen, so the caster list is culled
    /// against the light region, never the camera, or shadows would pop at the screen edge. This pass produces a
    /// SUPERSET of the final caster list (the wedge is looser than the fitted light volume) plus its
    /// <paramref name="casterBounds"/> and a parallel sphere array; the caller fits the light to those, then calls
    /// <see cref="CompactShadowCasters"/> to tighten the list against the light volume. The render list is final and
    /// sorted here; the shadow list is left unsorted for pass 2.
    /// <para>
    /// Zero-alloc: the lists and the sphere array are reused (Clear keeps capacity), iteration is chunk-based, no
    /// lambda. The culling is a linear scan (dot products per entity per frustum) — a spatial structure would have to
    /// refit every frame in a moving world and cost more than the scan below this milestone's entity counts (board D4).
    /// </para>
    /// </summary>
    public void CollectRenderLists(RenderList render, in RenderView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(render);

        render.Clear();
        var origin = view.Origin;

        foreach (ref var chunk in _world.Query(in DrawableDesc))
        {
            var worlds = chunk.GetSpan<WorldTransform>();
            var positions = chunk.GetSpan<WorldPosition>();
            var bounds = chunk.GetSpan<Bounds>();
            var meshes = chunk.GetSpan<MeshRef>();
            var orders = chunk.GetSpan<RenderOrder>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                // Every drawable is a scene CANDIDATE (P3-M4): the camera-frustum cull runs on the GPU, so the CPU
                // narrows the world sphere to camera-relative (in double, then cast), bakes the model, and uploads.
                WorldSphere(worlds[i].Value, positions[i].Value, bounds[i], out var worldCenter, out var radius);
                var center = worldCenter.ToVector3(origin);
                var sphere = new Vector4(center, radius);
                var model = ComposeModel(worlds[i].Value, positions[i].Value, origin);

                // Sorted by (material, mesh) so a run is one batch (P3-M1); carries its camera-relative sphere for
                // the GPU cull (P3-M4). Tie-break = the stable RenderOrder (spec §6 condition b).
                var key = RenderItem.ComposeSortKey(
                    meshes[i].Material.Index, meshes[i].Mesh.Index, orders[i].Value);
                render.Add(new RenderItem(model, meshes[i].Mesh, meshes[i].Material, key, sphere));
            }
        }

        render.SortByKey(); // into batch order for the GPU cull
    }

    /// <summary>
    /// Buckets the shadow casters into the CSM cascades (P3-M5): every drawable NOT tagged <see cref="NoShadowCast"/>
    /// is tested against each cascade's light frustum and added to the matching <paramref name="cascadeCasters"/>
    /// list (an entity straddling two cascades is drawn in both). This replaces the P3-M2 two-pass wedge: each
    /// cascade's fit is camera-only, so its frustum is known here without any caster bounds — no circularity, no
    /// parallel-sphere state. Each caster list keeps the MESH-major key (P3-M1) for instanced depth draws and is
    /// sorted here. Zero-alloc: the lists are reused (Clear keeps capacity), iteration is chunk-based, no lambda.
    /// </summary>
    public void CollectShadowCasters(
        ReadOnlySpan<Frustum> cascadeFrusta, RenderList[] cascadeCasters, in RenderView view)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(cascadeCasters);
        var n = cascadeFrusta.Length;
        if (cascadeCasters.Length < n)
        {
            throw new ArgumentException($"cascadeCasters must hold at least {n} lists.", nameof(cascadeCasters));
        }

        for (var c = 0; c < n; c++)
        {
            ArgumentNullException.ThrowIfNull(cascadeCasters[c]);
            cascadeCasters[c].Clear();
        }

        var origin = view.Origin;
        foreach (ref var chunk in _world.Query(in CasterDesc))
        {
            var worlds = chunk.GetSpan<WorldTransform>();
            var positions = chunk.GetSpan<WorldPosition>();
            var bounds = chunk.GetSpan<Bounds>();
            var meshes = chunk.GetSpan<MeshRef>();
            var orders = chunk.GetSpan<RenderOrder>();
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                WorldSphere(worlds[i].Value, positions[i].Value, bounds[i], out var worldCenter, out var radius);
                var center = worldCenter.ToVector3(origin);
                // The depth pass binds no material → MESH-major key (one instanced run per mesh).
                var shadowKey = RenderItem.ComposeShadowSortKey(
                    meshes[i].Mesh.Index, meshes[i].Material.Index, orders[i].Value);
                Matrix4x4? model = null; // built once, lazily, only if this caster lands in any cascade
                for (var c = 0; c < n; c++)
                {
                    if (!cascadeFrusta[c].Intersects(center, radius))
                    {
                        continue;
                    }

                    model ??= ComposeModel(worlds[i].Value, positions[i].Value, origin);
                    cascadeCasters[c].Add(new RenderItem(model.Value, meshes[i].Mesh, meshes[i].Material, shadowKey));
                }
            }
        }

        for (var c = 0; c < n; c++)
        {
            cascadeCasters[c].SortByKey(); // deterministic mesh-major run order for the instanced depth draw
        }
    }

    /// <summary>
    /// Guards the split (spec §3.3): the translation row of a <see cref="WorldTransform"/> must be zero, because
    /// <see cref="ComposeModel"/> OVERWRITES it with the camera-relative position. A translation baked in there —
    /// by an importer, an animation, a physics step — would be silently thrown away, and the entity would simply
    /// render in the wrong place with nothing to show for it. Debug-only: the check costs nothing in Release, and
    /// this is a coding error, not a runtime condition.
    /// </summary>
    [Conditional("DEBUG")]
    private static void AssertNoTranslation(in Matrix4x4 rotationScale)
    {
        if (rotationScale.M41 != 0f || rotationScale.M42 != 0f || rotationScale.M43 != 0f)
        {
            throw new WorldHierarchyException(
                "WorldTransform carries a translation " +
                $"({rotationScale.M41}, {rotationScale.M42}, {rotationScale.M43}), but the position must live in " +
                "WorldPosition (Double3): the render list overwrites that row, so this translation would be " +
                "silently dropped.");
        }
    }

    // Recombines the two halves of a world transform into the float model matrix the GPU consumes: the baked
    // rotation/scale, with the camera-relative position dropped into its translation row (row-vector layout).
    private static Matrix4x4 ComposeModel(in Matrix4x4 rotationScale, Double3 position, Double3 origin)
    {
        var relative = position.ToVector3(origin);
        var m = rotationScale;
        m.M41 = relative.X;
        m.M42 = relative.Y;
        m.M43 = relative.Z;
        return m;
    }

    // Composes an entity's world transform from its Parent chain (row-vector convention:
    // rotationScale(e) = rs(e) · rs(parent) · … · rs(root)), keeping the POSITION in double throughout.
    //
    // The walk collects the chain child-first (detecting cycles on the reused stack), then composes it
    // root-first, which is what makes the precision work: at the root the accumulated rotation/scale is the
    // identity, so the root's Double3 position passes through untouched — a root 10 000 km out stays exact
    // instead of being rounded to a float metre. Each child then adds its own offset rotated/scaled by its
    // parents' (float) matrix: those offsets are local, hence small, so a float matrix is enough for them.
    // A chain node without a LocalTransform ends the walk (it is a world anchor).
    private void ComputeWorld(Entity entity, out Matrix4x4 rotationScale, out Double3 position)
    {
        _walkStack.Clear();
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

            if (!node.Has<Parent>())
            {
                break;
            }

            node = node.Get<Parent>().Value;
        }

        rotationScale = Matrix4x4.Identity;
        position = Double3.Zero;
        for (var i = _walkStack.Count - 1; i >= 0; i--) // root -> entity
        {
            var lt = _walkStack[i].Get<LocalTransform>();

            // Transform BEFORE accumulating: rotationScale currently holds the PARENT's accumulation, which is
            // the frame this node's local offset is expressed in.
            position += lt.Position.TransformBy(rotationScale);
            rotationScale = LocalRotationScale(lt) * rotationScale;
        }
    }

    private static Matrix4x4 LocalRotationScale(in LocalTransform lt)
        => Matrix4x4.CreateScale(lt.Scale) * Matrix4x4.CreateFromQuaternion(lt.Rotation);

    private string BuildCyclePath(Entity repeated)
    {
        var ids = string.Join(" -> ", _walkStack.Select(e => e.Id));
        return $"Cycle in entity hierarchy: {ids} -> {repeated.Id}";
    }

    // --- Test helpers (internal) : build hierarchies the public API does not yet expose (M2 W2). They take and
    // return EntityRef (an opaque wrapper), NOT Arch's Entity — the test assembly cannot compile against Arch
    // (PrivateAssets=compile), and this keeps the ECS type out of even the internal surface. ---

    internal EntityRef SpawnLocalRoot(Double3 position, Quaternion rotation, float scale)
    {
        var id = _nextGlobalId++;
        MaterialiseNode(id, new LocalTransform { Position = position, Rotation = rotation, Scale = scale });
        return new EntityRef(id);
    }

    internal EntityRef SpawnLocalChild(EntityRef parent, Double3 position, Quaternion rotation, float scale)
    {
        var id = _nextGlobalId++;
        MaterialiseNode(id, new LocalTransform { Position = position, Rotation = rotation, Scale = scale });
        LinkParent(id, parent.Id);
        return new EntityRef(id);
    }

    // Resolves a handle to its real entity, or throws if it names none that is live (never spawned, a deferred spawn
    // not yet flushed, or destroyed at a past barrier). The single guarded gate D2.b requires: once a despawn takes
    // effect, every accessor routed through here throws instead of dereferencing a recycled slot.
    private Entity Deref(EntityRef entity)
    {
        if (!entity.IsNone && _live.TryGetValue(entity.Id, out var e))
        {
            return e;
        }

        throw new InvalidOperationException(
            $"EntityRef {entity.Id} does not name a live entity (never spawned, not yet flushed, or despawned).");
    }

    /// <summary>The entity's full world matrix, relative to <paramref name="origin"/> (absolute by default).</summary>
    internal Matrix4x4 GetWorld(EntityRef entity, Double3 origin = default)
    {
        var e = Deref(entity);
        return ComposeModel(e.Get<WorldTransform>().Value, e.Get<WorldPosition>().Value, origin);
    }

    internal Double3 GetWorldPosition(EntityRef entity) => Deref(entity).Get<WorldPosition>().Value;

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
