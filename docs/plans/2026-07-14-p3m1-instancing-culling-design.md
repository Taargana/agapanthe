# P3-M1 — Instanced rendering (SSBO) + culling debt paydown — Design

**Status**: approved design (2026-07-14, session 14) · **Phase**: 3 (gameplay foundations) · **Milestone**: P3-M1
**Supersedes debt**: P2-M4 legacy debts #1 (stale `AggregateBounds`) and #2 (conservative shadow-caster cull); bench FPS concern from the P2-M4 human verification.
**Related**: [P2-M4 board](../../.absolute-human/archive/board-session13-P2M4.md) · [AVANCEMENT](../AVANCEMENT.md) · Phase 2 spec §3.5 (render-list systems).

## Summary

The `grid:100x100` bench (10 000 helmets) is slow because the renderer issues **one
`vkCmdDrawIndexed(instanceCount=1)` per entity** (model matrix as a 64-byte push constant →
~12 556 draw calls/frame at 10k) **and** the shadow-caster list keeps **all 10 000 casters**
(the light-volume cull is conservative on a flat scene).

P3-M1 lays the **first step of GPU-driven rendering** (the shape Unreal 5 GPU Scene / Unity 6
BatchRendererGroup use) with **no throwaway work**: per-instance transforms travel through a
**GPU buffer** the vertex shader indexes, the visible set is **batched by (material, mesh)** into
one instanced draw per batch, and the two culling debts are paid. **Culling stays CPU-side this
milestone** (human decision); GPU compute culling + persistent dirty-tracked slots are the *next*
milestone (P3-M2 "GPU-driven rendering"), for which this is the clean stepping stone. The quantized
origin paid for in M4 remains the prerequisite for that next milestone and for physics — it is not
wasted.

**Target outcome**: identical pixels (byte-identical helmet capture), draw calls collapsed
(~12 556 → ≈ one instanced draw per distinct (material, mesh) batch — 1 scene + 1 shadow draw for the
single-model bench), 0-alloc/frame preserved, measured in **Release + AOT** (the human's
Debug+validation run at 74–92 ms/frame is not representative; ~3.7 ms is expected in JIT-Release).

## Current state (verified against code)

- **No instance buffer exists.** `CollectRenderLists` (`src/Agapanthe.World/GameWorld.cs:311`)
  rebuilds each entity's camera-relative model matrix every frame (`ComposeModel`, `:389`) into a
  transient `RenderItem.WorldTransform` (88 B struct, `src/Agapanthe.Core/RenderItem.cs`), then
  radix-sorts. `RecordScenePass`/`RecordShadowPass` (`src/Agapanthe.Rendering/Renderer.cs:748`/`:684`)
  loop one draw per item, pushing the model matrix as a vertex push constant (offset 0 scene, offset
  64 shadow). `BufferUsage` (`src/Agapanthe.Graphics/BufferUsage.cs`) knows only Vertex/Index/Uniform.
- **Debt #1**: `AggregateBounds` (`GameWorld.cs:263`) is folded **once** at spawn
  (`samples/Sandbox/Program.cs:158`), never per frame → `sceneBounds` goes stale the moment an entity
  translates; `ShadowFit.FitSceneSphere`/`UpstreamExtent` inherit the staleness.
- **Debt #2**: casters are tested against the light ortho volume (`lightFrustum` from
  `lightViewProj`); on a flat scene `sceneRadius ≤ frustumRadius` → the volume is the whole scene →
  every caster is kept (`GameWorld.cs:340,357`).

## Architecture (locked decisions)

### D1 — Instancing via per-frame compacted SSBO
The CPU cull produces the sorted `RenderList` (camera-relative matrices baked into
`RenderItem.WorldTransform`, a Core / GPU-free type). The **Renderer** copies those matrices into a
host-visible storage buffer (one per frame-in-flight) during recording — the one place the GPU-free
world and the GPU seam meet (same place `registry.Resolve` already lives; `Agapanthe.World` never
references Graphics). The vertex shader reads `transforms[gl_InstanceIndex]`. The SSBO offset per
batch is supplied via `DrawIndexed`'s **`firstInstance`** parameter (Vulkan:
`gl_InstanceIndex = firstInstance + i`) — no per-batch descriptor rebind.

