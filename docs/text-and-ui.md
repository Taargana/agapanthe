# Text Rendering & Debug Overlay

Covers the SDF text-rendering stack (`Agapanthe.Ui`) and the engine's built-in **debug overlay**.
The overlay shipped originally as a custom text/graph panel (**UI-1/UI-2/UI-3**, closed
milestones — design rationale in `docs/plans/2026-08-03-text-ui-design.md` and
`docs/plans/2026-09-13-ui3-gpu-timestamps-design.md`), then was **replaced entirely by a native
ImGui panel** (see `docs/plans/2026-09-28-imgui-debug-overlay-design.md`) — this document
reflects the current, post-replacement state.

**What this actually is today**: `Agapanthe.Ui` (`UiDrawList`, `TextLayout`, SDF font rendering,
§2.2-2.3) is generic, public, GPU-free text-rendering infrastructure — any game code can draw its
own text/panels with it (§1.4), and it remains the only text path available to a **Master**
build. The **debug overlay itself** (fps, alloc, draw counts, GPU timestamps, §1.1) is a
*separate* thing since the ImGui milestone: it lives in its own project, `Agapanthe.DebugUi`,
built on `Hexa.NET.ImGui` rather than on `Agapanthe.Ui` — real interactive widgets (a "Close"
button today; an entity/scene inspector is the natural next consumer), but **excluded from the
`Master` build configuration** (the one that ships to players) by construction — see §2.6.

---

## Part 1 — User Guide

### 1.1 The debug overlay

Every `AppHost`-based app that opts in (`samples/Sandbox`, `samples/TopDown`) ships a native
ImGui debug panel, titled "Agapanthe Debug". Toggle it with **F3** at runtime, or start it hidden
with `AGAPANTHE_OVERLAY=0`. Recording of the underlying stats happens unconditionally — hiding
the overlay never stops metrics from accumulating.

Unlike the panel's UI-1/UI-2 predecessor, it is a real ImGui window: draggable, resizable, and
interactive (a **Close** button hides it, same effect as F3). Clicking or dragging inside the
panel does not trigger the FPS-look camera capture (`ImGuiIO.WantCaptureMouse` arbitrates —
see §2.6).

The panel shows, top to bottom:

```
171 fps   2.31 ms   peak 13.5
alloc 544 B/frame   peak 2640 B
draws 1+4   candidates 1
gpu  shadow 0.03  scene 0.03  tonemap 0.01  ui -- ms   (only if GPU timestamps are supported)
[Close]
```

| Line | Meaning |
|---|---|
| `171 fps   2.31 ms   peak 13.5` | Average FPS derived from mean frame time; last frame's CPU-side engine time (see below for what "frame time" excludes); the peak over the retention window. |
| `alloc 544 B/frame   peak 2640 B` | Managed bytes allocated during the frame's engine work. **This is the project's blocking 0-alloc gate made visible**: green at exactly 0, red (as shown here) the instant a frame allocates. The peak stays visible for the whole retention window even after the gate returns to green — a warm-up spike does not mean the gate is currently broken. |
| `draws 1+4   candidates 1` | Instanced draw calls issued by the last scene pass + shadow pass; candidate entities considered for culling that frame. |
| `gpu  shadow … scene … tonemap … ui … ms` | Per-pass GPU time for the frame whose timestamps most recently became available (UI-3) — see §1.2. A `--` placeholder means that region did not run this cycle (e.g. `ui` when nothing was queued); it is never confused with a genuine `0.00`. |
| `[Close]` | Hides the panel — same effect as pressing F3 again. |

**v1 has no graphs** (no frame-time/allocation/GPU-total sparklines): this Hexa.NET.ImGui
version's `PlotLines` overloads all require a values-getter delegate (no plain
`ReadOnlySpan<float>` overload exists) — descoped honestly rather than reimplementing delegate
marshalling for a visual nice-to-have. The numbers above are the content that mattered for parity
with the old panel; an entity/scene inspector (the natural next real consumer of ImGui here) is
tracked in the milestone's Deferred section, not graphs.

**"Frame time" is CPU engine time, not GPU time, and not wall-clock time.** The measurement
bracket wraps `Tick` + `DrawFrame` (opened in `SimulationHost.BeginFrame`, closed in `EndFrame`)
— it deliberately excludes the host's windowing/input pump (GLFW event processing), which
allocates and which the engine does not control. It is *not* how long a frame took to reach the
screen; for that, look at the GPU line below.

### 1.2 GPU timestamps (UI-3)

