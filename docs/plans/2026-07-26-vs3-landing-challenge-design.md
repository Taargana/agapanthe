# VS-3 — Minimal gameplay glue: planetary landing challenge — design

> Third milestone of the **Vertical Slice** ([backlog §4ter](../BACKLOG.md)). Anchor decisions (S21): integration
> proof · planetary/spatial anchor · Windows first. Follows **[VS-1 world serialization](2026-07-24-vs1-world-serialization-design.md)**
> and **[VS-2 runtime spawn + Newtonian gravity](2026-07-25-vs2-spawn-runtime-newtonian-gravity-design.md)**.
> Status: **design approved (human, plan mode); revised v2 after scored review** — pending human spec review.

## Summary

VS-1 (serialization) and VS-2 (runtime spawn + Newtonian gravity) exist as capabilities but have never been *tied
together under a player intent*. VS-3 delivers that glue — the "minimal gameplay" piece of the vertical slice — as a
**planetary landing challenge**: the player flies above a planet, positions over a target zone, and drops probes
(radial drop below the camera); each probe falls under VS-2's radial gravity and lands on the surface. The rule:
land **N** probes in the target zone within a budget of **M** shots, else fail. `F5` quicksaves the world; relaunching
with `AGAPANTHE_LOAD` (VS-1) restores the scene and resumes the challenge.

The design is deliberately thin (no new render path — in-view HUD is VS-4) and rests on a **key synergy**: the
challenge state is **reconstructible from the world at load boundaries**. `shots ← rigid-body count` (probes are never
despawned) and `landed ← probes on the surface inside the zone` are re-derived from the reloaded world; the
target/N/M are deterministic scene constants. So relaunch-load resumes the challenge **without adding a single byte to
the VS-1 snapshot**.

The result proves the whole vertical: **player input → runtime spawn → physics integration → state rule → save/resume**,
under a coherent planetary anchor, with the query and rule unit-testable GPU-free.

## Context

The engine seams VS-3 builds on (verified against the code):
- **Systems/stages** (`Agapanthe.Engine`): `Stage { Input, Simulation, PostSimulation, Render }`; `Stage.Input` is
  empty engine-side (app territory). `ISystem.Execute(in TickContext)` where `TickContext` carries only
  `DeltaSeconds` + `FrameIndex` — **no input**. A gameplay system closes over external mutable state, exactly like
  VS-2's `ProbeDropSystem`. `FlushStructuralChanges` runs after every stage, so a deferred spawn enqueued in
  `Stage.Input` is materialised before `Simulation`.
- **Input**: all handled in `EngineWindow` callbacks (`KeyPressed` edge events + `Updated` polling → `FreeCameraController`).
  Nothing routes through an `ISystem` today except VS-2's out-of-band `Key.B → probeDropper.DropOne()`. No engine-owned
  input abstraction exists (`Key` is `Silk.NET.Input.Key`).
- **On-screen feedback without a HUD**: `EngineWindow.Title { get; set; }` is the cheapest channel and is already used
  for the FPS/draws debug line (throttled ~0.25 s). No in-view text rendering exists (that is VS-4). `Log` for events.
- **Serialization (VS-1)**: `GameWorld.Save(Stream)` (calls `FlushStructuralChanges()` first) / `Load(Stream)`; `Load`
  requires a **fresh empty world** (throws otherwise) — no in-process merge. The snapshot captures entities + present
  components (incl. `Velocity`) + `_nextGlobalId`; it does **not** capture physics settings, camera, or gameplay state.
  Sandbox triggers via `AGAPANTHE_SAVE`/`AGAPANTHE_LOAD` with the Option-1 "reload the same assets first" contract.
- **VS-2 planet-drop hooks**: `SetupPlanetDrop`, `ProbeDropSystem`, `Key.B`, `PhysicsSettings.WithAttractor(C, μ, R)`,
  `SpawnBodyDeferred`, `StepPhysics` (radial gravity + radial ground; a body's normal velocity is rest-clamped, but v1
  has **no friction** — tangential velocity is never damped). Probes are the only rigid bodies in the scene.

