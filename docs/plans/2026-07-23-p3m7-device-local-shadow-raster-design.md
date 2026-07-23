# P3-M7 — Device-local GPU buffers + shadow raster 4× reduction

> Status: **draft** (2026-07-23, session 20). Closes the two perf debts P3-M6 deferred:
> [BACKLOG.md](../BACKLOG.md) §1 (host-visible → device-local, "principal levier perf restant") and
> §2.0bis (shadow raster ~4× overlap). Foundations: persistent slots + GPU shadow cull (P3-M6),
> GPU-driven scene cull (P3-M4), CSM atlas (P3-M5).

## 1. Goal & scope

P3-M6 moved both culls onto the GPU but left two measured/known costs, confirmed by the double audit:
- **PCIe traffic (§1)**: the persistent candidate buffer and the compute-produced instance buffers are
  **host-visible**. On a discrete GPU without ReBAR, the GPU's atomics/writes to that memory cross PCIe
  per dispatch — a large part of the ~15 ms animated bench.
- **Shadow raster 4× (§2.0bis)**: the four cascade cull volumes are the ortho boxes of overlapping slice
  spheres, so nearly every caster enters all four → ~4× the depth geometry rasterized.

**Human decision (session 20): one milestone, two waves, green light between.**
- **(A) W1-W2 — device-local buffers** (lower risk, no visual change).
- **(B) W3-W4 — shadow raster 4× reduction** (touches shadow correctness → visual-gate risk).

**In scope:**
- (A) An **async intra-frame** `CommandList.CopyBuffer` (the existing `GpuUploader` is synchronous, load-time
  only); the **big** buffers become **device-local** fed by per-frame-in-flight host-visible **staging**:
  the persistent candidate buffer and the two instance buffers (scene, shadow).
- (B) A **per-cascade downstream cut plane** in the shadow cull so cascade boxes **tile in depth** instead
  of nesting — cutting the overlap on the far side, where it is worst, without touching the upstream margin.

**Out of scope → stays in [BACKLOG.md](../BACKLOG.md):**
- **Args buffer stays host-visible** (§1): it is tiny (a handful of `DrawIndexedIndirectCommand`), the compute
  atomics on it are negligible PCIe, and `ReadBackSceneVisible` (the GPU==CPU gate) reads it host-side via
  `MappedSpan`. Making it device-local would force a Debug readback copy for no measurable gain.
