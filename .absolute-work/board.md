# Absolute Work Board — MP-0d (input → timestamped commands)

**Status**: `completed` 2026-09-06 — all 4 waves + tail done, double audit applied. Awaiting human visual verdict on the `drive` scene, then commit on request.
**Spec**: `docs/plans/2026-09-06-mp0d-input-commands-design.md` (APPROVED 4.30/5)
**Session**: 29
**Created**: 2026-09-06

Skip INTAKE + SPEC (spec pre-approved). Straight to DECOMPOSE. Human greenlight between
each spec wave (W1→W2→W3→W4). Commit on explicit request only.

## Project Conventions

- .NET 10, `TreatWarningsAsErrors` (0 warning), xUnit, `dotnet build` / `dotnet test`.
- NativeAOT probe: `tools/AotComponentProbe` — AOT publish needs `PATH` prefixed with the
  `vswhere.exe` folder (`/c/Program Files (x86)/Microsoft Visual Studio/Installer`).
- No `Vk*` type outside `Agapanthe.Graphics`; no Arch type outside `Agapanthe.World`.
- `Agapanthe.Engine` closure = `{Core, World}` — enforced by `EngineIsHeadlessTests`
  (deny-list of forbidden assemblies, not a name allowlist).
- Gates: 0 warning · 0 validation message · 0 leak · 0 alloc/frame steady state · NativeAOT
  PASS · double audit (`csharp-lowlevel` + `engine-architect`) · human visual verdict.
- Conversation FR; code / commits / docs EN.
- Capture command: `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0`
  `AGAPANTHE_DROP_EVERY=12`, 1280×720, Debug, `AGAPANTHE_CAPTURE` + `AGAPANTHE_CAPTURE_UI`.
  Expected unchanged: HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa`.
  `HeadlessSim` default (`--ticks 600 --bodies 8`) = `7e8dc68f5a25914c84677a7a53ad3a58` (1868 B) unchanged.

## Rollback Point

`61eae834d2c489727a519c1484b708d0add2db1b` (tree clean at W1 start).

## Progress

- **W1 DONE** (2026-09-06): AW-001..006. 6 new Engine files + `GameWorld.SetBodyVelocity`.
  `dotnet build` 0 warning · `dotnet test` 575 passed (+17). W1 tests: `SimCommandTests` (5),
  `SimCommandQueueTests` (6), `InputTranslationTests` (6). Blittability 56/40 B pinned,
  queue order + FIFO + past-tick + 0-alloc + thread-throw, translation edges + axis + rebind.
- **W2 DONE** (2026-09-06): AW-007..009. `SimulationHost` input phase (5 members + `DiscardHandler`,
  drain before `_scheduler.Tick` at `_scheduler.TickIndex`), `SetBodyVelocity` already in W1.
  `dotnet test` 582 passed (+7). `SimulationHostInputTests` (5): full-phase determinism, no-callback
  no-op, one-edge-across-3-tick-catch-up, despawned-target no-op, Key.B-style direct enqueue.
  `AccumulatorEquivalenceTests` +2: scripted input 20×3 == 60×1 (command count + position) + 30≠60.
  Note: declarative axis binding leaves `SimCommand.Target` = default; the demo apply-point steers its
  known body (multi-entity ownership routing is out of scope per spec).
- **W3 DONE** (2026-09-06): AW-010..013.
  - `HeadlessSim --drive` (gate): scripted per-tick `InputSnapshot` (axis0 +1/[0,60), −1/[60,120), Brake@120),
    `InputMap` + `ApplyCommand` → `SetBodyVelocity`. JIT snapshot MD5 `97e786f0455a53d856b9ba4affca1003`
    (208 B), pinned in `HeadlessSimSnapshotFormatTests.HeadlessSimDriveScene_...`. Default `7e8dc68f…` unchanged.
  - `AotComponentProbe`: `AotInputCommandSmoke` — queue + map + `InputTranslation.Emit` + `DrainUpTo` + `InputAxes`.
  - Sandbox `Key.B` (planet-drop/challenge) → `orchestrator.Simulation.Commands.Enqueue` stamped `TickIndex`,
    carrying `camera.Position`; `ApplyCommand` routes to `TryShoot` / `DropOne`. Captures re-run (1×):
    HDR `12638edd…` / UI `03421357…` **unchanged**, 0 validation, 0 leak.
  - Sandbox `AGAPANTHE_SCENE=drive`: one steerable zero-gravity body, fixed camera (`controller.Update` skipped),
    interactive `SampleInput` (WASD/Space/C axes, X = brake edge). Launches clean (0 leak, 0 validation).
    Rider profile `drive (MP-0d steerable entity)` added.
  - `dotnet build` 0 warning · `dotnet test` 583 passed (+1: drive snapshot pin).
- **W4 DONE** (2026-09-06): AW-014..017.
  - Captures ×3 HDR `12638edd…` / UI `03421357…` unchanged; `HeadlessSim --drive` JIT==AOT
    `97e786f0455a53d856b9ba4affca1003`; default `7e8dc68f…` unchanged; `AotComponentProbe` AOT PASS.
  - Double audit: `engine-architect` PASS-with-concerns 4.3/5 (no 🔴); `csharp-lowlevel` PASS-with-concerns (1 🔴).
  - Fixes applied: 🔴 `SetBodyVelocity`/`GetVelocity` guard `Has<Velocity>()`+throw · 🟠 `SimCommandQueue.DrainUpTo`
    scratch-and-compact before handlers, re-entrant throw · 🟠 `DiscardedCommandCount` replaces the Debug.Assert ·
    🟠 `SimCommandTests` asserts field byte offsets · doc notes (`SimCommand.Target`, `Tick` phase order,
    `ApplyCommand` assign-not-+=) · spec broken example corrected.
  - After fixes: `dotnet test` **589 passed** (+31 total), 0 warning; captures + AOT re-verified unchanged.
  - CONVERGE: `CLAUDE.md`, `docs/AVANCEMENT.md`, `docs/BACKLOG.md`, spec §Execution outcome updated;
    session board archived to `.absolute-human/archive/board-session29-MP0d.md`.

## Remaining

- Human visual verdict: `dotnet run --project samples/Sandbox` with `AGAPANTHE_SCENE=drive` — steer the body
  (WASD, Space/C, X brake); and `AGAPANTHE_SCENE=planet-drop` — B still drops a probe.
- Commit on explicit request only.

---

## Tasks

### Wave W1 — Engine mechanism, isolated (no existing file touched)

#### AW-001 — `SimCommand` + `InputSnapshot` value types
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.Engine/SimCommand.cs` (new), `src/Agapanthe.Engine/InputSnapshot.cs` (new)
- `SimCommand` = `[StructLayout(Sequential)] readonly record struct (long TargetTick, byte Kind,
  EntityRef Target, Double3 Vector, float Scalar, uint Flags)` — 56 B, `Kind` opaque.
