# Text & UI — design

> First milestone family of the **engine cap** ([backlog §4quater](../BACKLOG.md)). Anchor decisions (S25): the
> artifact is **the engine** · generalist but especially large-scale space sims (a Stardew-like must stay feasible) ·
> server-authoritative multiplayer designed in from now.
> Status: **APPROVED — 4.4/5** (independent scored review, 2 iterations). v1 scored 3.6 "Needs Work" → 14 findings
> folded in (dependency graph, quad→GPU path, low-level changes, descriptor pools, input-annex correction,
> kerning/monospace tension, blend regression gate, cooker parameters, UI policies); v2 review found 6 residuals,
> all folded in here (per-frame descriptor set, premultiply/linear ordering, stale DoD, `Rendering` public surface,
> kerning fixture, `InternalsVisibleTo`). Ready for implementation.

## Summary

Agapanthe has **no text rendering at all**. The only feedback channel is `window.Title`, with a hardcoded FPS HUD
(`samples/Sandbox/Program.cs:735-745`) and a title-ceding hack (`&& landingChallenge is null`) because two writers
fight over the same bar. The code comment says it outright: this would need "a font atlas + overlay pass".

Text is the primitive **shared by both future UI layers** (immediate/debug, then retained/XAML), so it comes first —
not as a demo HUD, but as **engine infrastructure**. This design delivers: an offline font cooker with **zero native
dependencies**, a GPU-free text layout library, an overlay render pass, a reusable debug overlay, and a visual
profiler covering **CPU and GPU**.

**Structural finding from the codebase scan:** every *interactive* UI is blocked behind an input overhaul that belongs
to **MP-0**. Precisely: the raw capabilities *do* exist — `EngineWindow` publicly exposes `Input`/`Keyboard`/`Mouse`
(`EngineWindow.cs:118-124`), so `IMouse.Position`, `IMouse.Scroll` and `IKeyboard.KeyChar` are reachable — but only as
**raw Silk.NET escape hatches**, with no engine-owned abstraction over them. What actually makes a widget unclickable
today is behavioural, not missing API: **every click captures the cursor** (`EngineWindow.cs:252`) and `MouseDelta` is
forced to zero while uncaptured (`:132`), so a UI can never have a free mouse. **Displaying** text depends on none of
this. Hence the scope below. The overlay/profiler needs only a toggle key, and `KeyPressed` already exists.

## Context — what the codebase already provides

Verified against the code, not assumed:

- **`TonemapPass`** (`src/Agapanthe.Rendering/Passes/TonemapPass.cs:26-42`) is the exact template for an overlay pass:
  `VertexLayout = null`, vertices from `gl_VertexIndex`, `DepthTest = false`, `Cull = None`, `cmd.Draw(3)`.
- **`StorageBufferRing<T>`** (`src/Agapanthe.Rendering/StorageBufferRing.cs:12`) — a generic per-frame ring that grows
  by doubling and never reallocates in steady state. Exactly what a dynamic quad buffer needs. **It is `internal` to
  `Agapanthe.Rendering`** — which settles the dependency direction below: the quad ring lives in `Rendering`, and
  `Agapanthe.Ui` only ever produces plain structs.
- **`DescriptorAllocator`** (`src/Agapanthe.Graphics/DescriptorAllocator.cs:20`) — **public**, pools never reset,
  grow-on-demand, 64 sets per pool. This is where a *persistent* set (the font atlas) is allocated. `FrameContext`'s
  pool is reset every frame and is for per-frame sets only.
- **`IRenderSystem` + `orchestrator.Add(...)`** are **public** (`src/Agapanthe.Engine/Systems.cs:76`), and
  `RenderContext` carries `CommandList` / `FrameContext` / `SwapchainTarget`. **No change to `FrameOrchestrator` or
  `SystemScheduler` is required.** A UI render system registered after `CreateDefault` runs after `SceneViewSystem`,
  i.e. after `RecordTonemapPass`, which leaves the swapchain in `ColorAttachment` — the correct insertion point.