No gameplay/state concept exists anywhere in the repo — VS-3's state is net-new but, by design, reconstructible not stored.

## Locked decisions (design interview + review v2)

1. **Loop = aimed landing challenge.** Land N probes in a target zone within ≤ M shots, else fail.
2. **Aiming = radial drop below the camera.** The player positions above the target; `B` drops a probe on the ray
   `C → cameraPosition`; it falls radially under VS-2 gravity. (No camera-forward raycast — chosen for simplicity.)
3. **Save/load = relaunch-only.** `F5` quicksaves the world to a file; reload = relaunch with `AGAPANTHE_LOAD` (the
   VS-1 mechanism). No in-process quickload — VS-1 `Load` requires a fresh world.
4. **State is reconstructible from the world at load boundaries** (key synergy, corrected from "100% derived every
   frame"). Per frame: `landed`/`airborne` are derived from a world query. The **shot count** is an authoritative
   in-session counter (`_shotsIssued`, incremented at each successful drop) **seeded from the world's rigid-body count
   at scene setup and after `Load`** (fresh = 0; reload = the restored body count). The **won/lost latch** is session
   state, **re-evaluated once against the loaded world** on resume. No gameplay bytes enter the VS-1 snapshot.
   **Precondition (must hold for correct resume):** the relaunch uses the **same scene constants** (N, M, target
   placement, μ, radii). These are fixed in the `planet-challenge` launch profile; overriding them between save and
   load invalidates resume (documented, not defended against).
5. **Gate = new scene + GPU-free unit tests + human verdict.** New scene `AGAPANTHE_SCENE=planet-challenge` (leaves
   `planet-drop` and its VS-2 deterministic capture untouched). The **query** and the **rule** are proven by GPU-free
   unit tests (pure logic); the feel by an interactive human verdict. No deterministic headless capture (human input
   is not replayable).

## Architecture

Three layers. The **reusable core** (`Agapanthe.World`, `Graphics`, `Rendering`) stays gameplay-free: the World gains
only a *generic spatial query*. `Agapanthe.Engine` — the orchestration layer that already hosts app-facing `ISystem`s
(e.g. `PhysicsSystem`) — hosts the thin **pure rule** so it is unit-testable (a dedicated gameplay library is out of
scope; placing the rule in the un-testable Sandbox was rejected). The **`LandingChallengeSystem`** (needs Camera +
`window.Title`) lives in the Sandbox.

### 3.1 `Agapanthe.World` — a generic surface/zone query (testable primitive)

A new public, GPU-free, 0-alloc method (no Arch type leaks — it returns a plain int struct), on the pattern of
`StepPhysics`/`AggregateBounds`:

```
public readonly record struct LandingCounts(int Total, int Airborne, int InZone);

public LandingCounts QuerySurfaceContacts(
    Double3 attractorCenter, double surfaceRadius, double surfaceBand,  // "on the surface" test
    Double3 zoneCenter, double zoneRadius);                            // the target disc
```

Iterates `BodyDesc` (the same query the physics uses): `Total++` per body; a body is **Airborne** when
`|p − C| − r > surfaceRadius + surfaceBand` (still falling / bouncing above the ground); it is **on the surface**
otherwise; **InZone** when on the surface **and** `|p − zoneCenter| ≤ zoneRadius`. **No velocity threshold** — on a
frictionless sphere a landed probe may keep sliding tangentially, so "landed" means *touching the ground in the zone*,
not *stationary*. Deliberately generic (a spatial aggregation over rigid bodies), so the World never learns what a
"challenge" is. 0-alloc: three int counters over the existing chunk iteration.

### 3.2 `Agapanthe.Engine` — the rule (pure, latched, testable)

```
public enum LandingStatus { InProgress, Won, Lost }

public readonly struct LandingChallengeRule       // ctor (int targetCount, int shotBudget)
{
    // prev = the latched status so far; shotsIssued = authoritative session count (see 3.3).
    public LandingStatus Evaluate(LandingCounts c, int shotsIssued, LandingStatus prev);
}
```

Pure and **monotonic (latched)**: if `prev` is `Won` or `Lost`, return it unchanged (terminal). Otherwise **Won** when
`c.InZone ≥ targetCount`; **Lost** when the budget is spent (`shotsIssued ≥ shotBudget`) **and** nothing is still
falling (`c.Airborne == 0`) **and** not won; otherwise **InProgress**. The `Airborne == 0` guard is what prevents a
premature Lost while the last shots are mid-flight (and is robust to a frictionless slide, since sliding probes are
"on the surface", not airborne). `Total == 0` → trivially InProgress (no shots issued, budget not spent). No world
access, no mutation — a table-testable `(counts, shotsIssued, prev) → status`.

### 3.3 `samples/Sandbox/Program.cs` — the glue (app-specific)

- **`SetupPlanetChallenge(...)`** (twin of `SetupPlanetDrop`): reuses `SetupPlanetScene` (planet + Sun), the Newtonian
  `PhysicsSettings.WithAttractor(C, μ, R)`, and the probe spec. Adds a **target**: a deterministic surface point
  `T = C + t̂·R` (a tangential offset from the north-pole start anchor, a few hundred metres, tunable) plus a **beacon**
  = a bright emissive sphere (reuses `BuildSphereModel`) floating at `T + n̂·markerHeight`. The beacon is a **static
  drawable** (`SpawnImported`, `castsShadow:false`) — **not a rigid body**, so it is never counted as a shot and it is
  persisted by VS-1 like any drawable. The target zone is the surface disc of radius `zoneRadius` under the beacon.
- **`LandingChallengeSystem : ISystem`** (stage **PostSimulation**, so it reads positions *after* the Simulation-stage
  `PhysicsSystem`). Holds: the challenge params, the `LandingChallengeRule`, `_shotsIssued` (authoritative shot count),
  `_status` (latched), and the last rendered title tuple. Each `Execute`:
  1. `counts = world.QuerySurfaceContacts(...)`.
  2. `_status = rule.Evaluate(counts, _shotsIssued, _status)`.
  3. If `(counts.InZone, _shotsIssued, _status)` changed since the last frame, rebuild `window.Title`
     (`🎯 landed X/N · shots Y/M · <status>`) and, on a status **transition to** Won/Lost, `Log` one line. **In steady
     state nothing changes → no string is built → 0 alloc/frame** (satisfies the gate).
  - `_shotsIssued` is **seeded** at construction from `world.QuerySurfaceContacts(...).Total` (fresh = 0; after `Load` =
    the restored body count), and `_status` is seeded by one `Evaluate` on the loaded world — so a resumed game shows
    the correct title immediately and re-announces its terminal status at most once.
- **Input** (window callbacks — `TickContext` carries no input):
  - `B` → `challenge.TryShoot(camera.Position)`: **guarded by `_shotsIssued`, not the world query** (a just-dropped
    probe is in `_pendingSpawn`, invisible to the query until the next barrier — reading the query here would let two
    presses in one input pass exceed M). If `_status == InProgress` **and** `_shotsIssued < M`: compute
    `n̂ = normalize(camPos − C)`, drop via `SpawnBodyDeferred` at `C + n̂·(surfaceRadius + dropHeight)` with zero
    velocity, then `_shotsIssued++`. Race-free (single owner thread, incremented at enqueue). `TryShoot` reads
    `_status`, which only flips to Won on the *next* PostSimulation — so the player may drop one extra probe right after
    a win; harmless (still `≤ M`, and the Won latch keeps the win). Keep `surfaceBand` **tight** (a landed/settling
    probe reads on-surface, a falling/bouncing one airborne) — too generous and a still-descending probe within the
    band counts as landed a frame or two early; it is a feel parameter.
  - `F5` → `world.Save(file)` in the callback (a safe point between ticks). `Save` calls `FlushStructuralChanges()`
    first, so a probe dropped in the same input pass is materialised and included — no lost shot.
  - Quickload → relaunch `AGAPANTHE_SCENE=planet-challenge AGAPANTHE_LOAD=<file>`: generalise today's load path (which
    forces the `planet` scene) to also accept `planet-challenge` → reload assets + `SetupPlanetChallenge(spawnEntities:false)`
    + `world.Load` → the system re-seeds `_shotsIssued`/`_status` from the restored world. Back-compat: `AGAPANTHE_LOAD`
    alone ⇒ `planet` unchanged.
