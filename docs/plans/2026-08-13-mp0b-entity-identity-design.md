# MP-0b — Entity identity — design

> Second sub-milestone of **MP-0 (authority foundations)**, itself the next step of the **engine cap**
> ([backlog §4quater](../BACKLOG.md)). Anchor decisions (S25): the artifact is **the engine** · generalist but
> especially large-scale space sims · **server-authoritative** multiplayer designed in from now · massive/persistent
> targeted with small coop possible, so **topology is a deployment choice, never an architecture choice**.
>
> Status: **APPROVED — 4.4/5** (independent scored review, 2 rounds; threshold 4.0). Round 1 returned
> APPROVED WITH FINDINGS at 4.0/5 with 12 findings, all applied; round 2 scored 4.4/5 and left 3 open, all now
> closed in this v3 — a surviving copy of the round-1 miscitation in §Risks, a hash algorithm asserted from memory
> (**wrong**: it is MD5, recovered by measurement — see the DoD), and two corroborating tests specified in terms the
> code could not express (resolved with a spawn-order permutation and a key-level permutation test, adding **no**
> production surface). **Ready for W1.** Follows MP-0a
> ([2026-08-13-mp0a-headless-split-design.md](2026-08-13-mp0a-headless-split-design.md)), whose v1 scored 2.85/5 for
> one reason: it cited **doc comments instead of executable code**. Every factual claim below cites an executable
> line, and where the brainstorm's own wording turned out to be imprecise (the contact key is a **sort** key, not a
> pair-identity key) this document corrects it rather than repeating it — see §"What actually breaks".
>
> **What round 1 changed**, because the pattern is worth recording: the arithmetic and the line citations all held,
> but **three gates were weaker than they read**. (a) The AOT-coverage citation pointed at a two-body step, which
> produces one pair — and `Array.Sort` returns before touching `ArraySortHelper<,>` when `length <= 1`, so the cited
> lines could not provide the coverage claimed. (b) W1's capture-hash gate assumed the capture scene sorts more than
> one pair without ever checking. (c) The W2 headline test, landing after W1, could not fail. Same family as MP-0a
> v1: a premise that is plausible, load-bearing, and unverified. Three counts inherited from the board were also
> remeasured and were wrong.

## Summary

This milestone closes the two 🔴 identity items of §4quater. Both are about the same thing — an entity's name — and
both are **correct today and wrong the moment we partition**:

1. **`GlobalId` is a per-world counter.** `GameWorld.cs:79` is `private ulong _nextGlobalId = 1;`, incremented at
   seven call sites (`GameWorld.cs:191, 245, 263, 970, 977` and `GameWorld.Physics.cs:57, 74`). Two processes both
   hand out 1, 2, 3…; VS-1 writes that counter into the snapshot header (`WorldSerialization.cs:97`) and restores it
   verbatim (`:174`). Two saved worlds are therefore **unmergeable, and nothing in the files says so**.
2. **The contact-pair sort key truncates the id to 32 bits.** `GameWorld.Physics.cs:333` is
   `_pairKey[pairCount] = (_pGid[j] << 32) | (uint)_pGid[k];`, with the assumption written down at `:330-332`
   (*"Assumes GlobalId < 2^32"*).

Neither is broken at `HEAD`, and that is why they are being done together: the bug and its fix arrive in the same
change. Nothing degrades by having waited.

**Blast radius, remeasured** (the board's figures were carried over from session 26 and are wrong). `GlobalId` is
`internal` to `Agapanthe.World` (`Components.cs:22`) — **72 references over 12 files**: 6 files with executable uses,
all inside `Agapanthe.World` (`ComponentRegistry.cs`, `Components.cs`, `EntityRef.cs`, `GameWorld.cs`,
`GameWorld.Physics.cs`, `WorldSerialization.cs`), one **doc-comment mention** in `Agapanthe.Core/RenderItem.cs:66`,
and 5 test files. **No executable use outside the project.** `EntityRef` wraps a plain `ulong` (`EntityRef.cs:25`) and already hashes all
64 bits (`:36`), so it does not move. The **only** place in the repository that narrows an id to 32 bits is the
contact key: the render sort key's low 32 bits are `RenderOrder`, not `GlobalId` (`GameWorld.cs:788-789`, composed by
`RenderItem.ComposeSortKey` — `RenderItem.cs:76-83`), and its documented ceiling is on **mesh/material** indices
(`RenderItem.cs:70-74`), a separate backlog item.