- **`ReloadablePass`** gives shader hot reload for free, and `shaders/**` is **globbed automatically** by the
  precompiler, so `text.vert/.frag` requires **no build change** to be pre-cooked.
- **`tools/ShaderPrecompiler`** is a complete, copyable template for an offline cooker: console exe, non-AOT,
  deliberately **never referenced by a shipping project** ("a ProjectReference would drag shaderc into the AOT
  closure"), driven by incremental MSBuild targets in `samples/Sandbox/Sandbox.csproj:78-131`. Its **two
  already-paid-for pitfalls** are commented in the csproj and must be reproduced verbatim: `RemoveProperties` (to
  isolate the tool from `dotnet publish -r`) and pre-expanding the glob before `<Content>`.
- **`GpuUploader` / `GpuImage`** already handle staging upload, mip generation and layout transition
  (`src/Agapanthe.Rendering/SceneBuilder.cs:140-163`).
- **`Stopwatch.GetTimestamp`/`GetElapsedTime`** are already used and are **struct-based, allocation-free**
  (`Renderer.cs:722`). `Renderer` already exposes `LastSceneDrawCalls`, `LastShadowDrawCalls`, `LastSceneCpuVisible`.

Two gaps that must be filled in `Agapanthe.Graphics`:
- **No single-channel pixel format** exists (`PixelFormat.cs:10-32`: `Bgra8Srgb`, `Rgba8Srgb`, `Rgba8Unorm`,
  `Rgba16Sfloat`, `Rg16Sfloat`, `D32Sfloat`, vertex layouts). `R8Unorm` must be added — exact precedent: `Rg16Sfloat`
  was added for the IBL BRDF LUT.
- **Blending is hardcoded off**: `BlendEnable = false` at `GraphicsPipeline.cs:208-219`, with no option on
  `GraphicsPipelineDesc`. This is the single mandatory low-level change — and it also unblocks the known
  "second transparency lock" debt.
- **No `QueryPool` anywhere** — there is no GPU timing infrastructure at all (relevant to UI-3).

## Locked decisions (design interview)

1. **Scope = text & overlay, no interactivity.** Input requirements are *catalogued* and handed to MP-0 so the input
   abstraction is designed **once** (see the appendix).
2. **Screen-space only.** No world-space labels, no rich text — YAGNI. The chosen atlas format stays compatible with a
   later world-space addition, but that cost is not paid now.
3. **Offline rasterization (cook)** — *imposed by the codebase, not debated*: in Release, runtime shader compilation is
   forbidden (`precompiledOnly`, a cache miss throws) and `shaderc_shared.dll` is **physically stripped from the
   output** (`StripShadercFromRelease`). Doing the opposite for fonts would contradict an explicit architectural stance.
4. **Single-channel SDF atlas via `StbTrueTypeSharp`** (MIT, pure managed — sibling of `StbImageSharp`, already used by
   `ImageLoader.cs:34`). **Zero native dependency, including in the tool.** FreeType was rejected: it only adds
   hinting, which is useless when rendering SDF, at the price of native binaries for 3 OSes × 2 architectures.
   `SixLabors.Fonts` was rejected (Split license, paid above a revenue threshold — a trap for a distributable engine).
   *One atlas serves every size; outline and shadow are nearly free — essential to read a HUD over a sunlit planet.*
   Known limitation: `StbTrueTypeSharp` is TTF-first (partial CFF/OTF), which is irrelevant when the engine picks its
   own 1–3 fonts.
5. **Latin + kerning, with an explicit shaping seam.** `text → PositionedGlyph[]`, decoded as `Rune` (not `char`, so
   surrogate pairs never break). Trivial in v1, but it means HarfBuzz can be inserted later **without redesigning the
   API**. CJK and complex scripts are out of scope (CJK is 1:1 but 20 000+ glyphs do not bake into a reasonable atlas;
   complex scripts need a real shaping engine plus bidi, which would forfeit the zero-native property).
6. **New GPU-free project `Agapanthe.Ui`** (references `Core` + `Assets` only): draw list, shaping, layout, measure.
   The logic becomes unit-testable without a GPU — exactly as `Assets` already is for parsing — and it gives the two
   future UI layers a home without a later rename. The repo already enforces its boundaries by csproj everywhere.
7. **`.agfont`, a single-file binary format**: magic + version + blittable metrics + raw pixel payload, on the VS-1
   `WorldSerialization` pattern (already audited and idiomatic here). Loaded via `MemoryMarshal`, no parsing.
   **Deterministic output** → a "same input ⇒ identical bytes" test falls out for free. Not eyeball-inspectable, which
   is offset by a `--dump-atlas` option emitting a control PNG.
8. **Deliverable = primitive + reusable `DebugOverlay` + visual mini-profiler**, on the engine side, not the sample.
   The FPS HUD already exists hardcoded in `Program.cs:735-745`; moving it into the engine turns sample code into an
   engine feature — precisely the "the artifact is the engine" goal — and gives text a first **internal** consumer.
9. **Profiler covers CPU *and* GPU.** GPU timings via a brand-new `QueryPool`, with **capability detection and
   graceful degradation**: if timestamps are unavailable, the GPU series simply disappears and nothing fails. This
   matters because `timestampValidBits` and per-queue support vary by platform, and **macOS/Linux have never been
   validated** (P3-M0).
10. **One spec, three sequenced milestones** with a human green light between each; the risky, platform-dependent part
    (GPU) comes last and stays abandonable without breaking anything.

## Architecture

### Module layout

| Where | What |
|---|---|
| `src/Agapanthe.Graphics` | `PixelFormat.R8Unorm` · `BlendMode` enum + `GraphicsPipelineDesc.Blend` · `QueryPool` (UI-3) |
| `src/Agapanthe.Assets` | `FontAsset` record + `.agfont` reader + `internal` writer (`InternalsVisibleTo("FontCooker")`). **No new dependency** — Stb stays in the cooker |
| **`src/Agapanthe.Ui`** *(new, GPU-free)* | `UiDrawList` (quads), shaping, layout, `Measure`, alignment, `\n` line breaking |
| `src/Agapanthe.Rendering` | `UiPass` (mirrors `TonemapPass`) · `FontResources` (atlas → `GpuImage`) |
| `src/Agapanthe.Engine` | `UiRenderSystem` (`IRenderSystem`) · `FrameStats`/`Profiler` · `DebugOverlaySystem` |
| **`tools/FontCooker`** *(new)* | Non-AOT console → `StbTrueTypeSharp`; never referenced by a shipping project |
| `fonts/` *(new)* | **JetBrains Mono Regular** (OFL 1.1) vendored with its `OFL.txt`, aligned digits = a HUD that does not jitter. Default charset: ASCII + Latin-1 Supplement (~220 glyphs), listed in a `charset.txt` next to it |

### Dependency graph (F2 — the load-bearing decision)

```
Ui ──► Assets ──► Core          Ui is GPU-free: it never references Graphics or Rendering
Rendering ──► Ui                Rendering CONSUMES the draw list (it owns the GPU ring)
Engine ──► Rendering ──► Ui     Engine wires the two; it already references Rendering
```

`Agapanthe.Ui` references **`Assets` + `Core` only** (it needs `FontAsset` to measure/lay out text). **`Rendering`
references `Ui`** — the same relationship it already has with `Assets`: it consumes plain DTOs produced by a GPU-free
library. This direction is forced by `StorageBufferRing<T>` being `internal` to `Rendering`: the GPU-side ring cannot
leave that assembly, so the quads must travel *into* it, never the reverse. `Ui` stays testable with no device, and
`Rendering` gains no GPU-free logic. `Engine`'s `UiRenderSystem` owns the wiring and holds the shared `UiDrawList`.

### Quad → GPU path (F3)

**SSBO of quads + `gl_VertexIndex`, no vertex buffer, no index buffer** — consistent with `TonemapPass`
(`VertexLayout = null`) and with `StorageBufferRing<T>` already existing for exactly this shape.

```
readonly record struct UiQuad(Vector4 Rect, Vector4 UvRect, uint RgbaPremultiplied, uint Flags);
// Flags bit 0: 0 = solid (sample the white texel) | 1 = SDF glyph (apply the distance threshold + AA)
```

- `UiDrawList` (in `Ui`) accumulates `UiQuad` into a pooled array and exposes `ReadOnlySpan<UiQuad>`.
- `UiPass` (in `Rendering`) copies that span into a per-frame `StorageBufferRing<UiQuad>` slice and issues **one**
  `cmd.Draw(quadCount * 6, 1, 0, 0)`.
- `ui.vert` derives the corner from `gl_VertexIndex % 6` and the quad from `gl_VertexIndex / 6`, reading the SSBO.
- **Descriptor layout — one *per-frame* set from `FrameContext`** (`binding 0` = combined image sampler for the atlas,
  `binding 1` = storage buffer for the frame's quad slice). Push constant: `vec2 invScreenSize` + `float sdfPixelRange`.
  This follows the established pattern verbatim — `Renderer.cs:1145-1162` allocates one per-frame set and writes UBOs,
  combined image samplers **and** a storage buffer into it. A `VkDescriptorSet` is allocated from **one** pool, so a
  set cannot mix a persistent binding with a per-frame one; and rebinding a ring buffer into a persistent set each
  frame would race the frames still in flight. Cost is negligible: the atlas image/sampler handles are stable, only
  the descriptor write repeats, and the budget (`MaxCombinedImageSamplers=64`, `MaxStorageBuffers=16`) has room.

Result: **one pipeline, one atlas, one draw call** for all UI in a frame — text and rects alike.

**Ownership and public surface (R4).** `UiPass` and `FontResources` stay **`internal` to `Rendering`** and are owned by
`Renderer`, exactly like `TonemapPass` and the other `ReloadablePass`es — including registration in the
`_reloadablePasses` array (`Renderer.cs:445`) so hot reload comes for free. `Renderer` exposes a small public surface:
`LoadFont(FontAsset)` (creates `FontResources`) and `DrawUi(CommandList, FrameContext, SwapchainTarget,
ReadOnlySpan<UiQuad>)`. `Engine`'s `UiRenderSystem` holds the shared `UiDrawList` and simply forwards its span. No GPU
type crosses into `Ui`, and `Engine` needs no knowledge of the pass internals.

### Data flow

```
fonts/*.ttf  --[FontCooker, build time, pure C#]-->  *.agfont  (SDF atlas + metrics + kern pairs)
                                                        |
                                    [Assets: FontAsset reader, blittable, GPU-free]
                                                        |
                    +-----------------------------------+-----------------------------+
                    |                                                                 |
        [Ui: shaping -> layout -> UiDrawList]                          [Rendering: FontResources]
              (GPU-free, unit-tested)                                    atlas -> GpuImage + Sampler
                    |                                                                 |
                    +----------------> [Engine: UiRenderSystem] <---------------------+
                                                |
                                    [Rendering: UiPass, after tonemap]
```

Application code calls `DrawText`/`DrawRect` from **any stage** into a buffered draw list; a single `IRenderSystem`
consumes it at end of frame. This is required, not stylistic: gameplay logic runs in `Tick`
(`Input`/`PostSimulation`) while the `CommandList` only exists in `Render` — the two phases are disjoint. It is the
ImGui model, and it keeps GPU types out of gameplay code.

### `FontAsset` shape

- `byte[] AtlasPixels` (R8, tightly packed) + `AtlasWidth`/`AtlasHeight`
- `GlyphMetrics[]` as a flat array of `readonly record struct` (`uint Codepoint`, `Vector4 AtlasUv`,
  `Vector2 PlaneMin/PlaneMax`, `float Advance`); codepoint→index map built **at load**, not serialized
- Global metrics: `Ascender`, `Descender`, `LineHeight`, `SdfPixelRange`, `EmPixelSize`
- Kerning: a flat **sorted** `(uint First, uint Second, float Amount)` array + binary search — not a `Dictionary`,
  consistent with the engine's 0-alloc rule

### Details that must not be missed

- **Color space.** The swapchain is `B8G8R8A8Srgb` (`Swapchain.cs:257`) and fixed-function blending operates after the
  EOTF decode, i.e. in **linear** space. The shader must therefore convert its sRGB UI colors **to linear** before
  output. Two consequences to expect, not to be surprised by:
  - **Gamma-correct text AA thins light-on-dark text.** Blending coverage in linear space is *physically* right but
    perceptually makes light glyphs on a dark background look thinner than expected. The classic remedy is a small
    coverage adjustment (a gamma tweak on the SDF alpha); budget for tuning this rather than treating it as a bug.
  - `Swapchain.cs:263` **falls back to a non-sRGB format** when `B8G8R8A8Srgb` is unavailable, in which case the
    conversion would be wrong. The tonemap pass has the same blind spot today, so this is an **inherited debt**, not a
    new one — but the UI shader should read the target format rather than assume sRGB if it is ever hit.
- **Premultiplied alpha** for UI (avoids filtering halos). `Opaque` stays the default → **no existing pipeline is
  affected** by the `BlendMode` addition.
  **Order of operations (must not be left to chance):** `UiQuad.RgbaPremultiplied` stores the colour **as authored,
  in sRGB, NOT premultiplied** — the name refers to the *blend mode it feeds*, not to the stored encoding. The shader
  does, in this order: unpack → **convert RGB sRGB→linear** → **then multiply RGB by alpha** (alpha stays linear
  throughout, it is coverage, never gamma-encoded) → multiply by the SDF coverage → output. Premultiplying before the
  linear conversion would tint semi-transparent text; this is the classic halo bug and it is why the order is fixed here.
- **One atlas, one pipeline.** Reserve a **white texel** in the font atlas so solid rectangles (profiler graphs,
  backgrounds) draw with no second texture and no pipeline switch.
- **`LoadOp = Load`** on the swapchain — the tonemap uses `DontCare`; leaving it would erase the tonemapped image.
- **Coordinates in framebuffer pixels** plus a `UiScale` multiplier. True DPI awareness (window vs framebuffer) only
  matters for hit-testing → deferred with input.
- **0 alloc.** The API takes `ReadOnlySpan<char>`, never `string`. With `TryFormat` + `stackalloc`, formatting
  `"fps 142 · draws 2"` is **strictly allocation-free**, so the HUD can update **every frame** — the
  "only rebuild when it changes" discipline `LandingChallengeSystem` needs today becomes unnecessary.
- **Font → GPU without `ResourceRegistry`.** That registry is typed on `ModelAsset` and its handles are
  **positional**; routing fonts through it would add to the VS-1 asset-identity debt. Follow the
  `IblGenerator`/`IblMaps` pattern instead: a small `FontResources : IDisposable` owned by the renderer. Extract
  `SceneBuilder.UploadTexture` (currently `private static`) into a reusable helper.
- **Small-size SDF contingency — realistic expectations.** Around ~13 px, SDF is *legible and stable* but will not
  match hinted bitmap crispness: stems read slightly soft and contrast is lower. That is acceptable for debug text,
  and it is the accepted price of one atlas serving every size. Mitigations: bake at em 64 px with padding,
  `fwidth`-based AA, snap quads to the pixel grid.
  **Fallback, costed honestly**: `StbTrueTypeSharp` can also emit bitmap coverage, and the `.agfont` container is
  unchanged — but coverage and distance are **not decoded the same way** in the fragment shader. So the fallback is a
  cooker flag **plus** a shader variant (or a `SdfPixelRange == 0` sentinel selecting the coverage branch). Writing
  that sentinel into the format from day one keeps the fallback cheap on the day it is needed.
- **Descriptor pools.** The UI uses **one per-frame set** from `FrameContext` (see the quad path above): a set is
  allocated from a single pool, so the atlas sampler and the frame's quad buffer must share the per-frame set.
  `DescriptorAllocator` (`src/Agapanthe.Graphics/DescriptorAllocator.cs:20`, pools never reset) remains the right tool
  for genuinely persistent, never-rewritten sets — it is simply not what this pass needs.
- **Low-level changes the atlas actually requires (F5)** — beyond `R8Unorm` and `BlendMode`:
  - Adding `R8Unorm` means touching **three** exhaustive switches, not one:
    `PixelFormatExtensions.ToVk` (`PixelFormat.cs:48` throws on an unmapped format), `FromVk` (`:62`, returns
    `Undefined`), and `GpuUploader.BytesPerTexel` (`GpuUploader.cs:471-477`, **throws** on any unlisted format) →
    `PixelFormat.R8Unorm => 1`.
  - The atlas must be **`MipLevels = 1`**. `SceneBuilder.UploadTexture` (`SceneBuilder.cs:140-163`) builds a **full mip
    chain** and requires linear-blit support; mips would smooth the distance field into mush. So the "extract a
    reusable helper" step is more than a move: the helper must take an explicit mip count and usage.
  - **Sampler**: linear filter, `ClampToEdge`, **no mips, no anisotropy**. Linear is required — SDF interpolation *is*
    the antialiasing.
- **Kerning vs monospace (F8) — resolved: v1 ships kerning as a no-op path.** Two facts collide: a monospace face is
  wanted (aligned digits = a HUD that does not jitter) and has zero kerning by construction; and `StbTrueTypeSharp`
  reads only the **legacy `kern` table**, not **GPOS**, which is all most modern libre fonts ship. So the cooker
  *extracts* kern pairs when a `kern` table exists and writes an empty array otherwise — the format, the lookup and the
  seam all stay in place, and a proportional face with a `kern` table gets kerning for free later. **The unit test
  therefore asserts the lookup and its application on a synthetic in-memory `FontAsset`**, never on the shipped
  monospace face — against which it would assert nothing. Real GPOS kerning arrives with the shaping seam (HarfBuzz),
  not before.
- **MP-0 interaction — sequencing decided (F10).** `Agapanthe.Ui` is **immune** to MP-0's headless split (GPU-free by
  construction, no `Engine` dependency), and `UiRenderSystem`/`Profiler` are **pre-assigned to the "render" half** of
  the split, which is where they would land anyway. Therefore **UI-1 may run before MP-0 without creating rework**,
  and doing so is *recommended*: it delivers on-screen diagnostics that make MP-0's own work (tick/frame decoupling,
  command timing) far easier to observe. **The order remains the human's call** — the backlog §4quater sequence places
  Text & UI later, so this spec records the analysis, not a unilateral reordering.

