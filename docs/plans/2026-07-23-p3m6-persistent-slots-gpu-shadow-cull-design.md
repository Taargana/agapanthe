# P3-M6 — Persistent dirty-tracked slots + GPU shadow cull

> Status: **draft** (2026-07-23, session 19). Candidate **A** of the S18 resume.
> Closes two fresh debts in one milestone: [BACKLOG.md](../BACKLOG.md) §1 (C) (persistent
> dirty-tracked slots, deferred from P3-M4) and [§2.0bis](../BACKLOG.md) (P3-M5 per-cascade
> shadow cull nearly inoperative). Foundations: GPU scene cull + draw indirect (P3-M4),
> CSM 4-cascade atlas (P3-M5), scheduler + render seam (P3-M2), quantized origin (P2-M4).

## 1. Goal & scope

P3-M4 moved the scene cull onto the GPU but, as a foundation cost, made the **CPU do more per
frame**: `GameWorld.CollectRenderLists` re-narrows / re-bakes / **re-sorts** (radix, 8 passes +
gather of ~104-byte structs) and `Renderer.CullSceneOnGpu` **re-packs** into `SceneCandidate`
(96 B) then **re-uploads ~960 KB** for all **10 001** candidates — where P3-M1 only touched the
visible ones. P3-M5 (CSM) stacks a second O(n): `CollectShadowCasters` builds **four managed
lists (~12 MB)** of casters (the cascade volumes overlap → nearly every caster enters all four)
with a per-cascade scan + upload.

Both reduce to "sort/upload O(n) per frame". This milestone repays them.

**In scope:**
- **(W1) Persistent scene candidate buffer + per-entity dirty tracking.** A stable dense slot per
  drawable; a persistent host-visible GPU buffer indexed by slot; a **structural rebuild** (rare)
  vs a **per-frame incremental patch of only the dirty slots** (no sort, no repack, no full upload).
- **(W2) GPU scene cull reads the persistent buffer** instead of a per-frame candidate upload.
- **(W3) GPU shadow cull.** A `shadow_cull.comp` replaces `CollectShadowCasters` + the CPU concat
  in `RecordShadowPass`; the four managed caster lists disappear.

**Out of scope → stays in [BACKLOG.md](../BACKLOG.md):**
- **Shadow raster ~4×** (§2.0bis MINEUR-1/2): the cascade instance buffer keeps its `4 × casters`
  size; bounding cascades in depth to cut the raster touches `UpstreamExtent`-per-cascade (shadow
  correctness) → separate item, real visual-gate risk. This milestone removes the CPU + managed-memory
  cost of the overlap, not the GPU raster cost.
- **Device-local buffers** (§1): the persistent buffer stays **host-visible mapped** — dirty
  tracking already obviates device-local's benefit (we no longer re-upload everything) and it fights
  the scattered small-write pattern of dirty patches. Deferred, with the Debug `ReadBackSceneVisible`
  isolation it requires.
- MultiDrawIndirect; `SortKey` without depth; the 16-bit mesh/material ceiling.

## 2. Human decisions (locked — do not re-litigate)

Instructed via absolute-brainstorm this session; recorded in the approved plan.
- **One milestone P3-M6**, in waves: W1-W2 persistent scene slots, then **[human green light]**, then
  W3 GPU shadow cull (reuses the persistent buffer).
- **(C) = full per-entity dirty tracking** — repays both halves (sort *and* upload) — plus a **static
  proof scene** (the bench animates everything, so the upload win shows only on a mostly-static scene).
- **W3 = GPU compute shadow cull only.** Raster 4× deferred.
- **Persistent buffers host-visible mapped.** Device-local deferred.

## 3. Enabling fact (measured in code, not a fork)

`GameWorld.ComposeModel` / `WorldSphere` already produce coordinates relative to
**`RenderView.Origin`** (the 1024 m quantized cell), **not** to the eye — the eye lives in
`RenderView.View` via `EyeRelative`. So a drawable's baked model matrix and camera-relative sphere are
**already stable frame-to-frame** unless (a) the entity moves, or (b) the origin re-snaps (the camera
crosses a cell boundary). **No GPU coordinate-space change is required**; the persistent buffer stores
exactly what these two functions produce today.

