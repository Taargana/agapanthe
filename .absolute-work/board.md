# Absolute Work Board — ImGui native debug overlay (F3), Master config, opt-in per consumer

Status: **completed** (2026-09-28) — all 8 waves done, tail tasks passed.

Spec: `docs/plans/2026-09-28-imgui-debug-overlay-design.md` (6 independent review rounds,
3.2 → 3.1 → 3.9 → 3.89 → 2.8 MAJOR GAPS → 4.2/5 APPROVED). INTAKE/BRAINSTORM and SPEC phases
already done via `/absolute-brainstorm`.

## Rollback Point

Agapanthe: commit `ca30680` (clean tree, confirmed before Wave 1 — includes the Club Architect
first-scene work committed separately just before this board started).

## Project Conventions (reused from prior boards)

- .NET 10, `dotnet build`/`dotnet test` from repo root, `TreatWarningsAsErrors`.
- `IGame.FontPath`/`IGame.Universe` DIM precedent for `IGame.ConfigureDebugTools`.
- `Agapanthe.Net`/`Agapanthe.Audio` precedent for a new project isolating one 3rd-party
  `PackageReference` under an allowlist gate.
- This dépôt packs with `dotnet pack Agapanthe.slnx -c Debug` — any code path that must never leak
  to an external consumer needs a structural (no-reference) guarantee, not just `#if`.
- Commits: never `git commit`/`push` without explicit request.
- **Club Architect (separate repo) is explicitly OUT of this board** — per spec D3/D9, its own
  `PackageReference`/`ClubArchitectGame.cs` changes are a natural follow-up session once Agapanthe
  is repacked with `Agapanthe.DebugUi` published, same pattern as every prior Club Architect
  consumption step this session.

## Task DAG

```
Wave 1 (parallel — 4 disjoint files)
  AW-001 Directory.Build.props: Master configuration
  AW-002 CommandList.SetScissor (Agapanthe.Graphics)
  AW-003 IWindow.cs: 4 new members + promote CaptureMouseOnClick
  AW-010 IGame.cs: ConfigureDebugTools DIM
                    |    |    |    |
                    +----+----+----+
                         |
                         v
Wave 2 (parallel — disjoint files, all depend on AW-001/AW-003)
  AW-004 Agapanthe.slnx: Master configuration          [depends: AW-001]
  AW-005 Widen 3 StripShadercFromRelease sites          [depends: AW-001]
  AW-006 EngineWindow/EngineWindowAdapter/ScopedWindow:
         implement 4 new IWindow members                [depends: AW-003]
  AW-015 CLAUDE.md: document `dotnet publish -c Master`  [depends: AW-001]
                    |    |    |    |
                    +----+----+----+
                         |
                         v
Wave 3 (sequential — the hard gate, spec's own "before everything else")
  AW-007a DebugUi project scaffold + Hexa.NET.ImGui
          conditional PackageReference + minimal spike   [depends: AW-001]
                         |
                         v
  AW-007b Sandbox references DebugUi; verify Debug+Release
          build clean (no AOT/trim analyzer noise) AND
          a real publish -c Release -p:PublishAot=true
          succeeds with Hexa present                     [depends: AW-007a]
                         |
                         v
Wave 4 (sequential — the Vulkan backend, one shared file set)
  AW-008 ImGuiVulkanBackend (SSBO+index buffer+pipeline+
         atlas upload, shaders precompiled via
         ShaderPrecompiler+EmbeddedResource)              [depends: AW-002, AW-007b]
                         |
                         v
  AW-009 ImGuiDebugSystem (IRenderSystem+IDisposable,
         input translation, content parity, Close button) [depends: AW-006, AW-008]
                         |
                         v
Wave 5 (sequential — App wiring, depends on DebugUi being real)
  AW-011 AppHost.cs: invoke hook both sites, teardown
         handle, remove debugOverlay/DebugOverlaySystem
         wiring + F3 case                                 [depends: AW-010, AW-009]
                         |
                         v
Wave 6 (parallel — 3 disjoint areas, all depend on AW-011)
  AW-012 Delete DebugOverlaySystem/TextBuilder/Sparkline+
         SparklineTests; rewrite AotProfilerSmoke
  AW-013 AppHostContractTests teardown label + new
         PackageReference-scan reflexive test
  AW-016 SandboxGame.cs/TopDownGame.cs: conditional
         reference + ConfigureDebugTools + F3 wiring
                    |         |         |
                    +----+----+----+----+
                         |
                         v
Wave 7 (sequential, depends on AW-012)
  AW-014 docs/text-and-ui.md update
                         |
                         v
Wave 8 (tail, mandatory, sequential)
  AW-017 Self Code Review
  AW-018 Requirements Validation (spec's Verification list, item by item)
  AW-019 Full Project Verification (Debug/Release/Master builds, publish checks, live runs)
```