- **Camera**: reuses `FramePlanetDropCamera` as the start pose, oriented so the beacon is findable; the player flies
  (free camera `MoveSpeed`) to hover over the target. `up = +Y` stays valid (north-pole anchor; curvature negligible
  over a few hundred metres).
- **Title ownership**: in challenge mode the system owns `window.Title`; the existing debug FPS/draws line is ceded (or
  the challenge status is prepended) so the two do not fight and the 0-alloc-when-unchanged property holds.

### 3.4 Frame-order & save/load correctness

The barrier runs at the end of every stage. `B` enqueues the drop from the Input callback path; it is materialised at
**the next barrier (≤ 1 frame)** and integrated by `StepPhysics` (Simulation). `LandingChallengeSystem` runs in
PostSimulation, so it always reads post-physics positions. `F5`/`Save` (with its built-in flush) and relaunch/`Load`
happen between ticks, never mid-query. On reload the deterministic `SetupPlanetChallenge` recreates the attractor +
target + N/M identically (precondition: same env/profile constants), and the system re-seeds `_shotsIssued` and
`_status` from the restored bodies → the challenge resumes at the same state (terminal status re-announced once).

## Testing / verification

- **World unit tests (GPU-free)** — `QuerySurfaceContacts`: bodies placed via the existing `SpawnBody` + `BodyAt`
  helpers → assert `Total`/`Airborne`/`InZone` for: on-surface in zone, on-surface outside zone, airborne above the
  zone (`Airborne`, not `InZone`), and a body bouncing just above `surfaceBand` (counts as `Airborne`). Deterministic
  (pure read).
