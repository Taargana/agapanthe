# MP-0d — Input → timestamped commands — design

> Fourth and last sub-milestone of **MP-0 (authority foundations)** ([backlog §4quater](../BACKLOG.md), item 4).
> Follows MP-0a (headless split), MP-0b (entity identity), MP-0c (time authority). Anchor decisions (S25):
> server-authoritative multiplayer designed in from now (**NOT lockstep**), topology is a deployment choice,
> ownership is a concept from the start.
>
> **Status: APPROVED — 4.30/5** (independent scored review, round 2; threshold 4.0). Ready for `absolute-work`.
> Round-2 residuals folded in: **R1** `(Vector3)cmd.Vector` — `Double3` has no cast to `Vector3` (deliberate);
> use `cmd.Vector.ToVector3(Double3.Zero)` (a direction, no origin to subtract). **R2** the thread-guard test
> targets a *throwing* guard (`[Conditional("DEBUG")]` that throws `InvalidOperationException`, like
> `GameWorld.AssertOwnerThread`), not a `Debug.Assert` — a failed assert kills the xUnit host
> (`FixedTimestepAccumulator.cs:94-97`). **R3** `IsAlive` is true for a not-yet-flushed deferred spawn but
> `Deref` throws for it — `SetBodyVelocity`'s contract says `IsAlive` covers despawn, not a pending deferred
> spawn (a netcode-replay concern, not the demo). **R4** `SimCommand` / `InputSnapshot` carry explicit
> `[StructLayout(LayoutKind.Sequential)]` (house convention, and this is the future wire format); 8 new public
> types (owned by the 🟠 surface-growth risk).
>
> v1 scored 3.33/5 NEEDS WORK — **every cited premise held** (first round-1 in this project with no false
> citation), but 2 🔴 + 6 🟠: the command's `Vector` was a `Vector3` where the project locked `Double3` for
> world positions (F1); the `Tick` listing lagged `_previousInput` by two ticks (F2); two edge mechanisms with
> no precedence (F3); no threading contract on the queue that is *the* network seam (F4); `ApplyCommand` on a
> despawned target throws through the tick (F5); `SetBodyVelocity` omitted the guards every other `GameWorld`
> method carries (F6); "sorted array or binary heap" — a heap is not stable at equal key, and equal-tick
> commands are the normal case (F7); the `drive` scene as a pinned capture gate was gold-plating (F8); the
> equivalence test re-described from scratch, ignoring the existing file and its ULP rule (F9). All fixed below;
> no re-design — the locked interview decisions, the engine/demo split, and the wave order were all judged sound.

## Summary

Today input mutates the simulation directly. `samples/Sandbox/Program.cs:600` is
`window.KeyPressed += key => { switch (key) { ... } }`; `Key.B` (`:654`, `:657`) calls
`landingChallenge.TryShoot(camera.Position)` or `probeDropper.DropOne()` → `world.SpawnBodyDeferred(...)`
(`Program.cs:2138`), on the GLFW input-pump thread. `EngineWindow` (`Agapanthe.Platform`, one file) exposes raw
`Silk.NET.Input.Key` (`EngineWindow.cs:84` `KeyPressed`, `:144` `IsKeyDown`) — the backlog names this: *"règle
aussi l'absence d'abstraction d'input"* (§4quater item 4, verbatim).

Server-authoritative netcode needs **commands** — stamped, typed, bufferable, replayable units of intent — and a
**deterministic point in the tick** where they are applied. MP-0a created the seam ("the command type + the
queue live in `Agapanthe.Engine`"); MP-0c gave the stamp (`TickContext.TickIndex`) and the accumulator (one
frame = 0..N ticks, which is why translation must be per-tick, not per-frame).

**Engine-necessary (catastrophic to retrofit) is three things:** a stamped/typed/blittable command type carrying
a `Double3` (world positions are `double` since Phase 2), a queue + one deterministic apply-point, and the
discipline that external input reaches the sim only as commands. Everything else — the steerable entity, its
movement model, the scene — is disposable demo proving the three.