The `gpu` line and its graph appear only when the selected GPU and its graphics queue report
timestamp support (`timestampPeriod > 0` and `timestampValidBits > 0` on the queue actually in
use — not just the device's blanket feature bit). When unsupported, the block is cleanly
omitted and the panel is one line + one graph shorter — this is not an error state.

Force the disabled path on hardware that *does* support timestamps, to reproduce the degraded
layout or to compare CPU-only overhead:

```sh
AGAPANTHE_GPU_TIMESTAMPS=0 dotnet run --project samples/Sandbox
```

Numbers reflect the frame whose GPU work most recently finished and was read back — with
double-buffering (`FramesInFlight = 2`) that is roughly 2 frames behind what is currently being
recorded, which is invisible at the graph's resolution.

**Known gaps, not bugs**: the 4 timestamped regions (`shadow`/`scene`/`tonemap`/`ui`) do not
cover the candidate-buffer sync copy or the swapchain acquire/present transitions, so the total
graph is not literally "100% of the frame's GPU cost". On macOS/MoltenVK (never validated for
this project — see `docs/BACKLOG.md`), the timestamp `stage` parameter is known to be ignored
and granularity is per-Metal-encoder rather than per-draw; treat any future MoltenVK reading as
approximate until validated.

### 1.3 Capturing the overlay

Two different capture env vars exist, and they see different things:

- `AGAPANTHE_CAPTURE=<path.ppm>` dumps the **HDR scene target**, before tonemap and before the
  overlay is drawn — use it for scene/lighting captures, it never shows the overlay.
- `AGAPANTHE_CAPTURE_UI=<path.ppm>` dumps the **presented swapchain image**, after tonemap and
  after the UI pass — the only capture that shows the overlay (or any UI at all).

Both need `AGAPANTHE_MAX_FRAMES=N` (`N > 0`) for a reproducible, tick-for-tick deterministic
run — without it the frame loop consumes real wall-clock dt and nothing is comparable run to
run.

```sh
AGAPANTHE_SCENE=model AGAPANTHE_CAPTURE_UI=/tmp/overlay.ppm AGAPANTHE_MAX_FRAMES=8 \
  dotnet run --project samples/Sandbox -c Release --no-build
```

(A handful of frames, not 1-2, gives GPU timestamps time to clear their warm-up: the first
`FramesInFlight` frames never populate the `gpu` line at all, by design — see §2.4.)

### 1.4 Drawing your own text or graphs from game code

This is `Agapanthe.Ui`/`TextLayout`/`UiDrawList` — the GPU-free layer, unaffected by the ImGui
milestone and still the only text path a **Master** build ships with. `Club Architect`'s own
splash/menu text uses exactly this (`PresentationSceneContext.UiDrawList`/`.Font`, a recipe's own
`Stage.Render` system) — the debug overlay is no longer the reference consumer of this API.

```csharp
TextLayout.DrawText(_drawList, "score: 1200", _font, new Vector2(x, y), TextSize, TextColour);
```

`TextLayout.DrawText` appends quads into a `UiDrawList` your render system owns; `Renderer.DrawUi`
consumes whatever is in that list once per frame. A solid rectangle (a panel background) is
`UiDrawList.AddRect(rect, font.WhiteTexelUv, rgba)` — it samples the atlas's reserved white texel
instead of a glyph. For 0-alloc numeric formatting into a string (fps counters, scores), format
into a caller-owned `Span<char>` with `TryFormat` directly (`TextBuilder`, the old wrapper around
that pattern, was removed alongside the debug overlay it existed solely to serve — it had no
other consumer).

Two hard caps, silent-truncation (not growth) past them: **256 glyphs** and **64 lines** per
single `DrawText`/`Measure` call. A HUD with more content than that needs multiple calls, not a
bigger buffer.

To wire a brand-new draw list into the frame: own a `UiDrawList`, register an `IRenderSystem`
that calls `Renderer.DrawUi(cmd, frame, target, drawList.Quads)` once per frame (see
`UiRenderSystem` for the reference wiring — it clears its list in `Stage.Input` and draws in the
render stage).

### 1.5 Cooking a font

Fonts are cooked **offline** — Release builds cannot compile shaders or run `StbTrueTypeSharp`
at runtime, and there is no other reason to ship an SDF rasterizer at all. `tools/FontCooker`:

```sh
FontCooker <font.ttf> <charset.txt> <out.agfont> [--dump-atlas <out.pgm>]
```

`charset.txt` is one entry per line: a literal character, `U+XXXX`, or a range
`U+XXXX-U+YYYY`; `#` starts a comment, blank lines are ignored. The shipped font is
`fonts/JetBrainsMono-Regular.ttf` + `fonts/charset.txt` (ASCII + Latin-1 + a tofu glyph for
anything else — no CJK or complex scripts; the charset is what would need extending, not the
renderer). The atlas is capped at 2048×2048 and the cooker **fails loudly** rather than silently
dropping glyphs if a charset doesn't fit.

An app's `.csproj` wires this into the build automatically (see `Sandbox.csproj`/
`TopDown.csproj`'s `CookFonts`/`IncludeCookedFonts` targets — the same incremental-cook pattern
`Agapanthe.Cook.targets` uses for content): the cooker runs once per build (timestamp-gated),
stages to `obj/fontcache/`, and ships the result as `Content` under `fonts/` in the output
directory. At startup, `AppHost` looks for
`<AppContext.BaseDirectory>/fonts/JetBrainsMono-Regular.agfont`; if it's missing, the whole text/
overlay subsystem is simply not registered (`Log.Warn`, no crash) — a build without a cooked
font runs, just silently, with no HUD.

