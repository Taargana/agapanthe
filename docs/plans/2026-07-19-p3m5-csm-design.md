# P3-M5 — Cascaded Shadow Maps (CSM)

> Status: **approved** (2026-07-19, session 18). Fixes the single-cascade ceiling seen at P3-M4 close
> (rectangular footprint + acne on the flat ground). [BACKLOG.md](../BACKLOG.md) §2.

## 1. Goal & scope

Replace the single directional shadow map with **4 cascades**: split the camera frustum into depth
slices, fit one texel-snapped map per slice, so shadow-map resolution stays near-constant from the
camera to the far distance — sharp at the feet AND far away, no more coarse texels / acne on a large
flat receiver.

**In scope:** 4 cascades in a **2×2 atlas** of the existing 4096² map (2048²/cascade); per-cascade fit
(reusing the P3-M2 snap/quantize); per-cascade caster culling (simple, replacing the two-pass wedge);
cascade selection + blend band in the fragment shader; a `NoShadowCast` component so the ground plane
receives but does not cast (tightens every fit, kills the ground self-shadow acne).

**Out of scope → [BACKLOG.md](../BACKLOG.md):** PCSS (§2.1bis); GPU shadow cull (§1); per-cascade
`UpstreamExtent` sophistication (v1 uses a fixed generous setback — see §5); texture-array storage.

## 2. Human decisions (locked)

- **Storage = 2×2 atlas** of the existing 4096² D32 image (2048²/cascade). One `BeginRendering`, clear
  once, 4 viewport-scoped depth draws. Keeps `sampler2D` + manual PCF, no array render-targets, no
  MoltenVK array risk. (Texture-array rejected.)
- **Immutable/comparison samplers = N/A**: the engine already does *manual* PCF (`sampler2D`, not
  `sampler2DShadow`) precisely because MoltenVK forbids mutable comparison samplers. CSM keeps it.
- **Per-cascade caster cull = SIMPLE** (replaces the P3-M2 two-pass wedge): each cascade's footprint is
  its frustum slice (camera-only, no caster dependency → **no circularity**), so casters are culled by a
  plain sphere test against each cascade's light volume. `_casterSpheres`/`CompactShadowCasters`/the F7
  guard are retired in favour of this path.
- **`NoShadowCast` component included**: the ground receives but does not cast → tighter fits, no ground
  self-shadow acne.

## 3. Cascade split

`CascadeSettings { int Count = 4; float Lambda = 0.85f; float MaxDistance = 200f; }`. The split distances
follow the **practical scheme** (blend of logarithmic and uniform, weighted by `Lambda`):

```
dᵢ = lerp( near + (far-near)·(i/N),           // uniform
           near · (far/near)^(i/N),           // logarithmic
           Lambda )
```
over `[view.Near, min(view.Far, MaxDistance)]`. At the default λ=0.85 the result is ≈ **0-8 / 8-19 / 19-48 / 48-200 m**
(cascade 0 at ~1.0 cm/texel). **λ was 0.5 and that was wrong** (audit MAJEUR-1): with a ~0.1 m near plane the
logarithmic term collapses, so a 50/50 blend sits close to the *uniform* split — the real result was
0-25 / 25-52 / 52-90 / 90-200, i.e. a cascade 0 three times too wide (3.2 cm/texel, contact shadow smeared over
~16 cm by the 5×5 PCF). Raising λ costs ~2.5% coarser texels in cascade 3, whose radius is dominated by
`far·tan(fov)` rather than by its own slice width.

## 4. `ShadowFit.ComputeCascades` (`src/Agapanthe.Rendering/ShadowFit.cs`)

```csharp
public static void ComputeCascades(
    in RenderView view, Vector3 lightDirection, in CascadeSettings settings, uint tileResolution,
    Span<Matrix4x4> lightViewProj,   // length Count — filled
    Span<float> splitViewDepths);    // length Count — the far view-space depth of each cascade
```

For each slice `[dᵢ, dᵢ₊₁]`: `FitFrustumSphere` on the slice (already exists, camera-only) →
`QuantizeRadius` + `SnapToTexelGrid` (reused verbatim, snapped to the 2048² *tile* resolution) → the
ortho `lightView·proj` with a **fixed generous upstream setback** (eye at `−dir·(κ·radius)`, depth range
`[0, 2κ·radius]`, κ≈4) so a caster upstream of the slice is not clipped. `splitViewDepths[i] = dᵢ₊₁`.
No caster bounds needed here (decoupled — that is the whole simplification). Reuses `FitFrustumSphere`,
`QuantizeRadius`, `SnapToTexelGrid`.