**Blast radius:**
- `Agapanthe.Engine`: **8 new public types** — `SimCommand`, `InputSnapshot`, `InputAxes`, `InputMap`,
  `SimCommandQueue`, `InputTranslation` (static), `ButtonTrigger` enum, `SimCommandHandler` delegate — + ~4 new
  `SimulationHost` members. (The count feeds the 🟠 surface-growth risk.) No new project reference — the new
  types use only `System.*`, `Agapanthe.Core.Double3` and `Agapanthe.World.EntityRef`, so `EngineIsHeadlessTests`
  (a deny-list against forbidden assemblies, not a frozen name allowlist — verified) holds.
- `Agapanthe.World`: **one** new public method, `SetBodyVelocity(EntityRef, Vector3)`.
- `samples/Sandbox/Program.cs`: new `AGAPANTHE_SCENE=drive` branch (interactive demo only); `Key.B` handlers →
  `host.Commands.Enqueue`.
- `samples/HeadlessSim/Program.cs`: new `--drive` mode (the deterministic gate).
- `tools/AotComponentProbe/Program.cs`: a command/input smoke.
- Tests: extend `AccumulatorEquivalenceTests.cs`; 3 new GPU-free files.

## Context — what the codebase already provides (verified)

- **`SimulationHost.Tick(float)` is the single per-tick entry point.** `grep '\.Tick('`: the MP-0c accumulator
  (`FixedTimestepAccumulator.cs:109`, `host.Tick(FixedDeltaSeconds)` in the catch-up loop),
  `HeadlessSim/Program.cs:109` (`host.Tick(FixedDt)`), `HeadlessSimulationTests` / `HeadlessSimSnapshotFormatTests`,
  and `Sandbox/Program.cs:772` via `orchestrator.Tick` → `_accumulator.AdvanceFrame(_simulation, dt)`. Putting the
  input phase at the top of `Tick` covers windowed, headless and test drivers with **no accumulator change**.
  (A bare `SystemScheduler` is also ticked in `RenderBarrierTests` — no host, out of scope.)
- **`_scheduler.TickIndex` is the tick *about to run*.** `SystemScheduler.Tick` builds
  `new TickContext(deltaSeconds, _tickIndex)` (`SystemScheduler.cs:103`) then `_tickIndex++` **after** the stages
  (`:116`). Commands stamped for that value are "this tick".
- **`planet-drop` exercises no input under headless capture**, so its hashes cannot move — the argument MP-0c
  used and verified. `AGAPANTHE_MAX_FRAMES` forces `wallClockDt = orchestrator.FixedTickDeltaSeconds`
  (`Program.cs:771`) → exactly one tick per frame; the scene sets no `SampleInput` callback, so the whole input
  phase is skipped and `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 …` still hashes HDR `12638edd` / UI
  `03421357`. (`Key.B` *is* wired in that scene, `Program.cs:657` — but a keypress is a human, hands-on action
  outside the deterministic gate.)
- **The fat-blittable-command idiom is established and quoted verbatim.** `GameWorld.cs:138` `private enum
  CommandKind : byte`; `:149-160` `private struct StructuralCommand { CommandKind Kind; ulong Target; …
  Vector3 Velocity; float InverseMass; … }`; `:147-148` *"One fat value struct rather than a class hierarchy: it
  lives in a reused List, so no per-command allocation and no boxing."* `SimCommand` is the same shape at the
  gameplay layer, public.
- **`EntityRef`** (`EntityRef.cs`) is a `public readonly struct` over one `internal ulong Id` + `internal
  EntityRef(ulong)`. Engine can hold, compare and pass `EntityRef` values (`IEquatable`, blittable) but cannot
  fabricate one — fine: `SimCommand.Target` is filled from a `GameWorld` spawn result.