Corollary: the origin re-snap is a **global invalidation** — every slot must be re-baked. It is a
structural rebuild trigger (§6).

## 4. Data model

### 4.1 Candidate struct (stays 96 B — repurpose the three current pads)

`scene_cull.comp` today: `mat4 model; vec4 sphere; uint batchId; uint pad0,pad1,pad2;` (96 B).
New layout (same size, same std430):

```
struct Candidate {         // 96 B, std430
    mat4 model;            //  0  camera-relative-to-cell model matrix (ComposeModel output)
    vec4 sphere;           // 64  xyz = cell-relative centre, w = radius
    uint sceneBatchId;     // 80  material-major batch index (scene cull)
    uint shadowBatchId;    // 84  mesh-major batch index (shadow cull)
    uint flags;            // 88  bit 0 = casts shadow (drawable NOT tagged NoShadowCast)
    uint pad0;             // 92
};
```

### 4.2 Module ownership — `SceneCandidate` moves `Rendering → Core`

The candidate struct lives today in `Agapanthe.Rendering` (`SceneCandidate.cs`), which the World does
not reference. Since the World must now **fill** candidates (§6), `SceneCandidate` **moves down to
`Agapanthe.Core`** (blittable math, like `RenderItem` — no `Vk*`, no dependency-graph violation) and
gains the two `uint` fields + `flags` from §4.1. `Renderer` and `scene_cull.comp` bind it unchanged.

### 4.3 Slot component (AOT-rooted, in the archetype at creation)

- New component `[Component] [StructLayout(Sequential)] struct InstanceSlot { int Value }`. Its value =
  the drawable's index in the persistent sorted candidate buffer (material-major). Assigned/refreshed
  at each **structural rebuild**; **stable between rebuilds** — which is what makes a per-entity dirty
  patch address a fixed slot (holds precisely because `SortKey` is position-independent; **breaks the
  day depth enters `SortKey`**, backlog §0 — recorded as a validity condition).
- **AOT rooting**: add `Root<InstanceSlot>()` to `ComponentRegistry` (the P2-M0 constraint — an
  unrooted component array fails at runtime under NativeAOT with no publish warning); it must fall
  under the reflection completeness test and `AotComponentProbe` gate, exactly as `NoShadowCast`
  (P3-M5) did. `InstanceSlot` is part of the drawable archetype **at `MaterialiseDrawable`** (not a
  late `Add`, which would move the archetype and allocate).

### 4.4 Persistent set (Core type, owned by the Renderer, filled by the World)

Mirrors the `RenderList` ownership pattern. Each frame the World emits **exactly one of**:
- a **full candidate array** (structural rebuild path, §6), plus the freshly built **scene batch
  table** (material-major: (material, mesh, offset, count)) and **shadow batch table** (mesh-major:
  (mesh, meshBatchBase, count)); or
- a **dirty patch list** `(slot, model, sphere)[]` (incremental path) — only `model`/`sphere` change
  when an entity merely moves; its `sceneBatchId`/`shadowBatchId`/`flags` are stable, so the hot path
  never recomputes batch ids.

All buffers reused, `Clear` keeps capacity → 0 alloc steady state. The **batch tables are constant
between rebuilds** (uploaded once per rebuild; only the per-frame args buffer is re-zeroed, §7/§8).

### 4.5 Dirty tracking (three mutation surfaces, all internal to the World)

A drawable is marked dirty (its `InstanceSlot` enqueued) by whatever mutates its transform:
- `AnimateDrawables<TAnimator>` (`GameWorld.cs:563`) — every visited entity (the bench spin).
- Physics writeback (`GameWorld.Physics.cs:162`) — every body that moved; the scatter already holds
  `_pEntity[k]`, so it reads that entity's `InstanceSlot` (`Get<InstanceSlot>()`) to enqueue it. This
  requires the **`SpawnBody` path to include `InstanceSlot` in the body archetype** (as
  `MaterialiseDrawable` does for imported drawables), or the `Get` throws at the first physics step.
- `PropagateTransforms` (`GameWorld.cs:603`) — every recomputed hierarchical entity.

