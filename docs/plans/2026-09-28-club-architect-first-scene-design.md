# Club Architect — first real scene: splash → menu (live sequential scene switching) (v3)

## Summary

The scene-management spec (`2026-09-27-scene-management-sequential-switching-design.md`, implemented,
`a2b50f5`) and its prerequisite `GraphicsDevice` thread-safety spec (`75c367a`) were built specifically
so a game like Club Architect could have a real splash → menu → game flow without a process restart.
Club Architect today (`D:\MyProjects\club-architect`, commit `5c5e917`) has exactly one `IGame` with
one empty `ISceneRecipe` ("empty", spawns nothing). This spec builds the first real scenes: a splash
screen that auto-advances to a menu, using the actual scene-switching mechanism just shipped.

**Revision history**: v1 scored **2.6/5, NEEDS WORK** on independent review. Four blocking findings,
all verified against real code: (1) the cited packaging precedent (`build/` `PackagePath`) does not
exist — `Agapanthe.Graphics.csproj` actually uses `buildTransitive/`, and v1 contradicted its own
citation three lines later; (2) `PrivateAssets="all"` does not prevent `FontCooker.dll`/
`StbTrueTypeSharp` from leaking into Club Architect's own build/publish output — the mechanism did
not deliver the guarantee the user explicitly required; (3) no game recipe can draw its own text
today — `UiRenderSystem` clears its draw list every tick in `Stage.Input`, so a one-time `DrawText`
call in `Build` would never render on any frame, and `PresentationSceneContext` exposes neither the
draw list nor the loaded font; (4) `AppHost` hardcodes the debug-overlay font's filename
(`fonts/JetBrainsMono-Regular.agfont`) — a differently-named font would simply never be found, so
v1's framing of a "collision" was itself wrong (there is no collision path to even reach). Two
further serious findings on the packaging mechanism: `$(PkgAgapanthe_FontCooker)` is not
auto-generated for a plain `PackageReference` without `GeneratePathProperty="true"`; and `dotnet
exec` against a NuGet-cache-restored DLL cannot resolve that DLL's own transitive dependencies
(`Agapanthe.Assets`/`Core`/`StbTrueTypeSharp` live in separate cache folders), which the proposed
fallback did not fix. All six are resolved in this v2 by two decisions the user made directly:
expose real UI drawing to game recipes (not descope it), and switch the tool-distribution mechanism
to `PackAsTool` (the .NET tool package format designed exactly for this dependency-closure problem)
instead of trying to extend the shader/asset `.targets`-copy pattern to a case it was never built
for (invoking an executable, not copying files).

