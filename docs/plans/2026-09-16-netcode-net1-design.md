# Netcode réel, sub-milestone 1: minimal client/server loop (Net-1)

## Summary

Backlog §4quater's largest remaining item ("Netcode réel: transport, réplication delta,
prediction/reconciliation") — comparable in scope to all of MP-0 or the full job system,
decomposed into human-gated sub-milestones for the same reason. **This is sub-milestone 1**:
prove the "thin client / server-sole-simulator" authority model end-to-end over a real network
transport, with delta/dirty-tracking from day one (not a snapshot-per-tick placeholder later
replaced).

The project's own prior decisions already anchor this: "multijoueur pensé dès maintenant, serveur
autoritaire (pas lockstep)"; "la topologie est un choix de déploiement, jamais d'architecture";
`SimCommand`/`SimCommandQueue`/`InputSnapshot` (MP-0d) were explicitly built as "the network seam"
but have never carried a byte across a process boundary; `WorldSerialization`'s snapshot format is
"already ~80% a network snapshot" per the backlog, missing exactly the two things this
sub-milestone adds: per-component dirty-tracking and delta-against-baseline.

Two real constraints already on record must not be violated: "pas lockstep — la byte-identité
d'Agapanthe est intra-binaire seulement" (VS-2) and "le float bit-exact cross-machine à l'échelle
`double` planétaire est un piège." Neither is at risk here — the server is authoritative and the
sole simulator; the client never re-derives simulation state, only renders what it's told.

## Decisions

- **D1 — Authority model**: thin client, server-sole-simulator. Only the server ever runs
  `GameWorld.StepPhysics`/`SimulationHost.Tick`. The client never simulates.
- **D2 — Delta from day one**: no snapshot-per-tick placeholder. A real per-component dirty-
  tracking mechanism, in the spirit of `GameWorld`'s own existing CPU-side dirty tracking
  (`MarkDirty`/`_slotDirty`, set at its three established mutation surfaces — animation, physics
  writeback, hierarchy propagation, per its own doc comment — and consumed by
  `PersistentInstanceBuffer`/`SceneCandidateSet.EnqueueDirty` on the GPU side today), applied
  instead to the network path. Not a literal reuse of the GPU-facing consumer — a new,
  analogous dirty set consumed by the network layer instead.
- **D3 — Transport**: Reliable UDP via **LiteNetLib** (NuGet, MIT), not a hand-rolled reliable-UDP
  layer — building sequence numbers/ack windows/retransmission/wraparound correctly is a
  substantial sub-project orthogonal to the engine's own domain. **Verified empirically this
  session**: a scratch NativeAOT publish (`PublishAot=true`, `net10.0`, win-x64) referencing
  LiteNetLib 2.1.4 produced zero trim/AOT analyzer warnings, and a minimal server/client
  connect+accept handshake (`NetManager`, `EventBasedNetListener`, `ConnectionRequestEvent`,
  `PeerConnectedEvent`) behaved identically under JIT and NativeAOT.