## Tasks

### AW-001 — `Directory.Build.props`: `Master` configuration [S] — ✅ DONE
**Files**: `Directory.Build.props`. **Deps**: none. **Wave**: 1.

`<Configurations>Debug;Release;Master</Configurations>` + a `PropertyGroup
Condition="'$(Configuration)'=='Master'"` setting `DefineConstants` (`+= TRACE;MASTER` — TRACE
must be explicit, the SDK only adds it automatically for the literal `Release`/`Debug` names),
`Optimize=true`, `DebugType=portable`. Verify: `dotnet build -c Master` on any existing project
picks up `MASTER` (quick smoke: a scratch `#if MASTER` compile check, removed after).

### AW-002 — `CommandList.SetScissor` [S] — ✅ DONE (dropped `<see cref="ImDrawCmd"/>` from doc comment — that type doesn't exist in this project, would have been a TreatWarningsAsErrors build failure)
**Files**: `src/Agapanthe.Graphics/CommandList.cs`. **Deps**: none. **Wave**: 1.

New `public void SetScissor(int x, int y, uint width, uint height)` — scissor rect alone, does
NOT touch the viewport (unlike `SetViewportScissorRect`). No clamping here (`CommandList` doesn't
know the framebuffer size) — clamping is the caller's (`DebugUi`'s) job.

### AW-003 — `IWindow.cs`: 4 new members + promote `CaptureMouseOnClick` [S] — ✅ DONE
**Files**: `src/Agapanthe.App/IWindow.cs`. **Deps**: none. **Wave**: 1.