- **Engine unit tests (GPU-free)** — `LandingChallengeRule.Evaluate`: a table over `(Total, Airborne, InZone)` ×
  `(shotsIssued, N, M, prev)` covering: InProgress; Won at `InZone ≥ N`; **Lost only when `shotsIssued ≥ M && Airborne
  == 0 && InZone < N`**; the boundary where the last shot is still airborne (`Airborne > 0` → stays InProgress, not
  Lost); the **latch** (prev Won/Lost is returned unchanged even if counts regress — a probe sliding out of the zone
  after a win does not un-win); and `Total == 0` → InProgress.
- **Sandbox integration** — `AGAPANTHE_SCENE=planet-challenge`: build 0 warning (`TreatWarningsAsErrors`); a run shows
  0 validation message / 0 leak; **0 alloc/frame** in steady state (the query is 0-alloc; the title is rebuilt only on
  a `(InZone, shotsIssued, status)` change; a shot is a structural frame, like `SpawnDeferred`).
- **Save/resume** — `F5` mid-challenge → relaunch with `AGAPANTHE_LOAD` (same profile) → the scene + dropped probes
  return and the title shows the **same** `landed/N · shots/M · status` (re-seeded from the world).
- **NativeAOT** — the existing probe already roots `SpawnBodyDeferred`/physics; **unconditionally** add a touch of
  `QuerySurfaceContacts` to `AotRootingSmoke` (a new public chunk-query path — cheap insurance, not "if needed").
- **Human interactive verdict** — play it in Rider (fly, `B` aim/drop, reach N or exhaust M, `F5`+relaunch) → PASS.
- **Project gates** — 0 warning · 0 validation · 0 leak · 0 alloc/frame steady · NativeAOT PASS · **double audit**
  (`csharp-lowlevel` + `engine-architect`) + human verdict.

## Tunables (env vars, thin defaults)