- **Full per-cascade `UpstreamExtent`** (§2.0bis MINEUR-2, the "a tower's shadow vanishes as you approach"
  case): the generous fixed κ upstream margin is kept. B only adds the *downstream* bound. The upstream
  sophistication remains deferred (rarer, and it is the part that risks dropping a real caster's shadow).
- MultiDrawIndirect; the shadow cull GPU==CPU readback gate (test debt, §2.0bis).

## 2. Graphics layer additions (`Agapanthe.Graphics`)

```csharp
// Async buffer→buffer copy recorded on the frame command buffer (sync2 transfer). Distinct from GpuUploader,
// which submits + waits a fence (load-time only). One or more regions (offset/size) for dirty-range copies.
void CommandList.CopyBuffer(GpuBuffer src, GpuBuffer dst, ReadOnlySpan<BufferCopyRegion> regions);
readonly record struct BufferCopyRegion(ulong SrcOffset, ulong DstOffset, ulong Size);
```

- `BufferSync` gains a **transfer** scope (stage `COPY`/`TRANSFER`, access `TRANSFER_READ`/`TRANSFER_WRITE`) so
  `BufferBarrier` can express staging-write→transfer and transfer→compute/vertex hazards.
- Device-local targets add `BufferUsage.TransferDst`; staging sources are host-visible `TransferSrc` (reuse the
  existing `MemoryDomain.DeviceLocal` + `ToVkUsage` path — device-local buffers already auto-gain `TransferDst`
  per `GpuBuffer.cs`). First use of the transfer stage/flags on the per-frame path → **check each at its first
  VUID on MoltenVK** (portability rule); on MoltenVK (unified memory) device-local == host-visible cost, so the
  copy is a redundant-but-harmless intra-memory transfer (acceptable; the win is Windows/discrete).

## 3. W1-W2 — device-local (A)

### 3.1 Persistent candidate buffer (`PersistentInstanceBuffer`)
- Each of the F copies becomes a **pair**: a **persistent** host-visible **staging** buffer (the current mapped
  buffer, kept per copy for the buffer's whole life — never reused for anything else) + a **device-local** buffer
  (`TransferDst | Storage`).
- **Why the staging must stay persistent** (correctness, self-review): the P3-M6 replay model assumes the copy
  *retains* its unchanged slots between frames and only the dirty slots are rewritten. That only holds if the
  bytes we copy *from* are themselves the retained mirror. So the per-copy staging buffer IS the mirror: `Sync`
  applies the mirror fold + full-rewrite/dirty-replay into it exactly as today, then records
  `CopyBuffer(staging → device-local)` for **only the ranges it just wrote** — never a reused scratch, never a
  full copy on a replay frame:
  - **full rewrite** → one region covering `count` candidates;
  - **replay** → one `BufferCopyRegion` per replayed slot (contiguous 96 B each). Because staging and device-local
    are both persistent and byte-for-byte identical after each `Sync`, copying only the dirty ranges keeps the
    device-local buffer a faithful mirror — the untouched device-local bytes are already correct from prior frames.
    A coalescing pass (merge adjacent slots into one region) is deferred unless the region count bites (§3.4).
- The cull binds the **device-local** buffer. A `BufferBarrier(deviceLocal, TransferWrite → ComputeRead)` between
  the copy and the two cull dispatches (recorded once, before both, since both read it).
- The `_bufferVersion`/`CopySyncState` bookkeeping is unchanged — it drives *which staging bytes are fresh*, and
  the copy mirrors exactly those bytes. The §5 invariant (P3-M6) still holds: a copy's staging AND device-local
  are written only after its frame's fence (the fence-wait is per frame-slot, and both buffers of a pair belong to
  one slot).

### 3.2 Instance buffers (scene, shadow) — `InstanceBufferRing`
- The compute cull writes them and the vertex stage reads them — **no host access** → straight to
  **device-local** (`Storage`, no staging needed; the GPU both writes and reads). Removes the largest per-frame
  host-visible write surface (up to `4×totalCasters` matrices for shadows). `EnsureCapacity` just allocates
  device-local. `Compact` (the old CPU-write path) is already unused post-P3-M6; confirm and drop if so.

### 3.3 What stays host-visible
- **Args** rings (scene, shadow): compute-written + indirect-read + **host-read by `ReadBackSceneVisible`** → stay
  host-visible (scope decision §1). **BatchBase / meshBase / cascade-planes** uploads: tiny, CPU-written each
  frame, read by compute → stay host-visible (staging them would cost more than it saves).

### 3.4 Gate (W1-W2)
Bit-identical mono `4848F93F`, GPU==CPU MATCH, 0 alloc/frame, 0 leak, 0 validation, NativeAOT PASS.

**Where the ms win actually shows — be honest (self-review).** Two device-local moves with different profiles:
- The **instance buffers** (§3.2) become device-local *reads by the vertex stage* — that helps **every frame**,
  animated or not (the raster reads GPU-local memory instead of PCIe-backed host memory).
- The **candidate buffer** (§3.1) only wins when the per-frame *copy* is smaller than the old full host-visible
  write. On a **static/mostly-static** scene the dirty set is tiny → a few small region copies → clear win. On the
  **animated `grid:100x100` bench (10k dirty/frame = worst case)** the copy is `count` regions ≈ a full copy, so
  the candidate half may show **little or even negative** change (§3.4 note + the N-small-copies risk below). That
  is expected, not a bug: the bench is the adversarial case. **Measure BOTH a static scene (drop:N settled, or a
  frozen grid) and the animated bench**, and report each honestly; the milestone's value is the static/typical
  case, not the pathological all-moving one.
- **N-small-copies risk**: 10k dirty slots ⇒ 10k `BufferCopyRegion`s in one `vkCmdCopyBuffer`. If that dominates,
  fall back to a **single full-range copy when the dirty count exceeds a threshold** (e.g. > count/2) — cheaper
  than many tiny regions, and it degrades gracefully to the old full-write cost. Decide by measurement, not upfront.
- If neither scene shows a win, that is a **finding to surface** (device-local was the wrong lever on this
  hardware), not something to paper over — the whole point of A is the measured PCIe drop.

## 4. W3-W4 — shadow raster 4× reduction (B)

### 4.1 The asymmetry (why this is low-risk)
A cascade's cull volume is the ortho box of its slice sphere: `2r × 2r` laterally, `[near, eyeDistance+r]` deep
along the light (`eyeDistance = κr`, κ=4). Because the far cascades have large radii, their boxes reach **far
downstream** (away from the light) and **backward toward the camera**, swallowing the near field → a near caster
enters all four boxes. The overlap is dominated by the **downstream** extent, not the upstream margin.

### 4.2 The fix: a per-cascade near-side view-depth cut plane
> **Implementation note (corrected after the graphics audit).** The cut is on the **NEAR (view-depth) side**, not
> the "downstream/far" side the earlier draft described. What makes a far cascade swallow the near field is that its
> huge ortho box reaches *toward the camera* (low view depth); rejecting casters whose **view depth is well BEFORE
> the cascade's slice starts** is what removes that overlap. Cutting the far side would not fix it. The prose below
> is kept for the reasoning but read "cut" as the near-side view-depth plane the code implements.

Add, to each cascade's shadow-cull test, a **7th plane** that rejects a caster whose **view depth is before this
cascade's slice** (less than `sliceNear[c] − margin`), so it is culled from cascade `c` and kept by the nearer
cascade it actually belongs to. **Cascade 0 is exempt** (an all-keeping tautology plane): cutting its near side
would drop behind-camera casters whose shadow still reaches the view (the P3-M6 anti-popping guarantee). The
**light-direction upstream** margin κ is untouched — this cuts only the view-depth overlap, not a caster's height
above its slice. Result: cascade volumes **tile in view depth** instead of nesting → each caster lands in ~1
cascade (+overlap only in the thin margin bands), cutting the rasterized shadow geometry toward ~1×.

- **Camera-only, no circularity**: the cut plane is derived from the cascade's own slice depth (`splitViewDepths`,
  already computed camera-only), never from caster bounds. The P3-M2 wedge circularity is not reintroduced.