Structural change raises `_structuralDirty`: spawn/despawn (the `FlushStructuralChanges` barrier),
mesh/material edit, and **origin re-snap** (detected when `RenderView.Origin` differs from the last
rebuild's origin).

## 5. Double-buffer invariant (the correctness core — the audit checks this)

The persistent buffer is **read by the in-flight frame's compute cull** while the CPU patches it for
the next frame → host-write vs GPU-read hazard. Worse, a structural rebuild **reassigns slots** (a
spawn/despawn shifts the material-major indices): a *stale* copy (old per-slot batch id) consumed with
the **new** batch table / `batchBase` (uploaded fresh each frame) would atomic-compact into the wrong
regions → out-of-bounds writes / ghost instances. So the copies and the batch table must stay
**version-coupled**.

**Design decision: an authoritative CPU mirror + sync-before-use.**

- Rendering owns `Candidate[] _mirror` (the CPU source of truth), `GraphicsDevice.FramesInFlight`
  physical GPU copies, a flat `int[] _slotCountdown` (0-alloc, sized to `totalSlots`), a
  `uint _structuralVersion`, and per-copy `uint _copyVersion[F]`.
- The World's per-frame output is applied to `_mirror` first: a **structural rebuild** replaces
  `_mirror` wholesale + `_structuralVersion++`; a **dirty patch** writes `_mirror[slot].model/sphere`
  and sets `_slotCountdown[slot] = F`.
- **Sync the consumed copy `c = frame % F` *before* recording its cull**:
  - if `_copyVersion[c] != _structuralVersion` → **full rewrite** of copy `c` from `_mirror`, upload
    the (rebuild's) batch table / `batchBase` / re-zeroed args, `_copyVersion[c] = _structuralVersion`;
  - else → **replay** only the slots with `_slotCountdown > 0` into copy `c` (write `_mirror[slot]`),
    decrement their countdown; re-zero the args (batch table unchanged).
- Because the copy is **always synced to `_structuralVersion` before use**, the batch table uploaded
  that frame always matches the copy's slot assignment — version coupling is automatic. A structural
  rebuild that lands while F frames are in flight simply full-rewrites each copy as its turn comes
  (after its fence), so **no copy is written in flight and no stale copy is ever consumed** — and no
  position pop at origin re-snap (the consumed copy is full-rewritten to the latest bake).

Writing a copy only after its fence is the same guarantee `InstanceBufferRing` already relies on. A
test forces a spawn+despawn while F frames are in flight and asserts every consumed copy carries the
current slot assignment (no OOB, no ghost). `_slotCountdown` is a flat array, never a `Dictionary`
(0-alloc gate).

## 6. W1 — persistent slots + dirty tracking

Rewrite `GameWorld.CollectRenderLists` into two regimes:

**Structural rebuild** (when `_structuralDirty` or origin changed):
1. Query all drawables → `WorldSphere` + `ComposeModel` + `ComposeSortKey` (material-major) + the
   shadow key (mesh-major) + the casts-shadow flag.
2. `RenderList.SortByKey` (reuse the radix) into material-major order.
3. Assign `InstanceSlot.Value = sorted index`; refresh the GlobalId→slot map.
4. Build the scene batch table (material-major runs) and the shadow batch table (mesh histogram →
   `meshBatchBase`/`count`).
5. Emit the full candidate array + both batch tables to Rendering, which replaces `_mirror` and
   bumps `_structuralVersion` (§5).
6. Clear `_structuralDirty`, record the rebuild origin, clear the dirty queue (subsumed by the full
   emit).

**Incremental** (otherwise):
1. For each dirty slot: recompute `model` + `sphere` from the entity's (now updated)
   `WorldTransform`/`WorldPosition` against the current origin; enqueue `(slot, model, sphere)` (batch
   ids/flags are stable between rebuilds, never recomputed here).
2. Rendering applies the patch list to `_mirror`, then syncs the consumed copy (§5).

Rendering owns the persistent host-visible copies (model them on `InstanceBufferRing`: doubling grow,
N+2 deferred deletion) plus `_mirror`. It exposes `Rebuild(fullArray, sceneBatches, shadowBatches)` and
`Patch(updates)`, both feeding the §5 sync.

## 7. W2 — GPU scene cull reads the persistent buffer

- `scene_cull.comp`: binding 0 becomes the **persistent** candidate buffer (was a per-frame upload);
  the shader body is unchanged (sphere vs six planes, atomic compaction per `sceneBatchId`).
- `Renderer.CullSceneOnGpu`: drop the per-frame `_candidateScratch` pack + `_sceneCandidates.Upload`;
  bind the persistent copy. `batchBase` rides the **per-copy ring** (`frame.Slot`, like the copy
  itself), uploaded with that copy's full rewrite (§5) — **constant between rebuilds**, not per frame.
  Only the **args buffer is re-zeroed each frame**: re-upload the `DrawIndexedIndirectCommand[]` with
  `instanceCount = 0` (the CPU knows `indexCount` from the batch table; the compute fills the counts via
  atomics) — the existing per-slot args ring. The instance/args output buffers stay per-frame-in-flight.
- Gate `ReadBackSceneVisible` + `AGAPANTHE_CULL_VERIFY=1` reused unchanged: GPU visible == CPU.

## 8. W3 — GPU shadow cull

Replace `CollectShadowCasters` (four CPU lists) and the CPU concat in `RecordShadowPass`:

- New `shadow_cull.comp`, one invocation per candidate: test `flags` bit 0 (casts shadow), then the
  **four cascade frusta**. For each cascade `c` the candidate intersects, `atomicAdd` into the region
  **(cascade c, mesh-batch m)** at base `c × totalCasters + meshBatchBase[m]`, and write the compacted
  model. `meshBatchBase`/`count` come from the mesh-major shadow batch table (built at structural
  rebuild) → contiguous regions, order-independent, exactly the `scene_cull` trick generalised per
  cascade. Bindings (set 0): `0` persistent candidates (readonly, shared with scene cull), `1` out
  shadow instances, `2` shadow args (`DrawCmd[]`), `3` `meshBatchBase[]` (readonly), `4` cascade planes
  **as a flat storage buffer** (`vec4[]`, `cascade × 6` planes) — an SSBO, not a UBO: the count is
  dynamic and it mirrors `scene_cull`'s storage-buffer style, which also sidesteps any std140 UBO VUID
  on MoltenVK. The four loop bounds (cascade/shadow-batch/caster/candidate counts) ride a 16-byte push
  constant. The candidate buffer is read-only in both culls → no barrier needed between the two dispatches.
- Args are **re-zeroed each frame** before the dispatch (the compute fills `instanceCount` via
  atomics), one `DrawIndexedIndirectCommand` per (cascade, mesh-batch). The shadow draw loop issues one
  `DrawIndexedIndirect` **per region** into its atlas tile (P3-M4 W0 conversion, kept); a region the
  cull left at `instanceCount = 0` is a **GPU no-op** — the CPU cannot know which are empty without a
  readback (backlog §1 "empty batches inflate draw calls", accepted). Compute→indirect and
  compute→vertex `BufferBarrier`s guard the args and instance buffers, as in `scene_cull`.
- The shadow instance buffer keeps size `4 × totalCasters` (raster 4× deferred). The **~12 MB managed
  caster lists disappear** — the sanctioned §2.0bis win.
- `FrameOrchestrator.SceneViewSystem`: drop `CollectShadowCasters` and the four `_cascadeCasters`
  lists; pass the cascade frusta to the shadow cull dispatch instead.
- **Cleanup**: remove the wedge dead code still present (`ShadowFit.ComputeLightViewProj`,
  `ExtrudedShadowFrustum` + its tests, `Renderer.ComputeFrustumSphere`, `ShadowCasterDistance`) —
  verify what actually remains before deleting.

## 9. Testing strategy

Reuse the existing gates; add unit tests for the new invariants (World tests run headless, GPU-free).

- **Slot stability**: between structural rebuilds, an entity's `InstanceSlot` does not change; a dirty
  patch writes that slot only.
- **Dirty invalidation**: a moved entity is enqueued; a static entity is not. After a settle, the dirty
  queue is empty.
- **Structural triggers**: spawn/despawn/mesh-material edit/origin re-snap each force a full rebuild and
  a fresh slot assignment; the candidate array matches a from-scratch build (equivalence oracle).
- **Double-buffer invariant (§5)**: force a spawn+despawn while `FramesInFlight` frames are in flight;
  assert every consumed copy carries the current slot assignment (no OOB, no ghost, no stale batch id) —
  not merely that a change converges after F frames.
- **AOT rooting**: `InstanceSlot` is registered in `ComponentRegistry` (reflection completeness test) and
  survives the `AotComponentProbe` gate.
- **Shadow batch table**: mesh histogram → contiguous per-mesh regions; a caster in k cascades appears
  k times, once per cascade region.
- **Bench (animated)** `AGAPANTHE_SCENE=grid:100x100`, Release JIT **and** NativeAOT: scene draws **2**,
  shadow = (cascade × mesh-batch) regions; **0 alloc/frame**; `AGAPANTHE_CULL_VERIFY=1` → scene GPU ==
  CPU; measure the ms drop vs 11.4 ms (P3-M5).
- **W3 gate = visually identical + per-cascade caster count justified** (the P3-M4 W1 gate, not
  bit-identical): moving the shadow cull to a float GPU test can include/exclude a caster differently at
  a cascade-plane margin (FMA/rounding), changing a shadow-edge pixel. The **mono** capture stays
  **bit-identical `9790D95D`** because the single centred caster sits on no cascade plane — assert that,
  do not force the shader to reproduce `Frustum.Intersects` bit-for-bit (fragile cross-driver).
- **Static proof scene** (`AGAPANTHE_SCENE=drop:N` once settled, or a frozen grid): dirty queue empty →
  per-frame patch/upload ≈ 0.
- **Project gates (blocking)**: 0 validation messages, 0 leak, 0 warning, `AotComponentProbe` + Sandbox
  NativeAOT PASS, xUnit green.

## 10. Decision log

- **One milestone, waves, green light between W2 and W3** — the reprise frames A as "two debts, one
  blow"; the wave split keeps the project's green-light discipline.
- **Full per-entity dirty tracking** over "cache sort/batch only" — the latter leaves the upload half of
  §1 open ("only moved entities write their slot"); a half-done milestone we'd reopen.
- **Static proof scene** — the bench animates all 10k, so dirty tracking is *correct* there (everything
  genuinely moved) but shows no upload win; a mostly-static scene makes the win visible and gateable.
- **GPU shadow cull only, raster 4× deferred** — moving cull to compute removes the CPU scan and the
  12 MB managed lists (the "disparaît avec le vrai cull" of §2.0bis); cutting the raster needs
  depth-bounded cascades + per-cascade `UpstreamExtent`, which risks the visual gate (missing shadows).
- **Host-visible persistent buffer, device-local deferred** — dirty tracking already cuts the per-frame
  upload to moved entities, obviating device-local's main benefit; device-local fights scattered writes
  and would need the Debug `ReadBackSceneVisible` isolation.
- **Candidate stays 96 B** — the three existing pads absorb `shadowBatchId` + `flags`; no bandwidth
  regression on the persistent buffer.
- **Cascade planes in a storage buffer (SSBO), not push constants** (W3) — 24 planes exceed the 128 B push
  range; an SSBO (over a UBO) keeps the dynamic count and sidesteps std140 UBO VUIDs on MoltenVK. Only the four
  loop bounds ride the push constant.

Amended after the scored spec review (session 19, `engine-architect`, 3.6 → NEEDS WORK):
- **Authoritative CPU mirror + sync-before-use** (§5) over a background "trickle" — a structural rebuild
  reassigns slots, so a stale copy consumed with the new batch table would OOB/ghost; syncing the
  consumed copy to `_structuralVersion` before recording makes the batch table match by construction and
  removes the re-snap pop. Flat `_slotCountdown` array, never a `Dictionary` (0-alloc).
- **`SceneCandidate` moves `Rendering → Core`** — the World now fills candidates and must not reference
  Rendering; the struct is blittable math, so the move respects the dependency graph.
- **`InstanceSlot` rooted in `ComponentRegistry`, present at `MaterialiseDrawable`** — the P2-M0 AOT
  constraint (unrooted component array = silent runtime failure); a late `Add` would move the archetype.
- **W3 gate relaxed to P3-M4 W1's** (visually identical + count justified) — a float GPU cull can differ
  at cascade-plane margins; mono stays bit-identical because the centred caster sits on no plane.
- **Slot stability is conditional on a depth-free `SortKey`** (backlog §0) — recorded as a validity
  condition in §4.3, to fail loudly the day transparency adds depth to the key.