---

## Part 2 — Architecture

### 2.1 Pipeline overview

```
font.ttf + charset.txt --[tools/FontCooker, offline, build-time]--> font.agfont
                                                                        │
                                                    FontAssetFormat.Read (runtime, GPU-free)
                                                                        │
                                                                        ▼
game code --[TextLayout.DrawText / UiDrawList.AddRect]--> UiQuad[] (UiDrawList)
                                                                        │
                                              Renderer.DrawUi(cmd, frame, target, quads)
                                                                        │
                                          UiPass: 1 pipeline, 1 descriptor set, 1 draw call
                                     (gl_VertexIndex expands quads — no vertex/index buffer at all)
                                                                        │
                                                    presented swapchain image
```

Everything drawn — glyphs and solid panels alike — is a `UiQuad`. There is exactly one render
pipeline for the whole UI system.

### 2.2 `Agapanthe.Ui` — the GPU-free layer

Public surface, all allocation-free at steady state:

- **`UiQuad`** (`readonly record struct`, 48 bytes, `std430`-matched): `Rect` (min/max, framebuffer
  pixels, y-down), `UvRect` (normalized atlas UV), `Rgba` (sRGB, *not* premultiplied — the shader
  premultiplies), `Flags` (`FlagSdfGlyph = 1`).
- **`UiDrawList`**: `Add(in UiQuad)`, `AddRect(Vector4 rect, Vector4 whiteTexelUv, uint rgba)`,
  `Clear()`, `Count`, `Quads` (the span `Renderer.DrawUi` consumes). Grows on demand, cleared
  (not reallocated) every frame.
