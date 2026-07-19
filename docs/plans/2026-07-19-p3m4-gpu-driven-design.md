# P3-M4 — GPU-driven rendering: compute cull + draw indirect

> Status: **approved** (2026-07-19, session 17). Deferred from P3-M1 ([BACKLOG.md](../BACKLOG.md) §1).
> Foundations: instancing SSBO + CPU cull + batching (P3-M1), scheduler + render seam (P3-M2).

## 1. Goal & scope

Move the per-frame **draw emission** and the **frustum cull** of the opaque scene from the CPU onto
the GPU. Today: CPU cull O(n) → sorted `RenderItem[]` → `InstanceBufferRing.Compact` copies matrices
each frame → CPU loop of (material, mesh) runs → one `DrawIndexed` per run. Target: fill a GPU
indirect-args buffer and issue `vkCmdDrawIndexedIndirect`, and cull+compact in a compute shader.

**In scope (this milestone = A + B):**
- **(A) Draw-indirect plumbing** (W0): `BufferUsage.Indirect`, `CommandList.DrawIndexedIndirect`, a
  buffer/global memory barrier; batch offset in a **push constant** (not `firstInstance`); bit-identical.
- **(B) Compute cull of the SCENE** (W1): per-candidate GPU buffer, a compute shader that frustum-culls,
  compacts per batch, and writes `instanceCount` into the args buffer via atomics.

**Out of scope → [BACKLOG.md](../BACKLOG.md) §1 (human decision, session 17):**
- **(C) Persistent dirty-tracked slots** — the real 100k CPU win (only re-narrow moved entities +
  re-narrow all on origin change). Next milestone. Without it, the CPU still narrows+uploads O(n)/frame.
