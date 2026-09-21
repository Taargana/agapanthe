# Board — Net-1: minimal client/server loop (netcode réel, sub-milestone 1)

Status: **planned** (awaiting execution go-ahead)

Spec: `docs/plans/2026-09-16-netcode-net1-design.md` (approved 4.3/5)

## Rollback Point

`dff29566210deb3c22db63f3e3fe5b549c16f4df` (HEAD at DECOMPOSE time — Job-1's commit, pushed).
Working tree otherwise has an uncommitted `docs/BACKLOG.md` prose fix (Job-1 CLOS bullet, added
after Job-1's push) and this milestone's untracked spec file — neither touches code.

## Project Conventions

- .NET 10, `dotnet build` / `dotnet test` from repo root.
- TDD: write the failing test before the implementation for every task below.
- Headless closure discipline: `Agapanthe.Net` (new) must reference only `{Core, World, Engine}` —
  no GPU/window types — and must be added to `EngineIsHeadlessTests`'s static allowlist
  (`tests/Agapanthe.Tests/EngineIsHeadlessTests.cs`), the exact pattern MP-0a/Contenu-3a
  established for every new structural project since.
- Binary/blittable wire types, never JSON/Protobuf — matches `SimCommand`/`InputSnapshot`/
  `WorldSerialization`'s existing convention.
- `GameWorld`'s new network-facing mutators that touch shared state invisible to any conflict
  model use `AssertOwnerThreadStrict` (Job-1 precedent), never the sanctioned-allowlist-honoring
  `AssertOwnerThread`.
- Double audit before close: `csharp-lowlevel` + `engine-architect` (no new Vulkan surface, no
  `graphics-3d` deviation needed).
- Never commit/push without explicit user request.

## Dependency Graph

```
Wave 1   NET-001 (GameWorld.Network.cs — public network access surface)
              |
Wave 2   NET-002 (new Agapanthe.Net project: wire types + LiteNetLib + allowlist entry)
              |
Wave 3   NET-003 (samples/DedicatedServer)   NET-004 (samples/ThinClient)
              |________________________________________|
                              |
Wave 4                   NET-005 (live two-process verification + regression gate)
                              |
Wave 5 (tail, sequential)
   NET-006 Self Code Review -> NET-007 Requirements Validation -> NET-008 Full Project Verification
```

## Tasks

### NET-001 — `GameWorld` network access surface
- **Type**: code · **Size**: M · **Deps**: none
- **Files**: new `src/Agapanthe.World/GameWorld.Network.cs` (partial, mirrors the `.Physics.cs`/
  `.Queries.cs` sibling-file convention), `tests/Agapanthe.Tests/GameWorldNetworkAccessTests.cs`
- Per the spec's "GameWorld access surface" section: new **public** API (not internal+IVT — the
  spec's D2 reasoning, verified sound against the Job-1 `SetSanctionedWorkerThreads` precedent by
  spec review round 2). Concretely:
  - `bool TryGetEntity(ulong globalId, out EntityRef entity)` — resolves a live entity by its raw
    `GlobalId` value. Does not exist today (`EntityRef`'s constructor is `internal`, so no outside
    assembly can otherwise turn a received `ulong` into a usable handle).
  - A read accessor for a drawable's current position/orientation/scale by `EntityRef` (today's
    only accessor, `GetWorldPosition`, is `internal` and position-only — decide at implementation
    whether to expose position+rotation+scale from `LocalTransform`/`WorldTransform` or just
    position, per what NET-002's wire type actually needs).
  - A direct write path (`SetDrawableTransform`-shaped) that bypasses simulation entirely — the
    client's render-cache use case (D7). Guarded by `AssertOwnerThreadStrict` (Job-1 precedent —
    this mutates shared component state, not something a sanctioned worker should touch).
  - A drain of the dirty set established by `GameWorld`'s existing `MarkDirty`/`_slotDirty`
    bookkeeping (`GameWorld.cs:108-137`, fed by animation/physics-writeback/hierarchy-propagation
    per its own doc comment), filtered to drawables, returning what's dirty since the last drain
    and clearing it — this is the "new, analogous dirty set" the spec's D2 describes, distinct
    from the existing GPU-facing consumer (`SceneCandidateSet.EnqueueDirty`).
  - **D9 addendum (found before EXECUTE, see spec)**: the drain must distinguish an entity the
    server has never told this client about (needs `AssetKey` + initial transform — `EntityIntroduce`)
    from one it already has (needs transform only — `PositionUpdate`). `GameWorld` itself does not
    track per-client "known" state (that's `DedicatedServer`'s job, NET-003) — but the drain API
    must expose enough (the entity's `AssetRef`/`AssetKey`, via Contenu-3a's existing component)
    for `DedicatedServer` to build either packet shape.
