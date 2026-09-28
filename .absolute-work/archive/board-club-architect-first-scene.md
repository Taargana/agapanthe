# Absolute Work Board — Club Architect first scene: splash → menu

Status: **completed** (2026-09-28) — all 6 waves done, tail verification passed.

Spec: `docs/plans/2026-09-28-club-architect-first-scene-design.md` (v3, 2 independent review
rounds — 2.6/5 → 3.8/5 → fixes applied directly per reviewer's own recommendation, approved by
user). INTAKE/BRAINSTORM and SPEC phases already done via `/absolute-brainstorm` — this board
starts at DECOMPOSE.

**Cross-repo**: Part A in `D:\MyProjects\agapanthe` (engine capability + tool packaging), Part B in
`D:\MyProjects\club-architect` (game content). Two separate rollback points, two separate commits
expected at the end (never cross-commit).

## Rollback Points

- Agapanthe: commit `a2b50f5` (clean tree at time of writing this board — confirm before Wave 1).
- Club Architect: commit `5c5e917` (clean tree — confirm before Wave 4).

## Project Conventions (reused from prior boards)

- .NET 10, `dotnet build`/`dotnet test` from repo root, `TreatWarningsAsErrors`.
- `IGame.Universe`/`AppHost.ResolveUniverse` is the exact precedent `IGame.FontPath` mirrors —
  verified real (`AppHost.cs`, `ResolveUniverse(game, options) => options.Universe ?? game.Universe`).
- Font-cook precedent: `Sandbox.csproj`'s `CookFonts`/`IncludeCookedFonts` targets (local to the
  consuming project, never injected by a shared package) — verified real, cited exactly in the spec.
- Club Architect consumes Agapanthe **only** via NuGet from the local feed
  (`D:\MyProjects\agapanthe\artifacts\package`, configured in its `NuGet.config`) — never
  `ProjectReference`. This is the one invariant every task must respect.
- Commits: never `git commit`/`push` without explicit request, in **either** repo.

## Task DAG

```
Wave 1 (Agapanthe, parallel — 3 disjoint files)
  CA-001 IGame.FontPath DIM (IGame.cs)
  CA-002 PresentationSceneContext: UiDrawList/UiFont members
  CA-003 FontCooker.csproj: PackAsTool/PackageId/ToolCommandName
                    |         |         |
                    +----+----+----+----+
                         |
                         v
Wave 2 (Agapanthe, sequential — one file)
  CA-004 AppHost.cs: font-path resolution, hoist uiFont, populate both
         PresentationSceneContext construction sites   [depends: CA-001, CA-002]
                         |
                         v
Wave 3 (Agapanthe, sequential)
  CA-005 Repack Agapanthe; verify Agapanthe.FontCooker installs as a
         real local tool                                [depends: CA-003, CA-004]
                         |
                         v
Wave 4 (Club Architect, parallel — 3 disjoint files/areas)
  CA-006 Source Oswald-Bold.ttf (static) + OFL.txt + charset.txt
  CA-007 .config/dotnet-tools.json (dotnet tool install --local)   [depends: CA-005]
  CA-008 ClubArchitect.csproj: bump Platform.App version,
         add CookFonts/IncludeCookedFonts targets         [depends: CA-005]
                    |         |         |
                    +----+----+----+----+
                         |
                         v
Wave 5a (Club Architect, parallel — 3 disjoint new files)
  CA-009 Systems/SplashTimerSystem.cs
  CA-010 Systems/SplashTextSystem.cs
  CA-012 Systems/MenuTextSystem.cs
                    |         |         |
         +----------+         +---------+
         v                              v
Wave 5b (Club Architect, parallel — 2 disjoint files)
  CA-011 Scenes/SplashSceneRecipe.cs   CA-013 Scenes/MenuSceneRecipe.cs
  [depends: CA-009, CA-010]            [depends: CA-012]
                    |         |
                    +----+----+
                         v
Wave 5c (Club Architect, sequential)
  CA-014 ClubArchitectGame.cs: register both recipes, DefaultScene
         [depends: CA-011, CA-013]
                         |
                         v
Wave 6 (tail, mandatory, sequential)
  CA-015 Self Code Review
  CA-016 Requirements Validation (spec's 6-item Verification list)
  CA-017 Full Project Verification (both repos)
```

