# Club Architect — packaged separation from Agapanthe

## Summary

The user wants to start building a football-management game ("Club Architect") using Agapanthe as
its engine, explicitly to surface missing engine capabilities through real iteration. Locked
constraint, stated directly: **the game's code must not be coupled to the engine's code the way a
Unity game is coupled to the Unity editor**. Today, every consumer of Agapanthe
(`samples/Sandbox`, `TopDown`, `HeadlessSim`, `DedicatedServer`, `ThinClient`) lives in the **same
repository, the same solution** (`Agapanthe.slnx`), referencing engine projects via direct
`ProjectReference` — exactly the coupling mode the user rejects for a real game.

Interview (`/absolute-brainstorm`): the user chose all four decoupling axes at once — separate
repo/history, a real API/binary boundary, independent versioning, independent build/release
pipeline. This spec covers **only** the separation mechanics (packaging, feed, versioning, a
minimal `IGame` that runs from the new repo) — **not** the football-manager game's own design
(rules, match simulation, UI screens), which stays a separate, later effort, deliberately out of
scope here.

**Revision note**: two independent review rounds. Round 1 (2.65/5, NEEDS WORK) found the original
draft would not actually boot — packing in Release silently strips shader hot-compilation, the
leak tracker, and the Vulkan validation layers all at once (all three are `#if DEBUG`-gated inside
the packaged assemblies themselves, not the consumer's build configuration). Round 2 (3.85/5,
NEEDS WORK) found the round-1 fix for shader distribution was itself still incomplete (a missing
`PackageCopyToOutput` flag would have silently left the shaders un-copied), plus a font/debug-
overlay gap introduced by the revision itself. Both rounds' findings are fixed below; see
"Corrections from review round 1" and "round 2" at the end.

## Decisions

- **D1 — new repository**: `Club Architect`, `D:\MyProjects\club-architect`, a clean local
  `git init` (no remote created in this milestone), solution `ClubArchitect.slnx`, root namespace
  `ClubArchitect`. No `ProjectReference` to Agapanthe source, ever.
- **D2 — boundary is NuGet packages**, one per project in the packaged subset (D5), mirroring the
  current `ProjectReference` graph exactly for that subset (every internal dependency becomes a
  package dependency — NuGet resolves transitivity the same way MSBuild already does internally).