- **`FontAsset`**: the runtime-loaded font — atlas pixels (`R8Unorm`, single channel), glyph
  metrics, kerning pairs, `SdfPixelRange` (0 ⇒ plain coverage atlas, not a distance field —
  the cooker's documented fallback), `WhiteTexelUv` (the reserved solid-fill texel). Loaded via
  `FontAssetFormat.Read` — binary, deterministic, format version 1, same shape as every other
  cooked-asset reader in the project (`.agmodel`, `.agscene`).
- **`TextLayout`** (static): `Measure`/`DrawText`, both bounded to 256 glyphs / 64 lines per call
  (silent truncation past that, not growth — a deliberate ceiling, not an oversight).
  `TextAlign.Left/Center/Right`.
  (`TextBuilder`/`Sparkline`, the debug-overlay-only helpers this layer used to also carry, were
  removed alongside `DebugOverlaySystem` — see §2.6; they had no other consumer.)

### 2.3 GPU rendering path

`UiPass` (`Agapanthe.Rendering.Passes`) declares **no vertex layout**: `ui.vert` expands
`gl_VertexIndex` into one of 6 vertices per quad (2 triangles), reading the quad array from a
`std430` storage buffer (set 0, binding 1) — the same "null vertex layout + `Draw(n*6)`"
technique `TonemapPass`'s fullscreen triangle uses. One `Draw(quadCount * 6)` covers the entire
frame's UI: **one pipeline, one descriptor set, one draw call**, always.

Set 0 is a single **per-frame** descriptor set: binding 0 the font atlas (combined image
sampler, `R8Unorm`, `mipLevels = 1` — mips would smear a distance field), binding 1 the frame's
quad buffer. Blend mode is `PremultipliedAlpha` (the reason `BlendMode` exists as an enum at
all — every earlier pass is `Opaque`). Shader order is load-bearing: `ui.vert` converts sRGB to
linear **first**, then multiplies by alpha — reversing that order tints every semi-transparent
pixel (the classic premultiplied-alpha halo bug). `ui.frag` resolves an SDF glyph via
`smoothstep` over the screen-space `fwidth` of the signed distance (not the cooker's stored
pixel range) — this is what keeps a glyph edge exactly one pixel wide at any zoom or DPI, the
entire point of baking a distance field instead of a bitmap. The pass runs *after* the tonemap
pass, `LoadOp = Load` (the scene underneath is preserved, not cleared).

### 2.4 CPU profiler (UI-2)

`FrameStats`/`FrameSeries` (`Agapanthe.Engine`, headless-safe — no GPU, no window):
append-only circular buffers (~4 s retention at 60 Hz), 0-alloc `Record`/`CopyChronological`.
`SimulationHost.BeginFrame()`/`EndFrame()` own the measurement bracket; the debug panel (today
`ImGuiDebugSystem`, `Agapanthe.DebugUi`) only reads and draws, never samples on its own — this is
deliberate: sampling from a system would measure across the host's windowing/input pump instead
of engine work alone, permanently and falsely reddening the alloc gate.

### 2.5 GPU profiler (UI-3)

`QueryPool` (`Agapanthe.Graphics`) wraps a `VkQueryPool` of `QUERY_TYPE_TIMESTAMP`, sized
`2 (begin/end) × 4 (regions) × FramesInFlight` queries, created only when
`Renderer.SupportsGpuTimestamps` is true (device capability ANDed with the
`AGAPANTHE_GPU_TIMESTAMPS` knob). `CommandList.WriteTimestampBegin`/`WriteTimestampEnd` bracket
the 4 existing debug-label regions (`Shadow`, `Scene`, `Tonemap`, `UI`) at `ALL_COMMANDS` (not
`TOP_OF_PIPE` — that stage in a synchronization2 first scope is equivalent to `NONE` and would
measure cumulative, not per-region, durations).

Each frame, `Renderer.DrawScene` reads back **the current in-flight slot** — the one
`FrameRenderer` just fence-waited before this method runs, hence guaranteed complete — non-
blocking (`WITH_AVAILABILITY_BIT`, never `WAIT_BIT`), one region at a time, into
`Renderer.LastGpuPassTimingsMs` (a `GpuPassTimingsMs` struct of 4 independently-nullable
`float?` fields: `null` means that region did not run this cycle, never a stall, never a
fabricated value). The first `FramesInFlight` frames only reset their slot's queries and never
read — every Vulkan query is uninitialized until its first reset, so reading any earlier would
be a spec violation. `GpuTimestampMath.TryToMilliseconds` (pure, GPU-free, unit-tested) converts
the raw ticks, masking to the queue's actual `timestampValidBits` (Vulkan only defines the
low-order bits; NVIDIA commonly reports 64, but Intel/AMD/MoltenVK commonly report 36-40) and
rejecting a wrapped or non-finite result rather than fabricating a value.

The debug panel reads `Renderer.LastGpuPassTimingsMs` directly each frame it draws — no
intermediate `FrameSeries` of its own for the GPU numbers (v1 has no graphs at all, §1.1); the
per-pass breakdown is fully conveyed by the text line.

### 2.6 The debug overlay's own project (`Agapanthe.DebugUi`) and the `Master` build

The ImGui milestone (`docs/plans/2026-09-28-imgui-debug-overlay-design.md`) replaced
`DebugOverlaySystem`/`Sparkline`/`TextBuilder` (all deleted, §2.2) with a native ImGui panel in
its own project, **`Agapanthe.DebugUi`** — isolated the same way `Agapanthe.Net`/`Agapanthe.Audio`
isolate their one third-party dependency each. It is never referenced by `Agapanthe.App`/
`Agapanthe.Platform.App` (a consumer opts in itself, via `IGame.ConfigureDebugTools`); a headless
host (`HeadlessSim`/`DedicatedServer`) never sees it at all.

A 3rd build configuration, **`Master`**, is the one that actually ships to players —
`dotnet publish -r <rid> --self-contained -p:PublishAot=true -c Master`. `Release` stays an
internal QA/profiling tier that **keeps** the debug panel; only `Master` excludes it — every
`ProjectReference`/`PackageReference` touching `Agapanthe.DebugUi`/`Hexa.NET.ImGui` is conditional
on `'$(Configuration)' != 'Master'`, so no `DebugUi.dll`/`cimgui` native library reaches that
build at all (`DebugUiIsolationTests` + `EngineIsHeadlessTests` gate this).