## Tasks

### CA-001 — `IGame.FontPath` DIM [S] — ✅ DONE (also fixed a stale doc comment on `Scenes` still claiming "no in-process switching")
**Files**: `src/Agapanthe.App/IGame.cs`. **Deps**: none. **Wave**: 1.

New `string? FontPath => null;` default-interface member, mirroring `Universe`'s own DIM shape
exactly (`IGame.Universe => UniverseId.None`, verified). Doc comment: the value is a path relative
to `AppContext.BaseDirectory` (e.g. `"fonts/Oswald-Bold.agfont"`), resolved by `AppHost` — `null`
means the engine default (`fonts/JetBrainsMono-Regular.agfont`).

### CA-002 — `PresentationSceneContext`: `UiDrawList`/`UiFont` [S] — ✅ DONE
**Files**: `src/Agapanthe.App/PresentationSceneContext.cs`. **Deps**: none. **Wave**: 1.

Two new members, both **not** `required` (nullable-safe defaults):
- `public UiDrawList? UiDrawList { get; init; }` — the shared `UiRenderSystem.DrawList` instance,
  or `null` if no font was loaded (so no `UiRenderSystem` exists).
- `public FontAsset? UiFont { get; init; }` — the loaded font, or `null`.

Doc comment states the D5 contract explicitly: a recipe appends to `UiDrawList` via
`TextLayout.DrawText`, never constructs its own `UiRenderSystem`, and **never** calls
`presentation.Renderer.LoadFont(...)` (would silently corrupt glyph UVs — old `FontAsset` against
a new atlas). Needs `using Agapanthe.Ui;` (for `UiDrawList`) and `using Agapanthe.Assets.Font;` (or
wherever `FontAsset` lives — confirm the real namespace before writing the `using`).

### CA-003 — `FontCooker.csproj`: `PackAsTool` [S] — ✅ DONE
**Files**: `tools/FontCooker/FontCooker.csproj`. **Deps**: none. **Wave**: 1.

Add:
```xml
<IsPackable>true</IsPackable>
<PackAsTool>true</PackAsTool>
<PackageId>Agapanthe.FontCooker</PackageId>
<ToolCommandName>agapanthe-fontcooker</ToolCommandName>
<Description>Agapanthe engine — offline .ttf → .agfont SDF font cooker (build-time tool).</Description>
```
**Do not** change `<AssemblyName>FontCooker</AssemblyName>` (D6's explicit constraint —
`Agapanthe.Assets`'s `[assembly: InternalsVisibleTo("FontCooker")]` grant is keyed on it).

### CA-004 — `AppHost.cs`: font-path resolution + context population [M] — ✅ DONE (byte-identical regression confirmed: model capture 9a010fc3…, 167 resources, 0 leak)
**Files**: `src/Agapanthe.App/AppHost.cs`. **Deps**: CA-001, CA-002. **Wave**: 2.

- Replace the hardcoded `Path.Combine(AppContext.BaseDirectory, "fonts",
  "JetBrainsMono-Regular.agfont")` with a resolution mirroring `ResolveUniverse`:
  `var fontRelativePath = game.FontPath ?? Path.Combine("fonts", "JetBrainsMono-Regular.agfont");`
  then `Path.Combine(AppContext.BaseDirectory, fontRelativePath)` — **one single resolution point**.
- Hoist `uiFont` from its current `if (File.Exists(fontPath))` block-local scope to the method-level
  scope, alongside the already-hoisted `uiSystem`/`debugOverlay`.
- Populate `UiDrawList = uiSystem?.DrawList, UiFont = uiFont` on **both**
  `PresentationSceneContext` construction sites: the first-load site (`window.Loaded`) and
  `PerformHandOff`'s per-switch reconstruction (from the scene-management spec, verified: exactly
  two construction sites exist in this file).
- No behavior change for every existing host (Sandbox/TopDown/ThinClient) that doesn't implement
  `IGame.FontPath` — confirm by re-running the pinned-capture regression check in CA-017.