- **`Velocity`** (`Components.cs:126`) is `internal struct Velocity { … Linear … }` → `SetBodyVelocity` must be a
  `GameWorld` method. `StepPhysics` queries `WithAll<GlobalId, WorldPosition, Velocity, RigidBody>`
  (`GameWorld.Physics.cs:22-23`) and integrates `v += gravity*dt; pos += v*dt` (`:175-178`), so a body whose
  velocity is written before the tick's `StepPhysics` runs moves deterministically.
- **`GameWorld.Deref`** (`GameWorld.cs:1037-1046`) **throws `InvalidOperationException`** for a handle that was
  "never spawned, not yet flushed, or despawned". Every public `GameWorld` method opens with
  `ObjectDisposedException.ThrowIf(_disposed, this); AssertOwnerThread();`. `SetBodyVelocity` follows suit
  (F6); a command targeting a dead entity is the app's `ApplyCommand` to gate (F5, decision 6). `IsAlive`
  returns true for a `_pendingSpawn` (`GameWorld.cs:361`) though `Deref` still throws for it (`:1044-1045`) — a
  netcode-replay edge, not the demo (R3).
- **`PhysicsSettings`** has a zero-attractor uniform path (`Mu == 0`) VS-2 proved byte-identical; `new
  PhysicsSettings(gravity: Vector3.Zero, groundY: <far below>, fixedDt)` gives a body that only integrates its
  own velocity — the `drive` scene's "kinematic" steering with no new physics.
- **`Agapanthe.Core.Double3`** (`Double3.cs`, `[StructLayout(Sequential)]`, three `double` = 24 B) is already in
  Engine's `{Core, World}` closure — `SimCommand.Vector` as `Double3` costs no reference. Its only narrowing
  path is `ToVector3(Double3 origin)` (there is no `explicit operator Vector3` — deliberate, so every narrow
  names its camera-relative origin); `ToVector3(Double3.Zero)` is a plain narrow (R1).
- **`[InlineArray]`** is not used in the project's own source (verified). Its first use here is deliberate: a
  pure layout feature, no reflection, NativeAOT-safe by construction; `AotComponentProbe` will run it (MP-0c F-4
  discipline — the gate line must exercise the type). Fallback if ILC ever objects: four named `float` fields +
  a `switch` indexer.
- **`tests/Agapanthe.Tests/AccumulatorEquivalenceTests.cs` already exists** with the exact profiles this
  milestone needs (`Repeat(3f * Fixed, 20)` vs `Repeat(1f * Fixed, 60)`) and the rule this spec must not forget,
  in its class comment: *"Every profile is built from `FixedDeltaSeconds`, never `n / 60f`: `3f / 60f` and
  `3f * (1f / 60f)` differ by 1 ULP, which makes a '3 ticks' profile yield 2 on its first call."* MP-0d
  **extends** that file.
- **`AotComponentProbe/Program.cs`** already exercises `GameWorld`, serialization and (MP-0c) the accumulator;
  adding a command/input smoke follows the same three-line pattern.

## Locked decisions (interview, session 28)

1. **Scope**: the command mechanism + a **generic `InputSnapshot`** in `Agapanthe.Engine` + a **per-tick,
   engine-driven declarative translation**. Plus a steerable-entity demo.
2. **`InputSnapshot`** blittable, fixed-size, app-indexed: `ulong Held`, `ulong Pressed`, `ulong Released`,
   `InputAxes Axes` (`[InlineArray(4)] struct { float _element0; }`, 4 analog axes). **40 bytes** (8+8+8+16). The
   mouse is a camera/view concern and is not in the snapshot.
3. **Translation runs per tick, driven by the engine**, inside `SimulationHost.Tick`, before `_scheduler.Tick`.
4. **`SimCommand`** = blittable field-union struct, `[StructLayout(Sequential)]`:
   `readonly record struct SimCommand(long TargetTick, byte Kind, EntityRef Target, Double3 Vector, float Scalar, uint Flags)`.
   `Kind` is an **opaque byte** — the app defines its own constants; Engine never interprets it (no
   `SimCommandKind` enum ships). Fields reinterpreted per `Kind`: `Vector` is a world position for a spawn kind,
   a movement direction for a move kind. Size: 8+1(+7 pad)+8+24+4+4 = **56 bytes**.
