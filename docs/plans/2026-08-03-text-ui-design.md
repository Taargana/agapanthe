# Text & UI — design

> First milestone family of the **engine cap** ([backlog §4quater](../BACKLOG.md)). Anchor decisions (S25): the
> artifact is **the engine** · generalist but especially large-scale space sims (a Stardew-like must stay feasible) ·
> server-authoritative multiplayer designed in from now.
> Status: **design approved (human, brainstorm interview S25)** — pending scored spec review.

## Summary

Agapanthe has **no text rendering at all**. The only feedback channel is `window.Title`, with a hardcoded FPS HUD
(`samples/Sandbox/Program.cs:735-745`) and a title-ceding hack (`&& landingChallenge is null`) because two writers
fight over the same bar. The code comment says it outright: this would need "a font atlas + overlay pass".

Text is the primitive **shared by both future UI layers** (immediate/debug, then retained/XAML), so it comes first —
not as a demo HUD, but as **engine infrastructure**. This design delivers: an offline font cooker with **zero native
dependencies**, a GPU-free text layout library, an overlay render pass, a reusable debug overlay, and a visual
profiler covering **CPU and GPU**.

**Structural finding from the codebase scan:** every *interactive* UI is blocked behind an input overhaul that belongs
to **MP-0** — there is no public mouse position, no button events, no scroll, no `KeyChar`, and **every click captures
the cursor** (`EngineWindow.cs:252`), so clicking a widget is literally impossible today. **Displaying** text depends
on none of that. Hence the scope below. The overlay/profiler needs only a toggle key, and `KeyPressed` already exists.

## Context — what the codebase already provides

Verified against the code, not assumed:

- **`TonemapPass`** (`src/Agapanthe.Rendering/Passes/TonemapPass.cs:26-42`) is the exact template for an overlay pass:
  `VertexLayout = null`, vertices from `gl_VertexIndex`, `DepthTest = false`, `Cull = None`, `cmd.Draw(3)`.
- **`StorageBufferRing<T>`** (`src/Agapanthe.Rendering/StorageBufferRing.cs:12`) — a generic per-frame ring that grows
  by doubling and never reallocates in steady state. Exactly what a dynamic quad buffer needs.
- **`IRenderSystem` + `orchestrator.Add(...)`** are **public** (`src/Agapanthe.Engine/Systems.cs:76`), and
  `RenderContext` carries `CommandList` / `FrameContext` / `SwapchainTarget`. **No change to `FrameOrchestrator` or
  `SystemScheduler` is required.** A UI render system registered after `CreateDefault` runs after `SceneViewSystem`,
  i.e. after `RecordTonemapPass`, which leaves the swapchain in `ColorAttachment` — the correct insertion point.
- **`ReloadablePass`** gives shader hot reload for free, and `shaders/**` is **globbed automatically** by the
  precompiler, so `text.vert/.frag` requires **no build change** to be pre-cooked.