### CA-005 — Repack Agapanthe; verify the tool installs standalone [S] — ✅ DONE (live end-to-end: nupkg inspected — tools/net10.0/any/ carries FontCooker.dll + full dependency closure Assets/Core/StbTrueTypeSharp/StbImageSharp; installed as a local tool in Club Architect and cooked a real .agfont from JetBrainsMono-Regular.ttf, 192 glyphs, standalone, no Agapanthe checkout referenced)
**Files**: none (build/verification step). **Deps**: CA-003, CA-004. **Wave**: 3.

1. `dotnet build` (full solution, 0 warnings) then `dotnet test` (full suite, unaffected count).
2. `dotnet pack Agapanthe.slnx -c Debug -o artifacts/package`.
3. From Club Architect's own working directory (not Agapanthe's), with only Club Architect's own
   `NuGet.config` in scope: confirm `dotnet tool install --local Agapanthe.FontCooker --version
   <new>` succeeds, and `dotnet tool run agapanthe-fontcooker -- <a .ttf> <a charset.txt> <out
   path>` produces a valid `.agfont` — confirms the `--` separator and automatic tool-manifest
   creation behave as expected on this machine's SDK (spec Verification #1's explicit ask, not
   assumed).

### CA-006 — Source Oswald (static) + license + charset [S]
**Files**: new `src/ClubArchitect/fonts/Oswald-Bold.ttf`, `fonts/OFL.txt`, `fonts/charset.txt`.
**Deps**: none. **Wave**: 4.

Download the **static** `Oswald-Bold.ttf` (not the variable `Oswald[wght].ttf` — StbTrueType only
reads a variable font's default instance) from the real Google Fonts source, plus its `OFL.txt`.
`charset.txt`: start from Agapanthe's own `fonts/charset.txt` content (basic Latin + French
accents), verified to cover every character actually used in the splash title + menu option text
written in CA-011/CA-013 (check after those are drafted; adjust if a character is missing).

### CA-007 — Club Architect tool manifest [S]
**Files**: new `D:\MyProjects\club-architect\.config\dotnet-tools.json`. **Deps**: CA-005.
**Wave**: 4.

`dotnet new tool-manifest` (if none exists) then `dotnet tool install --local
Agapanthe.FontCooker --version <the version packed in CA-005>` from the Club Architect repo root
(its `NuGet.config` already points at the local feed).

### CA-008 — `ClubArchitect.csproj`: version bump + cook targets [M] — ✅ DONE (fixed a real MSBuild path-separator bug found live: `$(MSBuildProjectDirectory)fonts` with no separator resolved to `...ClubArchitectfonts\` — added explicit `\`; clean rebuild confirmed `CookFonts` produces `Oswald-Bold.agfont` (190 glyphs) and `IncludeCookedFonts` ships it to `bin/Debug/net10.0/fonts/`)
**Files**: `D:\MyProjects\club-architect\src\ClubArchitect\ClubArchitect.csproj`. **Deps**: CA-005.
**Wave**: 4.

- Bump `Agapanthe.Platform.App` `PackageReference` version to the one packed in CA-005 (the new
  `IGame.FontPath`/`PresentationSceneContext` members only exist from that version forward).
