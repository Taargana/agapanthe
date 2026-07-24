using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Agapanthe.Core;
using Arch.Core;
using Arch.Core.Extensions;

namespace Agapanthe.World;

// VS-1 — World serialization (save/load snapshot). Lives in GameWorld — like Physics/Propagate/Collect — so it
// reaches the internal components and Arch entities without exposing the ECS: the public surface is two Stream
// methods (GPU-free, no Arch type leaks). See docs/plans/2026-07-24-vs1-world-serialization-design.md.
//
// Format (little-endian, blittable): a header, then every entity sorted by GlobalId. Each entity is a GlobalId, a
// presence bitmask over ComponentRegistry.All (bit i = component index i is present), then each present component's
// raw bytes IN INDEX ORDER — except InstanceSlot (runtime, re-derived at the next rebuild) which is never written,
// and Parent, written as the parent's GlobalId (an Arch Entity is a memory handle, not persistable). Determinism:
// the GlobalId total order + the fixed component-index order make two Saves of one world byte-identical, and the
// round-trip byte-identical (Save(Load(bytes)) == bytes), which is the format's regression gate.
//
// Blittable notes (audit 🟡): components are StructLayout(Sequential) and written at Unsafe.SizeOf<T>() width, TAIL
// PADDING INCLUDED (e.g. LocalTransform is 44 useful bytes but sizeof 48). Byte-identity therefore relies on that
// padding being deterministically zero — which holds because Arch backs each component in a zero-initialised T[] and
// Get<T> copies the struct at full width. It would break under an assembly-wide [SkipLocalsInit] or a component with
// non-zeroed padding. Endianness: the header uses explicit little-endian primitives and the component bytes are host
// order; the header does NOT detect a byte-order mismatch (it forces LE) — safety comes from the invariant that every
// target (Windows/Linux/macOS on x64/arm64) is little-endian, not from the header.
public sealed partial class GameWorld
{
    // "AGWD" (Agapanthe World Data) as little-endian bytes, followed by a format version. A version bump is REQUIRED
    // if ComponentRegistry.All is ever reordered (the mask is positional): see the append-only invariant below.
    private static ReadOnlySpan<byte> SerializationMagic => "AGWD"u8;
    private const uint SerializationVersion = 1;

    // Components excluded from / specially handled by the format, resolved once from the registry order so a
    // (version-bumped) reorder carries them along instead of drifting against a magic number.
    private static readonly int ParentIndex = IndexOfComponent(typeof(Parent));
    private static readonly int InstanceSlotIndex = IndexOfComponent(typeof(InstanceSlot));
    private static readonly int MeshRefIndex = IndexOfComponent(typeof(MeshRef));

