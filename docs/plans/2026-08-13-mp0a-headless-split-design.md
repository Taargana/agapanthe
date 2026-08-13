# MP-0a — Headless split — design

> First sub-milestone of **MP-0 (authority foundations)**, itself the next step of the **engine cap**
> ([backlog §4quater](../BACKLOG.md)). Anchor decisions (S25): the artifact is **the engine** · generalist but
> especially large-scale space sims · **server-authoritative** multiplayer designed in from now · massive/persistent
> targeted with small coop possible, so **topology is a deployment choice, never an architecture choice**.
>
> Status: **APPROVED — 4.15/5** (independent scored review, 2 iterations). v1 scored **2.85/5 "Major Gaps"**. The central
> finding was fatal and is fixed here: v1 claimed `CollectRenderLists` *adds* `InstanceSlot` and therefore churns
> archetypes — it does not, and the parity gate built on that premise would have passed by construction. Two further
> false claims are corrected (`_sceneBounds` has no consumer; `AGAPANTHE_SAVE` writes the *pre-loop* state), and 8
> mechanical findings are folded in. **Root cause of all three: v1 cited doc comments instead of executable code.**
> Every claim in §"The render-stage guard" below now cites an executable line. The v2 review found 6 residuals, all
> 🟡 and all folded in here (a line-off citation, a wrongly attributed test file, `CurrentTick`'s post-increment
> semantics, `RenderContext` construction in tests, the delegated-vs-routed member split, per-wave test assignment).
> **Ready for W1.**

## Summary

The engine cap rests on one claim: *the same simulation code runs everywhere, only authority changes.* That claim is
**false in the build graph today**. `src/Agapanthe.Engine/Agapanthe.Engine.csproj:15-18` references both
`Agapanthe.Rendering` and `Agapanthe.Graphics`, so a dedicated server would link Vulkan, and there is no way to
construct a running simulation without a GPU — `FrameOrchestrator.CreateDefault` requires a non-null `Renderer`,
`ResourceRegistry` and `Camera` (`FrameOrchestrator.cs:94-101`).

This milestone makes `Agapanthe.Engine` **headless**, moves the render glue into a new `Agapanthe.Engine.Render`,
proves the result runs and links with no GPU, and installs two permanent guards so the property cannot silently rot.

MP-0 as written in the backlog bundles four independent subsystems (identity, headless split, time authority,
input→commands). They share no files and no risk, and their "irreversible" argument is very unequal, so MP-0 is
**decomposed**; this document specs the first sub-milestone only. The other three keep their backlog entries.

### Why the split goes first (this reverses the backlog's numbering)

The `1..5` numbering of §4quater is a **severity ranking, not an execution order**. The two 🔴 identity items are
*not broken today*: the contact-pair key at `GameWorld.Physics.cs:333` is correct while ids stay dense (< 2³²), and
it becomes wrong only **at the moment we partition** — bug and fix arrive together, and nothing degrades by waiting.

The split's cost is the opposite shape: **strictly monotone increasing** in the amount of code inside `Engine`
(UI-2 alone added two systems). It will never be cheaper than today — 9 files, of which only four import
`Rendering` or `Graphics` at all (`Systems.cs`, `UiRenderSystem.cs`, `DebugOverlaySystem.cs`,
`FrameOrchestrator.cs`), and **zero Vulkan types anywhere in the assembly**. It also decides where the remaining three sub-milestones land: the
time accumulator and the input abstraction both need a home, and the backlog already notes that `Agapanthe.App`
comes *after* MP-0 precisely because the split creates the seam `App` must formalise.

## Context — what the codebase already provides

Verified against executable code, not against comments.

- **`Agapanthe.Engine` contains no Vulkan type.** A grep for `Silk.NET|Vk[A-Z]|GraphicsDevice` over
  `src/Agapanthe.Engine` returns one hit, and it is the word "Silk.NET" inside a prose doc comment
  (`FrameOrchestrator.cs:193`). The entire GPU coupling is three **opaque handles** carried by `RenderContext`
  (`Systems.cs:35-55`): `CommandList` (a `readonly unsafe struct`), `FrameContext`, `SwapchainTarget`. Engine never
  dereferences them — it forwards them into `Renderer` calls.
