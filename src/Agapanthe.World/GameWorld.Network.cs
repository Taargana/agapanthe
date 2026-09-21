using System.Numerics;
using Agapanthe.Core;
using Arch.Core;
using Arch.Core.Extensions;

namespace Agapanthe.World;

// Net-1: the network layer's read/write surface on GameWorld. Sibling to .Physics.cs/.Queries.cs — a new
// consumer of the world, not a new kind of world.
public sealed partial class GameWorld
{
    /// <summary>
    /// Resolves a live entity by its raw <see cref="GlobalId"/> value — the network layer's counterpart to
    /// <see cref="EntityRef"/>'s internal constructor, which no outside assembly can call directly. Returns
    /// <see langword="false"/> for an unknown, not-yet-flushed, or despawned id.
    /// </summary>
    public bool TryGetEntity(ulong globalId, out EntityRef entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThreadStrict();

        if (globalId != 0 && _live.ContainsKey(globalId) && !_pendingDead.Contains(globalId))
        {
            entity = new EntityRef(globalId);
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>
    /// The raw <see cref="GlobalId"/> value behind a handle — the counterpart to <see cref="TryGetEntity"/>: a
    /// host that just spawned an entity (e.g. <c>SpawnBody</c> returns an <see cref="EntityRef"/>) has no other
    /// way to learn the number it would send a peer over the wire, since <c>EntityRef.Id</c> itself is internal
    /// to this assembly. Read-only — safe for a sanctioned worker thread, unlike every mutator in this file.
    /// </summary>
    public ulong GetGlobalId(EntityRef entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        return entity.Id;
    }

    /// <summary>
    /// A drawable's current replicated state (Net-1 D9): its <see cref="Core.MeshRefKey"/> asset identity plus
    /// its position/rotation/scale, decomposed from <c>WorldTransform</c>'s baked rotation+scale matrix and
    /// <c>WorldPosition</c>'s authoritative double-precision position. Throws if the handle names no live
    /// drawable (no <c>WorldPosition</c>/<c>WorldTransform</c>).
    /// </summary>
    public DrawableTransform GetDrawableTransform(EntityRef entity)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThreadStrict();

        var e = Deref(entity);
        if (!e.Has<WorldPosition>() || !e.Has<WorldTransform>())
        {
            throw new InvalidOperationException(
                $"EntityRef {entity.Id} is not a drawable (no WorldPosition/WorldTransform) — cannot read a " +
                "DrawableTransform for it.");
        }

        var assetIdentity = e.Has<AssetRef>() ? e.Get<AssetRef>().Value : MeshRefKey.None;
        var position = e.Get<WorldPosition>().Value;
        var scale = DecomposeUniformScaleAndRotation(e.Get<WorldTransform>().Value, entity.Id, out var rotation);
        return new DrawableTransform(entity.Id, assetIdentity, position, rotation, scale);
    }

    /// <summary>
    /// Writes a drawable's position/rotation/scale directly, bypassing simulation entirely — the render-cache
    /// client's write path (Net-1 D1/D7): a thin client never runs <c>StepPhysics</c>/<c>PropagateTransforms</c>,
    /// it only mirrors what the server told it. <see cref="AssertOwnerThreadStrict"/>, never the
    /// sanctioned-allowlist-honoring guard (Job-1 precedent) — this mutates shared component state a worker
    /// thread must never touch. Also marks the drawable's render slot dirty (P3-M6), so the existing GPU upload
    /// path picks the change up exactly as it would for a locally-simulated move.
    /// </summary>
    public void SetDrawableTransform(EntityRef entity, in DrawableTransform transform)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThreadStrict();

        var e = Deref(entity);
        if (!e.Has<WorldPosition>() || !e.Has<WorldTransform>())
        {
            throw new InvalidOperationException(
                $"EntityRef {entity.Id} is not a drawable (no WorldPosition/WorldTransform) — cannot write a " +
                "DrawableTransform to it.");
        }

        e.Get<WorldPosition>().Value = transform.Position;
        e.Get<WorldTransform>().Value =
            Matrix4x4.CreateScale(transform.Scale) * Matrix4x4.CreateFromQuaternion(transform.Rotation);

        if (e.Has<InstanceSlot>())
        {
            MarkDirty(e.Get<InstanceSlot>().Value);
        }
    }

    /// <summary>
    /// Drains every drawable whose <c>WorldPosition</c>/<c>WorldTransform</c> changed (via animation, physics,
    /// or hierarchy propagation) since the last drain, writing up to <c>results.Length</c> entries and clearing
    /// exactly what was written — a second drain with nothing new to report returns 0. Independent of P3-M6's
    /// render-facing dirty set (see the class remarks above): consuming this never starves rendering, and vice
    /// versa.
    /// </summary>
    public int DrainDirtyDrawables(Span<DrawableTransform> results)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThreadStrict();