- Add `CookFonts` (`Inputs="@(FontSourceFile);.config\dotnet-tools.json"
  Outputs="$(FontCacheStamp)"`, body: `dotnet tool restore` then `dotnet tool run
  agapanthe-fontcooker -- "$(FontSourceDir)Oswald-Bold.ttf" "$(FontSourceDir)charset.txt"
  "$(FontCacheStageDir)Oswald-Bold.agfont"`, then `Touch`) and `IncludeCookedFonts
  BeforeTargets="AssignTargetPaths"` (ships the `.agfont` as `Content` under `fonts\`) — mirror
  `Sandbox.csproj`'s own two targets' exact shape (property names `FontSourceDir`/
  `FontCacheStageDir`/`FontCacheStamp`, the pre-expand-then-`Content` pattern for
  `IncludeCookedFonts`), verified against the real file, not assumed.

### CA-009 — `Systems/SplashTimerSystem.cs` [S] — ✅ DONE
**Files**: new `src/ClubArchitect/Systems/SplashTimerSystem.cs`; new
`tests/...SplashTimerSystemTests.cs` if Club Architect has a test project (check — if not, this is
pure logic worth a quick manual trace instead, no new test infra for one class). **Deps**: none.
**Wave**: 5a.

`ISystem`, `Stage.Simulation`. Accumulates `ctx.DeltaSeconds` in a private field; once past a
threshold constant (e.g. 3 seconds), calls `sim.RequestSceneSwitch("menu")` **once**, then sets an
internal `_done` flag so it never calls again (a repeat call is harmless per D4/D9.2 of the
scene-management spec, but pointless — write the guard anyway, it costs nothing and documents
intent). Needs `SimSceneContext` — take it as a constructor parameter (mirrors how other
`ISystem`s needing world/sim access are constructed, e.g. `PhysicsSystem(world, in settings)`).

### CA-010 — `Systems/SplashTextSystem.cs` [S] — ✅ DONE
**Files**: new `src/ClubArchitect/Systems/SplashTextSystem.cs`. **Deps**: none. **Wave**: 5a.

`ISystem`, `Stage.PostSimulation` (same stage `DebugOverlaySystem` uses). Constructor takes
`PresentationSceneContext` (or just the two fields it needs — `UiDrawList`/`UiFont` — prefer the
narrower constructor, matching the project's general preference for minimal captured surface).
`Execute` calls `TextLayout.DrawText(uiDrawList, "CLUB ARCHITECT", uiFont, position, pixelSize,
rgba)` — throws a clear `InvalidOperationException` in the constructor if `uiDrawList`/`uiFont` are
null (D5's stated contract: text is the whole point of this scene, silent absence is wrong here).

### CA-011 — `Scenes/SplashSceneRecipe.cs` [S] — ✅ DONE
**Files**: new `src/ClubArchitect/Scenes/SplashSceneRecipe.cs`. **Deps**: CA-009, CA-010.
**Wave**: 5b.

`ISceneRecipe`, `Name => "splash"`. `PrefetchBackground` uses the DIM default (`=> null` — nothing
to prefetch). `Build(object? prefetched, SimSceneContext sim, PresentationSceneContext?
presentation)`: `var p = presentation ?? throw new InvalidOperationException("splash is
client-only");`, `p.Renderer.SetEnvironment(BlackEnvironment.Build())`, `sim.AddSystem(Stage.Simulation,
new SplashTimerSystem(sim))`, `p.Orchestrator.Add(Stage.PostSimulation, new SplashTextSystem(p))`
(confirm the exact registration call shape against `PresentationSceneContext.Orchestrator`'s real
API before writing — mirror how `AppHost.cs` itself registers `debugOverlay`).

### CA-012 — `Systems/MenuTextSystem.cs` [S] — ✅ DONE
**Files**: new `src/ClubArchitect/Systems/MenuTextSystem.cs`. **Deps**: none. **Wave**: 5a.

`ISystem`, `Stage.PostSimulation`. Draws each menu option as a line via `TextLayout.DrawText`,
highlighting the currently-selected index (a different `rgba` color, or a `>` prefix — pick
whichever is simpler to implement correctly first try). Takes the option label list + a
`Func<int>`/shared selected-index reference from `MenuSceneRecipe` (exact shape decided during
implementation — keep it simple, this is a 2-3 option list, not a generic widget).

### CA-013 — `Scenes/MenuSceneRecipe.cs` [S] — ✅ DONE
**Files**: new `src/ClubArchitect/Scenes/MenuSceneRecipe.cs`. **Deps**: CA-012. **Wave**: 5b.

`ISceneRecipe`, `Name => "menu"`. `Build`: `p.Renderer.SetEnvironment(BlackEnvironment.Build())`,
registers `MenuTextSystem`, wires `presentation.Window.KeyPressed` (through the host-provided
`ScopedWindow` — D11 of the scene-management spec covers cleanup transparently, zero new
subscription code) for Up/Down (move selection, wrap at both ends) and Enter (activate). Two
options: **"Quitter"** → `presentation.Window.Close()` (real); **"Nouvelle partie"** → `Log.Info("not
implemented yet")` (explicit stub, per D3).

### CA-014 — `ClubArchitectGame.cs`: register scenes [S] — ✅ DONE (kept EmptySceneRecipe dormant as a third recipe; clean build 0 warnings, 0 errors after purging the stale NuGet cache — same version string, needed a cache purge to pick up CA-004's changes)
**Files**: `D:\MyProjects\club-architect\src\ClubArchitect\ClubArchitectGame.cs`. **Deps**: CA-011,
CA-013. **Wave**: 5c.

`Scenes` gains `new SplashSceneRecipe(), new MenuSceneRecipe()` (the existing `EmptySceneRecipe`
may be removed if no longer needed, or kept as a dormant third recipe — decide during
implementation based on whether anything still references `"empty"`). `DefaultScene => "splash"`.
`FontPath => "fonts/Oswald-Bold.agfont"` (the new DIM override from CA-001).

## Tail Tasks

### CA-015 — Self Code Review — ✅ DONE
**Deps**: CA-014. **Wave**: 6.
Re-read every diff in both repos against the spec's D1-D7. Confirm no `ProjectReference` was
accidentally introduced anywhere (the one invariant that must never break). Confirm `AssemblyName`
on `FontCooker.csproj` is unchanged. Confirm every existing pinned Agapanthe capture/snapshot is
unaffected by CA-004 (font-path resolution must be a no-op for every host that doesn't set
`IGame.FontPath`).

**Result**: no `ProjectReference` in Club Architect's `.csproj` (grep only matched comments).
`AssemblyName` unchanged in `FontCooker.csproj`. `AppHost.cs` diff vs rollback `a2b50f5` is 12
insertions/2 deletions — small and additive as planned. `dotnet test` (Release, full suite):
1011/1015 pass; the 4 failures (`GameWorldOwnerThreadTests`/`GameWorldNetworkAccessTests`, all
thread-sanctioning guards) reproduce identically byte-for-byte on a clean `git stash` back to
rollback commit `a2b50f5` — confirmed **pre-existing, unrelated to any CA task** (none of CA-001
through CA-014 touch `Agapanthe.World`/`GameWorld` thread-guard code), not a regression from this
work. `model` scene headless capture: `9a010fc311dd51b74f755d306d4a819f`, identical to the hash
already confirmed after CA-004, 0 leak — font-path resolution is behavior-preserving.

### CA-016 — Requirements Validation — ✅ DONE
**Deps**: CA-015. **Wave**: 6.
Walk the spec's own 6-item Verification list one at a time, honestly reporting what was actually
exercised vs. assumed (this project's own established practice — see the scene-management board's
own precedent for this exact honesty format).

**Result, item by item**:
1. ✅ Done in CA-005 — `dotnet tool install --local`, `dotnet tool run` verified live end-to-end.
2. ✅ Done in CA-008 — clean `dotnet build` triggers `CookFonts`, `.agfont` lands under `bin/.../fonts/`.
3. ✅ **Live-verified this session, not just built**: found and fixed a real bug in the process —
   `TextLayout.DrawText`'s `TextAlign.Center` re-centers a *shorter* line against a *wider* one in a
   multi-line block; for a single line (the splash title, each menu option) its own extent IS the
   widest line, so `Center` is a no-op and the naive call rendered flush-left from the anchor point,
   not screen-centered. Fixed by measuring first (`TextLayout.Measure`) and computing the true
   screen-center offset by hand in `SplashTextSystem`/`MenuTextSystem`, verified by re-capturing and
   visually inspecting both (PPM→PNG, converted with a throwaway scratchpad tool since no image
   tool was on this machine). Splash title now dead-centered; menu shows "> Nouvelle partie"
   (highlighted white) / "Quitter" (grey), both centered. Scene switch confirmed via log line
   (`[scene switch] loading 'menu'` → `now on 'menu'`) at frame ~180 with **zero keypresses** sent
   — proves the `ISystem`-driven `RequestSceneSwitch` path, not the debug-key path. 0 leak both
   captures (140 → 196 resources across the switch). "Quitter"/"Nouvelle partie" keyboard logic
   traced by code reading (Up/Down wrap, Enter activates) — not exercised with a live keypress
   in this headless session (no interactive terminal here); this is the one sub-item taken on
   code-reading confidence rather than a live keypress trace.
4. ✅ `dotnet publish -r win-x64 --self-contained -p:PublishAot=true` succeeded (after adding
   `vswhere.exe`'s directory to `PATH` — an environment gap on this shell, unrelated to the code);
   published output inspected directly: no `FontCooker`-named file, no `StbTrueTypeSharp` anywhere,
   `fonts/` contains only the cooked `Oswald-Bold.agfont` — structurally guaranteed by the tool-
   manifest mechanism (D6), confirmed in practice. Ran the published AOT `.exe` standalone: clean
   splash capture, 0 leak.
5. ✅ Debug overlay (`HostOptions.OverlayVisible` defaults `true` engine-wide, per spec's own
   Deferred note) is visible in both captures above, rendering cleanly in the same Oswald atlas —
   no corruption, confirming the shared-atlas decision (D4) holds in practice.
6. ✅ Done as part of item 3 above: `AGAPANTHE_MAX_FRAMES=90` (splash, before the 3s/~180-tick
   threshold) vs. `AGAPANTHE_MAX_FRAMES=220` (menu, after) with `AGAPANTHE_CAPTURE_UI`, on a
   synthetic constant dt — turned the timer-driven switch into a repeatable, inspectable check
   rather than a purely manual one. Not formally added as a byte-pinned regression test/hash in
   this session (no test project exists in Club Architect yet) — captured and inspected visually,
   which is what item 6 asked for ("byte-comparable, repeatable" — the byte-comparison step itself
   is future work if Club Architect grows a test project).

### CA-017 — Full Project Verification — ✅ DONE
**Deps**: CA-016. **Wave**: 6.
- Agapanthe: `dotnet build` + `dotnet test` (full suite, unchanged count from before this spec) +
  every pinned capture re-verified byte-identical (CA-004 must be behavior-preserving for every
  existing host).
- Club Architect: `dotnet build`, live run (splash → auto-advance → menu → keyboard nav → Quitter
  closes the window), 0 leak / 0 validation message.
- `dotnet publish -r win-x64 --self-contained -p:PublishAot=true` on Club Architect: confirm the
  published output contains no `FontCooker`-related assembly, no `StbTrueTypeSharp` — structurally
  guaranteed by the tool-manifest mechanism (D6), verify it holds in practice.

**Result**: Agapanthe `dotnet build Agapanthe.slnx -c Release` — 0 warnings, 0 errors, all samples +
tests build. `dotnet test` — 1011/1015 pass; 4 failures confirmed pre-existing on rollback commit
`a2b50f5` (CA-015's stash A/B), unrelated to this milestone. `model` scene capture
`9a010fc311dd51b74f755d306d4a819f` unchanged, 0 leak.

Club Architect: `dotnet build -c Release` — 0 warnings, 0 errors. Headless captures (CA-016 item
3/6): splash title centered, auto-advances to menu at frame ~180 with zero keypresses (log-confirmed
`[scene switch] loading 'menu'` → `now on 'menu'`), menu renders "> Nouvelle partie"/"Quitter" with
correct highlight, 0 leak across the switch (140→196 resources). `dotnet publish -r win-x64
--self-contained -p:PublishAot=true` succeeded (needed `vswhere.exe`'s directory added to `PATH` —
a pre-existing environment gap on this shell/machine, not a code issue); published output inspected
directly — no `FontCooker`/`StbTrueTypeSharp` anywhere, `fonts/` holds only the cooked
`Oswald-Bold.agfont`. Ran the published AOT `.exe` standalone: clean splash capture, 0 leak,
confirming the whole packaging chain (Agapanthe.FontCooker as a `dotnet tool`, never a
`PackageReference`) holds under NativeAOT.

**Not exercised**: an actual interactive keypress trace of Up/Down/Enter/Quitter (no interactive
terminal in this session) — the logic was read and reasoned through instead of live-driven; noted
honestly rather than claimed as verified.

**Board status: `completed`** (2026-09-28).

## Deferred Work (per spec's own "Deferred" section)

Football-management gameplay/content. Mouse-driven UI/widgets. A second font atlas. Packaging
`AssetCooker`. Wiring the debug overlay for Club Architect.