- **`Agapanthe.World` is genuinely GPU-free**: its only `ProjectReference` is `Core` (`Agapanthe.World.csproj:7`),
  `Arch` is `PrivateAssets="compile"` so no ECS type can leak, and it names only Core types at its render seam —
  `CollectRenderLists(RenderList, SceneCandidateSet, in RenderView)` (`GameWorld.cs:738`). All three parameter types
  live in `Agapanthe.Core` (`RenderList.cs:10`, `SceneCandidateSet.cs:41`, `RenderView.cs:24`), and `RenderView` is
  a `readonly struct` with public fields, constructible with no device. **Half the road is already built.**
- **`Agapanthe.Engine` does not reference `Agapanthe.Platform`**, deliberately and with the reason written down
  (`Agapanthe.Engine.csproj:9-11`). Nothing to undo.
- **`ISystem` / `IRenderSystem` are already disjoint** (`Systems.cs:70-79`), and `SystemScheduler.Add(Stage.Render,
  ISystem)` throws (`SystemScheduler.cs:71-76`). `TickContext` is documented as carrying no GPU type
  (`Systems.cs:6-10`).
- **VS-1 provides a deterministic state comparator for free.** `GameWorld.Save(Stream)` writes entities sorted by
  `GlobalId` (`WorldSerialization.cs:89`) with a `u32` presence mask and blittable bodies; byte-identity of
  `Save(Load(bytes)) == bytes` is already a test. No hashing machinery needs writing.
- **`tools/AotComponentProbe` already references `Agapanthe.Engine`** (`AotComponentProbe.csproj:22`, added in UI-2
  for `FrameSeries`), and nothing else beyond `World`, `Core`, `Ui` (`:16-22`). After the split it becomes, at no
  cost, the proof that the simulation links under NativeAOT with no Vulkan in its closure — the same role it plays
  for `StbTrueTypeSharp`'s absence.

### Three facts that shape the design and are easy to get wrong

**The cut does not follow the `ISystem` / `IRenderSystem` line.** `DebugOverlaySystem` is an `ISystem` registered in
`Stage.PostSimulation` (`Program.cs:501`), yet it depends on `Rendering`, `Ui` and `Assets.Font`; `UiRenderSystem`
implements **both** interfaces (`UiRenderSystem.cs:21`). The cut follows **dependencies**, not interfaces. Splitting
by interface produces a build that does not compile and a design that means nothing.

**The structural barrier runs four times per drawn frame, three per skipped frame.** `SystemScheduler.Tick` invokes
it at the end of each of the three tick stages (`SystemScheduler.cs:110`) **and** `Render` invokes it again
(`:128`). This is behaviour: a structural command enqueued during the Render stage is applied at the end of Render
rather than at the start of the next frame. **`SystemScheduler.Render` is currently called by no test at all** — a
grep for `.Render(` in `tests/Agapanthe.Tests/SchedulerTests.cs` returns nothing — so this behaviour is entirely
unguarded today.

**`_frozen` is shared and `Render` also sets it** (`SystemScheduler.cs:121`, not only `Tick` at `:99`). Today a
`Render()` with no prior `Tick()` freezes `ISystem` registration too. The split changes this; see §"Error handling".

## Locked decisions (interview, session 26)

1. **Scope = the headless split only.** Identity partitioning, contact-key repacking, time authority and
   input→commands remain separate sub-milestones with their own instruction.
2. **`Agapanthe.Engine` becomes the headless simulation**; a new **`Agapanthe.Engine.Render`** takes the render glue.
   The headless half keeps the attractive default name **on purpose**: a casually-added system then lands in the
   server-runnable half, where the automated gate catches the rare GPU-touching exception. Under the inverse naming,
   gameplay written without thinking would land in a half no server can run, and **nothing would signal it**.
3. **The render-stage guard is a permanent regression guard, not a discovery experiment** (revised after review —
   see below). The cross-process parity run is **dropped**: it would require new Sandbox surface for a delta that
   the code shows to be structurally absent.
4. **`AggregateBoundsSystem` and `_sceneBounds` are deleted, not moved** — the review established they are dead.

## Architecture

### Target module graph

```
Core ←── World ←── Engine                          headless: no Rendering, no Graphics, no Platform
                     ↑
Core ←── Graphics ←── Rendering ←── Engine.Render   render glue: composes a SimulationHost
                                       ↑
                              Sandbox   /   HeadlessSim
```