5. **`OriginatorId` deferred** — `SimCommand` is transient, not a persisted format (unlike MP-0b's snapshot);
   adding peer attribution later is additive, and peer identity has no designed shape yet. Debt.
6. **Translation shape**: a declarative `InputMap` — `BindButton(int bit, byte kind, ButtonTrigger trigger)`
   (`OnPress` | `OnRelease` | `WhileHeld`) and one `BindAxisVector(byte kind, int x, int y, int z)` (the bound
   axes become the `Vector` of a command of that kind, emitted every tick). Engine's `InputTranslation.Emit`
   applies the map. The app provides `SimCommandHandler ApplyCommand` which **validates** (VS-3 budget, target
   liveness) and executes — the server-authoritative split: the client always emits the intent, the apply point
   decides whether to honour it.
7. **Steerable entity (demo)** = a velocity-controlled zero-gravity physics body. New
   `GameWorld.SetBodyVelocity(EntityRef, Vector3)`.
8. **`HeadlessSim` gains `--drive`** — a scripted deterministic `InputSnapshot` sequence steering an entity,
   saved as a snapshot → proves the whole path headless + NativeAOT + JIT==AOT. New pinned MD5. **This is the
   deterministic gate.** The Sandbox `drive` scene is an *interactive demo only* and its captures are **not
   pinned** (F8 — one fewer forever-maintained hash, one fewer "byte-repeatable premise" risk).
9. **`Key.B`** (both planet scenes) → direct `host.Commands.Enqueue` from the frame callback, stamped
   `host.TickIndex` (already the next tick's index from a frame callback — no `+1`, F11). It carries
   `camera.Position` (a `Double3`), client context the declarative map cannot supply. Still a command.
10. **`ProbeDropSystem`'s scripted cadence stays a direct `SpawnBodyDeferred`** — scripted content, not input;
    keeps `planet-drop` captures byte-identical.

## Architecture

### `Agapanthe.Engine` — new types

```csharp
namespace Agapanthe.Engine;
using Agapanthe.Core;      // Double3
using Agapanthe.World;     // EntityRef

/// A stamped unit of intent. Blittable; Kind opaque to Engine. Fields reinterpreted per Kind — the
/// GameWorld.StructuralCommand idiom, at the gameplay layer, public. 56 bytes.
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public readonly record struct SimCommand(
    long TargetTick, byte Kind, EntityRef Target, Double3 Vector, float Scalar, uint Flags);

/// One tick of input, produced by the app's SampleInput callback. Blittable, fixed size (40 B), app-indexed.
/// Pressed/Released are edges: the callback merges EngineWindow.KeyPressed events into a pending mask between
/// calls, returns it, then clears it — so one physical press yields exactly one command even on a catch-up
/// frame that runs N ticks (SampleInput is invoked once per tick).
[System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
public struct InputSnapshot { public ulong Held; public ulong Pressed; public ulong Released; public InputAxes Axes; }

[System.Runtime.CompilerServices.InlineArray(4)]
public struct InputAxes { private float _element0; }

public enum ButtonTrigger : byte { OnPress, OnRelease, WhileHeld }

/// Declarative input → command mapping. Fixed capacity (32 button bindings + 1 axis binding), 0-alloc to read.
public sealed class InputMap
{
    public void BindButton(int bit, byte kind, ButtonTrigger trigger);
    public void BindAxisVector(byte kind, int axisX, int axisY, int axisZ);   // Vector = (axes[x],[y],[z]) each tick
}

public delegate void SimCommandHandler(in SimCommand cmd);

/// Ordered by (TargetTick, insertion). Single-threaded — owner thread, like GameWorld: Enqueue/DrainUpTo carry a
/// [Conditional("DEBUG")] guard that THROWS InvalidOperationException off-thread (not Debug.Assert). Pre-grown
/// backing array; 0-alloc after warmup. It grows on overflow (flood protection is a netcode concern — deferred).
public sealed class SimCommandQueue
{
    public int Count { get; }
    public void Enqueue(in SimCommand cmd);     // insertion sort: shift while existing.TargetTick > cmd.TargetTick (strict >, keeps FIFO at equal tick)
    /// Applies every queued command with TargetTick <= tick, front to back, via handler. Returns how many ran.
    /// A command already past its tick runs now — never dropped (deterministic).
    public int DrainUpTo(long tick, SimCommandHandler handler);
}

/// The per-tick declarative translation. Static, stateless, 0-alloc. No previous-snapshot parameter: edges are
/// carried by InputSnapshot.Pressed/Released (only those survive a press+release contained within one frame).
public static class InputTranslation
{
    public static void Emit(in InputSnapshot cur, InputMap map, SimCommandQueue queue, long tick);
}
```

### `SimulationHost` (modified)

New members (`_previousInput` / `PreviousInput` from v1 are **removed** — no consumer, and their only use, `prev`
on `Emit`, is gone):

```csharp
public System.Func<InputSnapshot>? SampleInput { get; set; }   // app callback, invoked once per Tick
public InputMap? InputMap { get; set; }
public SimCommandHandler? ApplyCommand { get; set; }
public SimCommandQueue Commands { get; }                        // composed; app may Enqueue directly
public InputSnapshot CurrentInput { get; private set; }         // diagnostic; a Stage.Input system may read it

private static readonly SimCommandHandler DiscardHandler = static (in SimCommand _) =>
    Debug.Assert(false, "A SimCommand was queued but SimulationHost.ApplyCommand is not set.");
```

`Tick(float deltaSeconds)` gains a phase **before** `_scheduler.Tick`:

```csharp
public void Tick(float deltaSeconds)
{
    if (SampleInput is not null)
    {
        CurrentInput = SampleInput();
        if (InputMap is not null)
        {
            InputTranslation.Emit(in CurrentInput, InputMap, Commands, _scheduler.TickIndex);
        }
    }

    Commands.DrainUpTo(_scheduler.TickIndex, ApplyCommand ?? DiscardHandler);   // DiscardHandler is a static field → no per-tick alloc

    _dt = deltaSeconds;
    _scheduler.Tick(deltaSeconds);
    _frameTickCount++;
}
```

- The drain is **before any stage**, so `ApplyCommand` (e.g. `SetBodyVelocity`) mutates a component while no
  query iterates, and `Stage.Simulation`'s `StepPhysics` sees the new velocity the same tick.
- `ApplyCommand` null + a queued command: `DiscardHandler` fires the `Debug.Assert` per command (loud in Debug),
  and the queue is emptied (no Release leak).
- `SimulationHost.CreateDefault` registers no command/input system — `Stage.Input` stays the app's.

### `Agapanthe.World` (modified)

```csharp
/// Sets a physics body's linear velocity directly (MP-0d). For an externally-driven ("kinematic") body: a
/// command handler writes the velocity before the tick's StepPhysics integrates it. Throws for a dead handle,
/// like every GameWorld mutator — the caller (ApplyCommand) gates on IsAlive for buffered/replayed commands.
/// IsAlive covers a despawn; it does NOT cover a deferred spawn still pending the structural barrier (Deref
/// throws for that state too) — a netcode-replay concern, not the demo (MoveIntent targets a fixed body).
public void SetBodyVelocity(EntityRef entity, System.Numerics.Vector3 linear)
{
    ObjectDisposedException.ThrowIf(_disposed, this);
    AssertOwnerThread();
    Deref(entity).Set(new Velocity { Linear = linear });
}
```

### Sandbox — `AGAPANTHE_SCENE=drive` (interactive demo, not a gate)

- Fixed camera framing a small play volume (no `FreeCameraController` drive).
- One steerable body via `SpawnBody`; scene `PhysicsSettings` = `Vector3.Zero` gravity, no attractor, ground
  far below.
- `host.InputMap`: `BindAxisVector(kind: MoveIntent, 0, 1, 2)` and `BindButton(bit: Brake, kind: Brake,
  OnPress)` — the button binding exercises `OnPress` + the `Pressed` bits deterministically.
- `host.ApplyCommand`: `MoveIntent` → `if (world.IsAlive(cmd.Target)) world.SetBodyVelocity(cmd.Target,
  cmd.Vector.ToVector3(Double3.Zero) * MoveSpeed)` — `Vector` is a *direction*, so `ToVector3(Double3.Zero)` is a
  plain narrow, no origin to subtract; `Brake` → `SetBodyVelocity(cmd.Target, Vector3.Zero)`.
- `host.SampleInput`: interactive — reads WASD/Space/C from `EngineWindow`, writes axes 0/1/2 as −1/0/+1, sets
  the Brake bit from a pending-edge mask fed by `KeyPressed`.
- `Key.B` in planet-challenge / planet-drop: the `KeyPressed` handler builds
  `new SimCommand(host.TickIndex, kind: SpawnProbe, Target: default, Vector: camera.Position, 0f, 0)` and
  `host.Commands.Enqueue`. `ApplyCommand` for `SpawnProbe`: planet-challenge runs the existing budget check then
  the aimed radial drop; planet-drop calls `probeDropper.DropOne()` (position ignored — the spawner owns its
  spiral). Same kind, scene-appropriate handler.

### `HeadlessSim` — `--drive` (the deterministic gate)

`--drive` builds a one-body zero-gravity scene, sets a **scripted** `SampleInput` (a fixed per-tick axis script
keyed on `host.TickIndex` — e.g. axis0 = +1 for ticks [0,60), −1 for [60,120), Brake pressed at tick 120), an
`InputMap`, and an `ApplyCommand` calling `SetBodyVelocity`; runs `--ticks` ticks; `--save` writes the snapshot.
Proves input → command → mutation with no GPU, and JIT == AOT.

## Testing strategy

| Test | Kind | What breaks without it |
|---|---|---|
| `SimCommand` is 56 B, `InputSnapshot` is 40 B, both blittable (`Unsafe.SizeOf`, no managed refs) | GPU-free unit | a field change silently breaks the future wire format |
| `SimCommandQueue.DrainUpTo` runs `TargetTick <= tick` only, front-to-back; **equal-tick commands drain in enqueue order** (enqueue B@5, A@5, C@3 → drain order C, B, A); a past-tick command runs now | GPU-free unit | commands apply on the wrong tick / wrong order → non-determinism between drivers |
| `SimCommandQueue` is 0-alloc after warmup (enqueue+drain loop, mixed ticks) | GPU-free alloc gate | per-tick allocation |
| `SimCommandQueue.Enqueue`/`DrainUpTo` **throw `InvalidOperationException` on a foreign thread** (a `[Conditional("DEBUG")]` guard that *throws*, like `GameWorld.AssertOwnerThread` — **not** a `Debug.Assert`, which would kill the xUnit host, `FixedTimestepAccumulator.cs:94-97`; `DiscardHandler` right above uses a real `Debug.Assert` — do not copy that form here) | GPU-free unit (Debug) | the network seam has no thread contract — the first receive-thread Enqueue is silently wrong |
| `InputTranslation.Emit`: `Pressed` bit + `OnPress` binding → one command; `Released` + `OnRelease` → one on the falling edge; `Held` + `WhileHeld` → one per call; axis binding → one command per call with `Vector = (axes[x], axes[y], axes[z])`; unbound bits → nothing | GPU-free unit | the map is misread |
| `SimulationHost.Tick` full phase: synthetic `SampleInput` script + map + `ApplyCommand` → deterministic `SetBodyVelocity` calls → deterministic final position; `SampleInput` null → phase skipped, `Commands.Count == 0` after N ticks | GPU-free unit | the wiring is wrong; regression against "no input = no-op" (capture stability) |
| **Extend `AccumulatorEquivalenceTests`** (do NOT write `n/60f` — build every profile from `accumulator.FixedDeltaSeconds`, per that file's class comment): the same scripted input through `20 × Advance(3·fixed)` vs `60 × Advance(1·fixed)` → **identical command count** (primary, integer) **and** identical final position (secondary) | GPU-free unit | translation/apply depends on frame chunking — the MP-0c property, re-proven for input |
| Companion, non-vacuity: `30 × Advance(1·fixed)` ends in a **different** position from the 60-tick runs | GPU-free unit | the equality above passes by construction |
| Edge-bit: a `Pressed` bit set once by a scripted `SampleInput` that then clears it → exactly one command across a 3-tick catch-up frame | GPU-free unit | one key press spawns N probes on a slow frame |
| `Key.B` direct enqueue: `host.Commands.Enqueue` from outside a tick, stamped `host.TickIndex`, applies once at that tick with `camera.Position` in `Vector` | GPU-free unit (host-level) | the discrete path regresses |
| `ApplyCommand` targeting a despawned entity → the demo handler no-ops (`IsAlive` gate), the tick continues | GPU-free unit | a buffered command against a dead target throws through `SimulationHost.Tick` |
| `AotComponentProbe`: build a queue, translate a snapshot through a map, drain+apply, touch `InputAxes` | AOT publish + run | `SimCommandQueue` / `InputAxes` (`[InlineArray]`) missing native code under ILC |
| `HeadlessSim --drive` JIT == AOT at a new pinned MD5; default `--ticks 600 --bodies 8` hash `7e8dc68f…` still unchanged | run-level | the headless input path diverges JIT vs AOT, or `--drive` disturbed the default mode |
| Sandbox captures HDR `12638edd` / UI `03421357` **unchanged** (planet-drop wires no input under capture) | run-level | a "no input exercised" scene moved a pixel |

## Waves

**W1 — the Engine mechanism, alone.** `SimCommand`, `InputSnapshot` + `InputAxes`, `InputMap`, `ButtonTrigger`,
`SimCommandQueue`, `InputTranslation`. Blittability + queue (order, 0-alloc, thread throw) + translation tests.
No existing file changes.

**W2 — wire it into `SimulationHost` + the World API.** The `Tick` phase, the new host members, the edge-bit
contract doc, `GameWorld.SetBodyVelocity` (with the standard guards). The full-phase, extended
`AccumulatorEquivalenceTests`, edge-bit, dead-target and `Key.B`-level tests. Existing tests green (the phase is
a no-op when `SampleInput` is null).

**W3 — the demo.** `AGAPANTHE_SCENE=drive` (interactive `SampleInput` + `InputMap` + `ApplyCommand`), `Key.B` →
`Commands.Enqueue` in both planet scenes, `HeadlessSim --drive` (scripted), `AotComponentProbe` smoke.

**W4 — captures + tail.** Confirm `12638edd`/`03421357` unchanged (3 runs), `HeadlessSim --drive` JIT==AOT new
pin + default hash unchanged, `AotComponentProbe` PASS, self-review, double audit (`csharp-lowlevel` +
`engine-architect`), human verdict (steer the entity; `Key.B` still drops a probe), CONVERGE.

## Verification (DoD)

- `dotnet build` 0 warning · `dotnet test` green including every test above.
- `12638edd` / `03421357` unchanged (3 runs). **The `drive` scene is not pinned** (F8).
- `HeadlessSim --drive` JIT == AOT at a new full-length pinned MD5, recorded in `CLAUDE.md` + `AVANCEMENT.md`;
  `HeadlessSim` default `--ticks 600 --bodies 8` hash `7e8dc68f…` still unchanged.
- `AotComponentProbe` PASS (with the new smoke). AOT publish: `PATH` prefixed with the `vswhere.exe` folder.
- Sandbox headless: 0 validation, 0 leak. 0 alloc/frame steady state (queue + translation).
- `EngineIsHeadlessTests` green (closure `{Core, World}` unchanged).
- Double audit PASS + human verdict, then CONVERGE. **Commit on explicit request only.**

## Risks

- 🟠 **`SimulationHost` surface growth.** Four new members + a composed queue is the largest single addition to a
  type MP-0a scoped to "owns nothing". It is the right home (a dedicated server binds the network here). The
  audit should confirm; the alternative is a standalone `SimulationInput` type the host composes, the way
  `FixedTimestepAccumulator` is composed by `FrameOrchestrator`.
- 🟠 **`[InlineArray]` first use in-project.** AOT-safe by construction but unproven here until `AotComponentProbe`
  runs it. Fallback: four named `float` fields + a `switch` indexer.
- 🟡 **`SetBodyVelocity` under zero gravity** — the uniform `Mu == 0` path is proven byte-identical by VS-2; zero
  gravity is just `Gravity = Vector3.Zero`; the body integrates only its own velocity.
- 🟡 **Axis command emitted every tick even when zero** — `MoveIntent{Vector=0}` → `SetBodyVelocity(…, 0)` → the
  body stops. Correct and deterministic; one command/tick, absorbed by the 0-alloc queue.
- 🟡 **`Pressed`/`Released` require the app's `SampleInput` to maintain the pending-edge mask** — documented as a
  contract, pinned by the edge-bit test, but Engine cannot enforce it (a careless callback double-fires).
- 🟡 **`SimCommandQueue` unbounded growth** on a stuck consumer or a future network flood. Growth is silent for
  MP-0d (matches `GameWorld._commands`); a loud cap is a netcode concern, deferred.

## Out of scope (explicit)

`SimCommand.OriginatorId` / peer identity · the command wire format (send/receive) — `SimCommand` is blittable
now, a test asserts it · a command replay/log harness · ownership routing of `SimCommand.Target` across multiple
controlled entities · a chase camera / pinned capture for `drive` · rebindable bindings / input contexts /
action-map assets · `CameraInput` unification (camera stays a client/view concern in `Agapanthe.Rendering`) ·
`SimCommandQueue` flood protection · anything network.

## Decision log

| # | Decision | Why |
|---|---|---|
| Scope | mechanism + generic `InputSnapshot` + engine-driven per-tick declarative translation + a steerable demo | continuous input needs a sim consumer to be proven; the snapshot generic so every app shares the shape |
| `SimCommand.Vector` | `Double3` | world positions are `double` since Phase 2; a command that can't carry one is a dead-end to retrofit (R1/F1) |
| No `prev` on `Emit` | edges live in `InputSnapshot.Pressed/Released` | only those survive a press+release within one frame; removes a two-tick-lag bug and two tests (F2/F3) |
| Translation in `SimulationHost.Tick` | not the accumulator, not a system | `Tick` is the one per-tick entry point every driver shares; the accumulator stays MP-0c-minimal |
| `Key.B` → direct `Enqueue` | not through the map | it needs `camera.Position`, client context the declarative map can't provide; still a stamped command |
| `drive` scene not pinned | `HeadlessSim --drive` is the gate | one fewer forever-hash, one fewer "byte-repeatable" premise risk (F8) |
| `OriginatorId` deferred | transient struct, additive later | unlike MP-0b's snapshot it is not a persisted format; peer identity has no designed shape |

## Next step

`absolute-work` — waves W1 (Engine mechanism, isolated) → W2 (wire into `SimulationHost` + `GameWorld
.SetBodyVelocity`) → W3 (demo: `drive` scene, `Key.B`, `HeadlessSim --drive`, `AotComponentProbe`) → W4
(captures + double audit + tail), human greenlight between waves, commit on explicit request only.
