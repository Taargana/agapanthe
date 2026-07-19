# P3-M3 — Physics v1 (deterministic linear rigid bodies)

> Status: **approved** (2026-07-19, session 16). Supersedes nothing; first physics milestone.
> Reference foundations: quantised origin + camera-relative (P2-M3/M4), per-frame bounds (P3-M1),
> scheduler + lifecycle (P3-M2). This milestone makes `ShadowFit.UpstreamExtent` **bite** — it is
> derived from the caster list (P3-M2 D3.b) but has never been exercised by entities that move.

## 1. Goal & scope

A **deterministic rigid-body sandbox**: entities fall under gravity, bounce on a ground plane, and
(W2) collide with one another and pile up — at scale, camera-relative-correct at 10 000 km, 0
alloc/frame on the hot path, **reproducible run-to-run** on the same machine/build, NativeAOT-clean.

**In scope (v1):** linear rigid bodies (no rotation), gravity, semi-implicit Euler at a **fixed**
timestep, ground half-space collision with restitution, sphere↔sphere collision with impulse
resolution + positional correction, uniform-grid broadphase (0-alloc), deterministic resolution
order.

**Out of scope → [BACKLOG.md](../BACKLOG.md) §4:** angular dynamics (torque/inertia/friction),
persistent contacts / warm-starting, wall-clock accumulator + render interpolation, non-sphere
colliders, sleeping/islands, continuous collision, a dedicated `Agapanthe.Physics` project.

## 2. Determinism model (load-bearing)

The bit-identical captures rely on being **deterministic by frame count**, not by wall-clock dt:
`BenchSpinSystem` steps a fixed `0.02f`/tick and ignores `TickContext.DeltaSeconds`. Physics MUST
do the same — step a **fixed dt constant** (default `1/60 s`), one substep per tick — or the
captures stop being reproducible. A wall-clock accumulator (variable substep count) is deferred.

Two determinism requirements:
1. **Fixed dt** per tick (ignore `DeltaSeconds`).
2. **Stable resolution order.** Contact pairs are resolved in an order sorted by `(GlobalId, GlobalId)`,
   never Arch chunk order (Arch iteration is not insertion-stable — the same lesson as the SortKey
   tie-break, P3-M1). Otherwise two runs diverge on the first multi-contact frame.

Captures of a physics scene will **not** be bit-identical to the static baseline (things move); the
gate is **run-to-run** reproducibility: two headless runs of the same physics scene → same SHA.
Cross-platform determinism is a non-goal (captures were always Windows-only).

## 3. Components (`Agapanthe.World/Components.cs`, internal, `Sequential`)

```csharp
internal struct Velocity   { public Vector3 Linear; }                 // m/s, float (position stays Double3)
internal struct RigidBody  { public float InverseMass; public float Restitution; public float Radius; }
```

- `InverseMass == 0` ⇒ immovable (static/kinematic): integration and impulses leave it fixed.
- `Radius` is the **collision** radius in world metres, independent of the render `Bounds` sphere.
- Position stays the existing `WorldPosition` (Double3): `pos += (Double3)(Linear * dt)` keeps far-out
  precision. Velocity in float is enough (m/s magnitudes never need double).

A physics body is a **drawable created with these two extra components** — never added later (an
add-component is an archetype move: alloc + churn). Non-physics drawables keep their exact archetype,
so every existing capture stays byte-identical.

## 4. World API (`GameWorld`)

```csharp
public readonly struct PhysicsSettings          // public param struct, GPU-free
{ public Vector3 Gravity; public float GroundY; public float FixedDt; }   // (0,-9.81,0), groundY, 1/60

public EntityRef SpawnBody(in ImportedEntitySpec spec, Vector3 velocity,
                           float inverseMass, float restitution, float radius);   // IMMEDIATE, like SpawnImported
public void StepPhysics(in PhysicsSettings settings);   // integrate → ground → bodies, in Simulation stage
```

`StepPhysics` runs, in order, over the query `WithAll<GlobalId, WorldPosition, Velocity, RigidBody>`:
1. **IntegrateBodies** — `v += g·dt` (skip if `InverseMass==0`); `pos += (Double3)(v·dt)`. Chunk-iterated,
   0 alloc, following the `AnimateDrawables<T>` in-place pattern.
2. **CollideGround** — half-space `y = GroundY`: if `pos.Y − Radius < GroundY`, push out and reflect the
   normal velocity component scaled by `Restitution` (clamp tiny bounces to rest to avoid jitter).
3. **CollideBodies** (W2) — uniform-grid broadphase → sphere↔sphere narrowphase → impulse + positional
   correction (Baumgarte/slop), pairs resolved in `(GlobalId,GlobalId)` order.