`Agapanthe.Engine` declares `Core` and `World` explicitly (the repo's "explicit, not merely transitive" convention,
stated at `Agapanthe.Engine.csproj:16-17`). It gains **no** new reference; it only loses two.

`Agapanthe.Engine.Render` references `Core`, `World`, `Rendering`, `Graphics`, `Engine`. `Ui` and `Assets` continue
to arrive transitively through `Rendering`, as they do today. It must declare `IsAotCompatible=true` — that property
lives in each individual `.csproj` (`Agapanthe.Engine.csproj:26`), **not** in `Directory.Build.props`, so omitting
it would silently disable the AOT analysers and punch a hole in the permanent Phase-2 gate.

### File disposition

| File | Destination | Note |
|---|---|---|
| `Stage.cs` | Engine, unchanged | already GPU-free |
| `FrameStats.cs` | Engine, unchanged | pure float rings |
| `PhysicsSystem.cs` | Engine, unchanged | `using Agapanthe.World` only |
| `LandingChallengeRule.cs` | Engine, unchanged | `using Agapanthe.World` only |
| `Systems.cs` | **splits** | `TickContext` + `ISystem` stay in Engine · `RenderContext` + `IRenderSystem` move to Engine.Render |
| `SystemScheduler.cs` | **splits** | tick stages + barrier stay in Engine · the `IRenderSystem` list, `Render(in RenderContext)` and `CountIn(Stage.Render)` move to a new `RenderSystemScheduler` |
| `FrameOrchestrator.cs` | **splits** | headless half extracted as `SimulationHost`; the rest moves to Engine.Render keeping its name |
| `UiRenderSystem.cs` | Engine.Render | dependency-driven: it is also an `ISystem`, and that does not matter |
| `DebugOverlaySystem.cs` | Engine.Render | dependency-driven: an `ISystem` that needs `Rendering`+`Ui`+`Assets.Font` |

`SystemScheduler.CountIn(Stage.Render)` **throws `ArgumentException`** after the split, consistent with
`Add(Stage.Render, ISystem)` (`SystemScheduler.cs:71-76`); `RenderSystemScheduler` exposes its own `Count`.

### The three types

**`SimulationHost` — new, `Agapanthe.Engine`.** Owns the `SystemScheduler`, borrows the `GameWorld` (it owns
nothing, as `FrameOrchestrator` does not today — `FrameOrchestrator.cs:16-19`).

```csharp
public sealed class SimulationHost
{
    public static SimulationHost CreateDefault(GameWorld world);   // registers PropagateSystem
    public void Add(Stage stage, ISystem system);
    public long FrameIndex { get; }
    public TickContext CurrentTick { get; }        // see the note below — FrameIndex is POST-increment
    public void Tick(float deltaSeconds);          // opens the UI-2 measurement bracket
    public void EndFrame();                        // closes it, files into Stats
    public FrameStats Stats { get; }
    public long LastFrameAllocatedBytes { get; }
    public float LastFrameMs { get; }
}
```

`CurrentTick` replaces `FrameOrchestrator._dt` (`FrameOrchestrator.cs:61`), which exists only so the render delegate
can rebuild a `TickContext` (`:84`). Publishing the context itself is both smaller and honest: there is one tick
state, and it belongs to the thing that ticks.

**Its `FrameIndex` is post-increment, and that must be reproduced exactly.** Today the render delegate reads
`_scheduler.FrameIndex` *after* `SystemScheduler.Tick` has incremented it (`SystemScheduler.cs:113`), whereas the
tick systems of that same frame received the pre-increment value (`:100`). So a render system sees `N+1` where the
tick systems saw `N`. That is arguably a wart, but it is **existing behaviour in a milestone that claims to change
none**, so `CurrentTick` returns `new TickContext(lastDt, FrameIndex)` with the post-increment `FrameIndex` —
bit-for-bit what `FrameOrchestrator.cs:84` builds today. Fixing the off-by-one belongs to the time-authority
sub-milestone, which will revisit what a tick index means. Low stakes either way: `RenderContext.Tick` currently
has **no consumer at all** (grep for `ctx.Tick` over `src/`, `samples/`, `tests/` returns nothing).