*Verified*: read-only SSBO in a vertex shader is Vulkan 1.0 core (no feature; `vertexPipelineStores
AndAtomics` gates only writes/atomics). A non-zero `firstInstance` in a **direct** draw needs no
feature (`drawIndirectFirstInstance` gates only *indirect* draws). A `mat4` has identical std140/std430
layout, so push→SSBO transport is a byte-identical memcpy.

### D2 — Batching post-sort by (material, mesh)
`RenderItem.ComposeSortKey` extends from `(int materialIndex, uint tieBreak)` to
`(int materialIndex, int meshIndex, uint tieBreak)`, laid out `(material << 48) | (mesh << 32) |
tieBreak` — material and mesh 16 bits each (Debug assert on overflow), tie-break (RenderOrder) keeps the
**full low 32 bits** (determinism preserved — never truncate the tie-break, else equal keys follow Arch
iteration order). `meshIndex` comes from `MeshRef.Mesh.Index`, already in hand in `CollectRenderLists`
(no new component).
The radix sort is unchanged (64-bit, 8 passes). After sorting, walk the list and emit one instanced
draw per run of identical (mesh, material): bind material set (set 1) per material-run, vertex/index
buffer per mesh-run, then `DrawIndexed(indexCount, instanceCount=runLen, 0, 0, firstInstance=runStart)`.

### D3 — Shadow-caster cull via extruded frustum (debt #2)
New Core type `ExtrudedShadowFrustum` (GPU-free, unit-testable): take the camera frustum's 6 planes
(`Frustum.FromViewProjection`, inward normals) and **keep** a plane iff `dot(n_inward, dir) ≤ 0`
(where `dir` is the light propagation direction, source→surface; `|dot| < ε` → **keep**, conservative
bias), **drop** the rest → a semi-infinite wedge open toward the light = exactly the region of casters
whose shadow can enter the view. A caster is kept iff `inLightVolume && inExtruded` (**AND**).

`ShadowFit.ComputeLightViewProj` is **unchanged** — the shadow-map sizing is identical, so the helmet
capture stays byte-identical; the AND only **restricts** the caster set, never enlarges it, so no caster
kept is ever outside the shadow-map volume (no clipping). On a flat scene the wedge's slanted side
planes are the tight test → off-screen lateral casters are dropped.

*Conservative by construction* (Minkowski sum of a convex polytope with a ray adds no new faces; it
only removes upstream-facing faces and opens the rest). No false negatives → the anti-popping property
holds.

### D4 — Per-frame bounds (debt #1)
`world.AggregateBounds()` is called **every frame** inside `drawScene`, before
`ComputeLightViewProj` (today folded once at `Program.cs:158`). It is already 0-alloc (chunk iteration,
struct return). Dirty-tracking is deferred — recompute O(n) is always correct and negligible next to
the draw-call cost at this scale.

### Out of scope (→ next milestone P3-M2 "GPU-driven rendering")
Persistent dirty-tracked per-object slots, GPU compute culling, indirect draws, a visibility index
buffer. When those arrive, the vertex shader moves from `transforms[gl_InstanceIndex]` to
`transforms[visible[gl_InstanceIndex]]`.

> **Correction (closing audit, 2026-07-14).** This was originally written as "a one-line shader change; the draw
> path is unchanged". That is **wrong**: once the cull runs on the GPU, a batch's `instanceCount` is no longer known
> CPU-side, so the draw must become `vkCmdDrawIndexedIndirect(Count)` — which needs `BufferUsage.Indirect`, a
> `CommandList.DrawIndexedIndirect`, and the **`drawIndirectFirstInstance`** feature (a non-zero `firstInstance` is
> free in a *direct* draw, not in an *indirect* one). Consider carrying the batch offset in a push constant instead:
> it removes the dependency on `firstInstance` entirely and neutralises risk F4 (MoltenVK) at the same time.

