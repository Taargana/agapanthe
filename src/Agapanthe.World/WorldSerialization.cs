using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Agapanthe.Core;
using Arch.Core;
using Arch.Core.Extensions;

namespace Agapanthe.World;

/// <summary>Resolves a snapshot's stored drawable identity (Contenu-3a: <c>AssetRef</c>) back to live handles for
/// the <c>MeshRef</c> render cache. It <b>must throw</b> for a key/index it cannot resolve — the loader trusts the
/// returned pair. <c>null</c> ⇒ every <c>MeshRef</c> loads with <see cref="MeshHandle.Invalid"/> (a headless /
/// authoritative world that keeps only the <c>AssetRef</c>).</summary>
public delegate (MeshHandle Mesh, MaterialHandle Material) MeshRefResolver(AssetKey key, int localMesh, int localMat);

// VS-1 — World serialization (save/load snapshot). Lives in GameWorld — like Physics/Propagate/Collect — so it
// reaches the internal components and Arch entities without exposing the ECS: the public surface is two Stream
// methods (GPU-free, no Arch type leaks). See docs/plans/2026-07-24-vs1-world-serialization-design.md.
//
// Format (little-endian, blittable): a header, then every entity sorted by GlobalId. Each entity is a GlobalId, a
// presence bitmask over ComponentRegistry.All (bit i = component index i is present), then each present component's
// raw bytes IN INDEX ORDER — except InstanceSlot (runtime, re-derived at the next rebuild) which is never written,
// Parent, written as the parent's GlobalId (an Arch Entity is a memory handle, not persistable), and MeshRef
// (Contenu-1), written as a key-table index + local mesh/material indices, re-resolved at load. Determinism:
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
    //
    // v2 (MP-0b W3) inserts UniverseId(16) between componentCount and nextGlobalId:
    //   magic(4) "AGWD" | version(4) | componentCount(4) | universeId(16) | nextGlobalId(8) | entityCount(4)
    //   = 40-byte header. v1 had no universeId; both v1 and v2 are refused outright — no automatic upgrade.
    //
    // v3 (Contenu-1) keeps the 40-byte header and adds, immediately after it:
    //   keyTable: keyCount(4) >= 1, then keyCount × { byteLen(2) | UTF-8 bytes }
    //     entry 0 = AssetKey.None, written as byteLen 0; entries 1.. = distinct non-None keys, sorted ORDINAL.
    //   MeshRef (component index 5) is no longer a 16-byte blittable: it is keyIdx(4) | localMesh(4) | localMat(4).
    //
    // v4 (Contenu-3a) moves asset identity into the simulation. The drawable carries AssetRef (component index 12,
    //   a MeshRefKey) as its STORED identity; MeshRef becomes a process-local render cache derived at load. So:
    //   - AssetRef (index 12) is written as keyIdx(4) | localMesh(4) | localMat(4) — the shape v3 wrote at index 5.
    //   - MeshRef (index 5) is no longer written at all (derived from AssetRef + the resolver, like InstanceSlot).
    //   - Save takes no identifier delegate: the entity already holds its key (from ImportedEntitySpec.Identity).
    //   A v3 file is upgraded IN PLACE on load: its index-5 bytes are read as the AssetRef identity.
    private static ReadOnlySpan<byte> SerializationMagic => "AGWD"u8;
    private const uint SerializationVersion = 4;

    // A v3 file predates AssetRef — 12 components; a v4 file has 13. Both are frozen consts: appending component
    // #14 without a version bump must fail at the registry (the frozen-order test), not by rejecting every genuine
    // v4 file at load. The static check below ties V4ComponentCount to the live registry so they cannot drift.
    private const uint V3ComponentCount = 12;
    private const uint V4ComponentCount = 13;

    // ORDINAL — culture-independent — so the key table's byte layout is stable across machines (Save(Load(x)) == x).
    private static readonly IComparer<AssetKey> KeyOrdinal =
        Comparer<AssetKey>.Create(static (a, b) => string.CompareOrdinal(a.Value, b.Value));

    // Throws on malformed bytes instead of substituting U+FFFD — a corrupt key table entry must fail loudly.
    private static readonly Encoding Utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Components excluded from / specially handled by the format, resolved once from the registry order so a
    // (version-bumped) reorder carries them along instead of drifting against a magic number.
    private static readonly int ParentIndex = IndexOfComponent(typeof(Parent));
    private static readonly int InstanceSlotIndex = IndexOfComponent(typeof(InstanceSlot));
    // Contenu-3a: MeshRef (index 5) is no longer serialised (a derived render cache); AssetRef (index 12) is the
    // stored identity, written in MeshRef's former 3-u32 shape.
    private static readonly int MeshRefIndex = IndexOfComponent(typeof(MeshRef));
    private static readonly int AssetRefIndex = IndexOfComponent(typeof(AssetRef));

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
    /// every component, plus the universe identity and the id counter (MP-0b W3) — GPU-free, no Arch type crosses
    /// the boundary. Structural changes are flushed first so the snapshot is a settled world (no half-spawned
    /// entity, no pending command). Entities are
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

        // Pass 1 (Contenu-3a): read every drawable's stored AssetRef identity, collect the distinct non-None keys.
        // No handle→key lookup any more — the entity already carries its key, so a headless Save just works.
        var assetKeys = new MeshRefKey?[entities.Count];
        var distinctKeys = new SortedSet<AssetKey>(KeyOrdinal);
        for (var i = 0; i < entities.Count; i++)
        {
            if (!entities[i].Entity.Has<AssetRef>())
            {
                continue;
            }

            var k = entities[i].Entity.Get<AssetRef>().Value;
            assetKeys[i] = k;
            if (!k.IsNone)
            {
                distinctKeys.Add(k.Key);
            }
        }

        // Header.
        stream.Write(SerializationMagic);
        WriteU32(stream, SerializationVersion);
        WriteU32(stream, (uint)componentCount);
        WriteU64(stream, _universeId.High);
        WriteU64(stream, _universeId.Low);
        WriteU64(stream, _nextGlobalId);
        WriteU32(stream, (uint)entities.Count);

        // Key table: entry 0 is AssetKey.None (byteLen 0), entries 1.. the sorted distinct keys.
        WriteU32(stream, (uint)(distinctKeys.Count + 1));
        WriteU16(stream, 0); // None
        var keyToIndex = new Dictionary<AssetKey, uint>(distinctKeys.Count);
        uint nextIndex = 1;
        foreach (var key in distinctKeys)
        {
            var utf8 = Encoding.UTF8.GetBytes(key.Value!);
            if (utf8.Length > ushort.MaxValue)
            {
                // Contenu-3a: keys now come from entity state, not a host delegate — surface an over-long key as the
                // format's own exception, not a raw OverflowException.
                throw new WorldSerializationException($"Asset key '{key}' is {utf8.Length} UTF-8 bytes; the key table entry length is a u16.");
            }

            WriteU16(stream, (ushort)utf8.Length);
            stream.Write(utf8);
            keyToIndex[key] = nextIndex++;
        }

        // Body.
        for (var ei = 0; ei < entities.Count; ei++)
        {
            var (id, entity) = entities[ei];
            WriteU64(stream, id);

            // Presence mask over the registry order. Never serialized: InstanceSlot (runtime state) and MeshRef
            // (v4 — the render cache, derived from AssetRef on load). AssetRef (index 12) IS written, in MeshRef's
            // former shape.
            uint mask = 0;
            for (var index = 0; index < componentCount; index++)
            {
                if (index != InstanceSlotIndex && index != MeshRefIndex && EntityHasComponent(entity, index))
                {
                    mask |= 1u << index;
                }
            }

            WriteU32(stream, mask);

            for (var index = 0; index < componentCount; index++)
            {
                if ((mask & (1u << index)) == 0)
                {
                    continue;
                }

                if (index == AssetRefIndex)
                {
                    WriteAssetRef(stream, assetKeys[ei] ?? MeshRefKey.None, keyToIndex);
                }
                else
                {
                    WriteComponent(stream, entity, index);
                }
            }
        }
    }

    // Contenu-3a: the drawable's identity on disk — three u32 (key-table index, local mesh, local material). Same
    // shape v3 wrote at the MeshRef slot (index 5); v4 writes it at the AssetRef slot (index 12).
    private static void WriteAssetRef(Stream s, in MeshRefKey k, IReadOnlyDictionary<AssetKey, uint> keyToIndex)
    {
        // A None key writes (0, 0, 0) — canonical: the reader ignores the local indices for key 0, so writing
        // anything else would make two logically-identical worlds serialise to different bytes.
        if (k.IsNone)
        {
            WriteU32(s, 0u);
            WriteU32(s, 0u);
            WriteU32(s, 0u);
            return;
        }

        WriteU32(s, keyToIndex[k.Key]);
        WriteU32(s, (uint)k.LocalMesh);
        WriteU32(s, (uint)k.LocalMat);
    }

    // Reads the 3-u32 drawable identity and returns BOTH the stored AssetRef and its derived MeshRef render cache.
    // Called for the AssetRef slot on a v4 file, and for the MeshRef slot on a v3 file (in-place upgrade).
    private static (AssetRef Asset, MeshRef Mesh) ReadDrawableIdentity(Stream s, AssetKey[] keyTable, MeshRefResolver? resolve)
    {
        var keyIdx = ReadU32(s);
        var localMesh = ReadU32(s);
        var localMat = ReadU32(s);

        if (keyIdx >= (uint)keyTable.Length)
        {
            throw new WorldSerializationException($"Asset key index {keyIdx} is beyond the {keyTable.Length}-entry key table.");
        }

        var asset = new AssetRef
        {
            Value = keyIdx == 0 ? MeshRefKey.None : new MeshRefKey(keyTable[keyIdx], (int)localMesh, (int)localMat),
        };

        // MeshRef is the process-local render cache: Invalid when there is no resolver (a headless / authoritative
        // world keeps only the AssetRef), else re-resolved through the host.
        var mesh = keyIdx == 0 || resolve is null
            ? new MeshRef { Mesh = MeshHandle.Invalid, Material = MaterialHandle.Invalid }
            : ToMeshRef(resolve(keyTable[keyIdx], (int)localMesh, (int)localMat));

        return (asset, mesh);

        static MeshRef ToMeshRef((MeshHandle Mesh, MaterialHandle Material) h)
            => new() { Mesh = h.Mesh, Material = h.Material };
    }

    /// <summary>
    /// Restores a world snapshot written by <see cref="Save"/> into this <b>fresh</b> world (VS-1), reconciling
    /// <see cref="UniverseId"/> and adopting the header's id counter (MP-0b W3). Equivalent to
    /// <c>Load(stream, SnapshotAllocatorPolicy.AdoptFromHeader)</c>; see the overload for the full contract.
    /// </summary>
    public SnapshotLoadResult Load(Stream stream)
        => Load(stream, SnapshotAllocatorPolicy.AdoptFromHeader, resolve: null);

    /// <summary>
    /// Restores a world snapshot written by <see cref="Save"/> into this <b>fresh</b> world (VS-1). The world must be
    /// empty and settled; loading into a populated world throws (merge is out of scope). Parent links are rewired by
    /// GlobalId in a second pass (the format stores the parent's id, not an Arch Entity). A malformed stream (bad
    /// magic/unsupported version/count, truncation, out-of-range mask) throws <see cref="WorldSerializationException"/>
    /// rather than corrupting state or reading out of bounds. Returns a <see cref="SnapshotLoadResult"/> describing
    /// what happened — the caller decides whether that is worth a log line, this layer takes no logging dependency.
    /// <para><b>Universe reconciliation</b> (MP-0b W3), against this world's <see cref="UniverseId"/> as set at
    /// construction: both <see cref="UniverseId.None"/> → stays unidentified · snapshot set, world <c>None</c> →
    /// world <b>adopts</b> the snapshot's identity · snapshot <c>None</c>, world set → world <b>keeps</b> its
    /// identity (this is how an existing solo world gets named) · both set and equal → confirmed · both set and
    /// <b>different</b> → <see cref="WorldSerializationException"/> (the entity ids in this file mean something
    /// else). No mutation happens until every header field is validated — a rejected Load leaves this world exactly
    /// as it was before the call.</para>
    /// <para><b>Id allocator</b> (MP-0b W3, <paramref name="policy"/>): <see cref="SnapshotAllocatorPolicy.AdoptFromHeader"/>
    /// (VS-1's original behaviour) adopts the header's counter, but only if it falls within this world's declared
    /// <see cref="GlobalIdRange"/> — <c>Start &lt;= nextGlobalId &lt;= EndExclusive</c>, inclusive at the top (a
    /// world that consumed its whole block holds <c>EndExclusive</c>; issuing FROM there is what throws, not
    /// sitting at it). Outside that range throws: a world with a declared range loading a foreign allocator state
    /// is exactly the mistake <see cref="SnapshotAllocatorPolicy.KeepMine"/> exists for — the header's counter is
    /// ignored entirely and this world keeps allocating from its own construction-time range (decision 3): a node
    /// that accepts entities it did not create must not advance ITS OWN allocator by accepting them.</para>
    /// <para>Entities keep their serialized <see cref="GlobalId"/> regardless of whether it falls inside this
    /// world's range — deliberately unvalidated (decision 1): a node legitimately holds entities it did not
    /// allocate. A collision between a loaded id and one this world later issues itself throws
    /// <see cref="InvalidOperationException"/> at the point of the later spawn (see <c>RegisterLive</c>) rather than
    /// silently orphaning the earlier entity.</para>
    /// <para>Contenu-1 (<paramref name="resolve"/>): a v3 snapshot stores each drawable's <c>MeshRef</c> as a
    /// stable <see cref="AssetKey"/> + local mesh/material indices; <paramref name="resolve"/> turns those back
    /// into live handles (it holds the render-side registry). <c>null</c> ⇒ every <c>MeshRef</c> loads with
    /// <see cref="MeshHandle.Invalid"/> (a headless load, or a world with no renderer). A supplied delegate
    /// <b>must throw</b> for a key/index it cannot resolve — that surfaces as a failed Load.</para>
    /// <para>On a malformed stream the exception may be thrown after some entities were already created: this world is
    /// then partially populated and must be discarded (disposed), not reused — Load is all-or-nothing by contract, not
    /// by rollback.</para>
    /// </summary>
    public SnapshotLoadResult Load(Stream stream, SnapshotAllocatorPolicy policy)
        => Load(stream, policy, resolve: null);

    /// <inheritdoc cref="Load(Stream, SnapshotAllocatorPolicy)"/>
    public SnapshotLoadResult Load(Stream stream, SnapshotAllocatorPolicy policy, MeshRefResolver? resolve)
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
        if (version is not (3 or 4))
        {
            throw new WorldSerializationException(version switch
            {
                1 => "Snapshot is format v1, which predates universe identity (MP-0b W3). There is no automatic " +
                     "upgrade — resave it with this build first, from wherever it was last loadable.",
                2 => "Snapshot is format v2, which predates stable asset identity (Contenu-1): its MeshRefs are raw " +
                     "process-local handles with no key table. There is no automatic upgrade — resave it with this " +
                     "build first, from wherever it was last loadable.",
                _ => $"Unsupported snapshot version {version} (this build reads versions 3 and 4).",
            });
        }

        // v4 (Contenu-3a) appends AssetRef → componentCount 13; a v3 file predates it → 12. The v3 body is read
        // as-is except its MeshRef-slot bytes are materialised as the AssetRef identity (in-place upgrade).
        var isV3 = version == 3;
        Debug.Assert(V4ComponentCount == (uint)ComponentRegistry.All.Count,
            "V4ComponentCount is out of step with ComponentRegistry.All — bump SerializationVersion.");
        var expectedComponentCount = isV3 ? V3ComponentCount : V4ComponentCount;
        var componentCount = ReadU32(stream);
        if (componentCount != expectedComponentCount)
        {
            throw new WorldSerializationException(
                $"Snapshot (v{version}) has {componentCount} components, expected {expectedComponentCount} " +
                "(the component set changed without a version bump).");
        }

        // Read the rest of the header before mutating anything (audit MP-0b W3 finding): a rejected Load must not
        // leave this world half-renamed to the snapshot's universe. Both fields are read off the wire in their
        // fixed on-disk order regardless of validation outcome; only the ASSIGNMENTS below are deferred.
        var snapshotUniverse = new UniverseId(ReadU64(stream), ReadU64(stream));
        var headerNextGlobalId = ReadU64(stream);

        if (snapshotUniverse != UniverseId.None && _universeId != UniverseId.None && snapshotUniverse != _universeId)
        {
            throw new WorldSerializationException(
                $"Snapshot universe {snapshotUniverse} does not match this world's universe {_universeId} — " +
                "the entity ids in this file mean something else.");
        }

        if (policy == SnapshotAllocatorPolicy.AdoptFromHeader &&
            (headerNextGlobalId < _idRange.Start || headerNextGlobalId > _idRange.EndExclusive))
        {
            throw new WorldSerializationException(
                $"Snapshot's next-id counter {headerNextGlobalId} falls outside this world's declared range " +
                $"[{_idRange.Start}, {_idRange.EndExclusive}) — pass SnapshotAllocatorPolicy.KeepMine to receive " +
                "this snapshot's entities without adopting its allocator state.");
        }

        var entityCount = ReadU32(stream);

        // Key table (Contenu-1): keyCount >= 1, entry 0 = AssetKey.None (byteLen 0), entries 1.. distinct non-None
        // keys sorted ordinal. Read + validated BEFORE any state changes (below) so a malformed table leaves this
        // world exactly as the constructor left it — same guarantee as the header fields above.
        var keyCount = ReadU32(stream);
        if (keyCount < 1)
        {
            throw new WorldSerializationException($"Snapshot key table has {keyCount} entries; entry 0 (AssetKey.None) is mandatory.");
        }

        var keyTable = new AssetKey[keyCount];
        for (var k = 0u; k < keyCount; k++)
        {
            var byteLen = ReadU16(stream);
            if (k == 0)
            {
                if (byteLen != 0)
                {
                    throw new WorldSerializationException($"Snapshot key table entry 0 must be AssetKey.None (byteLen 0), got {byteLen}.");
                }

                keyTable[0] = AssetKey.None;
                continue;
            }

            var utf8 = new byte[byteLen];
            ReadExact(stream, utf8);
            try
            {
                // Strict decode: invalid UTF-8 throws here rather than silently substituting U+FFFD (which would
                // load a mangled key and break Save(Load(x)) == x). Caught below with the ctor's own rejections.
                keyTable[k] = new AssetKey(Utf8Strict.GetString(utf8));
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException or DecoderFallbackException)
            {
                throw new WorldSerializationException($"Snapshot key table entry {k} is not a valid AssetKey.", ex);
            }

            // Entries 1.. are written strictly ascending (ordinal) and distinct. Enforce it on read too, so a
            // hand-forged or reordered table is rejected as corruption instead of loading then re-saving to
            // different bytes (the symmetry of the "Duplicate GlobalId" guard below).
            if (k > 1 && string.CompareOrdinal(keyTable[k - 1].Value, keyTable[k].Value) >= 0)
            {
                throw new WorldSerializationException(
                    $"Snapshot key table is not strictly ascending at entry {k} ('{keyTable[k - 1]}' then '{keyTable[k]}').");
            }
        }

        // Every validation above has passed — only NOW does the call start changing state.
        UniverseOutcome outcome;
        if (_universeId == UniverseId.None)
        {
            outcome = snapshotUniverse == UniverseId.None ? UniverseOutcome.StayedUnidentified : UniverseOutcome.Adopted;
        }
        else
        {
            outcome = snapshotUniverse == UniverseId.None ? UniverseOutcome.Kept : UniverseOutcome.Confirmed;
        }

        if (_universeId == UniverseId.None && snapshotUniverse != UniverseId.None)
        {
            _universeId = snapshotUniverse; // adopt: this is how an existing solo world gets named
        }

        if (policy == SnapshotAllocatorPolicy.KeepMine)
        {
            // Receiving-node case (decision 3): the header's counter is ignored, this world keeps allocating from
            // wherever its own construction-time range already had it — Load requires a fresh, unspawned world, so
            // _idRange/_nextGlobalId are untouched here, exactly as the constructor left them.
        }
        else
        {
            _nextGlobalId = headerNextGlobalId;
        }

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

            // The drawable-identity slot: index 12 (AssetRef) on v4, index 5 (MeshRef's former slot) on a v3 file.
            var identitySlot = isV3 ? MeshRefIndex : AssetRefIndex;
            var hadIdentity = (mask & (1u << identitySlot)) != 0;

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
                else if (index == identitySlot)
                {
                    var (asset, mesh) = ReadDrawableIdentity(stream, keyTable, resolve);
                    entity.Add(asset);  // the stored identity (Contenu-3a)
                    entity.Add(mesh);   // the derived render cache (Invalid without a resolver)
                }
                else if (index != InstanceSlotIndex) // InstanceSlot is never in the stream, but never dispatch it either
                {
                    ReadAndAddComponent(stream, entity, index);
                }
            }

            // InstanceSlot is runtime state (excluded from the stream), re-added at the sentinel so the next rebuild
            // reassigns it. Invariant (audit 🟡): InstanceSlot only ever coexists with a drawable identity (every
            // drawable/body carries AssetRef + MeshRef + InstanceSlot, no node carries any), so keying the re-add on
            // the identity slot reconstructs the exact original archetype.
            if (hadIdentity)
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

        return new SnapshotLoadResult(outcome, (int)entityCount);
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
        // Contenu-3a: each drawable spec carries a non-None Identity so the v4 key-table path is exercised under the
        // ILC — UTF-8 encode, the SortedSet<AssetKey> comparer, the AssetKey ctor. Two distinct keys so the comparer
        // actually orders something.
        SpawnImported(new ImportedEntitySpec(
            new MeshHandle(1, 2), new MaterialHandle(3, 4), new Double3(10, 20, 30), Matrix4x4.Identity,
            new Vector3(1, 2, 3), 1.5f, 1, new MeshRefKey(new AssetKey("aot/probe-b"), 1, 0)));
        SpawnImported(
            new ImportedEntitySpec(new MeshHandle(2, 2), new MaterialHandle(3, 4), new Double3(-40, 0, 0),
                Matrix4x4.Identity, Vector3.Zero, 1f, 2, new MeshRefKey(new AssetKey("aot/probe-a"), 2, 0)),
            castsShadow: false);
        SpawnBody(
            new ImportedEntitySpec(new MeshHandle(3, 2), new MaterialHandle(3, 4), new Double3(5, 5, 5),
                Matrix4x4.Identity, Vector3.Zero, 1f, 3, new MeshRefKey(new AssetKey("aot/probe-b"), 3, 0)),
            new Vector3(0.5f, 0f, -0.5f), inverseMass: 1f, restitution: 0.2f, radius: 1f);
        var root = Spawn(new Double3(100, 0, 0), Quaternion.Identity, 2f);
        Spawn(new Double3(0, 10, 0), Quaternion.Identity, 1f, root);
        FlushStructuralChanges();

        // The resolve delegate invoke + strict UTF-8 decode are only reached with a resolver on Load.
        static (MeshHandle, MaterialHandle) Resolve(AssetKey key, int localMesh, int localMat)
            => (new MeshHandle(localMesh, 2), new MaterialHandle(3, 4));

        byte[] first;
        using (var ms = new MemoryStream())
        {
            Save(ms);
            first = ms.ToArray();
        }

        using var restored = new GameWorld();
        using (var ms = new MemoryStream(first))
        {
            restored.Load(ms, SnapshotAllocatorPolicy.AdoptFromHeader, Resolve);
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
    // Three switches over the SAME 13 concrete types, in registry-index order. Concrete instantiations root Has<T>/
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
        12 => e.Has<AssetRef>(),
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
            // index 5 (MeshRef) is a derived render cache in v4 — never serialized (excluded from the mask).
            case 6: WriteBlittable(s, e.Get<Bounds>()); break;
            case 7: WriteBlittable(s, e.Get<RenderOrder>()); break;
            case 8: WriteBlittable(s, e.Get<Velocity>()); break;
            case 9: WriteBlittable(s, e.Get<RigidBody>()); break;
            case 10: WriteBlittable(s, e.Get<NoShadowCast>()); break;
            // index 11 (InstanceSlot) is never written (excluded by the caller).
            // index 12 (AssetRef) is written by WriteAssetRef (key table index + local indices), never here.
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
            // index 5 (MeshRef): v4 never has it in the stream; a v3 file's index-5 bytes are read by the caller
            // via ReadDrawableIdentity (the in-place upgrade). Never dispatched here.
            case 6: e.Add(ReadBlittable<Bounds>(s)); break;
            case 7: e.Add(ReadBlittable<RenderOrder>(s)); break;
            case 8: e.Add(ReadBlittable<Velocity>(s)); break;
            case 9: e.Add(ReadBlittable<RigidBody>(s)); break;
            case 10: e.Add(ReadBlittable<NoShadowCast>(s)); break;
            // index 11 (InstanceSlot) is never in the stream (excluded by the caller).
            // index 12 (AssetRef) is handled by the caller (ReadDrawableIdentity — AssetRef + derived MeshRef), never here.
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

    private static void WriteU16(Stream s, ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        s.Write(buffer);
    }

    private static ushort ReadU16(Stream s)
    {
        Span<byte> buffer = stackalloc byte[2];
        ReadExact(s, buffer);
        return BinaryPrimitives.ReadUInt16LittleEndian(buffer);
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