The UI-2 measurement bracket moves **unchanged**, including the per-thread `Debug.Assert`
(`FrameOrchestrator.cs:167-173`) and its comment — which already names MP-0 as the milestone that could break the
single-thread assumption. That comment stays accurate and stays with the bracket.

**`RenderSystemScheduler` — new, `Agapanthe.Engine.Render`.** Holds `List<IRenderSystem>`,
`Render(in RenderContext)` including the **post-Render barrier invocation** (`SystemScheduler.cs:128`), `Count`, and
its own `_frozen` flag set by `Render`.

It takes the barrier delegate **directly at construction**, from `FrameOrchestrator`, which already has it as
`_world.FlushStructuralChanges` (`FrameOrchestrator.cs:80`). It is therefore **not** routed through `SimulationHost`,
and **no `InternalsVisibleTo` between the two engine assemblies is needed**.

**`FrameOrchestrator` — moves to `Agapanthe.Engine.Render`, keeps its name and its entire public API.** It
**composes** a `SimulationHost`. Two groups of members, and the distinction matters to the implementer:

- **Delegated to `SimulationHost`**: `Tick`, `EndFrame`, `Stats`, `LastFrameMs`, `LastFrameAllocatedBytes`,
  `FrameIndex`, `Add(Stage, ISystem)`.
- **Retained on the render side and routed to `_renderScheduler`**: `Add(IRenderSystem)`
  (`FrameOrchestrator.cs:115`, used at `Program.cs:490`) and `RenderDelegate` (`:210`, used at `Program.cs:765`).
  These never reach `SimulationHost` — the headless half must not learn that a render scheduler exists.

It retains `_world`, `_renderer`, `_registry`,
`_camera`, `_render`, `_persistent`, the cascade scratch (`_cascades`, `_splits`, `_cascadeFrusta`,
`_cascadeNearCutPlanes`), `_renderDelegate`, `_renderScheduler` and `SceneViewSystem`. It loses `_dt` (now
`SimulationHost.CurrentTick`) and `_sceneBounds` (deleted, below).

The name stays with the render half because it still means what it says — it *assembles a frame*. A
`FrameOrchestrator` that no longer orchestrates frames would be a misnomer, and the `Systems.cs` author already
warned about exactly this class of naming trap (`Systems.cs:30-34`).

### Deleting `AggregateBoundsSystem` and `_sceneBounds`

`_sceneBounds` is **written and never read**: a repo-wide grep finds only its declaration
(`FrameOrchestrator.cs:60`) and its single write (`:222`). The comment at `:58-59` claiming it is *"consumed by the
Render-stage light fit"* is a pre-P3-M5 vestige — `SceneViewSystem` now fits each cascade to its own camera-frustum
slice (`:246-250`) and never touches global bounds. `AggregateBoundsSystem` is therefore an `O(n)`-per-frame pass
with no observable effect.

Both are **deleted**. `GameWorld.AggregateBounds()` (`GameWorld.cs:688-708`) **stays** — it is pure (read-only, no
`MarkDirty`, no mutation), it is covered by `WorldSystemsTests.cs:165-215` and `:261-264` plus
`LifecycleTests.cs:123-136`, and a future consumer (interest management, a world-space UI) will want it. Only the
per-frame caller disappears.

*(Not `SceneBoundsTests` — that file covers `SceneBuilder.ComputeMeshLocalSphere`, the per-mesh **local** sphere on
the `Rendering` side, and its own header comment points at `WorldSystemsTests` for the world-space aggregation. This
correction is the third instance in this document's history of a claim deduced from a name rather than read from the
code; it is recorded here rather than quietly fixed.)*

Consequence for ordering: PostSimulation becomes `PropagateSystem` then the application's systems. This is a
**declared behaviour change** — one fewer system runs per frame — whose observable effect is nil by the argument
above, and which the unchanged capture hashes will confirm.

### Consequence for the application

`samples/Sandbox/Program.cs` gains **one `using`** (it currently has exactly one `using Agapanthe.Engine`, at
`:6`); it will name types from both assemblies — `Stage`/`ISystem`/`PhysicsSystem`/`LandingChallengeRule` from
`Engine`, `FrameOrchestrator` (`:476`), `UiRenderSystem` (`:487-490`), `DebugOverlaySystem` (`:495`) from
`Engine.Render`. No other line changes.