**v2 scored 3.8/5, PASS-with-concerns** on independent re-review — all four round-1 blocking
findings confirmed resolved against real code, no new 🔴. Four 🟠 remained, all fixed directly here
per the reviewer's own recommendation (no third automated round needed for fixes of this size):
the font-path plumbing (v2's `HostOptions.FontPath`) was both ambiguous (no single definition of
what the string meant, so v2's own example value would not have resolved to the file it named) and
structurally wrong (an explicit `HostOptions` in `Program.cs` would have silently disabled every
env-driven option, including the ones this very spec's Verification needs) — replaced with
`IGame.FontPath` (a DIM, mirroring the existing `IGame.Universe` precedent exactly), which also
made a separate `WithoutStartupOnlyPaths()` update entirely moot (the property no longer lives on
`HostOptions` at all). `PresentationSceneContext` now exposes only `UiDrawList`/`UiFont`, not the
whole `UiRenderSystem` (v2 would have also handed a recipe `Execute`/`Render` to wrongly re-invoke).
Added: an explicit never-call-`LoadFont`-from-a-recipe contract, an `AssemblyName`-must-not-change
constraint on the packaged tool (its `InternalsVisibleTo` grant is keyed on it), the tool manifest
in `CookFonts`'s own `Inputs` (so a `FontCooker` upgrade re-cooks instead of silently shipping a
stale atlas), the required `Agapanthe.Platform.App` version bump, and a deterministic
capture-based verification item alongside the live one.

## Decisions

- **D1 — Scope.** Unchanged from v1: splash → menu only, text/keyboard-driven, no mouse/widgets,
  no football-management content yet.
- **D2 — Splash → menu trigger: a real `ISystem`.** Unchanged from v1: `SplashTimerSystem`
  (`Stage.Simulation`) accumulates `ctx.DeltaSeconds`, calls `sim.RequestSceneSwitch("menu")` past
  a threshold, then goes inert — the exact scenario D4 of the scene-management spec targets (a
  system calling `RequestSceneSwitch` from `Tick`, not a debug keypress).
- **D3 — Menu content.** Unchanged from v1: keyboard-navigable options (up/down + Enter);
  "Quitter" is real (`presentation.Window.Close()` — confirmed `ScopedWindow.Close()` forwards to
  the real window; only `Dispose()`/`Run()` are restricted, verified directly against
  `ScopedWindow.cs`); other options (e.g. "Nouvelle partie") are explicit no-op stubs logging "not
  implemented yet."
- **D4 — Font: Oswald, one shared atlas, explicit tradeoff confirmed with the user.** The engine
  keeps exactly one font atlas live process-wide (`Renderer.LoadFont`'s own doc comment: "a second
  one would mean a second atlas, a second descriptor set and a second draw call" — verified,
  `Renderer.cs:1473-1494`). Loading Oswald means the debug overlay (`F3`), if ever enabled, renders
  in Oswald too — confirmed acceptable; not pursuing a second atlas (a real Vulkan rendering
  change, far outside a splash/menu scene's scope). Source: the **static** `Oswald-Bold.ttf`
  (Google Fonts ships Oswald as a variable font by default — `Oswald[wght].ttf` — StbTrueType reads
  only the default instance from a variable font, so the static per-weight file under `static/` is
  required), plus its `OFL.txt` license file committed alongside (mirrors Agapanthe's own
  `fonts/OFL.txt` next to `JetBrainsMono-Regular.ttf`).
- **D5 — New engine capability: expose UI drawing to a game recipe (real gap, not previously
  designed).** `UiRenderSystem`'s own doc comment already states the intent ("lets gameplay code
  say 'draw this text' without ever touching a GPU type") — it was simply never wired past
  `AppHost`'s own internal locals. Three changes, revised from v2 after round-2 review found the
  font-path plumbing (v2's `HostOptions.FontPath`) both ambiguous (no single definition of what the
  string means — a bare filename or a `BaseDirectory`-relative path — so the exact value v2's own
  D7 proposed would not resolve to the file it names) and structurally wrong (constructing an
  explicit `HostOptions` in `Program.cs` silently disables `AGAPANTHE_MAX_FRAMES`/`CAPTURE_UI`/every
  other env-driven option, since an explicit instance fully overrides `FromEnvironment()` — needed
  for this very spec's own Verification step 3):
  - **`IGame.FontPath` (new DIM, `=> null`), not `HostOptions.FontPath`.** Mirrors the existing
    `IGame.Universe`/`HostOptions.Universe` precedent exactly (`AppHost.ResolveUniverse(game,
    options) => options.Universe ?? game.Universe`, verified real): `AppHost` resolves
    `var fontRelativePath = game.FontPath ?? Path.Combine("fonts", "JetBrainsMono-Regular.agfont");`
    then `Path.Combine(AppContext.BaseDirectory, fontRelativePath)` — **one single resolution
    point**, so there is no second place that could disagree on what the string means (closes the
    ambiguity directly, not just by writing a convention down). A game overrides the property on
    its own `IGame`, `HostOptions`/`FromEnvironment()` stay completely untouched — `Program.cs`
    keeps calling `AppHost.RunClient(game, window, args)` exactly as today, no explicit `HostOptions`
    construction anywhere. (No env-var override — unlike `Universe`, a font choice is a game
    identity decision, not a per-run tuning knob; dropped from v2 to avoid manufacturing a knob
    nothing asks for.)
  - **`PresentationSceneContext` gains two narrow members, not the whole `UiRenderSystem`**
    (round-2 finding: exposing the system itself would also hand a recipe `Execute`/`Render`,
    which it could wrongly re-invoke — double-clear or double-submit): `UiDrawList` (the existing
    `UiRenderSystem.DrawList` instance, already constructed by `AppHost` when a font is found — the
    exact same list `DebugOverlaySystem` already appends to) and `UiFont` (the loaded `FontAsset`,
    nullable — `null` exactly when no cooked font was found, mirroring the debug overlay's own
    existing silent-absence convention, not a new failure mode). Both are threaded into every
    `PresentationSceneContext` construction site in `AppHost.cs` — verified there are exactly two:
    the first-load site (`window.Loaded`) and `PerformHandOff`'s per-switch reconstruction (from the
    scene-management spec) — no new lifecycle to design, both already reconstruct the whole context
    object at every hand-off. `uiFont` (currently a local declared inside `window.Loaded`'s `if
    (File.Exists(fontPath))` block) is hoisted to the method-level scope, alongside `uiSystem`/
    `debugOverlay` (already hoisted there), so `PerformHandOff` can read it too.
  - **A recipe wanting persistent on-screen text registers its own `ISystem`** (`Stage.PostSimulation`,
    same stage `DebugOverlaySystem` uses) that calls `TextLayout.DrawText(presentation.UiDrawList,
    text, presentation.UiFont, position, pixelSize, rgba)` every tick. This system is scene-owned
    (like `PhysicsSystem` in `SceneRecipe.Build` today) — never re-registered across a switch, it
    simply becomes garbage when the orchestrator is discarded at the next hand-off. Confirmed safe
    across a switch: `Renderer.ResetSceneState()` only touches `_sceneCandidates` (the GPU-cull
    bookkeeping); `_fontResources` is untouched by a switch and only freed at `Renderer.Dispose()` —
    `UiFont` stays valid across every hand-off, no re-load needed.
  - **Contract, stated explicitly (round-2 finding)**: a recipe must never call
    `presentation.Renderer.LoadFont(...)` itself — `Renderer` exposes it publicly, and a recipe
    reaching it directly would replace the shared atlas without updating `UiFont`, corrupting glyph
    UVs silently (old font's `FontAsset`, new atlas). Loading the font is `AppHost`'s job alone.
  - **If `UiFont`/`UiDrawList` are null** (no cooked font found), a recipe wanting text throws a
    clear, actionable exception rather than silently drawing nothing — text is the whole point of a
    splash/menu scene, unlike the debug overlay where silent absence is an acceptable degradation.
- **D6 — Package `FontCooker` as a .NET tool (`PackAsTool`), corrected mechanism.**
  - `tools/FontCooker/FontCooker.csproj` gains `<IsPackable>true</IsPackable>`,
    `<PackAsTool>true</PackAsTool>`, `<PackageId>Agapanthe.FontCooker</PackageId>` (explicit — the
    bare `AssemblyName=FontCooker` would otherwise default the package id to `FontCooker`, not
    `Agapanthe.FontCooker`, verified: no `PackageId` exists today), `<ToolCommandName>agapanthe-fontcooker</ToolCommandName>`,
    `<Description>`. **No custom `.targets` file, no `$(Pkg...)` property tricks** — `dotnet tool
    install` performs its own full, isolated dependency-closure restore into the tool's private
    install location, which is exactly what a plain `PackageReference` + `dotnet exec` cannot do
    (the actual root cause of v1's finding). This also fully resolves v1's version-alignment
    concern (🟠9): the tool's own `Agapanthe.Assets`/`Core` references live entirely inside its own
    isolated install, with zero relationship to whatever version Club Architect's own
    `Agapanthe.Platform.App` reference pins.
  - **Constraint, stated explicitly (round-2 finding)**: `AssemblyName` stays `FontCooker` — never
    renamed. `Agapanthe.Assets`'s writer-side internals are exposed via `[assembly:
    InternalsVisibleTo("FontCooker")]` (verified, `FontAssetFormat.cs:6`), keyed on the assembly
    name, not the new `PackageId`. Only `PackageId`/`ToolCommandName` change; `AssemblyName` does not.
  - Repack (`dotnet pack Agapanthe.slnx -c Debug -o artifacts/package`) — same step as every prior
    engine-side change in this chain.
  - **Guarantee this actually gives, stronger than v1's**: a tool manifest
    (`.config/dotnet-tools.json`) is **not** a `PackageReference` in any `.csproj` at all — it is a
    separate, project-independent file consumed only by the `dotnet tool` CLI. There is structurally
    no path from it into Club Architect's own compile/publish graph, unlike v1's `PrivateAssets="all"`
    approach which still placed the tool's DLL under `lib/net10.0/` of a real package reference.
- **D7 — Club Architect content and wiring.**
  - `fonts/Oswald-Bold.ttf` + `fonts/OFL.txt` + `fonts/charset.txt` (reuse Agapanthe's own charset
    content — basic Latin + French accents — verified to cover the actual splash/menu text).
  - `.config/dotnet-tools.json` (new, via `dotnet tool install --local Agapanthe.FontCooker
    --version <new>` against the existing local feed already configured in `NuGet.config`).
  - `ClubArchitect.csproj` gains a `CookFonts` MSBuild target, **local to this project** (mirrors
    `Sandbox.csproj`'s own `CookFonts` being local to Sandbox, never injected by a shared package —
    same principle, corrected mechanism):
    ```xml
    <Target Name="CookFonts" Inputs="@(FontSourceFile);.config\dotnet-tools.json" Outputs="$(FontCacheStamp)">
      <Exec Command="dotnet tool restore" />
      <Exec Command="dotnet tool run agapanthe-fontcooker -- &quot;$(FontSourceDir)Oswald-Bold.ttf&quot; &quot;$(FontSourceDir)charset.txt&quot; &quot;$(FontCacheStageDir)Oswald-Bold.agfont&quot;" />
      <Touch Files="$(FontCacheStamp)" AlwaysCreate="true" />
    </Target>
    ```
    (`.config\dotnet-tools.json` added to `Inputs` — round-2 finding: `Sandbox.csproj`'s own
    `CookFonts` includes the cooker's own sources in `Inputs` precisely so a cooker upgrade
    re-cooks instead of silently shipping a stale atlas; the tool-manifest file is this project's
    equivalent trigger, since the tool's own source is no longer local.) Plus an `IncludeCookedFonts
    BeforeTargets="AssignTargetPaths"` step shipping the `.agfont` as `Content` under `fonts\`,
    mirroring `Sandbox.csproj`'s own `IncludeCookedFonts` shape exactly (verified against the real
    target, not assumed).
  - `IGame.FontPath => "fonts/Oswald-Bold.agfont"` on `ClubArchitectGame` (D5's new DIM override —
    **not** `HostOptions`, **not** an env var). `Program.cs` is unchanged: still calls
    `AppHost.RunClient(game, window, args)` with no explicit `HostOptions`, so `FromEnvironment()`
    still resolves every other option from the process environment exactly as today (closes the
    round-2 finding that an explicit `HostOptions` instance would have silently disabled
    `AGAPANTHE_MAX_FRAMES`/`CAPTURE_UI`, needed for Verification step 3 below).
  - `Agapanthe.Platform.App` `PackageReference` version in `ClubArchitect.csproj` must be bumped to
    the version packed for this spec — the new `PresentationSceneContext`/`IGame` members only
    exist from that version forward (round-2 finding: the currently-pinned `0.1.5-ga2b50f5b33`
    predates this spec).
  - `Scenes/SplashSceneRecipe.cs` (`ISceneRecipe`, `Name => "splash"`) — `Build(object? prefetched,
    SimSceneContext sim, PresentationSceneContext? presentation)`: guards `presentation ?? throw`
    (client-only, matching every existing client recipe's pattern), sets a black environment,
    registers a `SplashTextSystem` (draws the title every tick via D5's mechanism) and
    `SplashTimerSystem` (D2) on `Stage.PostSimulation`/`Stage.Simulation` respectively.
  - `Systems/SplashTimerSystem.cs` (`ISystem`) — as v1, unchanged.
  - `Scenes/MenuSceneRecipe.cs` (`Name => "menu"`) — `Build` registers a `MenuTextSystem` (draws
    the option list, highlighting the selected one, every tick) and wires
    `presentation.Window.Updated`/`KeyPressed` (through the host-provided `ScopedWindow` — D11 of
    the scene-management spec already covers cleanup transparently, zero new subscription code) for
    up/down selection + Enter.
  - `ClubArchitectGame.cs`: `Scenes` gains `SplashSceneRecipe`/`MenuSceneRecipe`;
    `DefaultScene => "splash"`.

## Verification

1. `dotnet pack` Agapanthe; confirm `Agapanthe.FontCooker` installs as a real local tool
   (`dotnet tool install --local Agapanthe.FontCooker --version <new>` succeeds against the local
   feed at `D:\MyProjects\agapanthe\artifacts\package`, from Club Architect's own working
   directory, using only its own `NuGet.config`/tool manifest — not by referencing any Agapanthe
   source file directly) and `dotnet tool run agapanthe-fontcooker -- <ttf> <charset> <out.agfont>`
   produces a valid `.agfont`. Also confirm the `--` separator and automatic tool-manifest creation
   behave as expected on this machine's installed SDK version (round-2 finding: `FontCooker`'s own
   arg parser is strict — `args.Length is not (3 or 5)` — so this is worth confirming empirically,
   not assuming the CLI plumbing is transparent).
2. Club Architect: `dotnet tool restore` + a clean `dotnet build` triggers `CookFonts`; delete the
   `.agfont` output, confirm it regenerates; confirm it lands under `<bin>/fonts/`.
3. Live run: splash shows the title (drawn via D5's mechanism, not just logged), auto-advances to
   menu after N seconds with **no keypress**; menu options are keyboard-navigable and the title/
   text is visible on screen (not just asserted — an actual screenshot or live visual check);
   "Quitter" closes the window; 0 leak, 0 validation message.
4. `dotnet publish -r win-x64 --self-contained -p:PublishAot=true`: confirm the published output
   contains **no** `FontCooker`-related assembly and no `StbTrueTypeSharp` — this time structurally
   guaranteed (no `PackageReference` exists to leak), not just empirically hoped for.
5. Toggle `F3` during a live run (if the debug overlay ends up wired for Club Architect — optional,
   not blocking): confirm it renders in Oswald without corruption, proving the shared-atlas
   decision (D4) holds in practice, not just in theory.
6. **Deterministic, scripted check (round-2 finding — the project already has the infrastructure for
   this, unused in v2's all-manual plan)**: `AGAPANTHE_CAPTURE_UI` + `AGAPANTHE_MAX_FRAMES` make the
   splash timer run on a synthetic constant dt (existing project-wide convention, not new). Two
   pinned captures — one at a frame count before the auto-advance threshold (splash title visible),
   one after (menu visible, reached with zero keypresses) — turn "the timer-driven switch actually
   happened and text actually rendered" into a byte-comparable, repeatable check instead of a purely
   human one.

## Deferred (explicitly out of scope)

Football-management gameplay/content of any kind. Mouse-driven UI/widgets. A second font atlas
(would need real Vulkan rendering work — new pipeline/descriptor set/draw call). Packaging
`AssetCooker` (deferred until Club Architect needs real `.agmodel` content). Wiring the debug
overlay itself for Club Architect (D5's mechanism makes it possible but this spec does not do it —
`HostOptions.OverlayVisible` already defaults appropriately either way).