`AGAPANTHE_CHALLENGE_N` (3) · `AGAPANTHE_CHALLENGE_SHOTS` (6) · `AGAPANTHE_ZONE_RADIUS` (~15 m) ·
`AGAPANTHE_SURFACE_BAND` (~ a small multiple of the probe radius, e.g. `3·r` — the "on the surface" tolerance, sized so
a settled/sliding probe reads as landed while a falling/bouncing one reads as airborne) · `AGAPANTHE_TARGET_DIST` /
`_DIR` (target placement) · reused from VS-2: `AGAPANTHE_DROP_HEIGHT`, `_PROBE_RADIUS`, `_PLANET_MU`. A
`planet-challenge` profile in `samples/Sandbox/Properties/launchSettings.json` pins these so a plain relaunch
reproduces the same challenge (resume precondition).

## Out of scope / debts (anti-creep)

- **No in-process quickload** — F9/live reload needs a world reset (VS-1 `Load` wants a fresh world); relaunch-only.
- **No in-view HUD** — feedback via `window.Title` only; the font-atlas + overlay pass is VS-4.
- **No audio** — VS-5 (stretch).
- **No prefabs/pooling**, no multiple targets/levels, no continuous proximity score, no persisted non-derived state.
- **Frictionless landing** (inherited v1): a landed probe may slide tangentially on the sphere and never becomes
  "stationary" — hence "landed = on the surface", not "at rest". A probe could in principle slide out of the zone;
  the **Won latch** makes a win permanent **within a session**, but a not-yet-won probe sliding out could lower
  `InZone` — accepted for the thin milestone (small N/M, low restitution; the player can drop more within budget).
  Two consequences worth naming (audit VS-3-05):
  - **The latch is NOT monotone across a reload.** `_status` is session state, re-derived from the loaded world (no
    gameplay bytes in the VS-1 snapshot — locked decision 4). If the world regressed past the terminal condition
    between save and reload — a probe slid *out of* the zone after a win, or *into* it after a loss — the re-derived
    status can **un-win** (Won→InProgress) or **un-lose** (Lost→Won). Low probability (a zero-velocity radial drop does
    not slide; only inter-probe bounce imparts tangential speed), accepted as the price of "state reconstructible from
    the world". *The permanent alternative — a 1-byte companion `.state` file written by `F5` — is deferred (it would
    add a save-side-channel to a thin milestone).*
  - **Title churn at the zone edge.** A probe balanced on the zone boundary oscillates `InZone` in/out every frame,
    rebuilding the title string each frame. The 0-alloc gate holds (a moving probe is not "steady state"), but it is a
    real interaction of the frictionless debt with the gate. *A future hysteresis on `InZone` (or v1 friction) closes it.*
- **Terminal status shown, not re-logged, on reload** — the `_status` latch is session state, seeded once in the
  system ctor from the loaded world. On resume the **title** shows the reloaded `landed/N · shots/M · status`
  immediately, but the Won/Lost transition **log line is not re-emitted** (the ctor pre-latches `_status`, so the first
  `Execute` sees no transition). The title carries the info; the quieter behaviour is intentional. (Corrected from an
  earlier "re-emits the log line" claim — audit VS-3-05.)
- **Resume assumes identical scene constants** — see locked decision 4. The `planet-challenge` launch profiles now
  **explicitly pin** the resume-affecting constants (`AGAPANTHE_CHALLENGE_N/_SHOTS`, `_ZONE_RADIUS`, `_SURFACE_BAND`,
  `_TARGET_DIST/_DIR`, `_TARGET_MARKER_HEIGHT`, `_PROBE_RADIUS`, `_DROP_HEIGHT`) so save and load agree regardless of
  ambient env (audit A2). The physics **scale** (`AGAPANTHE_PLANET_RADIUS`/`_PLANET_MU`) is left to the deterministic
  code default (not typically set); overriding it on only one of the two runs still invalidates resume — not defended.
- **Inherited VS-2 debt**: no body lifetime / rest-cull — the shot budget bounds probe growth *per challenge*, but a
  sandbox-style long run would still grow unbounded (unchanged from VS-2).
- **Euclidean zone distance** (not great-circle) — exact enough at the zone's metre scale on a 3186 km sphere; noted.

## Rollback point

Clean tree at the VS-2 commit (`09637b5`, pushed). No wave touches a file before that.