- `InputSnapshot` = `[StructLayout(Sequential)] struct { ulong Held; ulong Pressed; ulong Released;
  InputAxes Axes; }` — 40 B. `InputAxes` = `[InlineArray(4)] struct { float _element0; }`.
  `ButtonTrigger : byte { OnPress, OnRelease, WhileHeld }`. `delegate void SimCommandHandler(in SimCommand)`.
- XML doc per spec §Architecture (edge-mask contract note on `InputSnapshot`).
- **Acceptance**: builds; only `System.*` / `Agapanthe.Core` / `Agapanthe.World` used.

#### AW-002 — `SimCommandQueue`
- **Type**: code · **Size**: M · **Deps**: AW-001
- **Files**: `src/Agapanthe.Engine/SimCommandQueue.cs` (new)
- Pre-grown backing array; `Enqueue(in SimCommand)` insertion-sort, shift while
  `existing.TargetTick > cmd.TargetTick` (strict `>` → FIFO at equal tick); `DrainUpTo(long tick,
  SimCommandHandler)` applies `TargetTick <= tick` front-to-back, returns count run, never drops a
  past-tick command; `Count`. Grows on overflow (no cap — deferred).
- Owner-thread guard on `Enqueue`/`DrainUpTo`: `[Conditional("DEBUG")]` that **throws
  `InvalidOperationException`** (like `GameWorld.AssertOwnerThread`) — **not** `Debug.Assert` (R2).
- **Acceptance**: builds; 0-alloc after warmup by construction.