Broadphase grid: cell size ≈ 2× the largest radius, integer-hashed cell → reused per-cell bucket
arrays (grown, never re-alloc'd per frame). Reused scratch lives on the World like `_walkStack` /
`_destroyScratch`. The camera-relative narrowing is irrelevant here — collision is in world-double
space, then only positions are written back.

## 5. Engine wiring (`Agapanthe.Engine`)

A `PhysicsSystem : ISystem` registered in `Stage.Simulation` (before `PostSimulation` propagate/
aggregate, which then see the new positions). It holds a `PhysicsSettings` and calls
`world.StepPhysics(in settings)`. Registered by the application (Sandbox), like `BenchSpinSystem` —
**not** by `FrameOrchestrator.CreateDefault` (physics is opt-in; the default frame stays physics-free
so every non-physics capture is unchanged).

## 6. Sandbox (`samples/Sandbox`)

- `AGAPANTHE_SCENE=drop:N` — spawn N copies of the model as a loose cloud above the ground (jittered
  deterministically by index), each a body (`SpawnBody`) with zero initial velocity, unit mass,
  restitution ≈ 0.3, radius from the model bounds. Reuses the existing ground plane
  (`BuildGroundModel`) as the collision plane (`GroundY` = its top Y).
- `AGAPANTHE_PHYSICS=1` — register the `PhysicsSystem`. Without it, `drop:N` is just a static cloud.
- Prove `UpstreamExtent` bites: with moving casters the two-pass shadow cull runs a non-trivial
  `casterBounds`; log `eyeDistance` as P3-M2 did.

## 7. Testing (TDD)

Unit (no GPU, `Agapanthe.Tests`):
- **Integration**: one body, no ground → after k ticks, position matches closed-form `½g t²` within ε;
  `InverseMass==0` body never moves.
- **Ground bounce**: a body dropped onto the plane reverses normal velocity × restitution; comes to
  rest (no infinite jitter); never tunnels below `GroundY − Radius`.
- **Sphere-sphere** (W2): two bodies on a collision course separate; overlapping bodies are pushed
  apart (positional correction); momentum sign correct.
- **Determinism**: same initial scene stepped twice → identical final `WorldPosition`s; a 3-body
  mutual-contact frame resolves order-independently of Arch iteration (assert via `(GlobalId)` order).
- **Zero-alloc**: `StepPhysics` in a churn-style loop allocates 0 bytes (GC delta) after warm-up.
- **AOT smoke**: extend `AotRootingSmoke` to spawn a body and step physics (roots the query + structs).

Integration (headless Sandbox):
- `AGAPANTHE_MAX_FRAMES=N AGAPANTHE_SCENE=drop:200 AGAPANTHE_PHYSICS=1 AGAPANTHE_CAPTURE=…` → 0
  validation, 0 leak; **two runs → same SHA**.
- Release+AOT `drop:1000` → 0 alloc/frame, AOT PASS.

## 8. Gates (exit criteria)

`dotnet test` green (new physics tests) · 0 warning · headless 0 validation / 0 leak · 0 alloc/frame
at bench · **2 runs same SHA** · NativeAOT PASS · double audit (`csharp-lowlevel` + `engine-architect`)
PASS · **human visual verdict** (a cloud that falls, bounces, piles, and rests) before `done`.

## 9. Decision log

- **Fixed dt, one substep/tick** (not a wall-clock accumulator): the only way to keep captures
  reproducible given the frame-count determinism model. Interpolation → backlog.
- **Linear-only** rigid bodies: rotation needs inertia tensors + angular impulses — a milestone of its
  own. Spheres without spin are acceptable for v1.
- **Physics logic in `GameWorld`** (not a new project): reuses the `internal` components without
  exposing the ECS, mirrors Propagate/Aggregate/Collect. Revisit (dedicated project) when it grows —
  flagged for the `engine-architect` audit.
- **Bodies created with physics components** (not added later): avoids archetype churn and keeps
  non-physics captures byte-identical.
- **Velocity float, position double**: velocity magnitudes never need double; position must stay double
  for far-from-origin precision.
- **`SpawnBody` is IMMEDIATE** (mirrors `SpawnImported`, a load-time seam), NOT deferred like
  `Spawn`/`SpawnDeferred`. There is deliberately **no** runtime deferred body spawn
  (`SpawnBodyDeferred` / `CommandKind.SpawnBody`) in v1: the drop scene fills the world at load. Calling
  `SpawnBody` from inside a running system would create an archetype mid-query — the corruption the
  deferred lifecycle exists to prevent. Runtime body spawning (projectiles) → [BACKLOG.md](../BACKLOG.md)
  §4. Naming caveat: `SpawnImported`/`SpawnBody` are immediate, `Spawn`/`SpawnDeferred` are deferred — the
  name does not carry the timing (audit finding, both reviewers).
- **Contact-pair sort key packs `GlobalId` in 32 bits** (`(minGid << 32) | (uint)maxGid`): deterministic
  while `GlobalId` is a dense per-run counter (< 2³²). When streaming/serialization makes ids
  process-unique (sparse/high-bit-tagged), this packing must be revisited — same family as the 16-bit
  mesh/material ceiling → [BACKLOG.md](../BACKLOG.md) §4.
