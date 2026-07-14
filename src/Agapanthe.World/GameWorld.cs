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
    private static readonly QueryDescription PropagateDesc =
        new QueryDescription().WithAll<LocalTransform, WorldTransform, WorldPosition>();
    private static readonly QueryDescription BoundsDesc =
        new QueryDescription().WithAll<WorldTransform, WorldPosition, Bounds>();
    private static readonly QueryDescription DrawableDesc =
        new QueryDescription().WithAll<WorldTransform, WorldPosition, Bounds, MeshRef, RenderOrder>();
    private static readonly QueryDescription AnimateDesc =
        new QueryDescription().WithAll<GlobalId, WorldPosition, WorldTransform>();

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
    /// Spawns an imported drawable (spec §3.2 seam). It carries the baked rotation/scale matrix and the
    /// <see cref="Double3"/> world position of the spec (copied verbatim — no TRS round-trip, spec §6 condition
    /// a), the mesh/material handles, its world bounds and its stable render order. It deliberately has NO
    /// <see cref="LocalTransform"/>, so the propagation system never recomputes its transform.
    /// </summary>
    public void SpawnImported(in ImportedEntitySpec spec)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        AssertNoTranslation(spec.RotationScale);
        _world.Create(
            new GlobalId { Value = spec.Order },
            new WorldTransform { Value = spec.RotationScale },
            new WorldPosition { Value = spec.Position },
            new MeshRef { Mesh = spec.Mesh, Material = spec.Material },
            new Bounds { Center = spec.BoundsCenter, Radius = spec.BoundsRadius },
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

        // Deferred structural change through a CommandBuffer (the path P2-M0 flagged as most AOT-fragile).
        using var cb = new CommandBuffer();
        var deferred = cb.Create([Component<WorldTransform>.ComponentType]);
        cb.Set(deferred, new WorldTransform { Value = Matrix4x4.Identity });
        cb.Add(deferred, new WorldPosition { Value = new Double3(1, 2, 3) });
        cb.Add(deferred, new RenderOrder { Value = 99 });
        cb.Playback(_world);

        // Hierarchy + the M2 systems, so the probe proves the exact chunk-iteration / entity-walk paths W2 uses
        // survive AOT (audit W1 M2 gate), not only Create/Add/Remove.
        var root = SpawnLocalRoot(new Double3(1, 0, 0), Quaternion.Identity, 1f);
        SpawnLocalChild(root, new Double3(0, 2, 0), Quaternion.Identity, 1f);
        PropagateTransforms();
        _ = AggregateBounds();

        // ALL THREE systems, so the probe covers exactly what the game runs per frame (audit M2, minor 1):
        // CollectRenderLists is the only user of GetSpan<MeshRef/RenderOrder/Bounds>() and Frustum.Intersects,
        // which the smoke would otherwise leave untested under AOT. A wide frustum containing the 8 imported
        // drawables (at x 0..7) makes the count deterministic while still exercising the cull path + the
        // camera-relative narrow. The CommandBuffer entity above has no MeshRef, so it is not a drawable.
        var wide = Frustum.FromViewProjection(
            MathHelpers.LookAt(Vector3.Zero, -Vector3.UnitZ, Vector3.UnitY)
            * MathHelpers.OrthographicVulkan(200f, 200f, -100f, 100f));
        var smokeView = new RenderView(
            Double3.Zero, Vector3.Zero, Matrix4x4.Identity, Matrix4x4.Identity, 1f, 1f, 0.1f, 1f);
        // Extruded shadow frustum (P3-M1): built here too so the ILC roots the type + Intersects under AOT.
        var wideExtruded = ExtrudedShadowFrustum.FromCameraFrustum(in wide, -Vector3.UnitY);
        var render = new RenderList();
        CollectRenderLists(render, new RenderList(), in smokeView, in wide, in wide, in wideExtruded);
        if (render.Count != 8)
        {
            throw new InvalidOperationException(
                $"AOT smoke: CollectRenderLists produced {render.Count} drawables, expected 8.");
        }

        // AnimateDrawables is generic over a struct animator — the M4 direct-write animation path. Instantiate it
        // here with a concrete struct so the ILC compiles that instantiation (a generic method over a struct is
        // the AOT-fragile shape P2-M0 flagged), and confirm the write-in-place actually mutated a component.
        var animator = new SmokeAnimator();
        AnimateDrawables(ref animator);
        if (animator.Count != 8)
        {
            throw new InvalidOperationException(
                $"AOT smoke: AnimateDrawables visited {animator.Count} drawables, expected 8.");
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
    /// Builds the two render lists (spec §3.5, systems 3-4). Each drawable's world bounding sphere is tested
    /// against two frusta: the CAMERA frustum decides the render list, the LIGHT volume decides the shadow-caster
    /// list. They are separate on purpose — a caster off-screen still throws its shadow on-screen, so the caster
    /// list is culled against the light, never the camera, or shadows would pop in and out at the screen edge.
    /// Both lists are sorted by <see cref="RenderOrder"/> so the draw order is deterministic even though Arch
    /// iterates by archetype/chunk (spec §6 condition b).
    /// <para>
    /// Zero-alloc: the lists are reused (Clear keeps capacity), iteration is chunk-based, no lambda. The culling
    /// is a linear scan (six dot products per entity per frustum) — a spatial structure would have to refit every
    /// frame in a moving world and cost more than the scan below this milestone's entity counts (board D4).
    /// </para>
    /// </summary>
    public void CollectRenderLists(
        RenderList render, RenderList shadowCasters, in RenderView view, in Frustum cameraFrustum,
        in Frustum lightFrustum, in ExtrudedShadowFrustum lightExtruded)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(render);
        ArgumentNullException.ThrowIfNull(shadowCasters);

        render.Clear();
        shadowCasters.Clear();
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
                // The world bounding sphere, narrowed to camera-relative (in double, then cast) — the same space
                // the frusta live in. Testing the sphere is what culling needs; the (heavier) model matrix is
                // built only for entities that survive.
                WorldSphere(worlds[i].Value, positions[i].Value, bounds[i], out var worldCenter, out var radius);
                var center = worldCenter.ToVector3(origin);

                var inCamera = cameraFrustum.Intersects(center, radius);
                // A caster matters only if it is in the light VOLUME (what the shadow map covers) AND upstream of
                // the view along the light (its shadow can reach the screen) — the extruded frustum (P3-M1, debt
                // #2). ANDing tightens the caster set without ever dropping a shadow that reaches the view.
                var inLight = lightFrustum.Intersects(center, radius) && lightExtruded.Intersects(center, radius);
                if (!inCamera && !inLight)
                {
                    continue;
                }

                var model = ComposeModel(worlds[i].Value, positions[i].Value, origin);
                // Sort key: material then mesh in the high bits (so a sorted run shares one (material, mesh) pair =
                // one instanced draw, P3-M1), the stable RenderOrder in the low bits as the deterministic tie-break
                // (spec §6 condition b — Arch's chunk order is not stable).
                if (inCamera)
                {
                    var key = RenderItem.ComposeSortKey(
                        meshes[i].Material.Index, meshes[i].Mesh.Index, orders[i].Value);
                    render.Add(new RenderItem(model, meshes[i].Mesh, meshes[i].Material, key));
                }

                if (inLight)
                {
                    // The depth pass binds no material, so the caster list is keyed MESH-major: one contiguous run
                    // (= one instanced depth draw) per mesh, even when several materials share it.
                    var shadowKey = RenderItem.ComposeShadowSortKey(
                        meshes[i].Mesh.Index, meshes[i].Material.Index, orders[i].Value);
                    shadowCasters.Add(new RenderItem(model, meshes[i].Mesh, meshes[i].Material, shadowKey));
                }
            }
        }

        render.SortByKey();
        shadowCasters.SortByKey();
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
        => new(_world.Create(new LocalTransform { Position = position, Rotation = rotation, Scale = scale },
                             new WorldTransform { Value = Matrix4x4.Identity },
                             new WorldPosition { Value = Double3.Zero }));

    internal EntityRef SpawnLocalChild(EntityRef parent, Double3 position, Quaternion rotation, float scale)
        => new(_world.Create(new LocalTransform { Position = position, Rotation = rotation, Scale = scale },
                             new WorldTransform { Value = Matrix4x4.Identity },
                             new WorldPosition { Value = Double3.Zero },
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

    /// <summary>The entity's full world matrix, relative to <paramref name="origin"/> (absolute by default).</summary>
    internal Matrix4x4 GetWorld(EntityRef entity, Double3 origin = default)
        => ComposeModel(
            entity.Value.Get<WorldTransform>().Value,
            entity.Value.Get<WorldPosition>().Value,
            origin);

    internal Double3 GetWorldPosition(EntityRef entity) => entity.Value.Get<WorldPosition>().Value;

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