#### AW-003 — `InputMap`
- **Type**: code · **Size**: S · **Deps**: AW-001
- **Files**: `src/Agapanthe.Engine/InputMap.cs` (new)
- `sealed class`, fixed capacity (32 button bindings + 1 axis binding), 0-alloc to read.
  `BindButton(int bit, byte kind, ButtonTrigger trigger)`, `BindAxisVector(byte kind, int x, int y, int z)`.
- **Acceptance**: builds; rebinding a bit replaces; over-capacity throws.

#### AW-004 — `InputTranslation.Emit`
- **Type**: code · **Size**: S · **Deps**: AW-001, AW-002, AW-003
- **Files**: `src/Agapanthe.Engine/InputTranslation.cs` (new)
- `static void Emit(in InputSnapshot cur, InputMap map, SimCommandQueue queue, long tick)` — static,
  stateless, 0-alloc. No previous-snapshot param (edges are in `Pressed`/`Released`). Button bindings:
  `OnPress`→`Pressed` bit, `OnRelease`→`Released` bit, `WhileHeld`→`Held` bit. Axis binding: one
  command/call, `Vector = (axes[x], axes[y], axes[z])`.
- **Acceptance**: builds.

#### AW-005 — W1 tests
- **Type**: test · **Size**: M · **Deps**: AW-002, AW-003, AW-004
- **Files**: `tests/Agapanthe.Tests/SimCommandTests.cs` (new), `SimCommandQueueTests.cs` (new),
  `InputTranslationTests.cs` (new) — all GPU-free
- Cases (spec §Testing strategy rows 1–4):
  - `Unsafe.SizeOf<SimCommand>()==56`, `<InputSnapshot>()==40`, no managed refs (`RuntimeHelpers.IsReferenceOrContainsReferences` false).
  - `DrainUpTo` runs `<= tick` only, front-to-back; enqueue B@5,A@5,C@3 → drain order C,B,A; past-tick command runs now.
  - queue 0-alloc after warmup (`GC.GetAllocatedBytesForCurrentThread` delta == 0 over enqueue+drain loop, mixed ticks).
  - `Enqueue`/`DrainUpTo` throw `InvalidOperationException` on a foreign thread (Debug build).
  - `Emit`: `Pressed`+`OnPress`→1 cmd; `Released`+`OnRelease`→1 on falling edge; `Held`+`WhileHeld`→1/call;
    axis→1/call with right `Vector`; unbound bits→nothing.
- **Acceptance**: all green.

### Wave W2 — wire into `SimulationHost` + World API

#### AW-006 — `GameWorld.SetBodyVelocity`
- **Type**: code · **Size**: S · **Deps**: none (World-only; can start with W1)
- **Files**: `src/Agapanthe.World/GameWorld.cs` (or the matching partial) — 1 new public method
- `public void SetBodyVelocity(EntityRef entity, Vector3 linear)` — opens
  `ObjectDisposedException.ThrowIf(_disposed, this); AssertOwnerThread();` then
  `Deref(entity).Set(new Velocity { Linear = linear })`. XML contract per spec (R3: `IsAlive` covers
  despawn, not a pending deferred spawn).
- **Acceptance**: builds; existing World tests green.

#### AW-007 — `SimulationHost` input phase + members
- **Type**: code · **Size**: M · **Deps**: AW-002, AW-003, AW-004
- **Files**: `src/Agapanthe.Engine/SimulationHost.cs`
- New members: `Func<InputSnapshot>? SampleInput`, `InputMap? InputMap`, `SimCommandHandler? ApplyCommand`,
  `SimCommandQueue Commands` (composed, ctor), `InputSnapshot CurrentInput { get; private set; }`,
  static `DiscardHandler` field (`Debug.Assert(false, …)`).
- `Tick`: before `_scheduler.Tick` — if `SampleInput` set, `CurrentInput = SampleInput()`; if `InputMap`
  set, `InputTranslation.Emit(in CurrentInput, InputMap, Commands, _scheduler.TickIndex)`; then always
  `Commands.DrainUpTo(_scheduler.TickIndex, ApplyCommand ?? DiscardHandler)`.