        var written = 0;
        var consumed = 0;
        for (; consumed < _networkDirtyIds.Count && written < results.Length; consumed++)
        {
            var globalId = _networkDirtyIds[consumed];
            _networkDirtyIdSet.Remove(globalId);

            if (!_live.TryGetValue(globalId, out var entity) || _pendingDead.Contains(globalId))
            {
                continue; // despawned since being marked
            }

            if (!entity.Has<WorldPosition>() || !entity.Has<WorldTransform>())
            {
                continue; // not (or no longer) a drawable
            }

            var assetIdentity = entity.Has<AssetRef>() ? entity.Get<AssetRef>().Value : MeshRefKey.None;
            var position = entity.Get<WorldPosition>().Value;
            var scale = DecomposeUniformScaleAndRotation(entity.Get<WorldTransform>().Value, globalId, out var rotation);
            results[written++] = new DrawableTransform(globalId, assetIdentity, position, rotation, scale);
        }

        _networkDirtyIds.RemoveRange(0, consumed);
        return written;
    }

    private static readonly QueryDescription DrawableSnapshotDesc =
        new QueryDescription().WithAll<GlobalId, WorldPosition, WorldTransform>();

    /// <summary>
    /// Every live drawable's current replicated state, independent of the dirty set — the late-join seam: a
    /// dedicated server calls this once per newly-connected peer to introduce the world as it exists today,
    /// rather than waiting for each entity to move again. <see cref="DrainDirtyDrawables"/> alone cannot serve
    /// this (a peer-connect audit finding — engine-architect, Net-1): a drawable that is neither a physics body
    /// nor animated is never marked dirty at all, so a client relying only on the dirty stream would never learn
    /// it exists. Not a hot-path method (called once per connection, not once per tick) — a plain allocating
    /// <see cref="List{T}"/> is the right shape here, unlike <see cref="DrainDirtyDrawables"/>'s caller-owned span.
    /// </summary>
    public List<DrawableTransform> SnapshotAllDrawables()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThreadStrict();

        var results = new List<DrawableTransform>();
        foreach (ref var chunk in _world.Query(in DrawableSnapshotDesc))
        {
            var ids = chunk.GetSpan<GlobalId>();
            var positions = chunk.GetSpan<WorldPosition>();
            var transforms = chunk.GetSpan<WorldTransform>();
            var entities = chunk.Entities;
            var n = chunk.Count;
            for (var i = 0; i < n; i++)
            {
                var assetIdentity = entities[i].Has<AssetRef>() ? entities[i].Get<AssetRef>().Value : MeshRefKey.None;
                var scale = DecomposeUniformScaleAndRotation(transforms[i].Value, ids[i].Value, out var rotation);
                results.Add(new DrawableTransform(ids[i].Value, assetIdentity, positions[i].Value, rotation, scale));
            }
        }

        return results;
    }

    /// <summary>
    /// <see cref="DrawableTransform.Scale"/> is a single <c>float</c> (spec D9's fixed-size wire shape) — it can
    /// only ever represent a UNIFORM scale. <see cref="Matrix4x4.Decompose"/> silently returns <see langword="false"/>
    /// on a degenerate matrix (leaving its <c>out</c> params unspecified), and a non-uniform authored scale would
    /// otherwise be truncated to its X component with no signal at all — both are rejected here rather than
    /// replicated as a plausible-looking but wrong pose.
    /// </summary>
    private static float DecomposeUniformScaleAndRotation(Matrix4x4 transform, ulong globalId, out Quaternion rotation)
    {
        if (!Matrix4x4.Decompose(transform, out var scale, out rotation, out _))
        {
            throw new InvalidOperationException(
                $"Entity {globalId}'s WorldTransform is not decomposable (degenerate matrix) — cannot replicate it.");
        }

        const float tolerance = 1e-4f;
        if (MathF.Abs(scale.X - scale.Y) > tolerance * MathF.Max(1f, MathF.Abs(scale.X))
            || MathF.Abs(scale.X - scale.Z) > tolerance * MathF.Max(1f, MathF.Abs(scale.X)))
        {
            throw new InvalidOperationException(
                $"Entity {globalId} has a non-uniform scale ({scale.X}, {scale.Y}, {scale.Z}) — DrawableTransform " +
                "(Net-1 D9) can only replicate a uniform scale.");
        }

        return scale.X;
    }
}