- **Where the plane lives**: appended as the 7th of each cascade's `shadow_cull.comp` planes (the shader loops
  `p < 7`, base `casc*7`). The plane is `(forward, −(dot(forward, eye) + sliceNear[c] − margin))` in the frame's
  camera-relative space, where `forward` is the camera view direction and `eye = view.EyeRelative`.
- **Instance-buffer shrink**: with each caster in ~1 cascade, the shadow instance buffer's live content drops
  from ~4×casters toward ~1×; the reservation stays `n×totalCasters` (worst case), but the raster and the
  compacted writes shrink. Draw-call count is unchanged (still one indirect per (cascade, mesh-batch) region).

### 4.3 Correctness risk & gate
The **failure mode to watch**: a caster whose view depth is before cascade `c`'s slice but whose shadow reaches
into that slice could be cut. The margin (`0.25 × slice thickness`) absorbs the common near-overhead case, and it
exceeds the 10% inter-cascade fade band, so no per-caster seam appears at a cascade boundary (graphics audit).
- **Low-sun caveat (graphics audit 🟠)**: the margin is indexed on the *slice thickness*, not the *shadow length*.
  With a **low / grazing sun**, shadows lengthen and the caster↔shadow view-depth offset grows for *every* caster
  (not only tall ones), so a light leak could appear on a far cascade's receivers. This is the deferred
  per-cascade `UpstreamExtent` concern (§2.0bis MINEUR-2), out of scope — but the **human visual gate MUST include
  a low-sun take** (`Lights.Directional` near the horizon) to prove no leak before signing B, not only the bench's
  steep sun.