- **D3 — local on-disk feed**: `dotnet pack` writes `.nupkg` files to
  `D:\MyProjects\agapanthe\artifacts\package\` (already covered by the existing `artifacts/` entry
  in `.gitignore` — no new rule needed). Club Architect points at it via its own `NuGet.config`.
  Chosen over GitHub Packages: zero auth/CI infrastructure before the game even compiles, fastest
  iteration loop for a solo discovery phase where the engine changes constantly. Migrating to
  GitHub Packages later needs no change on the consuming side beyond the feed URL.
- **D4 — automatic per-commit versioning** (Nerdbank.GitVersioning): every `dotnet pack` produces
  a version derived from the current commit, with no manual bump ritual on the Agapanthe side.
  Club Architect pins an exact version in its `.csproj` files — it only absorbs a new engine commit
  on an explicit bump. **Mechanism, corrected**: it is standard NuGet packing (`NuGet.Build.Tasks.Pack`),
  not NBGV itself, that rewrites each internal `ProjectReference` into a `PackageReference` at the
  referenced project's own version inside the generated `.nuspec` — NBGV's only job is making sure
  every project gets that same, consistent, commit-derived version number in the first place.
  **Known trap, must be a habit, not a surprise**: NBGV's version is derived from the **commit**,
  not the working tree — packing twice from the same commit with uncommitted changes in between
  produces the identical version string, and `dotnet restore` on the Club Architect side will keep
  serving the stale copy already cached under `~/.nuget/packages` (NuGet caches by exact version).
  **Rule**: commit on the Agapanthe side (even a WIP commit) before every pack that is meant to be
  consumed. If a version needs to be re-packed from a dirty tree during same-commit debugging, run
  `dotnet nuget locals global-packages --clear` on the Club Architect side first.
- **D5 — 13 packaged projects** (`IsPackable=true` set explicitly per project; default `false` in
  `Directory.Build.props`) = the closure reachable from `Agapanthe.Platform.App`: `Agapanthe.Core`,
  `World`, `Graphics`, `Rendering`, `Ui`, `Assets`, `Scene`, `Engine`, `Engine.Render`, `Audio`,
  `Platform`, `Platform.App`, `App`. **Verified against the real `ProjectReference` graph** (review
  round 1): exactly these 13, no more, no less — none references `Agapanthe.Assets.Pipeline`,
  `Agapanthe.Net`, or `Agapanthe.Ui.Noesis` (the dependency runs the other way for the last one:
  `Agapanthe.Ui.Noesis → Agapanthe.App`, never back). An explicit per-project `true` (rather than a
  blanket default with opt-outs) mirrors this repository's established allowlist culture
  (`EngineIsHeadlessTests`' static allowlists throughout) — safer against ever shipping a
  test/tool/sample project by accident.
- **D6 — explicitly excluded from v1** (stay `IsPackable=false`, addable later with zero design
  change): `Agapanthe.Assets.Pipeline` (cook-side only, never shipped), `Agapanthe.Net`
  (multiplayer, not needed yet), `Agapanthe.Ui.Noesis` (depends on the paid Noesis SDK, and only
  exists on the unmerged `spike/noesis-probe` branch — see D11), every
  `tools/*`/`tests/*`/`samples/*` project.
- **D7 — pack in Debug configuration**: `dotnet pack Agapanthe.slnx -c Debug -o
  artifacts/package`. **Changed from Release after review round 1**: `ShaderCompiler.CreateForBuild()`
  (`src/Agapanthe.Graphics/ShaderCompiler.cs:66-73`), `ResourceTracker.Enabled`
  (`src/Agapanthe.Core/ResourceTracker.cs:17-22`), and `GraphicsDevice.EnableValidation`
  (`GraphicsDevice.cs:37-42`) are each an `#if DEBUG` baked into the **packaged assembly itself** —
  they read the configuration Agapanthe was compiled with, not Club Architect's own. Packing in
  Release would have silently produced a package that (a) requires precompiled `.spv` shaders it
  doesn't ship (see D10) since it refuses to compile GLSL at runtime, (b) never prints or observes
  `ResourceTracker`'s "no leaks" line, and (c) never runs the Vulkan validation layers — the three
  gates this whole project is built around, disabled exactly when Club Architect's stated purpose
  is to find engine bugs. Packing in Debug keeps hot shader compilation, the leak tracker, and
  validation all live. **Corrected (review round 2)**: the native `shaderc_shared` (~6.6 MB) is
  NOT "stripped only in Release" as a package property — `StripShadercFromRelease` is a target
  local to `samples/Sandbox/Sandbox.csproj`, never packaged. For Club Architect it always ships,
  transitively via `Silk.NET.Shaderc.Native`, regardless of Club Architect's own build
  configuration — which is exactly what it needs, since Debug-configured `Agapanthe.Graphics`
  always compiles GLSL at runtime. Side effect also worth noting: every `Debug.Assert` inside
  Agapanthe stays active too, even in a Release/AOT publish of Club Architect (`[Conditional("DEBUG")]`
  resolves at Agapanthe's own compile time) — desirable during this discovery phase, but a real
  Release distribution story (see Deferred debt) will need to revisit it. Prerequisite worth
  stating plainly: the Vulkan SDK/validation layers must actually be installed on the machine
  running Club Architect for the "0 validation messages" gate to mean anything — without it,
  `GraphicsDevice.EnableValidation=true` just logs a warning and the gate is trivially green.
- **D8 — cook TOOLING explicitly deferred, game content only**: Club Architect v1 starts with a
  **completely empty** world — no `.agmodel`/`.agfont`/`.agscene`, no game-authored content of any
  kind. Distributing the 3 cook tools (`AssetCooker`/`FontCooker`/`ShaderPrecompiler`) to an
  external consumer (`.NET tool` packages + NuGet `.targets`) is a real standalone effort —
  deliberately treated as the NEXT gap this discovery process should surface organically, not
  pre-solved here. **This does NOT include the engine's own GLSL shaders** — see D10, those are
  engine payload, not game content, and must ship now for anything to boot at all.
  **Accepted consequence, found in review round 2**: `AppHost.cs:118-137` loads the debug-overlay
  font from `<bin>/fonts/JetBrainsMono-Regular.agfont`; with no `.agfont` shipped, that block logs
  a `Warn` and skips registering `UiRenderSystem`/`DebugOverlaySystem` — **Club Architect v1 has no
  `F3` overlay, no profiler, no live alloc counter, no GPU timings**. Unlike shaders, the default
  font is not raw source compiled at runtime — it only exists as an offline-cooked `.agfont`
  binary, so shipping it would mean either invoking `FontCooker` as part of Agapanthe's own pack
  step (un-deferring part of D8's cook-tooling scope for just this one file) or hand-committing a
  static pre-baked blob. Neither is done in v1: the debug overlay's absence is accepted, explicit,
  v1-only debt (see Deferred debt) rather than solved here — and losing it is itself a legitimate
  first discovery, likely the natural trigger for revisiting cook-tooling distribution.
- **D9 — Club Architect v1 scaffold**: a single executable project `ClubArchitect`
  (`OutputType=Exe`, `PublishAot=true`), with a `PackageReference` to `Agapanthe.Platform.App`
  **only** (pulls everything else transitively — `App`, `Engine`, `Engine.Render`, `Rendering`,
  `Graphics`, `World`, `Core`, `Platform`, `Audio`, `Scene`, `Assets`). A `ClubArchitectGame : IGame`
  with a single `ISceneRecipe` that spawns nothing (empty world, default camera). `Program.cs` is
  the **minimal subset** of `samples/Sandbox/Program.cs` — window + game + `AppHost.RunClient`
  only, dropping Sandbox-specific branches with no Club Architect equivalent (e.g. the
  `AGAPANTHE_IBL_TEST` → `IblTestTool` path, a Sandbox-only diagnostic). `ClubArchitect.csproj`
  itself (not `Directory.Build.props` — kept scoped to the one project that actually publishes
  AOT, per review round 2, rather than leaking into every future Club Architect test/tool project)
  must carry the same `<NoWarn>$(NoWarn);IL2104;IL3000;IL3002;IL3053</NoWarn>` that
  `Sandbox.csproj` sets (documented there as verified-benign third-party trim/AOT noise, arising
  from the same transitive Silk.NET dependencies) — without it, `dotnet publish
  -p:PublishAot=true` on Club Architect fails on warnings it never needed to investigate itself,
  because `TreatWarningsAsErrors` is also mirrored from Agapanthe's `Directory.Build.props`.
- **D10 — engine shaders ship as package content (new, closes the review's 🔴)**: `shaders/**`
  (the raw `.vert`/`.frag`/`.comp` GLSL sources — versioned with the engine, not authored per-game)
  are packed as `Content` items in the `Agapanthe.Graphics` package, with
  `PackagePath="contentFiles/any/any/shaders/"` (the `any/any` language/TFM segments are required
  by the `contentFiles` convention) **and `PackageCopyToOutput="true"`** — corrected in review
  round 2: `PackagePath` alone is not enough, the NuGet pack task defaults `copyToOutput` to
  `false`, which would land the GLSL as `Content` items in the consumer's project **without ever
  copying them to `bin/`** — the same "nothing at boot" failure as the original 🔴, just moved one
  step later. **Must NOT also set `CopyToOutputDirectory` on this item in
  `Agapanthe.Graphics.csproj` itself** — that would additionally copy the same files into every
  existing in-repo `ProjectReference` consumer's output (Sandbox, TopDown, the test project), which
  already links `shaders/**` from the repo root (`Sandbox.csproj:43-45`) — a second, redundant
  source for the same output path, which trips `NETSDK1152` and fails the build under this
  repository's `TreatWarningsAsErrors`. Because packages are built in Debug (D7),
  `ShaderCompiler.CreateForBuild()` compiles GLSL to SPIR-V at runtime via shaderc — the same
  hot-reload-capable path every existing Agapanthe host already uses in Debug — so **no
  precompiled `.spv` distribution is needed for v1** (that machinery, `ShaderPrecompiler` + the
  `.shadercache` staging targets, stays deferred with D8's cook tooling; it only matters for a
  Release/shipping build). `AppHost.ResolveShaderDirectory()` (`AppHost.cs:625-638`, already
  `public`) finds them at its existing fallback path (`<bin>/shaders`) with no change to that
  method needed — one caveat worth documenting rather than designing around: it walks UP from the
  output directory first, looking for an ancestor `shaders/mesh.frag`, so a `shaders/` folder ever
  created inside the Club Architect repo tree (or one of its parent directories) would silently
  shadow the packaged copy.
- **D11 — pack from `main`, not `spike/noesis-probe`**: verified (`git merge-base main
  spike/noesis-probe` = `main`'s tip `91673ff`, `git rev-list --left-right --count` = `0 4`) —
  `main` is a clean ancestor, exactly 4 commits behind the spike branch (the Noesis AOT probe, the
  `VulkanRenderDevice` vertical slice, `IUiHost`, and one docs-only commit recording it). None of
  those 4 commits are needed for an empty-world v1, and `spike/noesis-probe` is explicitly an
  unmerged experimental branch that could still be rebased or squashed — packing from it would tie
  Club Architect's very first consumed version to a commit that might stop existing. `main`
  already carries everything D5's 13 packages need (`Agapanthe.Platform.App` included, added in
  Slice-2, long before this branch existed) — **re-verified directly against `main`'s own
  `.csproj` files in review round 2**, same 13, same zero references to
  `Assets.Pipeline`/`Net`/`Ui.Noesis`. Every Agapanthe-side file change in this spec (`Directory.Build.props`,
  `version.json`, the 13 `IsPackable` edits, D10's shader-content items) must be made **on `main`
  or a branch cut from `main`** — not on `spike/noesis-probe`, which stays the Noesis-only spike it
  already is.

## Files touched (Agapanthe side, on `main` — see D11)

- `Directory.Build.props`: add `<IsPackable>false</IsPackable>` as the default, plus the
  `Nerdbank.GitVersioning` `PackageReference` (`PrivateAssets="All"`).
- `version.json` (new, repo root): minimal NBGV config (e.g. `"version": "0.1"`).
- The 13 `.csproj` files named in D5: add `<IsPackable>true</IsPackable>` plus minimal package
  metadata (`PackageId` already matches the assembly name; a short `Description`).
- `src/Agapanthe.Graphics/Agapanthe.Graphics.csproj`: add the `shaders/**` → `contentFiles` items
  from D10 (`Pack="true" PackagePath="contentFiles/any/any/shaders/" PackageCopyToOutput="true"`,
  no `CopyToOutputDirectory`).
- `.gitignore`: already correct (`artifacts/` exists) — confirm no rule excludes it by accident.

## Files created (Club Architect, new repo)

- `ClubArchitect.slnx`, `NuGet.config` (source `agapanthe-local` → Agapanthe's
  `artifacts/package` folder), a `Directory.Build.props` mirroring Agapanthe's own (`net10.0`,
  `Nullable=enable`, `TreatWarningsAsErrors=true`, etc.) — the `NoWarn` list from D9 stays local to
  `ClubArchitect.csproj`, not here.
- `src/ClubArchitect/ClubArchitect.csproj` (Exe, `PublishAot=true`, `PackageReference` to
  `Agapanthe.Platform.App`, `NoWarn` per D9).
- `src/ClubArchitect/ClubArchitectGame.cs` (`IGame`, one empty `ISceneRecipe`).
- `src/ClubArchitect/Program.cs` (minimal subset of `samples/Sandbox/Program.cs`, per D9).

## Verification

1. Agapanthe side (on `main`, D11): `dotnet pack Agapanthe.slnx -c Debug -o artifacts/package` —
   confirm exactly 13 `.nupkg` files produced, all at the same NBGV-derived version, and that the
   existing test suite (1016 tests as of the spike branch; count may differ on `main`) stays green
   (the `IsPackable`/NBGV/shader-content changes must not touch any runtime behavior).
2. Club Architect side: `dotnet restore`, then inspect `obj/project.assets.json` (confirmed
   against a real example in this repo, `tests/Agapanthe.Tests/obj/project.assets.json`, where
   entries look like `"Agapanthe.App/1.0.0": { "type": "project" ... }` under `libraries`) and
   confirm **zero** entries with `"type": "project"` — every one of the 13 Agapanthe dependencies
   must show `"type": "package"`. This is the concrete, mechanically-checkable form of "no
   `ProjectReference` to Agapanthe source" (a plain "did it resolve OK" is not evidence of that on
   its own). Drop any check on `*.nuget.g.props` — it carries no `"type"` field, checking it is
   noise (review round 2 finding).
3. `dotnet build`, then confirm `bin/<config>/net10.0/shaders/mesh.frag` exists (catches D10's
   content-copy mechanism failing BEFORE the opaque runtime failure it would otherwise cause) —
   then `dotnet run` → a window opens, ticks at the fixed step, `Escape` closes it, `ResourceTracker:
   no leaks (N resources created and destroyed).` at shutdown printed to the console (match by
   prefix, the resource count varies) — observable specifically because packages are
   Debug-configured (D7); this line would be silently absent from a Release package. No `F3`
   overlay is expected to appear (D8's accepted font-degradation consequence) — that is not a
   failure of this verification step.
4. `dotnet publish -r win-x64 --self-contained -p:PublishAot=true` on the Club Architect side —
   confirms that consuming Agapanthe by package (rather than by source) does not break the
   NativeAOT path already established for every engine project. Requires the `NoWarn` list from D9
   to be present, or this step fails on inherited trim/AOT analyzer warnings.

## Explicitly deferred debt

Cook tooling not distributed (real next gap the moment a custom asset is wanted, D8) · **no debug
overlay/profiler in v1** (D8's font consequence — `F3`, alloc counter, GPU timings all unavailable
until the default font ships or gets cooked another way) ·
`Agapanthe.Net`/`Agapanthe.Ui.Noesis` not packaged (addable on demand, identical mechanism) · no
git remote for Club Architect (local repo only) · the football-manager game's own design (rules,
simulation, screens) remains entirely unstarted, out of scope for this milestone · **no Release
distribution story** — packing in Debug (D7) is a deliberate, discovery-phase-only choice; a real
shipping build will eventually need precompiled `.spv` distribution and a decision on whether
`ResourceTracker`/validation stay queryable at runtime in Release · `EngineIsHeadlessTests`'
"Engine/World carry no PackageReference" static check (`EngineIsHeadlessTests.cs:123-127`) becomes
technically imprecise once NBGV's `PackageReference` is injected via `Directory.Build.props` (it
parses each project's own XML directly, so it won't see a props-injected reference and stays
green, but the invariant it's meant to express is no longer 100% literal — documented here rather
than silently drifting) · `ThisAssembly` (a class NBGV generates per assembly) could in principle
collide with a same-named type reached via an existing `InternalsVisibleTo`, though nothing in the
repository references `ThisAssembly` today, so this is inert unless that changes.

## Corrections from review round 1 (independent `engine-architect` review, weighted 2.65/5)

- **🔴 fixed**: packing in Release silently broke shader loading (no runtime compilation, no
  precompiled `.spv` shipped) — D7 now packs Debug, D10 ships raw shader sources as package
  content, closing the gap with less mechanism than first proposed (no precompiler distribution
  needed for v1).
- **🟠 fixed**: `ResourceTracker`/Vulkan validation being silently disabled in Release packages —
  resolved as a side effect of the D7 config change (both are Debug-only checks).
- **🟠 fixed**: D2 contradicted D5 (16 vs 13 projects) — D2 reworded to reference D5's subset
  directly.
- **Fixed**: wrong attribution — standard NuGet packing rewrites `ProjectReference` into
  `PackageReference`, not NBGV; NBGV only supplies the consistent version. D4 corrected.
- **Fixed**: D4's "every pack produces a unique version" was false (version is commit-derived, not
  tree-derived) — added the explicit commit-before-pack rule and the cache-clear fallback.
- **Fixed**: "Program.cs mirrors Sandbox's exactly" was untrue (Sandbox has an
  `AGAPANTHE_IBL_TEST` branch with no Club Architect equivalent) — D9 now says "minimal subset."
- **Fixed**: missing `NoWarn` list would have failed the AOT publish verification step — added
  explicitly to D9 and the Club Architect `Directory.Build.props` (**superseded in round 2**: moved
  to `ClubArchitect.csproj` only, see below).
- **Fixed**: verification step 2 ("confirm no resolution touches src as ProjectReference") was not
  mechanically checkable as originally worded — replaced with a concrete `project.assets.json`
  inspection.
- **Recorded as accepted, not fixed**: the `EngineIsHeadlessTests` gate-precision and `ThisAssembly`
  🟡 findings — both real but inert today, moved to Deferred debt rather than designed around.
- **Added**: D11 (pack from `main`, not the unmerged `spike/noesis-probe`) — not a finding from the
  review, but a related risk I verified independently while fixing D6's rationale for excluding
  `Agapanthe.Ui.Noesis`.

## Corrections from review round 2 (independent `engine-architect` re-review, weighted 3.85/5)

- **🔴 fixed**: D10's `contentFiles` mechanism was itself incomplete — `PackagePath` alone leaves
  `copyToOutput` defaulted to `false`, so the shader files would still never reach `bin/`. Added
  `PackageCopyToOutput="true"` and the exact `contentFiles/any/any/shaders/` path, and explicitly
  ruled out also setting `CopyToOutputDirectory` on the source item (would double-copy into every
  existing in-repo `ProjectReference` consumer and trip `NETSDK1152`).
- **🟠 fixed**: the debug-overlay font gap — introduced by the revision itself, not carried over
  from round 1. `AppHost.cs`'s font-load block degrades gracefully (no crash), but D8's "zero
  content" now has a concrete, named consequence (no `F3` overlay/profiler in v1) — recorded
  explicitly in D8 and in Deferred debt rather than left implicit.
- **Fixed**: "shaderc_shared stripped only in Release" was imprecise — the strip is a
  `Sandbox.csproj`-local target, not a package property; reworded in D7.
- **Fixed**: wrong file path for `ResourceTracker.Enabled` (`Agapanthe.Core`, not `Graphics`).
- **Fixed**: verification step 2 checked a file (`*.nuget.g.props`) with no `"type"` field to
  check — dropped, kept only the `project.assets.json` inspection, now anchored to a real example
  from this repo's own `obj/` output.
- **Fixed**: verification step 3 didn't say what "no leaks" actually prints, nor that no `F3`
  overlay is an expected outcome, not a failure — both made explicit, plus a new file-existence
  check (`shaders/mesh.frag` in `bin/`) that catches D10's mechanism failing before the harder-to-
  diagnose runtime symptom.
- **Fixed**: `NoWarn` was specified in two places (D9 and the Club Architect
  `Directory.Build.props`) — consolidated to `ClubArchitect.csproj` only, per the reviewer's own
  recommendation (keeps future Club Architect test/tool projects from silently inheriting it).
- **Fixed**: D11 under-counted the 4 commits (said 3) and didn't state which branch the Agapanthe-
  side file changes themselves must land on — both corrected.
- **Recorded as accepted, not fixed**: the Vulkan-SDK-must-be-installed prerequisite for the
  validation-layer gate to mean anything, and every `Debug.Assert` staying live even in a Release
  Club Architect publish — both true and both fine for a discovery-phase tool, called out in D7
  rather than designed around.