## What actually breaks — precisely

The brainstorm recorded the contact-key defect as *"two entities silently become the same collision pair"*. That is
not what the code does, and the real failure mode is both narrower and worse-behaved.

`_pairKey` is **only ever written at `:333` and read by `System.Array.Sort(_pairKey, _pairPacked, 0, pairCount)` at
`:347`**. The pair's actual contents travel in `_pairPacked` (`:334`, unpacked at `:352-353`). So the key decides
**resolution order and nothing else**. Two consequences, both real:

- **The left shift overflows.** For `min = 2³²+1`, `min << 32` drops the bit at index 32 and yields `1 << 32`. Past
  2³² the key stops being a monotone function of `(min, max)` altogether.
- **Distinct pairs collide.** `key(1, 2³²+2)` and `key(2³²+1, 2³²+2)` are both `0x0000_0001_0000_0002`.

That directly contradicts the invariant the code claims for itself at `:269-270`: *"Pairs are collected once … then
resolved in GlobalId order so the outcome never depends on Arch's iteration order."* The consequence to lead with is
**block-dependence**: resolution order — and therefore the positions and velocities of any pile of three or more
touching bodies — becomes a function of *where the id block starts*, so two nodes running the same scene from
different blocks compute different states. On a server-authoritative topology that is exactly the divergence that
cannot be tolerated, and it is what the id-offset test below measures.