### Cooker parameters (F4 — fixed, not left to the implementer)

| Parameter | Value | Why |
|---|---|---|
| Em size baked | **64 px** | High enough that the SDF holds up when scaled down to ~13 px |
| SDF spread (`SdfPixelRange`) | **4 px** | Standard; stored in the header and pushed to the shader. `0` = coverage-bitmap sentinel |
| Glyph padding | **spread + 1 px** | Prevents neighbouring glyphs bleeding into each other's distance field |
| Atlas size | **computed, power-of-two, square, capped at 2048** | Deterministic given the charset; fail loudly if the charset overflows the cap rather than silently truncating |
| Packing | **shelf (row-based), glyphs sorted by descending height** | Simple, deterministic, good enough for ~220 glyphs. Skyline is unnecessary here |
| Iteration order | **codepoint ascending** | Required for the byte-identical output guarantee |
| White texel | **reserved at (0,0)**, 2×2 opaque | Lets solid rects share the atlas, the pipeline and the draw call |

CLI: `FontCooker <font.ttf> <charset-file> <out.agfont> [--dump-atlas <png>]`. Exit codes 0 / 1 (cook failure) / 2
(bad arguments), mirroring `ShaderPrecompiler`.

### UI policies (F6 — decided here so nobody invents them)

- **Missing glyph**: fall back to a `?`-shaped tofu if present, otherwise **skip the glyph and advance by the space
  width**. Never throw at draw time; log once per missing codepoint at load, not per frame.