- `CreateDefault` registers no input/command system.
- **Acceptance**: builds; phase is a no-op when `SampleInput` null → all existing Engine tests green.

#### AW-008 — W2 host tests
- **Type**: test · **Size**: M · **Deps**: AW-006, AW-007
- **Files**: `tests/Agapanthe.Tests/SimulationHostInputTests.cs` (new), GPU-free
- Cases (spec §Testing strategy rows 6, 8, 10, 11):
  - full phase: synthetic `SampleInput` script + map + `ApplyCommand` → deterministic `SetBodyVelocity`
    → deterministic final position; `SampleInput` null → phase skipped, `Commands.Count==0` after N ticks.
  - edge-bit: `Pressed` bit set once by a script that then clears it → exactly one command across a
    3-tick catch-up frame (`AdvanceFrame` with `3*Fixed`).
  - `ApplyCommand` targeting a despawned entity → demo-style handler no-ops (`IsAlive` gate), tick continues.
  - `Key.B`-style: `host.Commands.Enqueue` from outside a tick, stamped `host.TickIndex`, applies once at
    that tick with `camera.Position` in `Vector`.
- **Acceptance**: all green.

#### AW-009 — extend `AccumulatorEquivalenceTests`
- **Type**: test · **Size**: S · **Deps**: AW-007
- **Files**: `tests/Agapanthe.Tests/AccumulatorEquivalenceTests.cs` (extend)
- Build every profile from `accumulator.FixedDeltaSeconds` / `Fixed` const — never `n/60f` (class comment).
- Same scripted input through `20 × Advance(3·fixed)` vs `60 × Advance(1·fixed)` → identical command count
  (primary, integer) **and** identical final position (secondary). Companion: `30 × Advance(1·fixed)` ends
  at a different position from the 60-tick runs (non-vacuity).
- **Acceptance**: green.

### Wave W3 — the demo

#### AW-010 — Sandbox `AGAPANTHE_SCENE=drive`
- **Type**: code · **Size**: M · **Deps**: AW-007, AW-006 · **shared file**: `samples/Sandbox/Program.cs` (owns it this wave; AW-011 serialized after)
- Fixed camera on a small play volume; one steerable body via `SpawnBody`; scene `PhysicsSettings` =
  `Vector3.Zero` gravity, no attractor, ground far below.
- `host.InputMap`: `BindAxisVector(MoveIntent, 0,1,2)` + `BindButton(Brake bit, Brake kind, OnPress)`.
- `host.ApplyCommand`: `MoveIntent` → `if (world.IsAlive(cmd.Target)) world.SetBodyVelocity(cmd.Target,
  cmd.Vector.ToVector3(Double3.Zero) * MoveSpeed)`; `Brake` → `SetBodyVelocity(cmd.Target, Vector3.Zero)`.
- `host.SampleInput`: interactive — WASD/Space/C → axes 0/1/2 as −1/0/+1; Brake bit from a pending-edge
  mask fed by `EngineWindow.KeyPressed`.
- Rider profile for `drive` in `samples/Sandbox/Properties/launchSettings.json`.
- **Acceptance**: builds; scene runs windowed; not a pinned capture (F8).

#### AW-011 — `Key.B` → `Commands.Enqueue` (both planet scenes)
- **Type**: code · **Size**: S · **Deps**: AW-007 · **shared file**: `samples/Sandbox/Program.cs` (run AFTER AW-010)
- `Key.B` handler builds `new SimCommand(host.TickIndex, kind: SpawnProbe, Target: default,
  Vector: camera.Position, 0f, 0)` → `host.Commands.Enqueue`.
- `ApplyCommand` for `SpawnProbe`: planet-challenge → existing budget check + aimed radial drop;
  planet-drop → `probeDropper.DropOne()` (position ignored).
- **Acceptance**: `planet-drop` / `planet-challenge` behave as before; captures unchanged (verified W4).

#### AW-012 — `HeadlessSim --drive`
- **Type**: code · **Size**: M · **Deps**: AW-007, AW-006
- **Files**: `samples/HeadlessSim/Program.cs`
- `--drive`: one-body zero-gravity scene, scripted `SampleInput` keyed on `host.TickIndex` (e.g. axis0 =
  +1 for [0,60), −1 for [60,120), Brake pressed at 120), an `InputMap`, `ApplyCommand` → `SetBodyVelocity`;
  runs `--ticks`; `--save` writes the snapshot. Default mode untouched.
