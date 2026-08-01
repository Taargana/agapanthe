# VS-2 — Runtime body spawn + Newtonian gravity — design

> Second milestone of the **Vertical Slice** ([backlog §4ter](../BACKLOG.md)). Anchor decisions (S21): integration
> proof · planetary/spatial anchor · Windows first. Follows **[VS-1 world serialization](2026-07-24-vs1-world-serialization-design.md)**.
> Status: **implemented + double-audited** (session 23). Spec reviewed Approved 4.4/5 (engine-architect); 4 findings
> folded in pre-code. **Double audit PASS-with-concerns** (`csharp-lowlevel` PASS-with-concerns · `engine-architect`
> 4.5/5): both independently caught the same 🟠 (unguarded `r2≈0` singularity in the Newtonian integration) — fixed,
> plus 🟡 m1/m3/m4 applied and m2 (no body lifetime) recorded below. **Pending: human visual verdict** (VS-2-07).

## Summary

VS-2 closes the P3-M3 debt: today `SpawnBody` is **immediate** (a load-time seam, like `SpawnImported`), so a body
cannot be created while the simulation is running — a system that spawned one mid-tick would mutate the archetype
storage under `StepPhysics`'s own chunk iteration. VS-2 adds `SpawnBodyDeferred`, the runtime, barrier-applied twin of
the existing `SpawnDeferred`, so projectiles / debris / a **dropped probe** can appear in a live simulation.

Because a "probe dropped toward a planet" is the natural integration demo and the flat `−Y` gravity of physics v1 does
not fit it, VS-2 also adds a **minimal Newtonian point-gravity**: a single optional attractor (`μ = G·M`, radial
inverse-square acceleration) plus a **radial ground half-space** — the exact analogue of the existing flat
`groundY` half-space, but on a sphere. Both live in `PhysicsSettings`; when the attractor is absent (`μ = 0`) the
existing uniform-gravity path is byte-for-byte unchanged. The demo: stand near a real-scale (½) planet's surface, drop
probes at runtime, watch them fall radially, land on the surface, bounce, and settle — driven by a deterministic
spawner (reproducible headless capture) and, for the hands-on verdict, a keypress.

The result proves the whole vertical: **runtime spawn → structural barrier → physics integration**, under a coherent
planetary anchor, deterministic and NativeAOT-pure.

## Context

`SpawnBodyDeferred` is the last missing lifecycle verb. The deferred lifecycle already exists (P3-M2 D2): the World
owns a reused command queue (`_commands`, `_pendingSpawn`, `_pendingDead`), and `FlushStructuralChanges()` applies it
at the end-of-stage barrier the scheduler runs — the one place where no query is iterating. `SpawnDeferred` (a drawable)
and `Spawn` (a hierarchy node) already ride that queue; `SpawnBody` is the only spawn still stuck on the immediate,
load-time path. Adding it is mechanical: a new `CommandKind`, four fields on the fat `StructuralCommand`, a
`MaterialiseBody` shared between the immediate and deferred paths, and one `case` in the flush.

The physics is v1 (P3-M3): deterministic linear rigid bodies, semi-implicit (symplectic) Euler at a fixed step,
uniform gravity, a flat ground half-space, sphere-sphere collision on a uniform-grid broadphase, contacts resolved in
GlobalId order — zero-alloc in steady state. Newtonian point-gravity slots into the same fixed-step, deterministic
integration; it only changes *how gravity is computed* and *what shape the ground is*.

## Locked decisions (design interview)

1. **Keep the immediate `SpawnBody`; add the deferred `SpawnBodyDeferred`.** Parity with the existing
   `SpawnImported` (immediate, load-time) / `SpawnDeferred` (runtime) pair. No deprecation.
2. **The attractor lives in `PhysicsSettings`, not in an ECS component.** One attractor is enough for the demo, and a
   struct field avoids touching the frozen `ComponentRegistry.All` order (R1, append-only) and the VS-1 serialization
   mask. A per-entity `Attractor` component generalises later (multi-planet) but is YAGNI now.
3. **`μ = 0` preserves the existing path exactly.** With no attractor, `StepPhysics` uses uniform gravity + the flat
   `groundY` half-space verbatim — the P3-M3 `drop` scene stays byte-identical, protecting its capture gate.