- **D4 — Sub-milestone scope**: the full vertical slice, not a read-only spectator step. The
  client sends a `SimCommand` (reusing MP-0d's existing shape unmodified), the server applies it
  via `SimulationHost.ApplyCommand`, the resulting state change is reflected in the next delta the
  client receives.
- **D5 — Replication perimeter**: `WorldPosition`/`WorldTransform` of drawables only, for this
  sub-milestone. Generalizing to arbitrary components is later work — deliberately deferred so
  this sub-milestone proves the transport+delta mechanism without also designing a generic
  per-component wire format in the same pass.
- **D9 (added post-D7 review, before EXECUTE)** — **entity introduction carries asset identity.**
  D7 keeps the client populated *purely* from network data (no cooked `.agscene`, confirmed
  against D5's mesh-identity gap found before EXECUTE): a client cannot render an entity it has
  never been told the mesh for, since D5 scopes ongoing updates to position/transform only. Two
  wire packet shapes, not one: **`EntityIntroduce`** (`GlobalId` + the entity's `AssetKey`,
  Contenu-3a's existing sim-side identity mechanism, + its initial transform) sent once per entity
  the FIRST time the server decides a given client needs to know about it (tracked server-side via
  a per-client "known" set — trivial with D8's single client); **`PositionUpdate`** (`GlobalId` +
  transform only, the hot per-tick path) for every subsequent change to an already-introduced
  entity. The client resolves a received `AssetKey` to GPU handles via the existing
  `AssetCatalog`/`ResourceRegistry`/`MeshRefResolver` path (Contenu-1/Contenu-2) — `ThinClient`
  therefore does need the cooked content available locally (an `AssetCatalog`), just not a
  `.agscene` scene definition. `AssetKey` is a string, not blittable — `EntityIntroduce` is a
  variable-length packet (LiteNetLib's `NetDataWriter.Put(string)` handles this natively, per this
  session's own scratch verification), unlike `PositionUpdate`'s fixed-size blittable shape.
- **D6 — Server host**: a new `samples/DedicatedServer`, not an extension of `samples/HeadlessSim`.
  Reuses `HeadlessSim`'s composition-root pattern (`SimulationHost` + `GameWorld`, zero GPU) but
  loops on network connections/ticks instead of a fixed tick count. `HeadlessSim` itself is
  untouched — it stays the bounded local benchmark/test tool it already is.
- **D7 — Client host**: a new `samples/ThinClient`. Keeps a local `GameWorld` purely as a
  **render cache**: incoming deltas write `WorldPosition`/`WorldTransform` directly; the client
  never calls `SimulationHost.Tick()`. This reuses the entire existing render pipeline
  (`CollectRenderLists`, instancing, `AppHost`'s GPU bootstrap/teardown) without duplicating any of
  it for a "dumb" rendering path.
- **D8 — Single client only**: this sub-milestone handles exactly one connected client. The server
  assigns the one pilotable entity to that client on connect, hardcoded for a single peer.
  Multi-client (peer identity, `SimCommand.Target` ownership routing — both explicitly deferred
  since MP-0d) is a later sub-milestone.

## Architecture

### `GameWorld` access surface (a real gap found during spec review)

`WorldPosition`/`WorldTransform` are `internal struct`s (`Components.cs`), and `GameWorld`'s own
dirty-read accessors (e.g. `WorldTransformForTest`) are `internal`. A new `Agapanthe.Net`-style
assembly cannot read or write these as D2/D5 describe without one of: (a) new **public**
`GameWorld` API scoped to what the network layer needs (read a drawable's current
position/transform by `GlobalId`; read-and-clear the dirty set; write a position/transform
directly for the client's render-cache path), or (b) `internal` API + a new
`[assembly: InternalsVisibleTo("Agapanthe.Net")]` entry (mirrors the existing pattern:
`GameWorld.cs`/`SimCommandQueue.cs` already grant this to `Agapanthe.Tests`/`AotComponentProbe`/
`Agapanthe.Engine`). **Decision for DECOMPOSE**: default to (a), public API — matching the
project's posture that `GameWorld`'s public surface is deliberately the sanctioned way to touch
the ECS (its own doc: "the ONLY sanctioned way to touch the ECS"), and a dirty-read/write path for
network replication is a legitimate, durable capability, not an internal wiring detail like Job-1's
`SetSanctionedWorkerThreads`.

### New project: `Agapanthe.Net` (or similar — exact name TBD at DECOMPOSE, likely sits alongside
`Agapanthe.Engine` in the headless closure: `{Core, World, Engine}`, no GPU/rendering types)

- **Wire types**: binary, blittable where possible, mirroring the project's existing convention
  (`SimCommand`, `InputSnapshot`, `WorldSerialization`'s header/record shapes) — not JSON/Protobuf.
  A `ReplicatedTransform` record (entity `GlobalId` + `WorldPosition`/`WorldTransform` fields) is
  the per-entity delta payload unit.
- **Server-side dirty-tracking**: a per-entity dirty flag/generation counter, set whenever
  `WorldPosition`/`WorldTransform` changes for a drawable (the same mutation surfaces P3-M6's GPU
  dirty-tracking already watches: physics step, transform propagation). Once per tick, after the
  structural barrier, the server gathers all dirty drawables into a delta packet, sends it over
  LiteNetLib to the single connected peer, then clears the dirty set.
- **Server-side command intake**: LiteNetLib's receive callback deserializes an incoming
  `SimCommand`-shaped payload and calls `SimCommandQueue.Enqueue` on the server's
  `SimulationHost.Commands` — the exact seam MP-0d built for this, finally fed from outside the
  process. Must marshal onto the owner thread first (per `SimCommandQueue`'s existing contract —
  LiteNetLib's `PollEvents()` model, called from the server's own tick loop on the owner thread,
  satisfies this without extra synchronization).
- **Client-side apply**: on receiving a delta packet, for each `ReplicatedTransform`, look up the
  entity by `GlobalId` in the client's local `GameWorld` (spawning it via the existing `Spawn`/
  `SpawnDeferred` path if not yet known — first-seen bookkeeping) and write
  `WorldPosition`/`WorldTransform` directly, bypassing `StepPhysics`/`PropagateTransforms` (the
  client never runs simulation stages).
- **Client-side input**: same `InputMap`/`SampleInput` shape as any existing `AppHost`-based
  recipe, but instead of enqueuing onto a local `SimulationHost.Commands` that a local tick will
  drain, the resulting `SimCommand` is serialized and sent to the server over LiteNetLib.

### `samples/DedicatedServer`

- Composition root mirrors `HeadlessSim`: `GameWorld` + `SimulationHost.CreateDefault`, zero GPU.
- Loop: `NetManager.PollEvents()` (drains incoming connections/`SimCommand`s) → `SimulationHost.Tick`
  (fixed step) → gather+send dirty deltas → repeat, indefinitely (not a fixed tick count like
  `HeadlessSim`).
- On the single client's connection, assigns/spawns its pilotable entity and replies with its
  `GlobalId` so the client knows which entity is "theirs" for input purposes.

### `samples/ThinClient`

- Uses `AppHost.RunClient` for the GPU bootstrap/frame loop/teardown exactly like Sandbox/TopDown,
  but its `IGame`/`ISceneRecipe` never touches `SimulationHost.Tick` for `Stage.Simulation`
  purposes — the "scene" is populated entirely by incoming network deltas, not by an
  `.agscene`/cook-time definition.
- Samples input the same way any existing recipe does (`InputMap`/`SampleInput`), but routes the
  resulting `SimCommand` to the network layer instead of a local `SimCommandQueue`.
- **Structural barrier, without `SimulationHost.Tick`** (a real gap found during spec review): a
  first-seen entity (per D7's "spawn it if not yet known" bookkeeping) still goes through
  `GameWorld.Spawn`/`SpawnDeferred`, which queues onto the deferred command list and requires
  `GameWorld.FlushStructuralChanges()` to actually materialize — normally driven by
  `SimulationHost.Tick`'s end-of-stage barrier, which `ThinClient` never calls. `ThinClient` must
  call `FlushStructuralChanges()` itself, once per received delta packet (or once per render
  frame, whichever is simpler at DECOMPOSE), after applying any new-entity spawns from that
  packet and before the frame renders.

## Testing strategy

- GameWorld-level dirty-tracking unit tests: moving a drawable's `WorldPosition` marks it dirty;
  gathering deltas clears the dirty set; an untouched drawable is never included in a delta.
- Wire-format round-trip tests: a `SimCommand` and a `ReplicatedTransform` serialize/deserialize
  byte-identical, GPU-free, no LiteNetLib dependency (pure format tests, mirroring
  `HeadlessSimSnapshotFormatTests`'s style).
- End-to-end integration test (or a manual live protocol, TBD at DECOMPOSE): start
  `DedicatedServer` and `ThinClient` as two real processes over localhost, send input, observe the
  reflected position change arrive client-side within N ticks.
- **Regression gate**: this sub-milestone adds new projects/samples; it must not touch
  `Agapanthe.Engine`'s headless closure test (`EngineIsHeadlessTests`) in a way that breaks it —
  `Agapanthe.Net` (or wherever the new wire types live) must be added to the static allowlist
  deliberately, not accidentally exempted. Existing Sandbox/TopDown/HeadlessSim captures and
  snapshots must remain byte-identical (nothing in this sub-milestone touches their code paths).
- AOT publish + JIT == NativeAOT re-confirmed on the two new hosts (`DedicatedServer`,
  `ThinClient`), following the exact precedent already used for every prior milestone.

## Out of scope (deferred to later sub-milestones, not silently dropped)

- Multi-client support, peer identity (`OriginatorId`), `SimCommand.Target` ownership routing.
- Replication of arbitrary/all components — this sub-milestone hardcodes
  `WorldPosition`/`WorldTransform` of drawables only.
- Client-side prediction, server reconciliation, rollback — explicitly not this authority model's
  concern (thin client never predicts).
- Interest management / relevance filtering (sending only what's near a client) — everything
  dirty is sent to the single client unconditionally.
- Lag compensation, anti-cheat, congestion/rate control beyond what LiteNetLib provides by default.
- Persistent/massive-scale server topology (cluster, server meshing) — this sub-milestone is a
  single server process, single client, proving the mechanism only.
- `RunDedicatedServer` as a first-class `AppHost` method — this sub-milestone's `DedicatedServer`
  is its own composition root, not yet folded into `Agapanthe.App`'s public contract (that
  integration, if warranted, is later work once the mechanism is proven).

## Verification (end-to-end)

`dotnet build` 0 warnings; `dotnet test` green (new wire-format + dirty-tracking tests); a live
two-process protocol (`DedicatedServer` + `ThinClient` over localhost) with human verdict; all
existing pinned captures/snapshots re-verified byte-identical (git-stash A/B against the rollback
point); AOT publish + JIT == NativeAOT on the two new hosts; double audit
`csharp-lowlevel` + `engine-architect` (network code touches threading/marshaling — a
`graphics-3d`-style deviation is not needed, no new Vulkan surface).

Once approved: task board, then execute.

## Outcome (closed session 44)

Delivered as specified: D1-D9 all implemented, `samples/DedicatedServer` + `samples/ThinClient` +
`Agapanthe.Net` all shipped. 994 tests, 0 warning, 0 regression (the only touch on shared
simulation code — `GameWorld.cs`/`GameWorld.Physics.cs` — is 23 additive lines, verified by
`git diff` before and after the audit-fix pass; no pinned capture or snapshot hash was at risk).

Two real bugs were found by actually running two processes together, not by re-reading the code:
dirty-tracking was originally coupled to `InstanceSlot` (never assigned by a headless server, so
`DrainDirtyDrawables` always returned 0) — fixed by a GlobalId-keyed `MarkNetworkDirty`, independent
of the render-facing mechanism; and a race between `EntityIntroduce` and `PositionUpdate` for the
same entity arriving in one `PollEvents()` batch with no `Tick()` in between — fixed by an explicit
`FlushStructuralChanges()` right after the deferred spawn.

The double audit (`csharp-lowlevel` 3.4/5, `engine-architect` 3.5/5, both PASS-with-concerns) found
4 more 🔴s, all fixed before closing: the demo's single always-dirty pilot masked that (a) a
drawable that's neither a physics body nor animated is never introduced to a client at all, and
(b) a client connecting after the world has settled — the actual primary flow for a dedicated
server — has no way to learn what already exists; fixed by a new `GameWorld.SnapshotAllDrawables()`
swept once per `PeerConnected`, decoupled from the dirty set. The other two: a malformed/truncated
packet could kill the server process outright (no bounds/try-catch on the wire reads) — fixed with
a caught-and-counted drop in `NetChannel`; and an unvalidated client `SimCommand.Vector` could
poison a body's velocity with NaN or bypass the server's own speed cap — fixed by rejecting
non-finite input and normalizing+clamping server-side before any mutation. A further 9 🟠 findings
were applied: unbounded dirty-set growth with no client connected, a hand-rolled `Thread.Sleep`
server loop instead of `FixedTimestepAccumulator` (the sole time-authority host was the only one
not using it), missing `ObjectDisposedException` guards, an ignored `Matrix4x4.Decompose` return
value silently truncating non-uniform scale, a `Math.Clamp` masking a content mismatch instead of
throwing, `NetChannel` never disposed client-side plus a `Listen()` double-subscribe bug, a
per-packet `NetDataWriter` allocation in the server's hot path, duplicated protocol-tag constants
across both samples, and a dead `ProjectReference` the static allowlist would have made permanent.
Six new regression tests were added, each verified to fail against the pre-fix code.

Debt deliberately left, documented rather than dropped: LiteNetLib types still cross
`Agapanthe.Net`'s public surface (no equivalent yet of "no `Vk*` leaves `Graphics`"); the dirty
mechanism marks every physics body unconditionally every tick, so it behaves closer to a full-state
broadcast than a true delta at this scale; despawn is not replicated at all (no consumer in this
sub-milestone despawns anything, so this is unexercised, not merely untested); the
`FlushStructuralChanges()` invariant is held by a comment at one call site rather than the type
system; `ThinClient`'s `NetChannel` is disposed via `AppDomain.ProcessExit` for lack of a teardown
hook on `ISceneRecipe`. Live human verdict: the pilot introduces correctly via the late-join sweep
and renders, 0 GPU leaks at shutdown, `DedicatedServer` re-published as NativeAOT and run
successfully after the audit-fix pass. Not committed — per project convention, commits happen only
on explicit request.