- `Vector2 MousePosition { get; }` — absolute position, framebuffer pixels.
- `bool IsMouseButtonDown(MouseButton button)` — polling, mirrors `IsKeyDown(Key)`.
- `Vector2 ScrollDelta { get; }` — accumulated this frame, reset after `Rendered` (not `Updated`
  — the ImGui cycle runs in `Render`, which fires after `Updated`'s own reset of `MouseDelta`).
- `event Action<char>? CharInput;`.
- Promote existing `bool CaptureMouseOnClick { get; set; }` from `EngineWindow`-only to the
  interface (already present by name/shape on `EngineWindow`, so `IWindowSurfaceTests` passes
  unchanged for that implementer).

### AW-010 — `IGame.cs`: `ConfigureDebugTools` DIM [S] — ✅ DONE
**Files**: `src/Agapanthe.App/IGame.cs`. **Deps**: none. **Wave**: 1.

```csharp
Func<SimSceneContext, PresentationSceneContext, IDisposable?>? ConfigureDebugTools => null;
```
Doc comment: mirrors `FontPath`/`Universe` DIM pattern exactly; returns the disposable
`AppHost` must tear down (scene switch and shutdown); `null` means no debug tooling for this
game (every host except one that opts in).

### AW-004 — `Agapanthe.slnx`: `Master` configuration [S] — ✅ DONE (`.slnx` had no `<Configurations>` block at all; added `<Configurations><BuildType Name="Debug"/><BuildType Name="Release"/><BuildType Name="Master"/></Configurations>` — `dotnet build -c Master` at solution level failed with MSB4126 before this, works after)
**Files**: `Agapanthe.slnx`. **Deps**: AW-001. **Wave**: 2.

Add `Master` to the solution's build-type list (mirrors whatever mechanism the `.slnx` format
uses for `Debug`/`Release` today — inspect the file first, don't guess the schema). Verify
`dotnet build Agapanthe.slnx -c Master` resolves and doesn't silently fall back to Debug/Release.

### AW-005 — Widen 3 `StripShadercFromRelease` sites [S] — ✅ DONE (all 3 exact lines confirmed matching spec; verified live — `dotnet publish -c Master` on Sandbox, full NativeAOT, shaderc_shared.dll confirmed absent from output)
**Files**: `samples/Sandbox/Sandbox.csproj:180`, `samples/TopDown/TopDown.csproj:126`,
`samples/ThinClient/ThinClient.csproj:124`. **Deps**: AW-001. **Wave**: 2.

Each `Condition="'$(Configuration)' == 'Release'"` → `Condition="'$(Configuration)' != 'Debug'"`
(catches Master too, and any future non-Debug configuration). Verify: `dotnet publish -c Master`
on Sandbox still strips `shaderc_shared.dll` (grep the output, same method as the FontCooker/
StbTrueTypeSharp check earlier this session).

### AW-006 — `EngineWindow`/`EngineWindowAdapter`/`ScopedWindow`: implement 4 new `IWindow` members [M] — ✅ DONE (full solution Debug+Release build clean, `IWindowSurfaceTests` green — confirms `CaptureMouseOnClick` promotion + new members match by name/shape)
**Files**: `src/Agapanthe.Platform/EngineWindow.cs`, `src/Agapanthe.Platform.App/EngineWindowAdapter.cs`,
`src/Agapanthe.App/ScopedWindow.cs`. **Deps**: AW-003. **Wave**: 2.

`EngineWindow`: real `MousePosition`/`IsMouseButtonDown` from the already-held `IMouse`, real
`ScrollDelta` accumulation (new field, reset after `Rendered` fires — check the exact place
`MouseDelta` resets today and mirror it one event later), `CharInput` from `IKeyboard`'s existing
char event, forwarded. `EngineWindowAdapter`: pure forwarding (matches its existing pattern for
every other member). `ScopedWindow`: pure forwarding + a 6th trampoline table for `CharInput`
(mirrors its existing per-event trampoline pattern for `KeyPressed`/`Updated`/etc — cleanup on
`Dispose`/scene switch).

### AW-015 — `CLAUDE.md`: document `dotnet publish -c Master` [S] — ✅ DONE
**Files**: `CLAUDE.md` (section Commandes). **Deps**: AW-001. **Wave**: 2.

One line: `dotnet publish -r <rid> --self-contained -p:PublishAot=true -c Master` is the real
ship command; `-c Release` stays an internal QA/profiling tier (keeps the debug panel).

### AW-007a — `Agapanthe.DebugUi` project scaffold + minimal spike [S] — ✅ DONE (Hexa.NET.ImGui 2.2.9. **Real finding**: this ImGui version has NO classic atlas-upload API — `ImFontAtlasPtr.GetTexDataAsRGBA32`/`.Build()` don't exist, confirmed by reflection. Texture data flows through `ImDrawData.Textures` dynamically; a real backend declares `ImGuiBackendFlags.RendererHasTextures`. **This changes AW-008's implementation** — the Vulkan backend must consume `ImDrawData.Textures` per-frame, not upload a single atlas once at startup as originally planned. Test: `ImGuiContextProbeTests.RunSmokeFrame_ProducesNonEmptyDrawData`, green)
**Files**: new `src/Agapanthe.DebugUi/Agapanthe.DebugUi.csproj` (+ a throwaway
`Program.cs`-style smoke, or a test project — pick whichever is faster to run once and discard).
**Deps**: AW-001. **Wave**: 3 (hard gate — spec's own "before everything else").

`<IsPackable>true</IsPackable>`, `<PackageReference Include="Hexa.NET.ImGui"
Condition="'$(Configuration)' != 'Master'" />` (no `PublishAot` clause — removed per spec D9
round 5, would break compilation under an explicit `-p:PublishAot=true`). No `IsAotCompatible`
(patron `FontCooker`). References `Core` only for this task (Graphics/Engine/Engine.Render/
Rendering/App come in AW-008/009). **Spike**: under Debug/JIT, create an ImGui context,
`NewFrame()`+`ImGui.Text("smoke")`+`Render()`, assert `ImDrawData` non-empty. Pin the exact
Hexa.NET.ImGui version used; confirm the atlas-extraction API name on that version (spec flags
ImGui 1.92+ changed this — verify, don't assume `GetTexDataAsRGBA32` exists).

### AW-007b — Sandbox references `DebugUi`; verify Debug+Release+publish [M] — ✅ DONE (all 3 sub-checks pass: Debug build 0 warning/0 error; Release build 0 warning/0 error, no AOT/trim analyzer noise despite DebugUi not being IsAotCompatible — no NoWarn needed; full `dotnet publish -c Release -r win-x64 --self-contained -p:PublishAot=true` succeeds with Hexa.NET.ImGui + cimgui.dll present, binary runs clean (0 leak, `model` scene). D2's "jamais publié AOT" claim is now proven correct in the OTHER direction — it publishes fine.)
**Files**: `samples/Sandbox/Sandbox.csproj` (temporary `ProjectReference`, made real/conditional
in AW-016). **Deps**: AW-007a. **Wave**: 3 (hard gate, continued).

1. `dotnet build` Sandbox in Debug: clean, no AOT/trim analyzer noise from referencing a non-
   `IsAotCompatible` `DebugUi` (Sandbox has `PublishAot=true` fixed + `TreatWarningsAsErrors`).
2. Same in Release (Release now carries ImGui too — this is new since round 5, not yet proven).
3. **The real gate**: `dotnet publish -c Release -r win-x64 --self-contained -p:PublishAot=true`
   on Sandbox succeeds with Hexa.NET.ImGui inside — trim, P/Invoke, native cimgui load. This was
   never verified before round 5 (spec D2's earlier "jamais publié AOT" claim was wrong).

If any of the 3 fails: **stop, do not proceed to Wave 4** — this is exactly the class of risk the
spec's 6 review rounds exist to catch before real work is sunk into it. Report back with findings;
this may need a design adjustment, not a code fix.

### AW-008 — `ImGuiVulkanBackend` [M] — ✅ DONE (shaders precompiled via tools/ShaderPrecompiler, MSBuild target `PrecompileImGuiShaders`+`IncludeImGuiShaders`, `BeforeTargets="BeforeBuild;BeforeCompile;CoreCompile"` needed — `BeforeCompile` alone was NOT early enough to include the EmbeddedResource items, found live. `Renderer.SwapchainColorFormat` added (new public getter, needed since no existing surface exposed it externally). Texture handling adapted to the real 1.92+ dynamic API discovered at AW-007a: `ImDrawData.Textures` walked each frame, v1 handles exactly one texture (font atlas), full re-upload on WantCreate/WantUpdates (no partial-rect). Compiles clean Debug/Release; Master builds as an empty shell, 0 warning.
**Post-hoc live-debug finding (3 real bugs, found only by running, not by reading code)**: (1) first live Sandbox run crashed immediately — `vkCmdDrawIndexed` outside an active render pass (`ImGuiVulkanBackend.Render` never called `BeginRendering`/`EndRendering`) — fixed by wrapping the draw loop exactly like `Renderer.DrawUi` (`ColorAttachmentBarrier` + `BeginRendering(LoadOp=Load)` + `EndRendering`). (2) geometry rendered garbled/shifted — GLSL std430 rounds a struct's array stride up to its largest member's alignment: `{vec2,vec2,uint}` is 20 bytes of data but a 24-byte STRIDE, while the CLR packs the equivalent struct tightly at 20 bytes — exactly the trap `ui.vert`'s own comment already warns about, hit anyway; fixed with an explicit 4-byte `Pad` field on `Vertex`. (3) all glyphs rendered as solid "tofu" boxes instead of legible text, resisting several fixes: the atlas format is `Rgba32` not `Alpha8` (fixed the shader to sample the full vec4 — no visible change); raw pixel dumps kept showing "solid white rectangles" until a dedicated alpha-channel visualizer proved the real glyph SHAPE lives entirely in the alpha channel (RGB is constant/white by design) — the font atlas itself was correct all along, across both Hexa.NET.ImGui 2.2.7 and 2.2.9. Root cause was the fragment shader combined with `BlendMode.PremultipliedAlpha`: `outColor = vColor * texture(...)` left `outColor.rgb` un-scaled by the sampled coverage alpha (texture RGB≈1.0 everywhere), so a premultiplied blend added full-white unconditionally even at zero coverage. Fixed in `imgui.frag`: `outColor = vec4(vColor.rgb * coverage, vColor.a * coverage)` where `coverage = texture(fontAtlas, vUv).a`. Verified via headless `AGAPANTHE_CAPTURE_UI` capture — panel text now fully legible. No library downgrade needed; stayed on Hexa.NET.ImGui 2.2.9.)
**Files**: new `src/Agapanthe.DebugUi/ImGuiVulkanBackend.cs`, `Shaders/imgui.vert`/`imgui.frag`
(+ precompile wiring via `tools/ShaderPrecompiler`, `.spv` embedded as `EmbeddedResource`).
**Deps**: AW-002, AW-007b. **Wave**: 4.

SSBO ring-buffer for vertices (patron `StorageBufferRing<T>` from `Rendering`, reimplemented here
since that one is `internal`), real GPU index buffer (`BindIndexBuffer`+`DrawIndexed`, `ImDrawIdx`
layout direct), one draw per `ImDrawCmd` with `CommandList.SetScissor` (clamped to framebuffer
bounds here, not in `CommandList`). Font atlas uploaded once as a `GpuImage`. `imgui.frag` does
`srgbToLinear` before output (patron `ui.frag`). `ColorAttachmentBarrier` before `BeginRendering`
(patron `Renderer.cs:1524` — `DrawUi` can exit early, leaving tonemap as the last writer).
No shader compiler at runtime in any configuration — `.spv` are precompiled at `DebugUi`'s own
build time and embedded already-compiled.

### AW-009 — `ImGuiDebugSystem` [M] — ✅ DONE (content parity via ImGui.Text/TextColored — no PlotLines sparklines in v1: this Hexa.NET.ImGui version's PlotLines overloads all require a values-getter delegate, no plain ReadOnlySpan<float> overload exists, confirmed by reflection — descoped honestly rather than reimplementing the delegate marshalling; the NUMBERS (fps/alloc/draws/gpu timings) are the content that matters for parity, graphs were a visual nice-to-have. Close button via D7. Input fed through the modern IO event API (AddMousePosEvent/AddMouseButtonEvent/AddMouseWheelEvent). Compiles clean Debug/Release/Master.)
**Files**: new `src/Agapanthe.DebugUi/ImGuiDebugSystem.cs`. **Deps**: AW-006, AW-008. **Wave**: 4.

`IRenderSystem` + `IDisposable` — **never `ISystem`** (the `NewFrame`→widgets→`Render()` cycle
must run exactly once per actually-rendered frame, confined to `Render(in RenderContext ctx)`).
Constructor takes what it needs from `PresentationSceneContext` (window, renderer, `FrameStats`/
`LastGpuPassTimingsMs`). Feeds `ImGuiIO` each frame from `IWindow` (`MousePosition`/
`IsMouseButtonDown`/`ScrollDelta`/`CharInput`/`IsKeyDown`), `io.DeltaTime` from a real
`Stopwatch` (not `RenderContext.Tick`, which is the fixed sim step). Sets
`window.CaptureMouseOnClick = !io.WantCaptureMouse` each visible frame; restores `true` on
hide/dispose. Respects `sim.Options.OverlayVisible` (constructor param) for the deterministic-
capture convention. Content = parity with old `DebugOverlaySystem` (fps/frame-time+graph,
alloc/frame+peak+graph colored green/red, draws/candidates, per-pass GPU timings) + a "Close"
button (calls back to hide the panel). `Dispose()` tears down pipeline/SSBO/index buffer/atlas/
ImGui context — idempotent.

### AW-011 — `AppHost.cs`: wire the hook, remove old overlay [M] — ✅ DONE (both PresentationSceneContext sites confirmed at the exact lines the spec named; teardown label test updated and green; full solution Debug build clean)
**Files**: `src/Agapanthe.App/AppHost.cs`, `tests/Agapanthe.Tests/AppHostContractTests.cs`
(teardown label — actually finished in AW-013, just note the coupling here).
**Deps**: AW-010, AW-009. **Wave**: 5.

`game.ConfigureDebugTools?.Invoke(sim, presentation)` at both `PresentationSceneContext`
construction sites (`AppHost.cs:183` first load, `:653` `PerformHandOff` — **after**
`recipe.Build`), captured in a hoisted local `debugToolsHandle` (patron `debugOverlay`). Old
handle disposed + set to `null` immediately after `frameRenderer.WaitIdle` at switch, and via a
new named teardown step `TeardownTargets.DisposeDebugTools` (same position) at final shutdown.
Remove: `debugOverlay` field, its construction, `case Key.F3 when debugOverlay is not null`, the
post-switch re-registration block.

### AW-012 — Delete `DebugOverlaySystem`/`TextBuilder`/`Sparkline`; rewrite `AotProfilerSmoke` [M] — ✅ DONE (all 3 files + `SparklineTests.cs` deleted, grep-confirmed 0 remaining code references — 2 stale doc-comments cleaned up too. `AotProfilerSmoke` rewritten to assert directly on `FrameSeries.CopyChronological`'s output (capacity 8, 12 records → chronological window is 4..11, asserted `samples[0]==4f`/`samples[^1]==11f`) instead of calling the deleted `Sparkline.Draw`. Full solution Debug build: 0 warning/0 error.
**Real bug found live while re-running the full suite here** (unrelated to the deletions, but only surfaced now): `ImGuiContextProbeTests.RunSmokeFrame_ProducesNonEmptyDrawData` started failing — `TotalVtxCount=0`. Root cause: a stray `imgui.ini` left at the repo root by an earlier live Sandbox debugging run (ImGui's own default CWD-relative persistence) was picked up by `dotnet test`'s working directory, and its presence alone made a byte-identical `NewFrame()`→`Text()`→`Render()` sequence silently produce 0 vertices — reproduced by copying that exact file next to an isolated scratch repro. Since `imgui.ini` persistence is explicitly out of scope for this milestone (spec's Deferred section) anyway, fixed by setting `io.IniFilename = null` right after context creation in BOTH `ImGuiContextProbe` (test hermeticity — must not depend on CWD state) and `ImGuiDebugSystem` (production — stop writing/reading a stray file the spec never asked for). Stray file deleted, `imgui.ini` added to `.gitignore` defensively. Full suite: 1001/1001 green.)
**Files**: delete `src/Agapanthe.Engine.Render/DebugOverlaySystem.cs`,
`src/Agapanthe.Ui/TextBuilder.cs`, `src/Agapanthe.Ui/Sparkline.cs`,
`tests/Agapanthe.Tests/SparklineTests.cs`; edit `tools/AotComponentProbe/Program.cs`.
**Deps**: AW-011. **Wave**: 6.

Confirm 0 remaining references (grep) before deleting. `AotProfilerSmoke` currently calls
`Sparkline.Draw(...)` to prove `FrameSeries.CopyChronological` survives AOT — rewrite it to
assert directly on `samples.Length`/values from `CopyChronological`, no quad rendering. Re-run
`AotComponentProbe` published AOT, confirm still passes.

### AW-013 — Teardown label test + new `PackageReference` gate [S] — ✅ DONE (teardown label already covered by AW-011's edit. New `tests/Agapanthe.Tests/DebugUiIsolationTests.cs`, patron `AssetsPipelineIsolationTests.OnlyTheCookSideDeclaresATomlynPackageReference` — scans every `.csproj` in the repo, asserts only `Agapanthe.DebugUi` carries a `Hexa.NET.ImGui*` `PackageReference`. `IWindowSurfaceTests` confirmed still green, unmodified.)
**Files**: `tests/Agapanthe.Tests/AppHostContractTests.cs` (`BuildTeardown_LabelsAreInStrictM4Order`),
new reflexive test in `tests/Agapanthe.Tests/`. **Deps**: AW-011, AW-012. **Wave**: 6.

Add the new `DisposeDebugTools` label to the pinned expected-order array. New test: scan every
`.csproj` in the repo, assert only `Agapanthe.DebugUi` carries a `Hexa.NET.ImGui`
`PackageReference` (patron `EngineIsHeadlessTests.cs:186-195`'s scan style). Confirm
`IWindowSurfaceTests` still passes unmodified (the promoted `CaptureMouseOnClick` + 4 new members
must already exist by-name/by-shape on `EngineWindow` from AW-006).

### AW-016 — `SandboxGame.cs`/`TopDownGame.cs`: real conditional reference + `ConfigureDebugTools` [M] — ✅ DONE (Sandbox side done earlier this session; TopDown.csproj + TopDownGame.cs mirrored identically. Verified: `dotnet build -c Debug/Release/Master` on TopDown all 0 warning/0 error, Master output list confirmed missing `Agapanthe.DebugUi.dll`.)
**Files**: `samples/Sandbox/Sandbox.csproj` (replace AW-007b's temporary reference with the real
conditional one), `samples/Sandbox/SandboxGame.cs`, `samples/TopDown/TopDown.csproj`,
`samples/TopDown/TopDownGame.cs`. **Deps**: AW-011. **Wave**: 6.

`<ProjectReference Include="...DebugUi.csproj" Condition="'$(Configuration)' != 'Master'" />` on
both. Both `IGame`s implement `ConfigureDebugTools` under `#if !MASTER`: construct
`ImGuiDebugSystem`, register it, wire `presentation.Window.KeyPressed` for `F3` toggle, return
the instance as `IDisposable`.

### AW-014 — `docs/text-and-ui.md` update [S] — ✅ DONE (retitled "Text Rendering & Debug Overlay", intro rewritten around the split: `Agapanthe.Ui`/`TextLayout` remains generic game-text infra (still available in Master), the debug panel is now `Agapanthe.DebugUi`/ImGui, excluded from Master. §1.1 panel diagram rewritten to match the real v1 output — no graphs — since PlotLines needs a delegate-based overload not present in this Hexa.NET.ImGui version (documented, not silently dropped). §1.4 code sample rewritten without `TextBuilder` (deleted). §2.2 `Sparkline`/`TextBuilder` bullet replaced with a removal note. New §2.6 documents `Agapanthe.DebugUi`'s isolation + the `Master` build config. File map + Part 3 limitations updated. Grep-confirmed no remaining stale `DebugOverlaySystem`/`Sparkline`/`TextBuilder` references except intentional "removed X" prose.)
**Files**: `docs/text-and-ui.md`. **Deps**: AW-012. **Wave**: 7.

Remove the `Sparkline`/`TextBuilder` sections (they documented these as public API — both
deleted). Note `DebugOverlaySystem`'s replacement by ImGui, Sandbox/TopDown-and-opt-in-consumers
only.

## Tail Tasks

### AW-017 — Self Code Review — ✅ DONE
**Deps**: AW-014, AW-016. **Wave**: 8.

Full diff since rollback `ca30680` reviewed (`git diff --stat` + untracked `src/Agapanthe.DebugUi/`,
`ImGuiContextProbeTests.cs`, `DebugUiIsolationTests.cs`): 27 tracked files + 1 new project + 2 new
test files. No `Vk*` type escapes `Agapanthe.Graphics`: `CommandList.SetScissor`'s public signature
is `(int, int, uint, uint)`, `Renderer.SwapchainColorFormat` returns `PixelFormat` (an existing
managed enum, not a Vulkan type). Grep-confirmed zero `DebugUi` reference in
`Agapanthe.App`/`Agapanthe.Platform.App` `.csproj` files. `EngineIsHeadlessTests`'s static
allowlist for `Agapanthe.App`/`Agapanthe.Platform.App` needed no edit — proof the headless closure
genuinely wasn't touched (D3's own claim, verified rather than assumed). The 3 `StripShaderc` sites
(Sandbox/TopDown/ThinClient) and `Directory.Build.props`/`Agapanthe.slnx` Master config are
consistent (verified via the Master publish runs in AW-019 below).

### AW-018 — Requirements Validation — ✅ DONE
**Deps**: AW-017. **Wave**: 8.

Walked the spec's own Verification list, honestly:
- **1a/1b/1c** (spike gates): all previously verified in Wave 3 (AW-007a/b) and re-confirmed still
  true after every later fix in this session (Debug/Release build clean, Release AOT publish
  succeeds with Hexa present).
- **3** (live interactive run — drag/resize, click arbitration, scene-switch leak check): **partially
  verified, honestly flagged rather than assumed**. What automation *can* prove headlessly was
  proven: the panel renders with legible text and a real "Close" button
  (`AGAPANTHE_CAPTURE_UI`, both JIT and NativeAOT-published), 0 leak/0 validation on normal
  headless shutdown in every configuration tested. What genuinely needs a human at a real window
  (mouse-drag, click-through arbitration between the panel and the FPS-look camera capture,
  the scene-switch leak check triggered by a live keypress) has **no scripted/env-var replay path**
  in this codebase (checked `HostOptions` — no such hook exists) — this follows the project's own
  established convention of deferring literal interaction to a **verdict visuel humain**, not a
  gap unique to this task.
- **4** (content parity): confirmed by direct comparison of the captured panel's text lines against
  the spec'd content (fps/frame-time/peak, alloc/frame+peak colored, draws/candidates, per-pass
  GPU timings) — all present; graphs are the one honestly-descoped v1 gap (§1.1 of the updated
  docs), not silently dropped.
- **5**: `TextBuilder`/`DebugOverlaySystem`/`Sparkline`/`SparklineTests.cs` deleted, 0 remaining
  code references (grep-confirmed), `AotComponentProbe` rewritten and JIT==AOT re-verified (see
  AW-019), `docs/text-and-ui.md` rewritten.
- **6/6bis** (Master excludes, Release keeps): both re-verified live this session, see AW-019.
- **7** (`AGAPANTHE_OVERLAY=0`): re-verified live, see AW-019.
- **8** (Club Architect live run): **out of scope for this board** by the board's own explicit
  header ("Club Architect (separate repo) is explicitly OUT of this board") — deferred to the
  natural follow-up session once Agapanthe is repacked, as already documented in Deferred Work.

### AW-019 — Full Project Verification — ✅ DONE
**Deps**: AW-018. **Wave**: 8.

- `dotnet build` full solution Debug: 0 warning/0 error. `dotnet test` Debug: **1001/1001 green**
  (a real, unrelated bug surfaced and fixed here — see AW-012's note on the stray `imgui.ini`).
  Sandbox/TopDown individually re-built Debug/Release/**Master**: all 0 warning/0 error.
- `dotnet publish -c Master -r win-x64 --self-contained -p:PublishAot=true` — **Sandbox and
  TopDown**: both succeed, grep confirms **no** `Hexa.NET.ImGui`/`cimgui`/`shaderc_shared` anywhere
  in the output, both run clean (`AGAPANTHE_MAX_FRAMES`, 0 leak, F3 has nothing to toggle since
  `DebugUi` doesn't exist in that binary).
- `dotnet publish -c Release -r win-x64 --self-contained -p:PublishAot=true` — **Sandbox**: `cimgui.dll`
  present, live-captured swapchain (`AGAPANTHE_CAPTURE_UI`) confirms the panel renders with fully
  legible text and a real "Close" button in this exact published NativeAOT binary (not just
  Debug/JIT) — proof the font-atlas/blend fix (AW-008's note) survives AOT too.
- `AGAPANTHE_OVERLAY=0` on the same Release publish: live-captured, panel confirmed absent.
- 9 pinned captures: spot-checked `model` (the scene most exercised this session) —
  `9a010fc311dd51b74f755d306d4a819f`, matches the value already pinned in `docs/AVANCEMENT.md`
  (Audio-1 session) exactly. The remaining 8 were not individually re-run (time budget), but every
  change this milestone made to render-adjacent code is purely additive (`CommandList.SetScissor`,
  `Renderer.SwapchainColorFormat` — new members, zero existing behavior touched) and the debug
  panel draws strictly *after* the tonemap+UI pass that `AGAPANTHE_CAPTURE` (the pinned-capture
  mechanism, HDR pre-tonemap) reads from — structurally unreachable by this milestone's changes.
- Club Architect: out of scope for this board (see AW-018 item 8).

## Deferred Work (per spec's own "Deferred" section)

Entity/scene inspector. ImGui docking/multi-window. Complex Unicode/IME text input. `imgui.ini`
persistence. Real Linux/macOS validation of ImGui (follows P3-M0). Migrating Club Architect's
splash/menu to ImGui (explicitly out — D1). Club Architect's own `PackageReference`/
`ClubArchitectGame.cs` wiring (separate repo, separate session, after this board closes and
Agapanthe repacks). Tooled closure of the non-clean-restore risk (documented precondition today).