- **Acceptance**: builds; `--drive --ticks N --save` writes a snapshot; default mode hash unchanged.

#### AW-013 — `AotComponentProbe` command/input smoke
- **Type**: test · **Size**: S · **Deps**: AW-002, AW-003, AW-004, AW-007
- **Files**: `tools/AotComponentProbe/Program.cs`
- Build a queue, translate a snapshot through a map, drain+apply, touch `InputAxes` — 3-line pattern like
  the existing accumulator smoke.
- **Acceptance**: JIT run PASS.

### Wave W4 — captures + tail

#### AW-014 — capture + AOT gate
- **Type**: infra · **Size**: M · **Deps**: AW-010, AW-011, AW-012, AW-013
- Sandbox headless capture ×3 → HDR `12638edd…` / UI `03421357…` unchanged; 0 validation, 0 leak.
- `HeadlessSim` default `--ticks 600 --bodies 8` → `7e8dc68f…` unchanged.
- `HeadlessSim --drive` JIT vs AOT publish → identical, record the new pinned MD5 in a test
  (`HeadlessSimSnapshotFormatTests` pattern) + `CLAUDE.md` + `AVANCEMENT.md`.
- `AotComponentProbe` AOT publish + run PASS (new smoke included).
- **Acceptance**: every hash as stated; probe PASS.

#### AW-015 — Self Code Review _(mandatory tail)_
- **Type**: docs · **Deps**: AW-014
- Re-read the full diff against the spec; fix anything off-spec / off-convention before external audit.

#### AW-016 — Requirements Validation _(mandatory tail)_
- **Type**: docs · **Deps**: AW-015
- Walk the spec DoD line by line; check each locked decision (1–10) and residual (R1–R4) is honoured.

#### AW-017 — Full Project Verification + double audit + human verdict _(mandatory tail)_
- **Type**: docs · **Deps**: AW-016
- `dotnet build` 0 warning · `dotnet test` green · `EngineIsHeadlessTests` green.
- Double audit: `csharp-lowlevel` + `engine-architect` subagents, findings applied.
- Human verdict: steer the entity in `drive`; `Key.B` still drops a probe.
- CONVERGE: update `CLAUDE.md` / `docs/AVANCEMENT.md` / `docs/BACKLOG.md`, archive board, suggest commit.

---

## Dependency graph

```
W1:  AW-001 ──┬─ AW-002 ──┐
              ├─ AW-003 ──┼─ AW-004 ── AW-005
              └───────────┘
     AW-006 (World, independent — may run in W1)

W2:  AW-002,003,004 ── AW-007 ──┬─ AW-008 ── (needs AW-006 too)
                                ├─ AW-009
     AW-006 ────────────────────┘

W3:  AW-007(+006) ──┬─ AW-010 ── AW-011      (both edit Program.cs → serial: 010 then 011)
                    ├─ AW-012
                    └─ AW-013

W4:  AW-010,011,012,013 ── AW-014 ── AW-015 ── AW-016 ── AW-017
```

## Wave / parallelism plan

| Wave | Sequential | Parallel-safe (disjoint files) | Gate |
|---|---|---|---|
| W1 | AW-001 → AW-004 → AW-005 | {AW-002, AW-003} then AW-004; AW-006 alongside | human greenlight |
| W2 | AW-007 → AW-008 | AW-009 alongside AW-008 | human greenlight |
| W3 | AW-010 → AW-011 (shared Program.cs) | AW-012, AW-013 parallel | human greenlight |
| W4 | AW-014 → AW-015 → AW-016 → AW-017 | — | human verdict + CONVERGE |

## Deferred Work

- `SimCommand.OriginatorId` / peer identity (spec §Out of scope).
- Command wire format (send/receive), replay/log harness.
- `SimCommandQueue` flood protection / loud cap.
- Ownership routing of `SimCommand.Target` across multiple controlled entities.
- Rebindable bindings / input contexts / action-map assets.
- `CameraInput` unification (camera stays a client/view concern).
- `[InlineArray]` fallback (4 named floats + switch indexer) if ILC objects — not expected.