- MultiDrawIndirect (one draw for all batches); compute cull of the **shadow** pass (stays the P3-M2
  two-pass wedge on the CPU — subtle, not moved this milestone; it still gets W0's draw-indirect).

## 2. Human decisions (locked)

- **Scope A+B**, C deferred (above).
- **Gate W0 = bit-identical** `9790D95D`: plumbing only, behaviour unchanged.
- **Gate W1 = visually identical + visible count == CPU cull.** The float frustum test on the GPU can
  differ from the CPU at plane margins (FMA/rounding), and intra-batch order changes (atomic compaction)
  — neither affects the opaque, depth-tested image. We assert the visible **count** matches the CPU cull
  and justify any pixel diff at plane margins, rather than forcing the shader to reproduce
  `Frustum.Intersects` bit-for-bit (fragile cross-driver / cross-GPU).

## 3. Foundations already instructed (not forks)

- **Batch offset in a push constant, not `firstInstance`** (backlog §1): removes the dependency on the
  `drawIndirectFirstInstance` feature AND neutralises the MoltenVK `baseInstance` risk. `mesh.vert` /
  `shadow.vert` index `instances.model[gl_InstanceIndex + pc.batchOffset]`.
- **Mesh-major shadow key**: already in place (P3-M1), kept.

## 4. Graphics layer additions (`Agapanthe.Graphics`)

```csharp
enum BufferUsage { …, Indirect = 1 << 4 }                       // → VkBufferUsageFlags.IndirectBufferBit
// One indirect draw command, byte-identical to VkDrawIndexedIndirectCommand.
[StructLayout(Sequential)] struct DrawIndexedIndirectCommand
{ uint IndexCount, InstanceCount, FirstIndex; int VertexOffset; uint FirstInstance; }
void CommandList.DrawIndexedIndirect(GpuBuffer args, ulong offsetBytes, uint drawCount, uint stride);
void CommandList.BufferBarrier(GpuBuffer buffer, BufferSync from, BufferSync to);  // sync2 buffer memory barrier
```

`BufferBarrier` covers the compute→indirect and compute→vertex hazards (W1). It routes through the
existing `GraphicsDevice.CmdPipelineBarrier2` with a `VkBufferMemoryBarrier2`; `BufferSync` is a small
engine enum mapping to (stage, access) — mirrors the image-layout `MapState` in `CommandList.cs`. The
args buffer is `Storage|Indirect`; the instance buffer stays `Storage`. First use of these flags/stages
→ **check each at its first VUID on MoltenVK** (portability rule, CLAUDE.md).

## 5. W0 — draw-indirect plumbing (bit-identical)

Keep the CPU cull + batching exactly as today. Where the scene/shadow loops call `DrawIndexed` per run,
instead:
1. Build a host-visible `DrawIndexedIndirectCommand[]` (one per batch): `IndexCount` = mesh index count,
   `InstanceCount` = run length, `FirstIndex`/`VertexOffset` = 0, `FirstInstance` = 0. A reused ring
   buffer per frame-in-flight (like `InstanceBufferRing`), grown by doubling, 0 alloc steady state.
2. Per batch: bind the mesh's vertex/index buffers and the (unchanged) material set, push a `uint`
   **`batchOffset`** = the run's start index into the instance SSBO, then `DrawIndexedIndirect(args,
   batchOffsetBytes, drawCount:1, stride)`.
3. Shaders: `instances.model[gl_InstanceIndex + pc.batchOffset]`. New vertex-stage push constant range
   for `batchOffset` (scene: a `uint` at a free offset alongside the frag debug-view constant; shadow:
   after `lightViewProj`).

Because the offset moves from `firstInstance` (a draw parameter) into the shader via a push constant,
the produced vertices are identical → **capture stays `9790D95D`**. Still one indirect draw per batch
(2 on the default scene) — MultiDrawIndirect is a later collapse.

## 6. W1 — compute cull (scene only)

The World stops filling a pre-culled `RenderItem[]` for the scene and instead produces a **candidate
buffer**: for every drawable in the batch order, its camera-relative float model matrix, its world
bounding sphere (`vec4`), and its `batchId` (dense index into the batch table). The CPU still narrows
double→float and uploads O(n) (that is what (C) later removes), and still builds the **batch table**
(one entry per (material, mesh) run: mesh handle, material set, index count, base offset).

A compute shader (`scene_cull.comp`, one invocation per candidate):
- frustum-test the sphere against the 6 camera planes (planes uploaded in a small UBO/push);
- if visible, `atomicAdd` the batch's `InstanceCount` in the args buffer to get a local slot, and write
  the candidate's matrix into the compacted instance SSBO at `batchBase + slot`.

The args buffer's `InstanceCount`s are **zeroed** before the dispatch (a tiny clear or a compute prepass).
Barriers: compute write → `DrawIndirect` read (args) and → vertex `ShaderRead` (instances). The scene
draw loop then binds per batch and issues `DrawIndexedIndirect` — the CPU never reads the visible count
(except a debug readback to assert the gate). The batch table is CPU-built, so the draw loop knows the
draw count without GPU feedback (no `DrawIndirectCount` needed).

Shadow pass unchanged (CPU two-pass wedge from P3-M2, now drawing via W0's indirect path).

**Gate:** visible count (debug readback of summed `InstanceCount`) == CPU-cull count for the same view;
capture visually identical to the CPU-cull baseline (diff only at plane margins, justified).

## 7. Testing

- **Graphics unit** (GPU-free where possible): `BufferUsage.Indirect` → correct VkFlags; the
  `DrawIndexedIndirectCommand` layout is 20 bytes / matches `VkDrawIndexedIndirectCommand`; `BufferSync`
  → (stage, access) mapping table.
- **Headless captures**: W0 → SHA `9790D95D` (bit-identical) on the default scene + `grid:100x100`. W1 →
  visible-count assertion + visual capture; a `CULL_STATS`-style log of GPU visible count vs CPU count.
- **Bench**: `grid:100x100` Release+AOT → draws == 2 (scene) + shadow draws, 0 alloc/frame. **Honest note
  (both audits):** the frustum *test* moves to the GPU, but without persistent slots (C, deferred) the CPU still
  narrows/bakes/**sorts all N candidates** and uploads them (~960 KB/frame at 10k). So A+B is a **net CPU
  regression at 10k** (the wall migrated from cull to sort+upload, ~8 ms vs ~6 ms P3-M1), not a scaling win —
  the win is (C). This milestone is the GPU-driven *foundation*, and the bench proves correctness (GPU==CPU
  visible set) and 0-alloc, not throughput.
- **NativeAOT**: Sandbox publish + probe unaffected (no new generics-over-struct); the compute path is
  device code.

## 8. Gates (exit criteria)

`dotnet test` green · 0 warning · 0 validation · 0 leak · W0 capture bit-identical `9790D95D` · W1 visible
count == CPU + visually identical · bench `grid:100x100` draws==2 + 0 alloc/frame · NativeAOT PASS ·
double audit (`csharp-lowlevel` + `engine-architect`) PASS · human visual verdict.

## 9. Decision log

- **A+B this milestone, C (persistent slots) next**: the plumbing + GPU cull are the MoltenVK-sensitive,
  self-contained half; persistent dirty-tracked slots carry the real 100k win but the hardest origin-move
  logic — a milestone of its own.
- **Push-constant batch offset, not `firstInstance`**: kills the `drawIndirectFirstInstance` feature
  dependency and the MoltenVK `baseInstance` risk in one move (backlog §1).
- **W1 gate = count + visual, not bit-identical**: a float GPU frustum test cannot be made bit-identical
  to the CPU across drivers without fragility; the visible SET matching + opaque depth test make the image
  correct. W0 stays strictly bit-identical because it changes no visibility decision.
- **Batch table stays CPU-built**: avoids `VK_KHR_draw_indirect_count` (not core 1.2, MoltenVK-uncertain);
  the CPU knows the batch count, the GPU only fills each batch's `InstanceCount`.
- **Shadow cull stays CPU**: the P3-M2 two-pass bounded wedge is subtle; moving it to compute is a separate
  effort. It still draws through the indirect path.