`samples/Sandbox/Sandbox.csproj` and `tests/Agapanthe.Tests/Agapanthe.Tests.csproj` each gain one
`ProjectReference` (7 → 8). `Agapanthe.slnx` gains one `<Project Path="src/Agapanthe.Engine.Render/…" />` entry
under `/src/` — without it the IDE and `dotnet build Agapanthe.slnx` miss the project even though a folder build
would succeed through the ProjectReference chain.

That the application's *code* barely moves is not a convenience: a refactor that rewrites its own call sites cannot
prove it changed nothing.

### Naming

`Agapanthe.Engine.Render` sits next to `Agapanthe.Rendering`, which is a real readability hazard. Two mitigations:
the two namespaces expose **disjoint** public types (no name defined in both), and the new `.csproj` carries a
one-line header comment stating the distinction — *`Rendering` draws things; `Engine.Render` decides when and in
what order, and is the only project that sees a `GameWorld` and a `Renderer` together.*

## The render-stage guard

**What is actually being guarded**: that running the Render stage does not alter simulation state.

**It holds today, and the code says why** — every claim here cites an executable line:

- `InstanceSlot` is **not added** during collection. `GameWorld.cs:807` is `entity.Set(new InstanceSlot { Value = i })`
  — an in-place write, not a structural change.
- Every creation path adds it **at spawn**: `GameWorld.cs:214` and `GameWorld.Physics.cs:105`
  (`new InstanceSlot { Value = -1 }`), and `WorldSerialization.cs:219` re-adds it on load.
- The gather query **requires** it: `GameWorld.cs:52` lists `InstanceSlot` in the `WithAll`, so an entity lacking it
  is not gathered at all rather than being mutated into a new archetype.

So there is no archetype churn, no chunk-iteration reordering, and no divergence. **v1 of this spec claimed the
opposite and built its headline gate on it; that gate would have passed by construction.**

This does not make the guard worthless — it makes it a **guard rather than an experiment**, and it must be labelled
as such. It fails the day someone adds a component write to the Render path, or registers a render system that
enqueues structural commands. Both are plausible in the milestones that follow.

### Test 1 — render-stage state neutrality (GPU-free, permanent)

Two `GameWorld`s loaded from the same snapshot. Tick both N times with identical `PhysicsSettings`. World B
additionally performs, per tick, the **full** windowed render-stage sequence:

```csharp
world.CollectRenderLists(renderList, candidates, in view);
world.FlushStructuralChanges();   // the 4th barrier — SystemScheduler.cs:128
```

Then `Save` both and assert byte equality. All types involved are `Core`; no device, no window.

Modelling the 4th barrier is what v1 got wrong a second time: omitting it means the test does not reproduce the
ordering it claims to compare.

The scene must contain bodies in contact (so the broadphase and contact resolution participate), a parent chain (so
`PropagateTransforms` participates), and at least one spawn/despawn during the run (so the barrier participates).

### Test 2 — barrier count and Render-stage barrier semantics (GPU-free, permanent)

Closes a real hole: `SystemScheduler.Render` is called by **no** existing test.

- A counting barrier delegate: `Tick` invokes it exactly **3** times; `Render` exactly **1**.
- A render system that enqueues a spawn is materialised **at the end of the Render stage**, not deferred to the next
  frame. This is the one place where the two orderings genuinely differ, and it is currently undocumented by any
  test.

**Constructing the `RenderContext` in a test needs no device.** `default(CommandList)`, `null!` for `FrameContext`
and `default(SwapchainTarget)` are sufficient: a test render system never dereferences the handles, no pointer type
is ever named, and `Agapanthe.Tests` therefore does **not** need `AllowUnsafeBlocks` (only `Graphics`, `Platform`
and `FontCooker` enable it). Written down because it is the one non-obvious mechanical step in W1, and an
implementer could otherwise conclude the test requires a GPU.

## The architecture gate

**Two assertions, because one is not enough.**

1. **Static, on the project file.** Read `src/Agapanthe.Engine/Agapanthe.Engine.csproj` and assert its
   `ProjectReference` set is exactly `{Agapanthe.Core, Agapanthe.World}`. Deterministic, and it fails **at the
   commit that introduces the regression**.