**Gate**:
- **NOT bit-identical** (self-review — the P3-M6 lesson): the cut plane changes *which cascades* hold a caster,
  which can change which atlas tile samples it at an inter-cascade fade boundary → a few pixels may move. Gate B
  is the P3-M6 GPU-shadow-cull gate: **visually identical + per-cascade caster count justified**. (W1-W2 keeps
  bit-identical because A changes no geometry; only B relaxes it.) The mono scene is expected to stay
  `4848F93F` in practice — the lone centred caster sits well inside cascade 0, far from any cut plane or fade band
  — but that is an *observation to confirm*, not a *required* gate; a sub-texel move at a fade boundary is
  acceptable and justified.
- on the grid bench, the shadow instance count per cascade (a new diagnostic, see §5) drops ~4× toward ~1×
  **with no visible shadow loss** — the human visual verdict is the real gate here (B is the risky wave).
- A unit test on the depth-bounded cascade assignment (a caster at depth d lands in the expected cascade[s]).

## 5. Testing / diagnostics
- Reuse all P3-M6 gates (bit-identical, GPU==CPU scene, 0 alloc/leak/validation, AOT PASS).
- **New**: a shadow-cull **region-count readback** (Debug/verify-only, symmetric to `ReadBackSceneVisible`) —
  sum the shadow args `instanceCount`s per cascade. Serves both the B raster-drop measurement AND closes the
  P3-M6 test-debt (shadow cull had no GPU==CPU gate). This lands in W3-W4.
- **ms measurement** on `grid:100x100` AOT before/after each wave (A: PCIe drop; B: raster drop), reported
  honestly (the animated bench is the worst case).
- Unit test: `CommandList.CopyBuffer` region math; depth-bounded cascade assignment.

## 6. Decision log
- **Two waves, green light between A and B** (human) — A is safe perf plumbing, B touches shadow correctness.
- **Only the big buffers go device-local; args + tiny uploads stay host-visible** — args is host-read by the
  GPU==CPU gate and negligible in size; staging it would cost more than the PCIe it saves.
- **Instance buffers go device-local with no staging** — GPU writes and reads them; no host access.
- **B = downstream cut plane only, upstream κ kept** — the overlap is asymmetric (downstream-dominated), so a
  far cut plane recovers most of the 4× at minimal correctness risk and with no reintroduced circularity; the
  full per-cascade `UpstreamExtent` (the tall-caster case) stays deferred.
- **Async `CommandList.CopyBuffer` is new** — the existing `GpuUploader` is synchronous (fence wait), correct for
  load-time but fatal to the per-frame hot path.
- **Shadow region-count readback added in W3** — measures B *and* closes the P3-M6 shadow-cull gate debt.

Amended after author self-review (no independent scored review this session — subagent hit the session limit; the
human approval gate stands in for it):
- **Per-copy staging stays persistent and IS the mirror** (§3.1) — the P3-M6 replay model needs the copy source to
  retain unchanged slots; a reused scratch would corrupt the device-local buffer on a replay frame.
- **Gate B is NOT bit-identical** (§4.3) — the cut plane can move a caster between cascades, shifting a few pixels
  at a fade boundary (the P3-M6 float-cull lesson); B uses "visually identical + count justified".
- **The candidate-buffer PCIe win shows on static/typical scenes, not the all-moving bench** (§3.4) — measure
  both, report honestly, and fall back to a single full-range copy when the dirty count is large.