## Components & seams

- **Agapanthe.Graphics** (owns all `Vk*`): `BufferUsage.Storage` (→`StorageBufferBit`);
  `DescriptorKind.StorageBuffer` (→`DescriptorType.StorageBuffer`); `DescriptorWrites.StorageBuffer` +
  `FrameContext.WriteStorageBuffer` + a storage `DescriptorPoolSize` in the per-frame pool;
  `GpuBuffer.MappedSpan<T>` for zero-copy compaction.
- **Agapanthe.Core** (GPU-free): `RenderItem.ComposeSortKey` extended; new `ExtrudedShadowFrustum`
  beside `Frustum.cs`.
- **Agapanthe.Rendering**: `Renderer` gains `_sceneTransforms[FramesInFlight]` and
  `_shadowTransforms[FramesInFlight]` SSBOs, the compaction + batching loops, set-layout binding, and
  `LastSceneDrawCalls`/`LastShadowDrawCalls` counters; `ScenePass` **drops the vertex model push range
  (offset 0, 64 B) and keeps the fragment `debugView` range at offset 64** (isolated fragment range,
  still valid); `ShadowPass` keeps only the `lightViewProj` push (offset 0, 64 B, down from 128) and
  gains a set layout; `ShadowFit` **untouched**. `RecordShadowPass` now takes a `FrameContext` (to
  allocate/write its set).