- **Draw order / z**: strictly **submission order** (painter's algorithm). The draw list is a flat, append-only span
  and quads are drawn in the order pushed — no sorting, no z field. Later layers can add explicit layer indices.
- **Clipping**: **out of scope for v1.** No scissor stack; an overlay HUD does not need one. Noted as the first thing
  a retained UI will require.
- **Multiple fonts**: **one `FontResources` (one atlas, one persistent set) in v1.** A second font would mean a second
  set and a second draw call — supported by the design (the pass is trivially re-runnable per atlas) but not built.
- **Window resize**: the draw list is rebuilt every frame and coordinates are in framebuffer pixels, so resize needs
  nothing beyond the push-constant `invScreenSize`, which is read from `SwapchainTarget` each frame.
- **`UiDrawList` ownership**: owned by `UiRenderSystem` (Engine), **cleared at the start of the frame's Tick**,
  appended to by any stage, consumed and rendered in `Render`. Single-threaded, same owner thread as the tick — the
  existing whole-engine assumption. Its backing array grows by doubling and is never freed → 0 alloc in steady state.
- **`.agfont` failure mode**: a dedicated `FontAssetException` thrown on bad magic, unknown version, truncated payload
  or inconsistent counts — mirroring VS-1's `WorldSerializationException` rather than overloading a generic type.

## Milestones

**UI-1 — Text on screen.** `R8Unorm` + `BlendMode` (Graphics) · `FontCooker` + `.agfont` + MSBuild target ·
`FontAsset` (Assets) · `Agapanthe.Ui` (draw list, shaping, layout, measure) · `UiPass` + `FontResources` (Rendering) ·
`UiRenderSystem` (Engine).
**DoD:** a fixed scene + fixed string produce a **reproducible capture hash** (recorded in the milestone notes) plus a
human legibility verdict · **an existing scene's capture is bit-identical before and after the `BlendMode` change** ·
0 alloc/frame · GPU-free layout tests green · AOT run loads the `.agfont`.

**UI-2 — DebugOverlay + CPU profiler.** `FrameStats` (frame-time ring via `Stopwatch`; bytes allocated per frame via
`GC.GetTotalAllocatedBytes(precise: true)` — **the 0-alloc gate made continuously visible on screen**), existing
`Renderer` counters, graphs drawn as rects, `F3` toggle.
**DoD:** replaces the `window.Title` HUD; the title-ceding hack disappears.

**UI-3 — GPU timestamps.** `QueryPool` (Graphics), per-pass instrumentation (shadow/scene/tonemap), a GPU series in
the profiler, **capability detection + graceful degradation**.
**DoD:** per-pass GPU milliseconds on screen where supported; clean absence where not.

## Testing / verification

- **GPU-free tests (`Agapanthe.Ui`)**: `Measure` of a known string, multi-line, alignments, quad generation, missing
  glyph, and a **0-alloc-after-warmup** test (exact precedent:
  `tests/Agapanthe.Tests/SurfaceContactsTests.cs:97`). **Kerning is tested against a synthetic in-memory `FontAsset`**
  — the lookup and the layout application are what must be proven, and a synthetic fixture proves them regardless of
  which tables the shipped face happens to carry (see the kerning decision above).
- **Cooker**: byte-identical output for identical input; `.agfont` write/read round-trip; **robustness battery**
  (bad magic, unknown version, truncated payload, inconsistent counts → `FontAssetException`), mirroring VS-1's suite.
  The round-trip test needs the `internal` writer → a **second `InternalsVisibleTo("Agapanthe.Tests")`** alongside the
  `FontCooker` one (the repo already does this, e.g. `GltfSchema.cs`).
- **Blend regression gate (F12, blocking).** Adding `BlendMode` touches **every existing pipeline**, so the DoD for
  UI-1 includes: a headless capture of an existing scene is **bit-identical before and after** the change (the repo
  already does exactly this — e.g. the `9790D95D` hash in P3-M2). `Opaque` being the default must be *proven*, not
  assumed.
- **Text capture hash (F13).** "Text renders in the Sandbox" is not objectively verifiable. The DoD is instead: a
  fixed scene + a fixed string produce a **reproducible capture hash**, recorded in the milestone notes — plus a human
  visual verdict for legibility (which a hash cannot judge).
- **Release/AOT path (F14).** An AOT run must prove the `.agfont` is copied as `Content` and loads correctly — this is
  precisely where the shader pipeline already bit (Release is cache-only, a miss throws).
- **Project gates**: 0 warning · 0 validation message · 0 leak · 0 alloc/frame · **NativeAOT PASS** — explicitly verify
  that `StbTrueTypeSharp` **does not enter the AOT closure** (it must exist only in `tools/FontCooker`) ·
  **double audit**: `csharp-lowlevel` + **`graphics-3d`** for UI-1/UI-3 (a new render pass and Vulkan query pools are
  squarely graphics work), reverting to the project-standard `csharp-lowlevel` + `engine-architect` for UI-2 (F9 — a
  deliberate, milestone-scoped deviation from `CLAUDE.md`, not an oversight).

## Out of scope / debts (anti-creep)

- **No interactivity** (hit-testing, focus, widgets) — blocked on MP-0 input; see the appendix.
- **No world-space labels** (billboarding, depth, distance fade, transparency sorting).
- **No rich text** (per-segment color, bold/italic, markup) — it is layout, not rendering, and can be added on top.
- **No CJK / complex scripts** — the shaping seam exists so this is additive, not a rewrite.
- **No XAML / retained UI** — a separate, much larger design (source generator XAML→C#, layout, styling, templates,
  binding, input routing, focus). Its v1 must be brutally restricted and pulled by real sample games.
- **No GPU timing before UI-3**, and UI-3 is abandonable.
- **DPI hit-testing** deferred with input.

## Appendix — input requirements handed to MP-0

To be designed **once** in MP-0, not here. **Framing correction (review F1): the raw capabilities mostly exist** —
`EngineWindow` publicly exposes `Input`, `Keyboard` and `Mouse` (`EngineWindow.cs:118-124`), so `IMouse.Position`,
`IMouse.Scroll`, `IMouse.MouseUp` and `IKeyboard.KeyChar` are all reachable. **What is missing is an engine-owned
abstraction over them**, plus one behavioural blocker. Concretely, MP-0 must provide:

- **An engine-owned input abstraction** — today every one of the above is a **raw Silk.NET escape hatch**, and
  `Silk.NET.Input.Key` **leaks** through the public API (`KeyPressed`, `IsKeyDown`) even though `EngineWindow`'s header
  comment claims the opposite. This is the core of the work: an engine `Key` enum, mouse/keyboard state owned by the
  engine, and Silk confined to `Agapanthe.Platform`.
- **Capture decoupling** *(the real blocker for widgets)* — every click captures the cursor (`EngineWindow.cs:252`) and
  `MouseDelta` is forced to zero while uncaptured (`:132`). A UI needs a **free** mouse; capture must become a mode the
  application chooses, not an automatic consequence of clicking.
- **Engine-surfaced mouse state**: position in **both** window and framebuffer space, button down/up **edge events**
  (`MouseDown` is currently private and only triggers capture, `:248`), and scroll.
- **Keyboard completeness**: `KeyReleased`, key repeat and modifiers (Ctrl/Shift/Alt) — needed for held
  Backspace/arrows and shortcuts; and `KeyChar` surfaced through the engine abstraction for text fields.
- **DPI scale factor** — `FramebufferSize` is in pixels while Silk mouse positions are in window coordinates;
  hit-testing will be wrong on HiDPI without it.
- **Timestamped commands** (MP-0's own item 4) — the UI's needs and the netcode's needs are the *same* abstraction;
  designing them together is the entire point of deferring this out of the text spec.

## Rollback point

Clean tree at `5ef130d` (roadmap reorientation, pushed). This document adds no code.