2. **Dynamic, on the assembly closure.** Recursively walk `typeof(SystemScheduler).Assembly`'s referenced
   assemblies; assert absence of `Agapanthe.Graphics`, `Agapanthe.Rendering`, `Agapanthe.Platform` and any name
   starting with `Silk.NET.`. `Assembly.Load` failures are caught and reported rather than thrown.

The dynamic check alone would be **falsely green** on the case that matters: the C# compiler elides unused
references from the manifest, so re-adding a `ProjectReference` without yet using a type would not trip it — the
failure would surface one commit later and be blamed on the wrong change. The static check is the one that bites in
time; the dynamic one catches an *effective* dependency arriving by some other route.

Same family as the frozen `ComponentRegistry.All` ordering guard and the ≤32-component guard.

## Error handling

This milestone introduces no new failure mode. Existing behaviours that must survive, each asserted:

- `SystemScheduler.Add(Stage.Render, ISystem)` still throws `ArgumentException` with its current message.
- Registration after the first tick still throws `InvalidOperationException` on the **tick** scheduler.
  ⚠️ **Corrected after audit**: it does NOT on the render scheduler — see the second declared behaviour change
  below. The original wording here was wrong.
- `SimulationHost.EndFrame()` on an unopened bracket is still a silent no-op, and `Tick` still closes a stale
  bracket belt-and-braces (`FrameOrchestrator.cs:126-131`).

**Two declared behaviour changes.**

*First*, today `_frozen` is shared, and `Render` sets it (`SystemScheduler.cs:121`), so a
`Render()` before any `Tick()` also freezes `ISystem` registration. After the split the two schedulers hold separate
flags: a `Render()` before the first `Tick()` freezes render-system registration only. In a real host this cannot
occur — `Tick` always precedes `DrawFrame` (`Program.cs:764-766`) — so the change is accepted rather than worked
around, and a test pins the new behaviour so it is a decision on the record and not a drift.

*Second* (found by the MP-0a audit, not by this spec): the same split relaxes the guard in the other direction.
`Add(IRenderSystem)` used to consult the shared flag that `Tick` set, so registering a render system after the
first tick threw; it now freezes only on the render scheduler's own first `Render`. Harmless for the same reason —
tick and render are sequential on one thread, so no list is ever mutated while being iterated — and pinned by
`Add_AfterATickButBeforeAnyRender_StillSucceeds`.

`HeadlessSim` exits non-zero with a diagnostic on an unknown argument or a `--ticks` value ≤ 0.

## Testing strategy

| Test | Wave | Kind | What breaks without it |
|---|---|---|---|
| Render-stage state neutrality (Test 1) | **W1** | GPU-free unit | a future component write in the Render path diverges client and server silently |
| Barrier count 3/1 + Render-stage spawn semantics (Test 2) | **W1** | GPU-free unit | the post-Render barrier is lost in the extraction — **untested today** |
| PostSimulation registration order | **W2** | GPU-free unit | the `AggregateBoundsSystem` deletion reorders the frame |
| `Render()` before `Tick()` freeze semantics | **W3** | GPU-free unit | the declared behaviour change drifts unrecorded |
| Architecture: static `.csproj` reference set | **W4** | file assertion | the split regresses in a later session, undetected until much later |
| Architecture: recursive assembly closure | **W4** | GPU-free unit | a Vulkan dependency arrives by an indirect route |
| `AotComponentProbe` | W5 | AOT publish + run | headless Engine does not survive NativeAOT |
| Sandbox headless captures | W5 | run-level | a "pure refactor" changed a pixel |

Only the first two can live in W1. "PostSimulation registration order" needs `SimulationHost.CreateDefault(GameWorld)`
— at `HEAD`, `FrameOrchestrator.CreateDefault` demands a non-null `Renderer` (`:97-101`) — and the freeze-semantics
test needs the two separate `_frozen` flags, which only exist after W3.

Existing `SchedulerTests` needing surgery, named rather than hand-waved:

- `CountIn_ReportsEveryStage` (`SchedulerTests.cs:137-149`) asserts one object reports all four stages. **Split it**:
  three stages on `SystemScheduler`, `Count` on `RenderSystemScheduler`, plus the new `ArgumentException` on
  `CountIn(Stage.Render)`.