- **shaders/**: `mesh.vert` and `shadow.vert` read the SSBO (`set 0`, binding 6 scene / binding 0
  shadow) instead of the model push constant.
- **Agapanthe.World**: `CollectRenderLists` passes `Mesh.Index` into the sort key and gains an
  `in ExtrudedShadowFrustum` parameter (caster kept on the AND); its **internal** call inside
  `AotRootingSmoke` (`GameWorld.cs:159`) takes the new arg — the `render.Count == 8` assert stays
  valid (a wide extruded frustum keeps all 8 drawables).
- **samples/Sandbox/Program.cs**: per-frame `AggregateBounds`, extruded-frustum construction from
  `view` + light direction, draw-call logging in the bench block.
- **tests/Agapanthe.Tests** (mechanically broken by the two signature changes — must be updated in the
  wave that introduces each): `WorldSystemsTests.cs` (~8 `CollectRenderLists` calls at lines
  115/139/142/211/230/245/284/293 → new extruded-frustum arg; `KeepsAnOffScreenCasterInTheShadowList`
  revised per risk F3); `RenderListTests.cs` (`ComposeSortKey` calls at lines 97/110-120/133-134 → 3-arg
  signature + new key-layout expected values). `ComponentRegistryTests.cs:36` only calls
  `AotRootingSmoke` and needs no change.

Descriptor layout: scene reuses **set 0** (per-frame, reallocated each frame) at **binding 6** (0–5 are
camera/lights/shadow/IBL), Vertex stage. Shadow pass — which has no set today — gets a **dedicated set 0
with one storage binding**, Vertex stage.

## Waves (gate per wave)

| Wave | Deliverable | Gate |
|---|---|---|
| **V0** | SSBO plumbing (Graphics; no render change) | build + all tests green, 0 regression |
| **V1** | SortKey `(material, mesh, tie-break)` | batching + determinism tests; sort still 0-alloc |
| **V2** | Scene pass instanced | helmet capture **byte-identical (≤1 LSB)**, 0 validation, scene draw-call log |
| **V3** | Shadow pass instanced | helmet capture (shadows) byte-identical, 0 validation, shadow draw-call log |
| **V4** | Per-frame bounds (debt #1) | bounds-under-translation test; 0-alloc/frame (bench = 0 B) |
| **V5** | Extruded shadow cull (debt #2) | extruded tests (4 cases below); anti-popping revised & green; helmet byte-identical; caster count drops |
| **V6** | Bench, measure, dual audit, close | bench `grid:100x100` in **Release JIT + AOT**; draw calls before/after; 0-alloc; 0 leak; 0 validation; `csharp-lowlevel` + `engine-architect` PASS |

## Testing strategy

- **Byte-identical**: PPM helmet capture (scene + shadows), ≤1 LSB vs baseline, before merging V2/V3/V5
  (`AGAPANTHE_MAX_FRAMES=3 AGAPANTHE_CAPTURE=out.ppm`, `DebugView=0`).
- **Bench perf**: `AGAPANTHE_SCENE=grid:100x100 AGAPANTHE_CULL_STATS=1` in **Release JIT + AOT** only
  (never Debug+validation for perf); compare cull+collect ms and draw calls before/after.
- **0-alloc/frame**: bench counter = 0 B (compaction via `MappedSpan`, no scratch; SSBO recreated only
  on growth, outside steady state).
- **0 validation, 0 leak** (`ResourceTracker`), **NativeAOT** PASS.
- **Unit tests**: `ExtrudedShadowFrustum` — (i) keeps a lateral off-screen caster whose shadow projects
  into view, (ii) drops an off-screen caster whose shadow cannot enter, (iii) keeps every on-screen
  caster, (iv) ε/parallel-light → keep (conservative); `AggregateBounds` correct under translation; sort
  `(material,mesh,tie-break)` batching + determinism (forward vs reverse); run/batching logic (batch
  count, `firstInstance`/`instanceCount` correctness).

## Risks & watch-items

- **F4 — `baseInstance` on MoltenVK/Apple**: the only real portability risk (non-zero `firstInstance`).
  Not blocking on desktop → dedicated macOS gate (joins the already-outstanding Linux/macOS debt).
- **F3 — anti-popping test**: `CollectRenderLists_KeepsAnOffScreenCasterInTheShadowList` must be revised
  under the tighter semantics — decided explicitly in V5 before merge (either the caster is a genuine
  shadow-into-view projector and the test stays green, or the test is adjusted).
- **F8 — `AotRootingSmoke`** calls `CollectRenderLists` internally (`GameWorld.cs:159`) and asserts
  `render.Count == 8` — an AOT-gate path. When the extruded-frustum arg is added in V5, that internal
  call must be updated (pass a wide extruded frustum); the `Count == 8` assert stays valid. (An
  independent spec reviewer misread this method as not calling `CollectRenderLists`; re-verified against
  `GameWorld.cs:110-164` — the call and assert are real.)
- **Extruded-frustum plane sign** is pinned by tests (i)/(ii)/(iii) before any merge (a sign error =
  false negative = popping).

## Decision Log

| # | Decision | Rationale |
|---|---|---|
| D0 | Scope stops at CPU cull; GPU-driven is P3-M2 | Human decision. CPU cull retained → persistent slots give ~0 FPS gain now; full GPU-driven is a coherent later milestone. Avoids over-building. |
| D1 | SSBO + `firstInstance`, per-frame compacted (not persistent) | Delivers the whole draw-call collapse at minimal risk; the SSBO/shader/batching infra is reused verbatim by the future GPU-driven feed. |
| D2 | `(material, mesh, tie-break)` sort key, tie-break un-truncated | Enables (material, mesh) batching while preserving deterministic ordering. |
| D3 | Extruded wedge ANDed with unchanged ShadowFit | Pays debt #2 while keeping the helmet capture byte-identical (shadow-map sizing untouched). |
| D4 | Per-frame `AggregateBounds` (recompute, not dirty-track) | Always-correct, 0-alloc, negligible at this scale; dirty-track deferred. |
| — | Board at `.absolute-human/`, prose in French, code/docs English | Project conventions (CLAUDE.md) override the absolute-work skill defaults. |