- **Tests**: writing via the new write path marks an entity dirty; draining returns it exactly
  once then clears; a second drain with no intervening write returns nothing; `TryGetEntity`
  resolves a spawned entity's `GlobalId` and fails cleanly for an unknown one; the write path
  throws off the owner thread (mirrors Job-1's `AssertOwnerThreadStrict` test pattern) — a
  sanctioned worker thread must NOT be able to call it either.
- **Acceptance**: `dotnet build` 0 warnings; existing `GameWorld` tests unaffected (new file,
  additive only).

### NET-002 — New `Agapanthe.Net` project: wire types + LiteNetLib integration
- **Type**: code · **Size**: M · **Deps**: NET-001
- **Files**: new `src/Agapanthe.Net/Agapanthe.Net.csproj` (references `Core`, `World`, `Engine`
  only — no GPU/window types), `ReplicatedTransform.cs` (blittable wire struct: `GlobalId ulong` +
  position/rotation/scale, mirrors `SimCommand`'s `[StructLayout(LayoutKind.Sequential)]`
  pattern), a thin wrapper type around LiteNetLib's `NetManager`/`EventBasedNetListener` for
  send/receive (name TBD at implementation, e.g. `NetChannel`).
- `PackageReference Include="LiteNetLib" Version="2.1.4"` (version verified this session to
  publish NativeAOT-clean with 0 trim/AOT analyzer warnings).
- **`tests/Agapanthe.Tests/EngineIsHeadlessTests.cs`**: add an `[InlineData]` entry for
  `Agapanthe.Net.csproj` — the exact allowlist pattern every structural project since MP-0a has
  required. Do not skip this; it is the gate that would otherwise silently let a GPU/window
  reference leak into the headless closure.
- **Tests**: `ReplicatedTransform` serialize/deserialize round-trips byte-identical (GPU-free,
  no LiteNetLib dependency — pure format test, mirrors `HeadlessSimSnapshotFormatTests`'s style);
  a loopback LiteNetLib send/receive test (two `NetManager`s on localhost, one sends a
  `ReplicatedTransform`-shaped payload, the other receives it byte-identical) — this is the
  scratch verification already done manually this session, now committed as a real test.
- **Acceptance**: `dotnet build` 0 warnings; `EngineIsHeadlessTests` passes with the new
  allowlist entry; AOT publish of a throwaway consumer is NOT required here (covered by NET-005
  once the real hosts exist).

### NET-003 — `samples/DedicatedServer`
- **Type**: code · **Size**: M · **Deps**: NET-002
- **Files**: new `samples/DedicatedServer/DedicatedServer.csproj` + `Program.cs`.
- Composition root mirrors `samples/HeadlessSim/Program.cs`: `GameWorld` +
  `SimulationHost.CreateDefault`, zero GPU. Unlike `HeadlessSim`, loops indefinitely on network
  activity instead of a fixed `--ticks` count:
  ```
  loop:
    netChannel.PollEvents()          // drains incoming SimCommand payloads into SimulationHost.Commands
    simulationHost.Tick(fixedDt)     // the ONLY place StepPhysics/PropagateTransforms run
    dirty = gameWorld.DrainDirtyDrawables()   // NET-001's drain API
    netChannel.SendDelta(dirty)      // to the single connected peer, if any
  ```
- On the single client's connection: spawn/assign its one pilotable entity, reply with its
  `GlobalId` so the client knows which entity is "theirs" for input purposes (D8).
- **Tests**: the command-intake glue (deserialize → `SimCommandQueue.Enqueue`) and the
  delta-gather-and-encode glue are extractable, testable units even without a live socket —
  write them as small pure functions/types with unit tests; the full loop itself is verified live
  in NET-005, not unit-tested (a `Program.cs` network loop is not a meaningful unit-test target).
- **Acceptance**: `dotnet build` 0 warnings; new project does not appear in any existing
  allowlist that would reject it incorrectly (it's a leaf sample, like Sandbox/TopDown/HeadlessSim
  — not part of the headless closure test itself, since it CAN use GPU-free-only code but is not
  a library other projects reference).

### NET-004 — `samples/ThinClient`
- **Type**: code · **Size**: M · **Deps**: NET-002 (parallel-safe with NET-003 — disjoint projects)
- **Files**: new `samples/ThinClient/ThinClient.csproj` + `Program.cs` + a minimal `IGame`/
  `ISceneRecipe` implementation.
- Uses `AppHost.RunClient` for GPU bootstrap/frame loop/teardown exactly like Sandbox/TopDown.
  Its `ISceneRecipe.Build` populates nothing at build time (empty scene — no `.agscene`/cook-time
  definition) — the "scene" arrives entirely via incoming network deltas.
- On receiving an `EntityIntroduce` packet (D9): resolve the received `AssetKey` via the client's
  own `AssetCatalog`/`ResourceRegistry` (Contenu-1/Contenu-2's existing path — `ThinClient` needs
  the cooked content available locally, but no `.agscene`), spawn the drawable
  (`GameWorld.SpawnDeferred`/`SpawnBody`-shaped) with that mesh, write its initial transform.
- On receiving a `PositionUpdate` packet: `TryGetEntity` (NET-001) by `GlobalId` (must already be
  known — a `PositionUpdate` for an unintroduced entity is a protocol violation, log and drop);
  write its position/rotation/scale via NET-001's direct write path (never
  `SimulationHost.Tick()` — the client never simulates, D1/D7).
- **Structural barrier without `Tick`** (spec's explicitly-flagged gap, closed by spec review):
  call `GameWorld.FlushStructuralChanges()` itself, once per received delta packet, after applying
  any new-entity spawns from that packet and before the frame renders — verified safe outside
  `Tick`'s cadence (its only enforced precondition is owner-thread identity, and
  `SceneMaterializer`/`WorldSerialization` already call it standalone elsewhere).
- Samples input via the same `InputMap`/`SampleInput` shape as any existing recipe, but routes
  the resulting `SimCommand` to the network channel instead of a local `SimCommandQueue`.
- **Tests**: mostly integration-level (verified live in NET-005); the "apply one delta packet to
  an empty `GameWorld`, confirm the entity spawns with the right transform" step is unit-testable
  without a real network connection (feed a `ReplicatedTransform` array directly) — write that
  test.
- **Acceptance**: `dotnet build` 0 warnings.

### NET-005 — Live two-process verification + regression gate
- **Type**: code · **Size**: M · **Deps**: NET-003, NET-004
- Start `DedicatedServer` and `ThinClient` as two real OS processes over localhost. Move via
  `ThinClient`'s input, observe the reflected position change arrive client-side within a few
  ticks. Human/live verdict (mirrors every prior milestone's live-demo verification step).
- **Regression gate**: full `dotnet test` green; all 9 existing pinned Sandbox/TopDown captures +
  `HeadlessSim`'s snapshot hashes re-verified byte-identical via `git stash` A/B against the
  `dff2956` rollback point (this milestone should not touch any existing rendering/simulation code
  path at all — verify that claim held, don't just assume it); AOT publish + JIT == NativeAOT
  confirmed on `DedicatedServer` and `ThinClient` specifically (new hosts, no prior baseline to
  diff against — the gate is "a scripted deterministic input sequence produces the same observable
  client-side transform sequence under JIT and under NativeAOT," mirroring `HeadlessSim --drive`'s
  existing deterministic gate shape).
- **Acceptance**: live verdict PASS; full regression gate green.

### NET-006 — Self Code Review (tail, mandatory)
- **Type**: code · **Size**: S · **Deps**: NET-001…NET-005
- Full diff self-review against the spec's Decision Log (D1-D8): confirm the client never calls
  `SimulationHost.Tick()`; confirm `GameWorld`'s new write path is `AssertOwnerThreadStrict`, not
  the sanctioned-allowlist version; confirm no `Task`/`Parallel.Invoke`-shaped allocation-per-call
  pattern crept into the network loop; confirm `EngineIsHeadlessTests`' new allowlist entry is
  correct (no GPU/window reference in `Agapanthe.Net`).

### NET-007 — Requirements Validation (tail, mandatory)
- **Type**: code · **Size**: S · **Deps**: NET-006
- Walk the spec's Testing strategy and Out-of-scope sections line by line; confirm every item is
  either delivered or explicitly, correctly deferred (multi-client, `OriginatorId`, arbitrary
  component replication, prediction/reconciliation, interest management, `RunDedicatedServer` as
  an `AppHost` method — none of these should have crept in).

### NET-008 — Full Project Verification (tail, mandatory)
- **Type**: code · **Size**: M · **Deps**: NET-007
- `dotnet build` 0 warnings; `dotnet test` full suite green; regression gate (captures/snapshots/
  AOT) re-confirmed after any audit-fix pass; dispatch the double audit (`csharp-lowlevel` +
  `engine-architect`), apply findings, re-verify anything the findings touch.

## Wave 5 results (NET-006 → NET-008) — CLOSED

- **NET-006 Self Code Review**: complete. Confirmed the client never calls `Tick()` for simulation
  purposes; confirmed `GameWorld.Network.cs`'s mutating/dirty-draining methods use
  `AssertOwnerThreadStrict`, and its two pure reads (`TryGetEntity`, `GetDrawableTransform`) use
  the strict guard too — a deliberate, conservative choice (no Net-1 code runs on a worker thread
  today, so the stricter default costs nothing and matches the project's reject-by-default
  posture), not a bug. No `Task`/`Parallel.Invoke` anywhere in the new code (confirmed by grep) —
  `DedicatedServer`'s `Thread.Sleep` was replaced by `FixedTimestepAccumulator` during the audit-fix
  pass (see below). `EngineIsHeadlessTests`' allowlist entries confirmed correct.
- **NET-007 Requirements Validation**: complete. Walked the spec's Testing strategy and Out-of-
  scope sections; confirmed no scope creep (grepped for `OriginatorId`, `RunDedicatedServer`,
  `interest management`, `reconciliation`, `prediction` — only comments correctly citing deferral).
- **NET-008 Full Project Verification**: `dotnet build` 0 warnings, `dotnet test` 994/994 green,
  double audit dispatched and findings applied (below), live two-process re-verification + AOT
  publish re-confirmed after the audit-fix pass.

### Double audit: `csharp-lowlevel` (3.4/5) + `engine-architect` (3.5/5), both PASS-with-concerns

Convergent finding from both: the demo's single physics-driven, always-dirty pilot entity masked
two real gaps — (1) a drawable that is neither a physics body nor animated is never marked network-
dirty, so it would never be introduced to a client at all; (2) a client connecting after the world
has settled (the primary flow for a single dedicated server) had no path to learn about existing,
non-dirty entities. Fixed by decoupling introduction from dirtiness: new `GameWorld.SnapshotAllDrawables()`
gives a late-joining peer the entire live world state once, independent of the dirty set; the dirty
stream now only drives ongoing `PositionUpdate`s for already-known entities.

**Applied fixes** (both 🔴 findings from each audit, plus the highest-value 🟠s):
- Malformed/truncated packets no longer crash the server or client: `NetChannel`'s dispatch is
  wrapped in try/catch + `finally { reader.Recycle(); }`, with a `MalformedPacketCount` signal
  (patron `DiscardedCommandCount`/`SanitisedInputCount`). Regression test: `NetChannelTests.MalformedPacket_IsDroppedRatherThanCrashingTheChannel`.
- A client's `SimCommand.Vector` is validated (rejects non-finite) and normalized+clamped to the
  server's own `MoveSpeed` before reaching `SetBodyVelocity` — an uncooperative/buggy client can no
  longer poison a body's `Velocity` with NaN or bypass the server's speed cap.
- Late-join fixed via `GameWorld.SnapshotAllDrawables()`, called once per `PeerConnected` — see
  above. Regression tests: `SnapshotAllDrawables_ReportsADrawableNeverMarkedDirty`,
  `SnapshotAllDrawables_IncludesAPhysicsBodyToo`.
- Unbounded dirty-set growth with no client connected: `DedicatedServer` now drains
  `DrainDirtyDrawables` unconditionally every tick (discarding the result when no peer is
  connected) rather than only when a client is present — bounds the set by "dirty since last tick,"
  never by uptime.
- `DedicatedServer`'s loop now uses `FixedTimestepAccumulator` (measured wall-clock delta via
  `Stopwatch`) instead of a fixed `Thread.Sleep(16)` — the sole time-authority host was previously
  the only host in the project not using MP-0c's own mechanism, silently drifting behind wall-clock
  under any load.
- `GameWorld.Network.cs`'s 5 public methods gained the `ObjectDisposedException` guard every other
  public `GameWorld` method already has.
- `Matrix4x4.Decompose`'s `bool` return is now checked (throws on a degenerate matrix) and a
  non-uniform scale is rejected rather than silently truncated to its X component — `DrawableTransform.Scale`
  can only represent a uniform scale (wire shape, D9), and the old code violated that silently.
  Regression tests: `GetDrawableTransform_ThrowsOnANonUniformScale`, `SnapshotAllDrawables_ThrowsOnANonUniformScale`.
- `ThinClientSceneRecipe` now throws on an out-of-bounds `LocalMesh` (content mismatch between
  server and client cooks) instead of silently clamping into range and rendering the wrong mesh —
  mirrors `SceneMaterializer`'s own posture on the same field (Contenu-3b).
- `NetChannel.Listen` is now idempotent (a second call no longer double-subscribes
  `ConnectionRequestEvent`); `Dispose` now calls `DisconnectAll()` and clears both event fields;
  `PollEvents`/`Connect`/`Listen` guard `_disposed`.
- `ThinClientSceneRecipe` now disposes its `NetChannel` via `AppDomain.ProcessExit` (no ordered
  teardown hook exists on `ISceneRecipe`/`PresentationSceneContext` for a recipe-owned disposable —
  documented as remaining debt below, not a full fix).
- Server hot path: `NetDataWriter` is now allocated once and `Reset()` between packets, not
  allocated per entity per tick.
- Protocol tag duplication removed: new `Agapanthe.Net.NetProtocol` is the single source of the 4
  wire tags + `MoveKind`, referenced by both samples (previously redeclared identically in both).
- `Agapanthe.Net`'s dead `ProjectReference` to `Agapanthe.World` removed (nothing in the project
  names a World/Arch type); `EngineIsHeadlessTests` gained a closure walk rooted on
  `Agapanthe.Net` (`NetAssemblyClosure_ContainsNoGpuAssembly`) and a `PackageReference` allowlist
  restricting it to exactly `{LiteNetLib}` — previously only `Agapanthe.Engine` had either gate.

**Re-verified after the fix pass**: `dotnet build` 0 warnings, `dotnet test` 994/994 green, live
two-process run (JIT) — pilot entity introduced via the late-join snapshot path and rendered
correctly, `AppHost: clean shutdown, no GPU resource leaks` — and `DedicatedServer` re-published
as NativeAOT and run successfully. `GameWorld.cs`/`GameWorld.Physics.cs` diff unchanged since
NET-005 (2/21 lines, additive-only) — no risk to any pinned Sandbox/TopDown/HeadlessSim capture or
snapshot hash, none re-verified needed beyond the ones already confirmed at NET-005.

**Debt deliberately left** (documented, not silently dropped):
- LiteNetLib types (`NetPeer`, `NetDataWriter`) still cross `Agapanthe.Net`'s public surface
  (`NetChannel.Received`, `PacketCodec`'s signatures) — the project's own "no `Vk*` leaves
  `Graphics`" discipline has no equivalent here yet. A `NetChannel.Send(peerId, ReadOnlySpan<byte>)`
  shape would close it; deferred, no functional impact today (single consumer pair).
- Physics writeback still marks every physics body dirty unconditionally every tick (not gated on
  an actual position change) — D2's "delta" is a real, independent dirty-tracking *mechanism*, but
  its *effect* at the demo's single-body scale is closer to a full-state broadcast. A per-body
  previous-value comparison would make it a true delta; deferred.
- Despawn is not replicated at all (no wire tag, no client-side removal) — no consumer in Net-1's
  demo scope ever despawns a drawable, so this is unexercised, not merely untested. Real risk once
  any despawn-capable gameplay (e.g. VS-2's probes) is replicated.
- `FlushStructuralChanges()` after `SpawnDeferred` in `ThinClientSceneRecipe.Introduce` is correct
  and complete (verified) but is a non-local invariant held by one call site's comment, not by the
  type system — a future packet-driven spawn site could silently omit it. A `GameWorld.SpawnImported`
  overload returning `EntityRef` (the client never needs the deferred path at all) would remove the
  invariant entirely; deferred.
- A second client connecting mid-session silently steals the server's single peer slot (D8 says
  "one client," nothing refuses a second) — out of scope per spec, D8.
- `NetChannelTests` hardcodes ports (`29_050`, `29_051`) — a concurrent test run or an already-bound
  port makes an unrelated test flake. Pre-existing pattern, not introduced by the fix pass.

## Deferred Work (out of scope, not dropped)

Per spec: multi-client support and peer identity (`OriginatorId`, `SimCommand.Target` ownership
routing); replication of arbitrary/all components (this sub-milestone hardcodes
`WorldPosition`/`WorldTransform` of drawables only); client-side prediction/server reconciliation/
rollback; interest management/relevance filtering; lag compensation, anti-cheat, congestion/rate
control beyond LiteNetLib's defaults; persistent/massive-scale server topology; folding
`DedicatedServer` into `AppHost` as a first-class `RunDedicatedServer` method.