- `Tick_RunsNoRenderSystem` (`:68-81`) registers both kinds on one object. After the split it degenerates into a
  typing tautology. **Delete it**; Test 2 covers the real invariant it was reaching for.

## Waves

**W1 — Guards first.** Write Test 1 and Test 2 against current `HEAD`. **No structural change in this wave.** They
must be green before anything moves: a guard written after the refactor guards nothing, and Test 2 is genuinely new
coverage of untested behaviour.

**W2 — Extract `SimulationHost`.** Inside the existing `Agapanthe.Engine`; **no `.csproj` touched**.
`FrameOrchestrator` composes and delegates. Delete `AggregateBoundsSystem` + `_sceneBounds`. Every test and every
capture hash unchanged.

**W3 — Split the project.** Create `Agapanthe.Engine.Render` (with `IsAotCompatible=true` and
`InternalsVisibleTo("Agapanthe.Tests")`), move the render types, drop `Rendering` + `Graphics` from
`Agapanthe.Engine.csproj`, add the `Agapanthe.slnx` entry, add the `ProjectReference` to `Sandbox.csproj` and
`Agapanthe.Tests.csproj`, add the `using` to `Program.cs`.

**W4 — Gate + host.** Both architecture assertions. `samples/HeadlessSim` with `PublishAot=true`: builds a small
physics scene **in code** (mesh/material handles are `(int, uint)` values the World never dereferences, so default
handles are fine), boots a `GameWorld` + `SimulationHost` + `PhysicsSystem`, ticks `--ticks N`, no window and no
device, optionally `--save`. It goes in `samples/` rather than `tools/` because it is the seed of the dedicated
server, whereas `tools/` is by convention never referenced by a shipped project.

**W5 — Tail.** Self-review of the diff, double audit (`csharp-lowlevel` + `engine-architect` — the standard pair; no
GPU pass is added, so `graphics-3d` is not warranted), full verification, human verdict.

W2 and W3 are separate on purpose: two clean rollback points instead of one large jump, and W2's "everything still
green with no project change" is the evidence that the extraction itself was behaviour-preserving.

## Verification (DoD)

- `dotnet build` **0 warning** · `dotnet test` green, including all new tests above.
- **Capture hashes unchanged**: HDR `12638edd`, UI overlay-hidden `03421357`. This is a pure refactor — a changed
  pixel means a changed behaviour.
- Sandbox headless: **0 validation message** (synchronization validation active since `141e374`), **0 leak**.
- `AotComponentProbe` PASS, with the stronger claim now available: **no Vulkan in its AOT closure**. Report the
  before/after referenced-assembly list as milestone evidence.
- `HeadlessSim` publishes NativeAOT and runs to exit 0 with no GPU present — **the milestone's headline artifact**.
- Double audit PASS + human verdict, then CONVERGE (`AVANCEMENT` / `BACKLOG` / `CLAUDE`, board archived).
  **Commit on explicit request only.**

## Risks

- 🟠 **`Camera` lives in `Agapanthe.Rendering`** (`Camera.cs:28`) and is a near-pure math type. Moving it to `Core`
  would let more of the view path go headless, and interest management will eventually want it. **Explicitly
  deferred** — scope creep here, and nothing in this milestone needs it.
- 🟠 **Sandbox systems reference `Rendering` from tick stages** (`BenchSpinSystem` mutates `camera.Yaw`;
  `LandingChallengeSystem` holds a `Camera` and an `EngineWindow`). Legal — an application may reference anything;
  only `Agapanthe.Engine` must stay clean. But it means `HeadlessSim` cannot reuse them, which is why it builds its
  own scene rather than sharing the Sandbox's builders (that sharing is the *content* milestone's job).
- 🟠 **The guard cannot see cross-process differences** (system registration order, host-specific setup), since the
  cross-process run was dropped. Accepted: the delta it would have covered is structurally absent today, and the
  static architecture assertion covers the regression that would reintroduce one.
- 🟡 Two-project naming adjacency (`Engine.Render` / `Rendering`), mitigated above.

## Out of scope

Identity partitioning · contact-key repacking · time authority / accumulator · input→commands · `Agapanthe.App` ·
moving `Camera` out of `Rendering` · sharing scene builders between hosts · any transport, replication or netcode ·
relevance/interest management · partial/incremental persistence. The other three MP-0 sub-milestones keep their
backlog entries and their order.