## 5. World — per-cascade caster lists (`GameWorld`)

`CollectRenderLists` today produces one wedge-culled caster list + `_casterSpheres`. Replace with:
`CollectShadowCasters(cascadeVolumes[], Span<RenderList> perCascade)` — one `RenderList` per cascade,
each drawable added to cascade `i` iff its world sphere intersects cascade `i`'s light volume (a plain
`Frustum.Intersects`, the frusta built from the `lightViewProj[i]`). Skips any entity carrying
`NoShadowCast`. Each list keeps the **mesh-major** key (P3-M1) for instanced depth draws. The scene
candidate path (P3-M4) is unchanged.

`NoShadowCast` — a new tag component (`Agapanthe.World/Components.cs`, `internal`, empty struct or a
`byte` flag), rooted in `ComponentRegistry`. The Sandbox marks the ground plane with it.

## 6. Shadow pass — atlas (`Renderer.RecordShadowPass`)

One `BeginRendering` over the full 4096² (clear once). For each cascade `i`:
`SetViewportScissor` to tile `(i%2, i/2)·2048` (2048²); push `lightViewProj[i]`; compact cascade `i`'s
casters into `_shadowTransforms` (reuse the P3-M1 ring) and draw them instanced via `DrawIndexedIndirect`
(reuse the P3-M4 indirect path). Four cascades → up to 4× (mesh-batch) depth draws.

## 7. Uniforms & shader

- **`LightsUniforms`**: replace `LightViewProj` (mat4 @176) with `LightViewProj[4]` (@176, 256 B) +
  `CascadeSplits` (vec4 @432 — the 4 far view-space depths). Total 448 B. `mesh.frag` LightsUbo matches.
- **`mesh.frag` `directionalShadow`**: compute the fragment's **view-space depth**
  (`-(camera.view·vec4(worldPos,1)).z`); select cascade `i` = first split it fits under; project by
  `lightViewProj[i]`; map NDC→tile UV then to **atlas UV** = `vec2(i%2,i/2)·0.5 + uvTile·0.5`; run the
  existing 5×5 weighted PCF **clamped to the tile** (so the kernel never bleeds into a neighbour tile);
  **blend band**: within a small margin before a split, lerp cascade `i` and `i+1` to hide the seam.
- **Debug view**: a new debug mode tints the fragment by cascade (red/green/blue/yellow) — the fastest
  way to verify selection + splits. Add to the `DebugViews` cycle (N key) + `mesh.frag`.

## 8. Testing

- **Unit (GPU-free, `ShadowFit`)**: splits are monotone increasing over `[near, far]`; each cascade
  sphere covers its slice's 8 corners; `SnapToTexelGrid` stable under sub-texel camera translation (the
  existing snap test, per cascade); a caster upstream of a slice stays inside that cascade's depth range.
- **Headless**: `grid:20x20` capture — **NOT bit-identical** (CSM changes the shadow) → new **visual
  protocol** (`docs/visual-checks/`): sharp contact shadow AND sharp far shadow, no visible seam between
  cascades, flat ground free of acne; the cascade-debug view shows clean depth bands. 0 validation / 0 leak.
- **Bench AOT** `grid:100x100`: 0 alloc/frame; memory unchanged (4×2048² = 1×4096²).

## 9. Gates (exit criteria)

`dotnet test` green (new `ShadowFit.ComputeCascades` tests) · 0 warning · 0 validation · 0 leak · bench AOT
0 alloc/frame · NativeAOT PASS · double audit (`csharp-lowlevel` + `engine-architect`) PASS · **human
visual verdict** (sharp near+far, no seam, no ground acne).

## 10. Decision log

- **Atlas over texture array**: reuses the existing image/sampler/render-target, MoltenVK-safe, minimal
  diff. Array would need per-layer render targets — deferred.
- **Simple per-cascade cull, footprint from the frustum slice**: decouples the fit from the casters
  (no circularity), so the P3-M2 two-pass wedge is retired. A fixed upstream setback (κ·radius) replaces
  `UpstreamExtent`; a caster far upstream of a slice could in theory clip — acceptable at 4 cascades, and
  the sophistication is backlogged.
- **`NoShadowCast` on the ground**: the flat receiver casting on itself was a real acne source
  (eyeDistance 248 m at P3-M4); excluding it tightens every cascade.
- **New visual baseline (not bit-identical)**: CSM deliberately changes the shadow; the gate is the visual
  protocol + the cascade-debug view, as agreed.
- **Blend band + tile-clamped PCF**: the two atlas-specific correctness points (seam hiding, no tile bleed).