- **`tools/ShaderPrecompiler`** is a complete, copyable template for an offline cooker: console exe, non-AOT,
  deliberately **never referenced by a shipping project** ("a ProjectReference would drag shaderc into the AOT
  closure"), driven by an incremental MSBuild target in `samples/Sandbox/Sandbox.csproj:62-117`. Its **two
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
| `fonts/` *(new)* | One OFL/Apache monospace face (aligned digits = a HUD that does not jitter). Default charset: ASCII + Latin-1 Supplement (~220 glyphs) |

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

- **Color space.** The pass draws after tonemap into an **sRGB** swapchain, and Vulkan blending operates in linear
  space. The shader must convert **sRGB → linear** before output, or UI colors will be wrong.
- **Premultiplied alpha** for UI (avoids filtering halos). `Opaque` stays the default → **no existing pipeline is
  affected** by the `BlendMode` addition.
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
- **Small-size SDF contingency.** Around ~13 px, SDF can be less crisp than a hinted bitmap — and that is exactly
  where debug text lives. Mitigations: bake at em 64 px with padding, `fwidth`-based AA, snap quads to the pixel grid.
  Documented fallback: `StbTrueTypeSharp` can also emit bitmap coverage → same format, one flag.
- **Descriptor budget.** `FrameContext` caps `MaxSets=64` / `MaxCombinedImageSamplers=64` (`FrameContext.cs:27-34`).
  One persistent set for the atlas keeps the UI well inside budget.
- **MP-0 interaction.** `UiRenderSystem`/`Profiler` live in `Engine`, which MP-0 will split for headless; they belong
  in the "render" half. `Agapanthe.Ui` is unaffected (it is GPU-free by construction).

## Milestones

**UI-1 — Text on screen.** `R8Unorm` + `BlendMode` (Graphics) · `FontCooker` + `.agfont` + MSBuild target ·
`FontAsset` (Assets) · `Agapanthe.Ui` (draw list, shaping, layout, measure) · `UiPass` + `FontResources` (Rendering) ·
`UiRenderSystem` (Engine).
**DoD:** text renders in the Sandbox, 0 alloc/frame, GPU-free layout tests green.

**UI-2 — DebugOverlay + CPU profiler.** `FrameStats` (frame-time ring via `Stopwatch`; bytes allocated per frame via
`GC.GetTotalAllocatedBytes(precise: true)` — **the 0-alloc gate made continuously visible on screen**), existing
`Renderer` counters, graphs drawn as rects, `F3` toggle.
**DoD:** replaces the `window.Title` HUD; the title-ceding hack disappears.

**UI-3 — GPU timestamps.** `QueryPool` (Graphics), per-pass instrumentation (shadow/scene/tonemap), a GPU series in
the profiler, **capability detection + graceful degradation**.
**DoD:** per-pass GPU milliseconds on screen where supported; clean absence where not.

## Testing / verification

- **GPU-free tests (`Agapanthe.Ui`)**: kerning applied, `Measure` of a known string, multi-line, alignments, quad
  generation, and a **0-alloc-after-warmup** test (exact precedent: `QuerySurfaceContacts_AllocatesNothingAfterWarmup`).
- **Cooker**: byte-identical output for identical input; `.agfont` write/read round-trip.
- **Integration**: headless capture (`AGAPANTHE_CAPTURE`) showing rendered text; human visual verdict.
- **Project gates**: 0 warning · 0 validation message · 0 leak · 0 alloc/frame · **NativeAOT PASS** — explicitly verify
  that `StbTrueTypeSharp` **does not enter the AOT closure** (it must exist only in `tools/FontCooker`) ·
  **double audit** (`csharp-lowlevel` + `graphics-3d`).

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

To be designed **once** in MP-0, not here. Today's `EngineWindow` cannot support an interactive UI:

- **Mouse position** (window *and* framebuffer space) — none is exposed publicly.
- **Button events** (down/up) — `MouseDown` is private and only triggers capture (`EngineWindow.cs:248`).
- **Scroll wheel** — absent.
- **Capture decoupling** — every click captures the cursor (`EngineWindow.cs:252`) and `MouseDelta` is forced to zero
  when uncaptured (`:132`). A UI needs the mouse **free**.
- **`KeyChar`/codepoint** — absent; without it, no text field is possible at all.
- **`KeyReleased`, key repeat, modifiers** (Ctrl/Shift/Alt) — absent; needed for held Backspace/arrows and shortcuts.
- **DPI scale factor** — `FramebufferSize` is in pixels while Silk mouse positions are in window coordinates;
  hit-testing will be wrong on HiDPI without it.
- **An engine-owned `Key` enum** — `Silk.NET.Input.Key` currently **leaks** through the public API (`KeyPressed`,
  `IsKeyDown`) even though `EngineWindow`'s header comment claims the opposite.

## Rollback point

Clean tree at `5ef130d` (roadmap reorientation, pushed). This document adds no code.