    private static int IndexOfComponent(Type t)
    {
        var all = ComponentRegistry.All;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i] == t)
            {
                return i;
            }
        }

        throw new InvalidOperationException($"{t.Name} is not registered in ComponentRegistry.All.");
    }

    // Every entity carries GlobalId (universal since P3-M2), so this matches the whole world.
    private static readonly QueryDescription AllEntitiesDesc = new QueryDescription().WithAll<GlobalId>();

    /// <summary>
    /// Writes the whole world to <paramref name="stream"/> as a deterministic binary snapshot (VS-1): every entity,
    /// every component, plus the identity counter — GPU-free, no Arch type crosses the boundary. Structural changes
    /// are flushed first so the snapshot is a settled world (no half-spawned entity, no pending command). Entities are
    /// written sorted by <see cref="GlobalId"/> and components in registry-index order, so the bytes are stable:
    /// two saves of the same world are identical, and <c>Save(Load(bytes)) == bytes</c>.
    /// </summary>
    public void Save(Stream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(stream);

        // Settle the world: no pending spawn/despawn/reparent, so what we write is exactly what is live.
        FlushStructuralChanges();

        // Gather (GlobalId, Entity) for every entity and sort by GlobalId → a total order, hence a canonical byte
        // layout. Allocating here is fine: Save is a punctual operation, never on the per-frame hot path.
        var entities = new List<(ulong Id, Entity Entity)>(_live.Count);
        foreach (ref var chunk in _world.Query(in AllEntitiesDesc))
        {
            var ids = chunk.GetSpan<GlobalId>();
            var ents = chunk.Entities;
            var count = chunk.Count;
            for (var i = 0; i < count; i++)
            {
                entities.Add((ids[i].Value, ents[i]));
            }
        }

        entities.Sort(static (a, b) => a.Id.CompareTo(b.Id));

        var componentCount = ComponentRegistry.All.Count;

        // Header.
        stream.Write(SerializationMagic);
        WriteU32(stream, SerializationVersion);
        WriteU32(stream, (uint)componentCount);
        WriteU64(stream, _nextGlobalId);
        WriteU32(stream, (uint)entities.Count);

        // Body.
        foreach (var (id, entity) in entities)
        {
            WriteU64(stream, id);

            // Presence mask over the registry order. InstanceSlot is never serialized (runtime state).
            uint mask = 0;
            for (var index = 0; index < componentCount; index++)
            {
                if (index != InstanceSlotIndex && EntityHasComponent(entity, index))
                {
                    mask |= 1u << index;
                }
            }

            WriteU32(stream, mask);

            for (var index = 0; index < componentCount; index++)
            {
                if ((mask & (1u << index)) != 0)
                {
                    WriteComponent(stream, entity, index);
                }
            }
        }
    }

    /// <summary>
    /// Restores a world snapshot written by <see cref="Save"/> into this <b>fresh</b> world (VS-1). The world must be
    /// empty and settled; loading into a populated world throws (merge is out of scope). The identity counter is
    /// restored from the header and NEVER bumped by the entities created here — each is created with its serialized
    /// <see cref="GlobalId"/>. Parent links are rewired by GlobalId in a second pass (the format stores the parent's
    /// id, not an Arch Entity). A malformed stream (bad magic/version/count, truncation, out-of-range mask) throws
    /// <see cref="WorldSerializationException"/> rather than corrupting state or reading out of bounds.
    /// <para>The caller's contract (Option 1 seam): the same GPU assets must be (re)loaded in the same order BEFORE
    /// Load, so the serialized <see cref="MeshHandle"/>/<see cref="MaterialHandle"/> values still resolve.</para>
    /// <para>On a malformed stream the exception may be thrown after some entities were already created: this world is
    /// then partially populated and must be discarded (disposed), not reused — Load is all-or-nothing by contract, not
    /// by rollback.</para>
    /// </summary>
    public void Load(Stream stream)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();
        ArgumentNullException.ThrowIfNull(stream);

        if (_live.Count != 0 || _commands.Count != 0 || _pendingSpawn.Count != 0 || _pendingDead.Count != 0)
        {
            throw new WorldSerializationException("Load requires a fresh (empty, settled) world; merge is not supported.");
        }

        // Header.
        Span<byte> magic = stackalloc byte[4];
        ReadExact(stream, magic);
        if (!magic.SequenceEqual(SerializationMagic))
        {
            throw new WorldSerializationException("Not an Agapanthe world snapshot (bad magic).");
        }

        var version = ReadU32(stream);
        if (version != SerializationVersion)
        {
            throw new WorldSerializationException(
                $"Unsupported snapshot version {version} (this build reads version {SerializationVersion}).");
        }

        var componentCount = ReadU32(stream);
        if (componentCount != (uint)ComponentRegistry.All.Count)
        {
            throw new WorldSerializationException(
                $"Snapshot has {componentCount} components, this build has {ComponentRegistry.All.Count} " +
                "(the component set changed without a version bump).");
        }

        _nextGlobalId = ReadU64(stream);
        var entityCount = ReadU32(stream);

        // Pass 2 work list: (childGlobalId, parentGlobalId), wired after every entity exists.
        var parentLinks = new List<(ulong Child, ulong Parent)>();

        // Pass 1 — create every entity with its components (except Parent + InstanceSlot) and register it.
        for (var e = 0u; e < entityCount; e++)
        {
            var globalId = ReadU64(stream);
            var mask = ReadU32(stream);

            // A bit at or beyond the component count cannot name a component: reject rather than dispatch out of range.
            if (componentCount < 32 && (mask >> (int)componentCount) != 0)
            {
                throw new WorldSerializationException(
                    $"Entity {globalId} presence mask 0x{mask:X8} sets a bit beyond the {componentCount} components.");
            }

            var entity = _world.Create(new GlobalId { Value = globalId });

            for (var index = 0; index < componentCount; index++)
            {
                if ((mask & (1u << index)) == 0)
                {
                    continue;
                }

                if (index == ParentIndex)
                {
                    parentLinks.Add((globalId, ReadU64(stream))); // stored as the parent's GlobalId
                }
                else if (index != InstanceSlotIndex) // InstanceSlot is never in the stream, but never dispatch it either
                {
                    ReadAndAddComponent(stream, entity, index);
                }
            }

            // InstanceSlot is runtime state (excluded from the stream), re-added at the sentinel so the next rebuild
            // reassigns it. Invariant (audit 🟡): InstanceSlot only ever coexists with MeshRef (every drawable/body
            // carries both, no node carries either), so keying the re-add on MeshRef reconstructs the exact original
            // archetype. A future entity carrying one without the other would round-trip to a different archetype —
            // revisit this coupling if InstanceSlot's usage ever widens beyond drawables.
            if ((mask & (1u << MeshRefIndex)) != 0)
            {
                entity.Add(new InstanceSlot { Value = -1 });
            }

            // TryAdd, not indexer: a forged snapshot with two entities sharing a GlobalId would otherwise silently
            // overwrite _live (one entity orphaned, re-save non-deterministic). Reject it as corruption (audit 🟡).
            if (!_live.TryAdd(globalId, entity))
            {
                throw new WorldSerializationException($"Duplicate GlobalId {globalId} in snapshot.");
            }
        }

        // Pass 2 — wire parent links by GlobalId (reuses the deferred-flush pattern; a missing parent is dropped).
        foreach (var (child, parent) in parentLinks)
        {
            LinkParent(child, parent);
        }

        // A load is a wholesale structural change: the first CollectRenderLists must rebuild the persistent slots.
        _structuralDirty = true;
    }

    /// <summary>
    /// Round-trips a world containing every archetype through <see cref="Save"/>/<see cref="Load"/> so the
    /// <see cref="AotComponentProbe"/> (and a JIT unit test) proves the serialization paths survive NativeAOT — in
    /// particular the per-component <c>Add&lt;T&gt;</c> dispatch and the <see cref="MemoryMarshal"/> blittable
    /// read/write, which are generic-over-struct shapes the ILC only compiles if it sees them instantiated. Returns
    /// the restored entity count. Throws if the round-trip is not byte-identical. Internal: validation only.
    /// </summary>
    internal int AotSerializationSmoke()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        AssertOwnerThread();

        // Every archetype, so every component type's Add<T> is exercised at load: a plain drawable, a non-caster
        // (NoShadowCast), a physics body (Velocity + RigidBody), and a parented hierarchy (LocalTransform + Parent).
        SpawnImported(new ImportedEntitySpec(
            new MeshHandle(1, 2), new MaterialHandle(3, 4), new Double3(10, 20, 30), Matrix4x4.Identity,
            new Vector3(1, 2, 3), 1.5f, 1));
        SpawnImported(
            new ImportedEntitySpec(new MeshHandle(2, 2), new MaterialHandle(3, 4), new Double3(-40, 0, 0),
                Matrix4x4.Identity, Vector3.Zero, 1f, 2),
            castsShadow: false);
        SpawnBody(
            new ImportedEntitySpec(new MeshHandle(3, 2), new MaterialHandle(3, 4), new Double3(5, 5, 5),
                Matrix4x4.Identity, Vector3.Zero, 1f, 3),
            new Vector3(0.5f, 0f, -0.5f), inverseMass: 1f, restitution: 0.2f, radius: 1f);
        var root = Spawn(new Double3(100, 0, 0), Quaternion.Identity, 2f);
        Spawn(new Double3(0, 10, 0), Quaternion.Identity, 1f, root);
        FlushStructuralChanges();

        byte[] first;
        using (var ms = new MemoryStream())
        {
            Save(ms);
            first = ms.ToArray();
        }

        using var restored = new GameWorld();
        using (var ms = new MemoryStream(first))
        {
            restored.Load(ms);
        }

        byte[] second;
        using (var ms = new MemoryStream())
        {
            restored.Save(ms);
            second = ms.ToArray();
        }

        if (!first.AsSpan().SequenceEqual(second))
        {
            throw new InvalidOperationException(
                $"AOT serialization smoke: round-trip not byte-identical ({first.Length} vs {second.Length} bytes).");
        }

        return restored.LiveEntityCount;
    }

    // --- Per-component dispatch (single source of truth = ComponentRegistry.All order) ----------------------------
    // Three switches over the SAME 12 concrete types, in registry-index order. Concrete instantiations root Has<T>/
    // Get<T>/Add<T> under the ILC (P2-M0). A test guards that this order matches ComponentRegistry.All (append-only,
    // else bump SerializationVersion): reordering the registry without updating the format would silently
    // reinterpret old snapshots.

    private static bool EntityHasComponent(Entity e, int index) => index switch
    {
        0 => e.Has<GlobalId>(),
        1 => e.Has<LocalTransform>(),
        2 => e.Has<Parent>(),
        3 => e.Has<WorldTransform>(),
        4 => e.Has<WorldPosition>(),
        5 => e.Has<MeshRef>(),
        6 => e.Has<Bounds>(),
        7 => e.Has<RenderOrder>(),
        8 => e.Has<Velocity>(),
        9 => e.Has<RigidBody>(),
        10 => e.Has<NoShadowCast>(),
        11 => e.Has<InstanceSlot>(),
        _ => throw new WorldSerializationException($"No component at registry index {index}."),
    };

    private static void WriteComponent(Stream s, Entity e, int index)
    {
        switch (index)
        {
            case 0: WriteBlittable(s, e.Get<GlobalId>()); break;
            case 1: WriteBlittable(s, e.Get<LocalTransform>()); break;
            case 2: WriteU64(s, e.Get<Parent>().Value.Get<GlobalId>().Value); break; // parent as GlobalId, not Entity
            case 3: WriteBlittable(s, e.Get<WorldTransform>()); break;
            case 4: WriteBlittable(s, e.Get<WorldPosition>()); break;
            case 5: WriteBlittable(s, e.Get<MeshRef>()); break;
            case 6: WriteBlittable(s, e.Get<Bounds>()); break;
            case 7: WriteBlittable(s, e.Get<RenderOrder>()); break;
            case 8: WriteBlittable(s, e.Get<Velocity>()); break;
            case 9: WriteBlittable(s, e.Get<RigidBody>()); break;
            case 10: WriteBlittable(s, e.Get<NoShadowCast>()); break;
            // index 11 (InstanceSlot) is never written (excluded by the caller).
            default: throw new WorldSerializationException($"No serializer for registry index {index}.");
        }
    }

    private static void ReadAndAddComponent(Stream s, Entity e, int index)
    {
        switch (index)
        {
            case 0: e.Set(ReadBlittable<GlobalId>(s)); break; // GlobalId already present from Create → Set, not Add
            case 1: e.Add(ReadBlittable<LocalTransform>(s)); break;
            // index 2 (Parent) is handled by the caller (recorded for pass 2), never here.
            case 3: e.Add(ReadBlittable<WorldTransform>(s)); break;
            case 4: e.Add(ReadBlittable<WorldPosition>(s)); break;
            case 5: e.Add(ReadBlittable<MeshRef>(s)); break;
            case 6: e.Add(ReadBlittable<Bounds>(s)); break;
            case 7: e.Add(ReadBlittable<RenderOrder>(s)); break;
            case 8: e.Add(ReadBlittable<Velocity>(s)); break;
            case 9: e.Add(ReadBlittable<RigidBody>(s)); break;
            case 10: e.Add(ReadBlittable<NoShadowCast>(s)); break;
            // index 11 (InstanceSlot) is never in the stream (excluded by the caller).
            default: throw new WorldSerializationException($"No deserializer for registry index {index}.");
        }
    }

    // --- Blittable + primitive read/write (little-endian) --------------------------------------------------------

    private static void WriteBlittable<T>(Stream s, T value)
        where T : unmanaged
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
        MemoryMarshal.Write(buffer, in value);
        s.Write(buffer);
    }

    private static T ReadBlittable<T>(Stream s)
        where T : unmanaged
    {
        Span<byte> buffer = stackalloc byte[Unsafe.SizeOf<T>()];
        ReadExact(s, buffer);
        return MemoryMarshal.Read<T>(buffer);
    }

    private static void WriteU32(Stream s, uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static void WriteU64(Stream s, ulong value)
    {
        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static uint ReadU32(Stream s)
    {
        Span<byte> buffer = stackalloc byte[4];
        ReadExact(s, buffer);
        return BinaryPrimitives.ReadUInt32LittleEndian(buffer);
    }

    private static ulong ReadU64(Stream s)
    {
        Span<byte> buffer = stackalloc byte[8];
        ReadExact(s, buffer);
        return BinaryPrimitives.ReadUInt64LittleEndian(buffer);
    }

    // Fills the whole buffer or throws: a truncated snapshot (fewer bytes than the header promised) becomes a typed
    // WorldSerializationException instead of a raw EndOfStreamException or a silent short read.
    private static void ReadExact(Stream s, Span<byte> buffer)
    {
        try
        {
            s.ReadExactly(buffer);
        }
        catch (EndOfStreamException ex)
        {
            throw new WorldSerializationException("Snapshot is truncated (stream ended mid-record).", ex);
        }
    }
}