4. **Radial ground half-space, NOT a giant rigid-body planet.** Making the planet an immovable rigid sphere would
   reuse sphere-sphere collision but pollute the broadphase (cell size `2·R`, `R ≈ 3.19e6 m`) and pointlessly
   gather/integrate/scatter a 3185 km body every step. Instead the planet stays a **pure drawable**, and a radial
   half-space (`|p − C| − r < R`) — the exact analogue of the flat ground — resolves body-vs-surface. Probe-vs-probe
   keeps the existing sphere-sphere path.
5. **Demo scale/framing: real (½) scale, near-surface view.** The camera stands on the planet's surface; the probe
   falls onto the ground (surface = the curving horizon), Sun in the sky. Newtonian gravity fixes the *direction*
   (radial from any point); near-surface framing is what keeps a ~1 m probe visible against a 3185 km planet.
6. **Demo trigger: both.** A deterministic scheduled spawner (reproducible headless capture = the automated gate) AND
   an edge-triggered keypress (the human's hands-on verdict, excluded from the deterministic gate).
7. **Newtonian honesty, not orbital fidelity.** Inverse-square is exact in `double` position; `μ` is a scene tunable
   chosen for a legible fall (surface g ≈ 10 m/s²). No orbits are claimed (see Debts).

## Architecture

### 3.1 World core (`GameWorld.cs` / `GameWorld.Physics.cs`)

`CommandKind` gains a value:

```
enum CommandKind : byte { SpawnNode, SpawnDrawable, SetParent, Despawn, SpawnBody }
```

The fat `StructuralCommand` gains the body payload (flat fields, matching the existing `Local` / `Imported` style):

```
struct StructuralCommand {
    CommandKind Kind;
    ulong Target;
    ulong ParentId;
    LocalTransform Local;         // SpawnNode
    ImportedEntitySpec Imported;  // SpawnDrawable, SpawnBody
    Vector3 Velocity;             // SpawnBody
    float InverseMass, Restitution, Radius;  // SpawnBody
}
```

New public API (twin of `SpawnDeferred`):

```
public EntityRef SpawnBodyDeferred(
    in ImportedEntitySpec spec, Vector3 velocity, float inverseMass, float restitution, float radius)
{
    // ObjectDisposed / owner-thread guards
    var id = _nextGlobalId++;
    _pendingSpawn.Add(id);                    // IsAlive true immediately, before the barrier
    _commands.Add(new StructuralCommand {
        Kind = CommandKind.SpawnBody, Target = id, Imported = spec,
        Velocity = velocity, InverseMass = inverseMass, Restitution = restitution, Radius = radius });
    return new EntityRef(id);
}
```

Refactor: extract `MaterialiseBody(ulong globalId, in ImportedEntitySpec spec, Vector3 velocity, float invMass,
float restitution, float radius)` from the current `SpawnBody` body (the `_world.Create(... Velocity, RigidBody,
InstanceSlot{-1})` + `_live[id]=entity` + `_structuralDirty=true`). The immediate `SpawnBody` calls it with a
freshly bumped id; `FlushStructuralChanges` pass 1 calls it from a new `case CommandKind.SpawnBody`.

The deferred body's `InstanceSlot` is `-1` (unassigned), exactly like `MaterialiseDrawable`; the next structural
rebuild stamps it. `_structuralDirty` is set, forcing that rebuild.

### 3.2 Physics — Newtonian point-gravity + radial ground (`GameWorld.Physics.cs`, `PhysicsSettings.cs`)

`PhysicsSettings` (a `readonly struct` with ctor `(Vector3 gravity, float groundY, float fixedDt)` and a
`Default(groundY)` factory) gains an optional attractor. Because it is a `readonly struct`, the fields are set through
a **new `WithAttractor(Double3 center, double mu, double surfaceRadius)` instance method** that returns a copy with the
attractor filled (and an added ctor overload it delegates to) — never an object initializer:

```
Double3 AttractorCenter;   // world-space centre of the attracting body
double   Mu;               // G·M; 0 disables Newtonian gravity (uniform path stays)
double   SurfaceRadius;    // R; radial ground half-space radius (only when Mu > 0)

// PhysicsSettings.Default(groundY: …).WithAttractor(C, mu, R)  — the planet-drop scene's settings
```

`StepPhysics` branches once on `Mu > 0`:

- **Pass 1 (integrate).** Uniform path (`Mu == 0`): `v += gravity·dt` verbatim. Newtonian path (`Mu > 0`): for a
  movable body, `d = C − p; r2 = d·d; a = μ·d / (r2·√r2); v += a·dt` (computed in `double`, cast to the body's `float`
  velocity), then `p += v·dt`. Gravity weakens with altitude (inverse-square) — physically correct.
- **Pass 3 (ground, applied last).** Uniform path: the existing flat `pos.Y − r < groundY` half-space. Newtonian path:
  radial — `dist = |p − C|`; if `dist − r < R`, push out to `dist = R + r` along `n̂ = (p − C)/dist`, and if the
  normal velocity `v·n̂ < 0`, reflect it (`v −= (1+e)(v·n̂) n̂`). Tangential velocity is untouched (v1 has no friction —
  consistent with the flat scene).
  - **Rest-speed clamp (critical):** the flat path clamps at `restSpeed = 2·|gravity.Y|·dt`. In the Newtonian path the
    effective surface gravity is `μ/R²`, **not** `gravity.Y` (the planet-drop scene sets `Gravity = 0` and relies on
    `μ`). So the radial clamp is `restSpeed = 2·(μ/R²)·dt`; below it the reflected normal speed is zeroed. Using
    `gravity.Y` here would leave `restSpeed = 0` and the probe would micro-bounce forever — the demo would never
    settle.

Broadphase, narrowphase, pair resolution, determinism (fixed dt + GlobalId order): **unchanged**. Probe-vs-probe is
the existing sphere-sphere path; only body-vs-planet is the new radial half-space.

`μ` is a scene tunable (`AGAPANTHE_PLANET_MU`), defaulting to a value giving surface g ≈ 10 m/s² at `R`.

### 3.3 Demo (`samples/Sandbox/Program.cs`, `Agapanthe.Engine` spawner system)

New scene variant `AGAPANTHE_SCENE=planet-drop` (the P3-M8 `planet` scene is untouched, capture protected):

- Planet: ½-real radius (`3.1855e6 m`) drawable sphere at absolute centre `C`; surface point `P_s = C + (0, R, 0)`
  (north pole → local up = `+Y`). Camera near `P_s`, looking toward the horizon with the Sun sphere in frame; lighting
  reuses the P3-M8 Sun-co-located point light.
- `PhysicsSettings` with `AttractorCenter = C`, `Mu`, `SurfaceRadius = R`.
- **Deterministic spawner** — a new `ISystem` (in `Agapanthe.Engine` or the Sandbox) registered in **Stage.Input**:
  every `N` ticks (`AGAPANTHE_DROP_EVERY=N`) it calls `world.SpawnBodyDeferred(probeSpec, v0, invMass, e, r)` for a
  probe at `P_s + n̂·h`. Enqueued during Input → materialised at the end-of-Input barrier → integrated the same frame
  by `StepPhysics` (Simulation). Deterministic cadence + fixed-step physics → **byte-identical headless capture**.
- **Keypress** — an edge-triggered key (e.g. `Key.B`) in the Sandbox loop drops one probe on press. Non-deterministic;
  for the human hands-on verdict only.
- Probe: small sphere (~1–5 m), falls radially, lands, bounces, settles; a pile of probes collide with each other.

### 3.4 Frame-order correctness

The barrier runs at the end of *every* stage. Spawner in **Input** → body exists before **Simulation** → `StepPhysics`
integrates it that frame. The deferred command is applied only at the barrier, where no query iterates — so the spawn
never mutates archetypes under `StepPhysics`'s chunk iteration. That invariant is the entire reason the immediate
`SpawnBody` could not be used from a running system, and the reason VS-2 is needed.

## Testing / verification

- **World unit tests (GPU-free):**
  - `SpawnBodyDeferred` returns a handle that is `IsAlive` immediately (before flush); after `FlushStructuralChanges`
    the body carries the exact `Velocity` / `RigidBody` (invMass, restitution, radius) / `InstanceSlot == -1`, and
    `_structuralDirty` is set.
  - A body spawned via `SpawnBodyDeferred` is **not** seen by `StepPhysics` before the flush and **is** after.
  - Newtonian gravity: a body released above the attractor accelerates toward `C`; angular symmetry (released from
    `+Y` vs `+X` at the same radius reaches the same speed); inverse-square (higher altitude → smaller acceleration).
  - Radial ground: a body driven below the surface is lifted to `R + r`, its normal velocity reflected by restitution;
    tangential velocity preserved.
  - **Radial settling (locks the `restSpeed = 2·(μ/R²)·dt` formula):** a probe released just above the surface with
    `Gravity = 0` and only `μ` set comes to rest on the surface within a bounded number of steps (normal speed reaches
    exactly 0), instead of micro-bouncing forever — the exact regression the reviewer flagged.
  - **Determinism:** two identical spawn+step sequences produce bit-identical positions **within one binary**.
  - **`μ = 0` regression:** existing physics tests stay green; a `drop`-shaped scene steps byte-identically.
- **AOT probe:** extend `AotRootingSmoke` / `AotComponentProbe` to enqueue `SpawnBodyDeferred`, flush it, and run a
  Newtonian `StepPhysics` — rooting the `CommandKind.SpawnBody` → `MaterialiseBody` path and the radial integration
  under ILC.
- **Sandbox integration:** `AGAPANTHE_SCENE=planet-drop AGAPANTHE_DROP_EVERY=N AGAPANTHE_PHYSICS=1
  AGAPANTHE_MAX_FRAMES=… AGAPANTHE_CAPTURE=out.ppm` → a reproducible headless capture of probes fallen/piled on the
  surface. **Human visual verdict** required before closing.
  - *Byte-identity scope:* the capture gate is **intra-binary** (same build, deterministic cadence + fixed step →
    reproducible). Unlike VS-1's blittable snapshot, the physics uses `double→float` casts whose operation order the
    JIT and ILC may schedule differently, so cross-JIT/AOT byte-identity of the pixels is **not** claimed — only that
    each binary reproduces its own capture and NativeAOT runs the path without error.
- **Project gates:** 0 warning, 0 validation message, 0 leak; 0 alloc/frame in steady state (between spawns — a spawn
  frame is structural, like `SpawnDeferred`); **NativeAOT PASS**; **double audit** (`csharp-lowlevel` +
  `graphics-3d`/`engine-architect`) + human verdict.

## Out of scope / debts (anti-creep)

- **No orbits.** Symplectic Euler drifts at orbital speeds and `Velocity` is `float` (~mm/s at 7.9 km/s) — VS-2 claims
  only infall + bounce. *Debt: double-precision (or attractor-relative) velocity the day real orbits are wanted.*
- **No n-body / mutual attraction.** One fixed attractor; the immovable planet is not itself attracted (no Sun-pull on
  the planet).
- **No friction** (v1 has none — tangential slip on the surface is free and consistent with the flat scene).
- **No non-spherical terrain collision**; the radial half-space is a perfect sphere.
- **Single attractor in `PhysicsSettings`**, not a component — multi-planet / per-entity attractors deferred (YAGNI).
- **Broadphase cell at planetary scale:** a large cloud of probes clustered near one surface point shares ~1 cell
  (narrowphase O(n²)). Fine for the demo's handful of probes; noted.
- **No probe lifetime / rest-cull (audit m2):** the `planet-drop` spawner drops indefinitely and never despawns, so a
  long run grows the entity count (and persistent slots) without bound. Inherent to the "pile of probes" demo; a
  lifetime / at-rest-cull policy is required before the persistent planetary anchor. *Debt, also in `CLAUDE.md`.*
- **No body lifetime / at-rest cull** (audit m2): the demo spawner drops probes forever — entity + persistent-slot count
  grows unbounded over a long run. Inherent to a "pile of probes" demo, but the persistent planetary anchor will need a
  lifetime or rest-cull policy for runtime-spawned bodies. Deferred.

## Rollback point

Clean tree at `3057bb4` (VS-1 committed + pushed). No wave touches a file before that.