*(Secondary, and deliberately not the headline: `Array.Sort` is an unstable introsort, so two colliding pairs are
ordered arbitrarily. That does **not** break determinism within one binary — CLAUDE.md's determinism scope is
byte-identity **intra-binaire** only, the collection order at `:297-340` is itself deterministic, and introsort is a
deterministic function of its input. Overstating this as "implementation-defined non-determinism" would misdescribe
the project's own contract.)*

Stating it this way matters for the tests: the gate is **ordering equivalence**, not pair membership.

## Context — what the codebase already provides

- **The snapshot header is 24 bytes** — `magic(4)` + `version(4)` + `componentCount(4)` + `nextGlobalId(8)` +
  `entityCount(4)`, written at `WorldSerialization.cs:94-98` and read at `:152-175`. Three refusals already exist and
  are the pattern to extend: bad magic (`:154-157`), unsupported version (`:159-164`), component-count mismatch
  (`:166-172`), plus duplicate-id rejection at load (`:224-227`).
- **Byte offsets in the header are pinned by tests, by hand.** `WorldSerializationTests.cs:135` pokes `bytes[4]`
  (version), `:143` pokes `bytes[8]` (component count), and `:161` pokes `bytes[35]` after re-deriving the layout in
  a comment at `:159-160`. A header change moves all three, and the comment is part of the format's documentation —
  it must be recomputed, not left stale.
- **`Load` already creates entities with their serialized ids**, never with freshly allocated ones
  (`WorldSerialization.cs:193`, `_world.Create(new GlobalId { Value = globalId })`). So the **bulk, fresh-world**
  ingest already assigns foreign ids correctly; what is missing is a way to say so about the allocator. Note the
  limit precisely: **incremental** ingest into a running world does not exist — `Load` throws unless the world is
  empty and settled (`:146-149`) — so the only reachable use of an allocator override this milestone is `Load` into
  a fresh world. Decision 3 is about not *foreclosing* the receiving-node case, not about enabling it now.
- **The physics hot path already has a 0-alloc gate that exercises contacts**:
  `PhysicsTests.StepPhysics_IsAllocationFree_AfterWarmup` (`PhysicsTests.cs:204-227`), a 64-body cluster with a
  120-step warm-up (`:214-217`) followed by a 240-step measured window. The pair buffers are explicitly in scope
  (comment `:202-203`). W1's allocation claim lands in an existing test rather than a new one.
- **The pair de-dup rule already normalises the pair**: `if (_pGid[j] >= _pGid[k]) continue;` (`:313-316`) means the
  pair is `(min = _pGid[j], max = _pGid[k])` **by construction**. The key only has to impose a total order on that
  ordered couple — which two parallel `ulong` fields do exactly.
- **`Array.Sort(keys, items, index, length)` is already the sorting call** (`:347`), and `_pairKey`/`_pairPacked` are
  grown together by `EnsurePairCapacity` (`:432-442`). The change is the element type, not the algorithm.
- **The AOT probe reaches the sort — but not where one would first look.** `GameWorld.AotRootingSmoke` spawns two
  overlapping bodies and steps (`GameWorld.cs:570-575`); that is **one pair**, and `Array.Sort` returns before
  touching `ArraySortHelper<TKey, TValue>` when `length <= 1`, so those lines root the pair *collection* path and
  nothing of the sort. The coverage comes from `:583-588`: a third body is spawned at the same position
  (`bodySpec`, centre `(600, 0, 0)`, `radius: 1`) and a second `StepPhysics` runs, giving **three mutually
  overlapping bodies → three pairs → `length > 1`**. `tools/AotComponentProbe/Program.cs:24` runs it. So
  `ArraySortHelper<ContactPairKey, long>` *is* covered by the existing NativeAOT gate at no cost — **provided that
  third body stays**. Anyone trimming `AotRootingSmoke` to "the two bodies it needs" silently removes the rooting;
  the comment added there must say so.
- **`GameWorld` has one parameterless constructor** (`GameWorld.cs:173`) and `new GameWorld(` appears **75 times over
  13 files**, including `samples/HeadlessSim/Program.cs:84`, `samples/Sandbox/Program.cs:174` and
  `WorldSerialization.cs:276`. Keeping it is what lets this milestone prove it changed nothing.

## Locked decisions (interview, session 26)

1. **Identity only — no authority, no ownership.** Current authority requires a transfer protocol to mean anything,
   and that is netcode. **The id names the entity's BIRTH, never its current authority**: an entity born on A and
   handed to B keeps an id that says A. Conflating the two would be irreparable, because it lives in the save format.
2. **The id stays an opaque `u64`; no bit split is frozen into the format.** Motivated by the **dynamic-meshing**
   target: nodes there are ephemeral and numerous over time, so spending id bits on node identity is a trap — a
   16-bit prefix exhausts in **~4.5 days** at 100 nodes recycled every 10 minutes. High bits become a **lease block**,
   and who allocates them is an execution policy behind a seam. **Solo = block 0, counting from 1 = bit-for-bit what
   the engine does today.**
3. **The allocator state stays in the header but becomes overridable at `Load`.** Today `_nextGlobalId` conflates two
   questions: *what may I issue?* and *what do I hold?* A node that receives entities it did not create must not
   advance its allocator by accepting them. Keeping the field preserves the byte-identical round-trip; the override
   opens the door.
4. **The header gains a universe identity; format v1 → v2; v1 refused.** This — not id partitioning — is what
   actually closes 🔴 (1): two solo universes both counting from 1 collide, and **nothing in the files reveals it**.
   Universe identity is a fact **per file**, not per entity. Version refusal already exists
   (`WorldSerialization.cs:159-164`). `challenge.save` referenced by the Rider profile
   (`Sandbox/Properties/launchSettings.json:46`) is regenerated; nothing is shipped.

### Decided during planning — to be validated at review

- **The default universe identity is EMPTY, not random.** A `Guid` drawn at world creation would make two runs of the
  same scene produce different bytes, breaking VS-1's cross-process byte-identity **and** `HeadlessSim`'s
  JIT-vs-AOT snapshot equality (`7c889fec`). "Unidentified universe" is an honest state for a solo world, and merging
  two anonymous universes is ambiguous anyway — so requiring that they be named is the right constraint.
- **Allocation is a RANGE, not an interface.** `[start, end)` supplied by the host, defaulting to `[1, ulong.MaxValue)`.
  A future coordinator handing out blocks *is* exactly a range — a lease without inventing a lease protocol.
  **Exhaustion fails loudly** (the project's standing preference over silent degradation).
- **The contact key becomes a 128-bit comparable struct**, sorted by the same `Array.Sort<TKey,TValue>` overload.
  **0-alloc is to be PROVEN, not assumed** — see the risk on `ArraySortHelper` below.

## Architecture

### 1. The contact key (W1)

A new internal `readonly struct ContactPairKey : IComparable<ContactPairKey>` in `Agapanthe.World`, two `ulong`
fields (`Min`, `Max`), comparing lexicographically. `_pairKey` (`GameWorld.Physics.cs:38`) changes from `ulong[]` to
`ContactPairKey[]`; `:333` becomes `new ContactPairKey(_pGid[j], _pGid[k])` (already ordered by the de-dup rule at
`:313-316`); `:347` and `EnsurePairCapacity` (`:432-442`) are unchanged in shape. Memory per pair goes 8 B → 16 B,
on a buffer that is a few hundred entries at the observed scale.

**Why the captures cannot move.** While every id is < 2³², today's key `(min << 32) | max` orders by `min` first and
`max` second — which *is* lexicographic order on `(min, max)`. The new key produces the **same permutation** on all
existing scenes, so `12638edd` and `03421357` must come back unchanged.

**And why that equality is worth anything.** An unchanged hash proves nothing if the capture scene never sorts more
than one pair — under `length <= 1` the sort is a no-op and the hash would match against any implementation,
including a broken one. It does sort: `ProbeDropSystem.DropOne` nudges successive probes onto a golden-angle spiral
*"so successive probes land in a small pile instead of a perfect stack"* (`samples/Sandbox/Program.cs:2118-2132`),
and the capture protocol drops 35 of them (`AGAPANTHE_MAX_FRAMES=420`, `AGAPANTHE_DROP_EVERY=12`). A pile is
multi-pair. **Because a hash cannot distinguish "same permutation" from "sort never ran", the capture gate does not
stand alone**: W1 also carries a unit test that resolves a 3-body cluster and asserts the resolution order
explicitly, so the two gates corroborate rather than one of them being trusted.

**Comparison must be the constrained generic path.** `Array.Sort<TKey,TValue>` with `TKey : IComparable<TKey>` calls
`CompareTo` through a constrained call — no boxing. Passing a `Comparison<T>` or an `IComparer<T>` instance instead
would allocate: P2-M2 already paid for that lesson, where `Span.Sort(structComparer)` boxed the comparer for ~88 B
per call and had to be replaced by a hand-written radix sort.

**Documented fallback** if the probe shows an allocation or a regression: LSD radix over two parallel `ulong[]`,
16 byte-passes (all 8 bytes of `Max` first, then all 8 of `Min`, so `Min` dominates), patterned on
`RenderList.SortByKey` (`RenderList.cs:105-154`). That code is **not reusable as-is** — it is coupled to
`RenderList`'s own `_keys`/`_indexA`/`_indexB`/`_scratchItems` fields and sorts an index permutation.

### 2. The allocation range (W2)

```csharp
public readonly record struct GlobalIdRange(ulong Start, ulong EndExclusive)
{
    public static GlobalIdRange Default => new(1, ulong.MaxValue);
}
```

Public, in `Agapanthe.World`: a host must be able to name it. `Start == 0` is rejected — id 0 is the
`default(EntityRef)` "no entity" sentinel (`EntityRef.cs:29-30`) — as is `Start >= EndExclusive`
(`ArgumentOutOfRangeException`).

`GameWorld` gains `GameWorld(GlobalIdRange range, UniverseId universe = default)`; **the parameterless constructor
stays** and delegates to `(GlobalIdRange.Default, UniverseId.None)`. The 75 existing call sites do not move, which
is the evidence that the default path is unchanged.

The seven `_nextGlobalId++` sites collapse into one `private ulong NextId()` that throws `InvalidOperationException`
naming the exhausted range. Consolidating them is a prerequisite for anything later (a lease renewal has exactly one
place to hook), and it is the only way exhaustion can be made loud everywhere at once.

**What is deliberately NOT validated**: entities materialised by `Load` keep their serialized ids
(`WorldSerialization.cs:193`) even when those fall outside this world's range. That is decision 1 in force — you hold
entities you did not create — and it is the subtle rule an implementer is most likely to "fix" by mistake. It gets a
test of its own.

### 3. Snapshot v2 (W3)

```
magic(4) "AGWD" | version(4)=2 | componentCount(4) | universeId(16) | nextGlobalId(8) | entityCount(4)   = 40 bytes
```

`UniverseId` is a `readonly struct` of **two little-endian `ulong`s**, not a `Guid`: `Guid.ToByteArray()` is
mixed-endian, and this format's determinism rests on explicit little-endian primitives
(`WorldSerialization.cs:379-405`). `UniverseId.None` is all-zero. It exposes hex `ToString`/`Parse` so a host can put
one in a config file.

`Load(Stream)` gains `Load(Stream stream, GlobalIdRange? allocatorOverride = null)`:

- **No override** (today's behaviour): the header's `nextGlobalId` is adopted, as at `:174`. If it falls outside this
  world's range, throw — a world with a declared range loading a foreign allocator state is exactly the mistake the
  override exists for. **The predicate is `Start <= nextGlobalId <= EndExclusive`, inclusive at the top**: a world
  that has consumed its whole block holds `_nextGlobalId == EndExclusive` and `Save` writes that value (`:97`), so a
  half-open test would make a saturated world unable to reload **its own snapshot**. Sitting *at* the exclusive end
  is the legal representation of "exhausted"; issuing from there is what throws.
- **With an override**: the header's value is ignored and the range takes over. This is the receiving-node case.

Universe reconciliation at load, five cases, all pinned by tests:

| snapshot | world | outcome |
|---|---|---|
| None | None | load; world stays unidentified (today's solo behaviour) |
| set | None | load; the world **adopts** the snapshot's identity (the ordinary "load a named world" case) |
| set | set, **same** | load; identity confirmed — **the case every server node hits on every load**, and the one a wrong comparison (wrong `ulong` half, an `!=` that never matches) would pass through unnoticed if it were left implied by the row below |
| None | set | load; the world **keeps** its identity (this is how an existing solo world gets named) |
| set | set, different | **throw** — the entity ids in this file mean something else |

Version 1 is refused by the existing check (`:159-164`) with a message saying that v1 predates universe identity and
that there is no automatic upgrade.

**Consequences that must be carried, not discovered**: `WorldSerializationTests.cs:135` and `:143` keep their
offsets (version and component count do not move), `:161` moves from `bytes[35]` to `bytes[51]` (header 40 + the
first entity's `globalId(8)` → the presence mask is the `u32` at offset 48), and the layout comment at `:159-160`
is recomputed.

`HeadlessSim`'s snapshot hash **changes** with the header, and re-pinning it needs a procedure, because `7c889fec`
exists **only in prose** (`CLAUDE.md`, `AVANCEMENT.md`, the session-26 board and archive) — no test, no script
asserts it, and the hash depends on arguments the parser reads at `HeadlessSim/Program.cs:28-72`. The DoD below
therefore states the exact invocation, the same way it states the capture protocol. W3 also **adds the missing
test**: a `HeadlessSim`-shaped scene built in-process, saved, and compared against a committed expected hash — a
milestone that changes the snapshot format should not leave its own format gate living in a paragraph.

### 4. The stale comments — three of them, not one

MP-0a's own experience is the argument for treating this as work rather than tidying: a comment claiming
`_sceneBounds` had a consumer kept a dead per-frame `O(n)` system alive for eleven sessions.

- `Components.cs:16-18` states that downstream packing depends on `GlobalId` staying a dense per-run counter below
  2³². **False after W1/W2.** Rewritten to what is then true: the id is opaque, allocated from a range, and no
  consumer may narrow it.
- `EntityRef.cs:19-21` states *"Process-local: the `ulong` is a per-run monotonic counter, not a persisted key.
  Cross-process identity (serialization/streaming) is a separate future concern."* **This milestone IS that
  concern**, and the comment sits on a **public** type — worse than the internal one. Rewritten in the same wave as
  the header change.
- `RenderItem.cs:66` offers `GlobalId` as a candidate tie-break value. The tie-break is a `uint`
  (`RenderItem.cs:76`) and the real code uses `RenderOrder` (`GameWorld.cs:788-789`); with sparse ids that
  suggestion becomes actively dangerous. One clause, corrected while `RenderItem.cs:68-74` is open anyway.

## Testing strategy

| Test | Wave | Kind | What breaks without it |
|---|---|---|---|
| Key collision — `key(1, 2³²+2)` vs `key(2³²+1, 2³²+2)` must differ | **W1** | GPU-free unit | the truncation returns unnoticed; **this test fails at `HEAD`** |
| Key order equals today's packing for all dense-id pairs | **W1** | GPU-free unit | the permutation changes and the captures move for an unexplained reason |
| The HEAD packing and `ContactPairKey` induce **different permutations** over three sparse-id pairs | **W1** | GPU-free unit | the capture hash is trusted alone, and it cannot tell "same permutation" from "sort never ran"; this is where the defect actually lives and it is testable at `HEAD` |
| `StepPhysics_IsAllocationFree_AfterWarmup` still 0 B | **W1** | existing alloc gate | the sort helper allocates per call and the 0-alloc gate is lost |
| **Id-offset invariance**: identical scene, ids from 1 vs ids from 2³²−1 → identical positions after N steps | **W2** | GPU-free unit | contact resolution order depends on where the id block starts — silent divergence between nodes |
| **Order-sensitivity**: same three bodies spawned in a different order → positions **differ** | **W2** | GPU-free unit | the test above passes by construction and measures nothing, because the chosen configuration turns out to be interchangeable |
| Two worlds with disjoint ranges issue disjoint ids | **W2** | GPU-free unit | the whole point of the range |
| Range exhaustion throws; `Start = 0` and `Start >= End` rejected | **W2** | GPU-free unit | quiet wraparound onto the "no entity" sentinel |
| Default range round-trips bit-for-bit (snapshot unchanged) | **W2** | byte comparison | the default path was not actually preserved |
| v2 round-trip byte-identical; v1 refused with a typed exception | **W3** | GPU-free unit | the format regresses |
| The **five** universe-reconciliation cases (including set/set-same) | **W3** | GPU-free unit | the adoption policy drifts into whatever the code happens to do; a broken comparison passes every case except the one nobody wrote |
| `Load` keeps out-of-range serialized ids without throwing | **W3** | GPU-free unit | a future "fix" makes receiving foreign entities impossible |
| Allocator override ignores the header value | **W3** | GPU-free unit | decision 3 is documentation only |
| `AotComponentProbe` | W4 | AOT publish + run | `ArraySortHelper<ContactPairKey, long>` is missing native code under ILC |
| Sandbox headless captures + `HeadlessSim` JIT==AOT | W4 | run-level | a "pure generalisation" changed a pixel or a byte |

**The id-offset invariance test deserves its construction spelled out**, because it is the one that turns an
abstract claim into a measurement — and because, written naively, it cannot fail.

Build three mutually-overlapping bodies in two worlds — one with `GlobalIdRange.Default`, one with `[2³²−1, …)` —
step both, compare positions. With ids `2³²−1, 2³², 2³²+1`, today's key gives pair `(2³², 2³²+1)` the key
`0x0000_0000_0000_0001` while `(2³²−1, 2³²)` gets `0xFFFF_FFFF_0000_0000`: the pair that must resolve **last**
resolves **first**. Because a uniform offset preserves the relative order of ids, a correct key makes the two runs
identical — so the assertion is exact equality, not a tolerance.

**It lands in W2, after W1 has already fixed the key, so it passes by construction and can never go red** — and its
premise (that this particular three-body configuration is order-sensitive at all) is exactly the kind of assumption
this document exists to stop asserting. `ResolvePair` (`GameWorld.Physics.cs:358-409`) is order-sensitive only when
the bodies are not interchangeable: three bodies with equal `inverseMass`, equal `restitution` and a symmetric layout
can yield the same final state under any permutation, which would make the test a tautology. So the configuration is
specified as **deliberately asymmetric** — distinct masses, distinct restitutions, asymmetric layout — and that
asymmetry is **proven, not declared**, by a companion test.

**The companion test needs no seam and no golden file.** Resolution order is not observable from outside `GameWorld`
(`_pairKey`, `_pairPacked`, the sort at `:347` and `ResolvePair` are all private; the only readbacks are
`GetWorldPosition` and `internal GetVelocity` at `:450`), and injecting the legacy key would mean adding production
surface for a test. Neither is necessary, because ids are assigned in **spawn order** (`GameWorld.Physics.cs:57`):

> **Order-sensitivity test (W2)** — build the same three bodies, same geometry and same physical parameters, but
> **spawn them in a different order**. That permutes their ids, hence the `(min, max)` order, hence the sequence in
> which `ResolvePair` applies impulses — while the scene itself is unchanged. Assert the final positions **differ**.
> If they do not, the configuration is interchangeable and the invariance test below would be vacuous.

Together the two are exact: the order-sensitivity test proves the scene *can* distinguish orders, and the invariance
test proves that shifting the whole id block *does not* change the order. Both exercise the real
`ResolveBodyContacts` through the public spawn API. Both assert exact equality/inequality of positions, not a
tolerance — a uniform offset preserves the relative order of ids, so a correct key makes the two runs identical.

The third leg is at the key level, where order *is* directly observable and no world is needed: **assert that the
HEAD packing expression and `ContactPairKey` induce different permutations over the three sparse-id pairs**
`(2³²−1, 2³²)`, `(2³²−1, 2³²+1)`, `(2³², 2³²+1)`. That one is implementable at `HEAD` today, is where the defect
actually lives, and belongs in W1 with the other key tests.

## Waves

**W1 — the contact key, alone.** Ids are still dense, so **every capture hash must be unchanged**: that is the proof
the new key induces the same order. Add the two key tests (one of which fails at `HEAD` when written against today's
packing expression) and confirm the existing 0-alloc gate.

**W2 — the allocation range.** `GlobalIdRange`, the single `NextId()`, loud exhaustion, the parameterless
constructor preserved. The id-offset invariance test becomes possible here and is the wave's headline.

**W3 — snapshot v2.** `UniverseId` in the header, allocator override at `Load`, v1 refused, byte offsets and the
layout comment updated, `challenge.save` regenerated, `HeadlessSim`'s hash re-pinned once.

**W4 — tail.** Self-review of the diff, double audit (`csharp-lowlevel` + `engine-architect`; no GPU pass is touched,
so `graphics-3d` is not warranted), full verification, human verdict, CONVERGE.

W1 and W2 are separate on purpose: W1 must be provable by *unchanged* hashes, and it cannot be if the ids move in the
same commit.

## Verification (DoD)

- `dotnet build` **0 warning** · `dotnet test` green including every test above.
- **Capture hashes unchanged**: HDR `12638edd`, UI overlay-hidden `03421357`. Capture protocol (do not drop the last
  variable — the default of `AGAPANTHE_DROP_EVERY` is 30 and omitting it changes both hashes):
  `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12`, 1280×720, Debug.
- `HeadlessSim` still JIT == AOT byte-identical, at its **new** pinned hash after W3. **The algorithm was never
  recorded** when `7c889fec` was pinned (it appears only in prose, in `CLAUDE.md`, `AVANCEMENT.md` and the
  session-26 board/archive), so it was recovered by measurement while writing this spec rather than guessed: it is
  **MD5**, and the full value is `7c889fec0df503fe8137ef6c28c7751a` — `7c889fec` is its displayed prefix.
  Reproduced at `HEAD` on 2026-08-16:

  ```
  dotnet run --project samples/HeadlessSim -c Debug -- --ticks 600 --bodies 8 --save <path>
  Get-FileHash -Path <path> -Algorithm MD5      # → 7c889fec0df503fe8137ef6c28c7751a, 1852 bytes
  ```

  `--ticks 600 --bodies 8` are the defaults (`HeadlessSim/Program.cs:19-20`), stated explicitly so the invocation
  does not depend on them. Run it once from `dotnet run` and once from the NativeAOT publish, require the two to be
  equal, then record the **full 32-character** value in `CLAUDE.md` and `AVANCEMENT.md` — carrying only the prefix is
  what made this recovery necessary.
- AOT publish reminder (MP-0a): prefix `PATH` with the folder containing **`vswhere.exe`**
  (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`), not the MSVC folder, or ILC fails with code 123.
- Sandbox headless: **0 validation message**, **0 leak**.
- `AotComponentProbe` PASS.
- Double audit PASS + human verdict, then CONVERGE (`AVANCEMENT` / `BACKLOG` / `CLAUDE`, board archived).
  **Commit on explicit request only.**

## Risks

- 🟠 **`Array.Sort<TKey,TValue>` on a custom comparable struct.** The runtime resolves an
  `ArraySortHelper<ContactPairKey, long>` lazily, once per closed generic. Expected to be absorbed by the existing
  120-step warm-up (`PhysicsTests.cs:214-217`), and expected to be rooted under ILC because the instantiation is
  statically visible and the probe reaches the sort with **three** pairs (`GameWorld.cs:583-588` — *not* the two-body
  step at `:570-575`, which sorts one pair and never touches the helper; see §Context). **Both are expectations, and
  the measurement is the arbiter** — the radix fallback above exists for the case where it is not.
- 🟠 **Header growth invalidates hand-computed byte offsets** in `WorldSerializationTests.cs`. Declared, enumerated
  above, and the tests are the mechanism that catches getting it wrong.
- 🟠 **The universe reconciliation table is policy, and a wrong default is not visible at runtime** — a world that
  silently adopts the wrong identity behaves normally until a merge. Hence all five cases are pinned.
- 🟠 **Naming an anonymous world is an unauthenticated, irreversible write.** The `None | set` row lets a named world
  absorb an anonymous snapshot with nothing verifying that those ids belong to it; re-saved, the file now asserts an
  identity that was never checked. This is the one hole left in decision 4's guarantee that a collision is visible in
  the files, and the policy is still right — it is the only migration path for existing solo saves. Mitigation: the
  adoption is **logged** at `Load`, so the naming event is recorded at least once rather than being silent.
- 🟡 **`ContactPairKey` doubles the pair-key buffer** (8 → 16 B/pair) on a buffer of a few hundred entries.
- 🟡 **The range does not prevent two hosts from choosing overlapping ranges.** Nothing here can: that is the
  coordinator's job, and it is out of scope. The universe identity is what makes the mistake *detectable*.

## Out of scope

Merging two universes (this milestone makes it **detectable and possible**, not implemented) · **incremental ingest
into a running world** (`Load` requires an empty, settled world — `WorldSerialization.cs:146-149` — so the allocator
override's only reachable use here is bulk load into a fresh world) · lease protocol /
coordinator · any notion of authority or ownership · entity graph · replication · partial/incremental persistence ·
an asset fingerprint in the header (VS-1's separate debt) · `FrameIndex` → `TickIndex` (MP-0c) · input → commands
(MP-0d). The remaining two MP-0 sub-milestones keep their backlog entries and their order.