`ImGuiVulkanBackend` (`Agapanthe.DebugUi`) is a hand-written Vulkan backend on top of
`Agapanthe.Graphics`'s public primitives — never a type from Hexa.NET.ImGui's own reference
backends (those want a raw `VkDevice`/`VkCommandBuffer`, which `Agapanthe.Graphics` never lets
out). Vertices live in a per-frame SSBO ring read via `gl_VertexIndex` (same technique as
`ui.vert`), paired with a real GPU index buffer. `ImGuiDebugSystem` is an `IRenderSystem`, never
an `ISystem` — the `NewFrame`→widgets→`Render()` cycle must run exactly once per actually-rendered
frame.

---

## Part 3 — Known limitations (documented, not silently accepted)

- The debug panel has no graphs (v1) — no `PlotLines` overload takes a plain
  `ReadOnlySpan<float>` in this Hexa.NET.ImGui version (§1.1).
- No entity/scene inspector yet — the natural next real consumer of ImGui's interactivity here,
  explicitly deferred by the milestone.
- `Agapanthe.Ui`'s own text path (§1.4) still has no interactivity — no mouse, click, focus, or
  widgets (blocked on backlog §4quater's input work, by design); this is unrelated to the ImGui
  panel, which has its own independent input plumbing (`IWindow.MousePosition`/
  `IsMouseButtonDown`/`ScrollDelta`/`CharInput`).
- 256 glyphs / 64 lines per `DrawText`/`Measure` call — silent truncation, not growth.
- No CJK or complex scripts — charset-driven (ASCII + Latin-1 + tofu today), not a renderer
  limit; extending `charset.txt` and re-cooking is additive.
- Atlas hard-capped at 2048×2048; the cooker fails loudly rather than dropping glyphs.
- The GPU "Total" graph does not cover the candidate-buffer sync copy or swapchain transitions —
  it is not literally 100% of a frame's GPU cost, only the 4 instrumented regions.
- The hardware-genuinely-unsupported GPU-timestamp branch has zero test coverage on the
  project's reference (NVIDIA) hardware — only the explicitly-disabled path
  (`AGAPANTHE_GPU_TIMESTAMPS=0`) is exercised today.
- macOS/MoltenVK is not validated for this project at all (backlog 🔴, P3-M0). For GPU
  timestamps specifically, `docs/BACKLOG.md` documents 4 concrete MoltenVK risks found by
  research (ignored `stage` parameter, per-Metal-encoder granularity, a zero-value fallback on
  failure, and 2 open MoltenVK bugs on this exact double-buffered reset+read pattern) — treat
  any future Apple-hardware reading from this feature as unverified until P3-M0 lands.

---

## Part 4 — File map

| Concern | Files |
|---|---|
| Font cooking (offline) | `tools/FontCooker/`, `fonts/*.ttf`, `fonts/charset.txt` |
| Font format (runtime read) | `src/Agapanthe.Assets/Font/FontAsset.cs`, `FontAssetFormat.cs` |
| GPU-free UI layer | `src/Agapanthe.Ui/` (`UiDrawList`, `UiQuad`, `TextLayout`, `TextShaper`) |
| GPU rendering (game text) | `src/Agapanthe.Rendering/Passes/UiPass.cs`, `Renderer.LoadFont`/`DrawUi`, `shaders/ui.vert`/`ui.frag` |
| Frame wiring (game text) | `src/Agapanthe.Engine.Render/UiRenderSystem.cs` |
| CPU profiler data | `src/Agapanthe.Engine/FrameStats.cs`, `SimulationHost.BeginFrame`/`EndFrame` |
| GPU profiler data | `src/Agapanthe.Graphics/QueryPool.cs`, `GpuTimestampMath.cs`, `CommandList.WriteTimestampBegin`/`End`/`ResetQueryPool`, `GraphicsDevice.SupportsGpuTimestamps`/`TimestampValidBits`/`TimestampPeriodNs`, `Renderer.LastGpuPassTimingsMs` |
| Debug panel (ImGui, Debug/Release only) | `src/Agapanthe.DebugUi/` (`ImGuiDebugSystem.cs`, `ImGuiVulkanBackend.cs`, `Shaders/imgui.vert`/`imgui.frag`) |
| `Master` build config | `Directory.Build.props`, `Agapanthe.slnx`, each consumer's `.csproj` (conditional `DebugUi` reference) |
| Host wiring + env vars | `src/Agapanthe.App/HostOptions.cs`, `AppHost.cs`, `IGame.ConfigureDebugTools` |
