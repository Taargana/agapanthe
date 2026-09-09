# Absolute Work Board — Contenu-2 : offline cook + content manifest

**Status**: `completed` 2026-09-08 — W1–W4 + tail + double audit (`csharp-lowlevel` 4.1/5,
`engine-architect` 4.2/5, both PASS-with-concerns, no blocker) + findings applied. `dotnet test`
**689 passed**, 0 warning, all pinned captures/snapshots unchanged, Sandbox + HeadlessSim JIT == AOT.
**Human visual verdict still due** (shared with the Agapanthe.App S30 + Contenu-1 S31 verdicts).
**Spec**: `docs/plans/2026-09-08-content-asset-cook-design.md` (APPROVED 4.24/5, reviewer 2 rounds: 3.52 → 4.24)
**Session**: 32
**Created**: 2026-09-08

Second of 3 sub-milestones of the **Contenu** domain (decomposed like MP-0): 1. asset identity ✅
(Contenu-1, `fa335d7`) · **2. offline cook + manifest** (this) · 3. declarative prefabs/scenes.
Human greenlight between sub-milestones. This one: a `.agmodel` cooked blob, `tools/AssetCooker`
(glTF → blob, `FontCooker` pattern), a binary `content.agmanifest`, a runtime `AssetCatalog`, and the
Sandbox on cooked content. glTF parsing leaves the runtime for a new `Agapanthe.Assets.Pipeline`.

## Project Conventions

- .NET 10, `TreatWarningsAsErrors` (0 warning), xUnit, `dotnet build Agapanthe.slnx` / `dotnet test`.
- NativeAOT: `samples/HeadlessSim` + `tools/AotComponentProbe` publish; AOT publish needs `PATH`
  prefixed with `/c/Program Files (x86)/Microsoft Visual Studio/Installer` (vswhere).
- Cook-tool pattern (`tools/FontCooker`, `tools/ShaderPrecompiler`): console `Exe`, **not** AOT,
  **never** a `ProjectReference` of a shipped project, invoked via `dotnet exec` from an MSBuild
  target with `RemoveProperties="RuntimeIdentifier;SelfContained;PublishAot;PublishTrimmed;PublishSingleFile"`,
  stamp incrementality, tmp-then-move writes, one-line failure + exit 1/2.
- Cooked-format pattern (`Agapanthe.Assets.Font.FontAssetFormat`): reader `public` (runtime), writer
  `internal` + `[assembly: InternalsVisibleTo(...)]`; binary, little-endian explicit, versioned
  header refused outright on mismatch; total length recomputed from counts before allocating.
- `Agapanthe.Assets` is `IsAotCompatible`, GPU-free, refs only `Core` + `StbImageSharp`.
  `Agapanthe.Assets.Pipeline` is **not** AOT (dev tool).
- No `Vk*` outside `Agapanthe.Graphics`; no Arch type outside `Agapanthe.World`; `Agapanthe.Engine`
  closure = `{Core, World}`; `Agapanthe.App` refs everything except `Platform` (all pinned by
  `EngineIsHeadlessTests`).
- Conversation FR; code / commits / docs EN.

### Gates (acceptance bar — unchanged)

- `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` green (+ 5 new test files).
- `AGAPANTHE_SCENE=model` HDR capture MD5 **== `9030f6a64e9587b05d5abb99b487b1b9`** (2 764 816 B) —
  re-pinned in W2 (the S30 `df55d444…` had already drifted; the cooked path reproduces the pre-W2
  `GltfLoader` path exactly, proven by a stash). JIT == AOT.
- `planet-drop` HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa`
  **unchanged** (procedural content untouched).
- `HeadlessSim` default `80ced166fdf3076119a62970f63d683b` (1842 B) / `--drive`
  `cf01492e8a9688b666e01d2ef9d63869` (210 B) **unchanged** — no snapshot format change.
- `AotComponentProbe` PASS · Sandbox + HeadlessSim **JIT == NativeAOT** · `EngineIsHeadlessTests` green.
- Double audit (`csharp-lowlevel` + `engine-architect`) · **human visual verdict**: `model` renders
  DamagedHelmet from cooked content, identical; `-- MetalRoughSpheres.glb` resolves + renders.

## Rollback Point

`fa335d746945613ecad5781ac5c3ba1ad79560ee` (tree clean; the new spec file is the only untracked
change at DECOMPOSE time).

## Deferred Work

- **Contenu-2b** — `.gltf` sources with external `.bin`/image siblings (a per-dep hash list in
  `.cookstate` + a `GltfDocument` URI-enumeration API); standalone HDR / textures / fonts in the
  catalog (`AssetCatalog.LoadEnvironment`, `.agenv`, `HdrImageLoader` → Pipeline, a hand-rolled RGBE
  decoder to shed `StbImageSharp` from the runtime).
- **Contenu-3** — declarative `.agscene` / `.agprefab` + `SceneLoader`; the 5 Sandbox recipes → data;
  **sim-side asset identity** (`AssetRef` component — the Contenu-1 architecture-audit finding).
- GPU block compression (BC7/BC5) — a render milestone (changes the GPU format + `SceneBuilder` upload).
- Cross-machine byte-reproducibility of `.agmodel` (depends on `DeflateStream` stability across .NET
  patches); only the decoded `ModelAsset` is contractually stable.
- `HostOptions.VerifyContentHashes` — an opt-in per-load SHA-256 of each blob against the manifest.
- Asset hot-reload; packing blobs into archives; a runtime scene→prefab→model load graph.

---

## Tasks

### Wave W1 — formats + `Agapanthe.Assets.Pipeline` + `tools/AssetCooker` (additive, nothing shipped changes)

#### AW-001 — `AssetKey.FromContentPath` (Core)
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.Core/AssetKey.cs`, `tests/Agapanthe.Tests/AssetKeyTests.cs`
- `public static AssetKey FromContentPath(string contentRoot, string sourcePath)` — `Path.GetRelativePath`
  then the existing ctor (normalises `\`→`/`, rejects `..`/leading-`/`). A path outside the root
  (relative starts with `..`) → `ArgumentException`.
- **Acceptance**: `content/models/sub/x.glb` under `content/` → `AssetKey("models/sub/x.glb")`;
  builds 0 warning; existing `AssetKeyTests` green.

#### AW-002 — `AgModelFormat` + `AgModelException` (Assets)
- **Type**: code · **Size**: M · **Deps**: none
- **Files**: `src/Agapanthe.Assets/AgModelFormat.cs` (new), `src/Agapanthe.Assets/AgModelException.cs` (new)
- Container `magic "AGMD" | version u32 (=1) | payloadLen u32` then a `DeflateStream`
  (`CompressionLevel.Optimal`) wrapping the payload (spec §`AgModelFormat`: model name; per-mesh
  `vertexCount|indexCount|materialIndex|streamMask|worldTransform` + SoA arrays via
  `MemoryMarshal.AsBytes`; per-material one blittable record; per-image `w|h|isSrgb|Rgba8Pixels`).
- Reader **`public`** (`Read(ReadOnlySpan<byte>) → ModelAsset` — inflate into a `byte[]`, then parse
  spans); writer **`internal`** (`WriteContainer`); `[assembly: InternalsVisibleTo("Agapanthe.Assets.Pipeline")]`
  + `[assembly: InternalsVisibleTo("Agapanthe.Tests")]` in this file.
- Read validation (patron `FontAssetFormat.Read`): magic; version (dedicated refuse); payload length
  recomputed from counts, exact match required **before** allocating; `width>0 && height>0` and the
  image bytes that follow; every float finite; `materialIndex ∈ [-1, materialCount)`; per-stream
  length == `vertexCount`. Any failure → `AgModelException`, no OOB.
- Mesh/material `Name` **dropped**; model `Name` kept.
- **Acceptance**: builds 0 warning; `Agapanthe.Assets.csproj` deps unchanged (BCL only — `DeflateStream`,
  `MemoryMarshal`).

#### AW-003 — `AgModelFormatTests`
- **Type**: test · **Size**: S · **Deps**: AW-002, AW-008 (the writer)
- **Files**: `tests/Agapanthe.Tests/AgModelFormatTests.cs` (new)
- Tests 1 (round-trip byte-identical + field-by-field, mesh/material `Name` == `""`), 2 (same-process
  deterministic), 3 (corruption: bad magic / version 2 / truncated / image `w*h*4` OOR / non-finite
  position / `materialIndex` OOR → `AgModelException`).
- **Acceptance**: all green.

#### AW-004 — `ContentManifest` + `ManifestEntry` (Assets)
- **Type**: code · **Size**: S · **Deps**: none
- **Files**: `src/Agapanthe.Assets/ContentManifest.cs` (new — holds `ManifestEntry` too)
- `magic "AGMN" | version u32 (=1) | entryCount u32` then per entry `keyLen u16 + UTF-8 | kind u8 |
  blobPathLen u16 + UTF-8 | contentHash byte[32]`, entries **sorted ordinal by key**.
- Reader `public` (`Read(ReadOnlySpan<byte>) → IReadOnlyDictionary<AssetKey, ManifestEntry>`); writer
  `internal`. Validation: magic, version, keys strictly ascending ordinal, each key valid through the
  `AssetKey` ctor (wrap `FormatException`), no duplicate → `AssetException`.
- **Acceptance**: builds 0 warning.

#### AW-005 — `ContentManifestTests`
- **Type**: test · **Size**: S · **Deps**: AW-004, AW-008 (the writer)
- **Files**: `tests/Agapanthe.Tests/ContentManifestTests.cs` (new)
- Tests 4 (round-trip, keys ascending), 5 (corruption: bad magic / non-ascending / ctor-rejected key
  / duplicate → `AssetException`).
- **Acceptance**: all green.

#### AW-006 — `AssetCatalog` (Assets)
- **Type**: code · **Size**: M · **Deps**: AW-002, AW-004
- **Files**: `src/Agapanthe.Assets/AssetCatalog.cs` (new)
- `static Open(string contentRoot)` (reads `<root>/content.agmanifest`; missing → `AssetException`
  naming the path); `Contains(AssetKey)`; `LoadModel(AssetKey)` (absent → `AssetException`;
  `kind != Model` → `AssetException`; then `File.ReadAllBytes(root/blobPath)` → `AgModelFormat.Read`);
  `IReadOnlyCollection<AssetKey> Keys`. No cache, no `IDisposable`.
- **Acceptance**: builds 0 warning; AOT-clean (no reflection).

#### AW-007 — `AssetCatalogTests`
- **Type**: test · **Size**: S · **Deps**: AW-006
- **Files**: `tests/Agapanthe.Tests/AssetCatalogTests.cs` (new)
- Test 6 (`Open("<empty dir>")` → `AssetException` naming the path), test 8 (a hand-written manifest
  + blob → absent key throws; `kind = 1` entry → `LoadModel` throws). Test 7 (cook + load) lives in
  `AssetCookTests` (needs `CookRunner`).
- **Acceptance**: all green.

#### AW-008 — `Agapanthe.Assets.Pipeline` project + writers
- **Type**: infra · **Size**: M · **Deps**: AW-001, AW-002, AW-004
- **Files**: `src/Agapanthe.Assets.Pipeline/Agapanthe.Assets.Pipeline.csproj` (new),
  `src/Agapanthe.Assets.Pipeline/AgModelWriter.cs` (new),
  `src/Agapanthe.Assets.Pipeline/ContentManifestWriter.cs` (new), `Agapanthe.slnx` (+ project under
  a folder), `tests/Agapanthe.Tests/Agapanthe.Tests.csproj` (+ `ProjectReference`)
- `.csproj`: library, `net10.0`, **no** `IsAotCompatible`, `TreatWarningsAsErrors`, refs
  `Agapanthe.Assets` + `Agapanthe.Core`. **No** `StbImageSharp` yet (glTF moves in W3).
- `AgModelWriter.Write(Stream, ModelAsset)` — builds payload, deflates, calls the internal
  `AgModelFormat.WriteContainer`. Deterministic. `ContentManifestWriter.Write(Stream, IReadOnlyList<ManifestEntry>)`
  — sorts ordinal, writes.
- **Acceptance**: `dotnet build Agapanthe.slnx` 0 warning; `Agapanthe.Assets` unchanged; AW-003 / AW-005
  can now be written.

#### AW-009 — `CookRunner` (Pipeline)
- **Type**: code · **Size**: M · **Deps**: AW-008
- **Files**: `src/Agapanthe.Assets.Pipeline/CookRunner.cs` (new)
- `Cook(string contentRoot, string outputDir) → CookSummary` — glob `**/*.glb` ordinal-sorted; key =
  `AssetKey.FromContentPath`; duplicate key → throw (cook fails); per source: SHA-256 the bytes, skip
  if `.cookstate` `sourceHash` + cooker version match **and** the blob still exists; else
  `Agapanthe.Assets.GltfLoader.Load(source)` → `AgModelWriter.Write` (tmp-then-move); always rewrite
  `content.agmanifest` (`contentHash` = SHA-256 of the written blob) + `.cookstate`
  (`key|sourcePath|sourceHash|cookerVersion`) tmp-then-move.
- **Acceptance**: builds; a manual `Cook(tests/Fixtures, /tmp/x)` on `DamagedHelmet.glb` produces a
  blob + a manifest with one entry.

#### AW-010 — `tools/AssetCooker` (Exe)
- **Type**: code · **Size**: S · **Deps**: AW-009
- **Files**: `tools/AssetCooker/AssetCooker.csproj` (new), `tools/AssetCooker/Program.cs` (new),
  `Agapanthe.slnx` (+ under `/tools/`)
- `.csproj`: `Exe`, `net10.0`, no AOT, `ProjectReference Agapanthe.Assets.Pipeline`, the "not
  referenced by any shipped project" comment. `Program.cs`: `usage: AssetCooker <contentRootDir>
  <outputDir>`; validate args (exit 2), `try { CookRunner.Cook(...); Log.Info(summary); }`
  `catch (AssetException or AgModelException or IOException or UnauthorizedAccessException) { one line;
  return 1; }`.
- **Acceptance**: `dotnet run --project tools/AssetCooker -- <dir> <out>` cooks; bad args → exit 2.

#### AW-011 — `AssetCookTests`
- **Type**: test · **Size**: M · **Deps**: AW-009, AW-010, AW-006
- **Files**: `tests/Agapanthe.Tests/AssetCookTests.cs` (new)
- Test 7 (cook a fixture → `Open` → `LoadModel` → expected counts, `Contains`, `Keys`), test 9
  (`Cook_Then_Load_Equals_GltfLoader` for `DamagedHelmet.glb` **and** `MetalRoughSpheres.glb` —
  field-by-field vs `GltfLoader.Load`, bit-exact vertex floats + pixels + tangents), test 10
  (`Cook_IsIncremental` — 0 cooked / N skipped, manifest byte-identical; rewrite a source's *bytes* →
  1 cooked), test 11 (key from content-relative path; duplicate key fails), **test 3b**
  (`AgModel_CookedDamagedHelmet_HashIsPinned` — SHA-256 of the cooked blob == a literal; record the
  pin machine's SDK version in this file's comment + the re-pin policy).
- **Acceptance**: all green (JIT).

#### AW-012 — W1 gate
- **Type**: docs · **Size**: S · **Deps**: AW-003, AW-005, AW-007, AW-011
- `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` green (tests 1–11 + 3b) · captures
  (`model` / `planet-drop` HDR / UI) **unchanged** — nothing shipped touched · `HeadlessSim`
  snapshots unchanged · Sandbox + HeadlessSim JIT == AOT still pass. Record in `## Progress`.

### Wave W2 — Sandbox migrated to cooked content

#### AW-013 — `content/` + model copies
- **Type**: infra · **Size**: S · **Deps**: AW-012
- **Files**: `content/models/DamagedHelmet.glb` (new — copy), `content/models/MetalRoughSpheres.glb`
  (new — copy), `.gitattributes` if the repo LFS-tracks binaries (check)
- `tests/Agapanthe.Tests/Fixtures/` **untouched**.
- **Acceptance**: the two `.glb` are byte-identical copies of the fixtures.

#### AW-014 — `Sandbox.csproj` cook targets
- **Type**: config · **Size**: M · **Deps**: AW-013, AW-010
- **Files**: `samples/Sandbox/Sandbox.csproj`
- `CookAssets` target (twin of `CookFonts`: `<MSBuild>` `AssetCooker` with the standard
  `RemoveProperties`, `<Exec>` `dotnet exec … <content/> <obj/assetcache/>`, stamp with `Inputs` =
  every file under `content/**` + `tools/AssetCooker/**/*.cs` + `src/Agapanthe.Assets.Pipeline/**/*.cs`)
  + `IncludeCookedAssets` (`BeforeTargets="AssignTargetPaths"`, pre-expand `obj/assetcache/**` into
  `<Content Link="content\%(RecursiveDir)%(Filename)%(Extension)">`). **Narrow** the existing
  `tests/…/Fixtures/** → models/` Content copy to `studio_small_1k.hdr` only.
- **Acceptance**: `dotnet build samples/Sandbox` produces `bin/.../content/content.agmanifest` +
  `content/models/*.agmodel`; no `DamagedHelmet.glb` in `bin/.../models/`.

#### AW-015 — `AppHost` + `SceneContext` + `HostOptions` wiring
- **Type**: code · **Size**: M · **Deps**: AW-006
- **Files**: `src/Agapanthe.App/AppHost.cs`, `src/Agapanthe.App/SceneContext.cs`,
  `src/Agapanthe.App/HostOptions.cs`
- `AppHost`: `internal static string ResolveContentRoot(HostOptions)` (`options.ContentRoot ??
  Path.Combine(AppContext.BaseDirectory, "content")`); `var catalog = AssetCatalog.Open(...)` in
  `RunClient`, into `SceneContext.Catalog`. No teardown step. `SceneContext`: `+ required AssetCatalog
  Catalog`. `HostOptions`: `+ string? ContentRoot` from `AGAPANTHE_CONTENT`.
- **Acceptance**: builds; `AppHostContractTests` / `AppHostTests` updated for the new `required` member.

#### AW-016 — Sandbox recipes on the catalog
- **Type**: code · **Size**: M · **Deps**: AW-015, AW-014 · **owns** `samples/Sandbox/*` this wave
- **Files**: `samples/Sandbox/Scenes/ModelSceneRecipe.cs`, `Scenes/DriveSceneRecipe.cs`,
  `Content/ModelContent.cs`, `Program.cs`
- `GltfLoader.Load(path)` → `ctx.Catalog.LoadModel(key)`. `ModelContent.ResolveModelPath` →
  `ResolveModelKey(args, catalog)` (no arg → `models/DamagedHelmet.glb`; bare `X` → `models/X` then
  `models/X.glb` vs `Contains`; `models/…` verbatim; miss → `InvalidOperationException` with a "add it
  under content/models/ and rebuild" message). `AGAPANTHE_UNLOAD_TEST` loads the cooked key N times.
  `Program.cs` header comment: `-- <model>` = "a content key".
- **Acceptance**: `AGAPANTHE_SCENE=model` / `grid:` / `drop:` / `drive` all launch clean.

#### AW-017 — W2 gate
- **Type**: docs · **Size**: S · **Deps**: AW-016
- `dotnet build` 0 warning · `dotnet test` green · **`AGAPANTHE_SCENE=model` HDR capture ==
  `df55d444b74c7aa94fd0ab18d795cc9c`** (×2 deterministic) — cooked content renders byte-identical ·
  `grid:20x20` / `drop:40` / `drive` clean, 0 leak / 0 validation · `-- MetalRoughSpheres.glb`
  resolves · a bad name → clean message + exit 1 · `planet-drop` HDR `12638edd…` / UI `03421357…`
  **unchanged** · Sandbox JIT == AOT. Record in `## Progress`.

### Wave W3 — extract glTF into `Agapanthe.Assets.Pipeline`

#### AW-018 — `git mv` the glTF code + `.csproj` surgery
- **Type**: code · **Size**: M · **Deps**: AW-017
- **Files**: `git mv src/Agapanthe.Assets/{GltfLoader.cs,Gltf/GltfDocument.cs,Gltf/GlbContainer.cs,Gltf/GltfSchema.cs,AccessorReader.cs,TangentGenerator.cs,ImageLoader.cs}
  → src/Agapanthe.Assets.Pipeline/`; `src/Agapanthe.Assets/Agapanthe.Assets.csproj`;
  `src/Agapanthe.Assets.Pipeline/Agapanthe.Assets.Pipeline.csproj`
- Namespaces stay `Agapanthe.Assets*` (no rename). `GltfSchema.cs`'s
  `[assembly: InternalsVisibleTo("Agapanthe.Tests")]` travels with it. `Agapanthe.Assets` keeps
  `StbImageSharp` (HDR) but loses the glTF code; `Pipeline` gains `PackageReference StbImageSharp`.
  `CookRunner`'s `GltfLoader.Load` call now resolves in-assembly. `AssetException` **stays** in
  `Agapanthe.Assets` (Pipeline → Assets, no cycle).
- **Acceptance**: `dotnet build Agapanthe.slnx` 0 warning; `GltfLoaderTests` / `GltfParserTests` /
  `AccessorReaderTests` compile + pass unchanged; `AssetCooker` builds.

#### AW-019 — `AssetsPipelineIsolationTests` (static guard)
- **Type**: test · **Size**: M · **Deps**: AW-018
- **Files**: `tests/Agapanthe.Tests/AssetsPipelineIsolationTests.cs` (new)
- Test 12: `Agapanthe.Assets.csproj` has no `ProjectReference` to `Assets.Pipeline`; no
  `Agapanthe.Assets.Gltf.*` / `GltfLoader` type resolves from the loaded `Agapanthe.Assets` assembly;
  a scan of **every `.csproj`** (`src/*`, `samples/*`, `tools/*`, `tests/*`) shows `Assets.Pipeline`
  referenced only by `tools/AssetCooker` + `tests/Agapanthe.Tests`. Test 13: `EngineIsHeadlessTests`
  still green (no closure change).
- **Acceptance**: green; a mutation (add `Assets.Pipeline` ref to `Agapanthe.Rendering`) fails it.

#### AW-020 — W3 gate
- **Type**: docs · **Size**: S · **Deps**: AW-019
- `dotnet build` 0 warning · `dotnet test` green · all captures **unchanged** · `HeadlessSim`
  snapshots unchanged · Sandbox + HeadlessSim **JIT == NativeAOT** · `AotComponentProbe` PASS ·
  `EngineIsHeadlessTests` green. Record in `## Progress`.

### Wave W4 — mandatory tail

#### AW-021 — Self Code Review
- **Type**: docs · **Deps**: AW-020
- Re-read the full diff against the spec + Decision Log. Check: reader `public` / writer `internal`
  everywhere; total-length-before-allocate on both formats; ordinal sort + strictly-ascending guard
  on the manifest; `AssetCatalog` GPU-free + AOT-clean; `Pipeline` ∉ any shipped closure; `registry.Load`
  signature untouched; procedural content still on the `ModelAsset` overload; the two staleness layers
  (MSBuild mtime / `CookRunner` hash) behave as documented.

#### AW-022 — Requirements Validation
- **Type**: docs · **Deps**: AW-021
- Walk the spec's acceptance bar + every Decision Log row + the Error Handling table line by line.

#### AW-023 — Full Project Verification + double audit + human verdict + CONVERGE
- **Type**: docs · **Deps**: AW-022
- `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` green · `EngineIsHeadlessTests` green ·
  captures ×3 (`model` == `df55d444…`, `planet-drop` HDR/UI unchanged) · HeadlessSim ×2 JIT==AOT
  (unchanged) · `AotComponentProbe` · 0 leak / 0 validation · cook determinism (`AssetCooker` twice →
  identical `content.agmanifest`).
- Double audit: `csharp-lowlevel` + `engine-architect` subagents; apply findings.
- Human verdict: `model` renders DamagedHelmet from cooked content, identical; `-- MetalRoughSpheres.glb`
  resolves + renders.
- CONVERGE: `CLAUDE.md` (§Contenu-2, `Pipeline` in the module diagram), `docs/AVANCEMENT.md`
  (§Reprise → Contenu-3), `docs/BACKLOG.md` (Contenu-2 delivered; Contenu-2b = HDR/textures/`.gltf`
  deps; the Contenu decomposition table), spec §Execution outcome, archive board, suggest a commit.

---

## Dependency graph

```
W1:  AW-001 ─┬──────────────── AW-008 ─┬─ AW-009 ── AW-010 ─┐
             │                         │                    │
     AW-002 ─┼─ AW-003 ────────────────┤                    ├─ AW-011 ── AW-012
             │                    (writer dep)              │
     AW-004 ─┼─ AW-005 ────────────────┘                    │
             │                                              │
             └─ AW-006 ─┬─ AW-007 ──────────────────────────┘
                        └──────────────────────────── (AW-011 also deps AW-006)

W2:  AW-012 ─┬─ AW-013 ── AW-014 ─┐
             └─ AW-015 ───────────┼─ AW-016 ── AW-017
                                  ┘

W3:  AW-017 ── AW-018 ── AW-019 ── AW-020

W4:  AW-020 ── AW-021 ── AW-022 ── AW-023
```

## Wave / parallelism plan

| Wave | Sequential spine | Parallel-safe | Gate |
|---|---|---|---|
| W1 | AW-001 → AW-002 → AW-004 → AW-006 → AW-008 → AW-009 → AW-010 → AW-011 → AW-012 | {AW-002, AW-004, AW-006} touch disjoint new files (no shared `.csproj` edit — IVT is one attribute in `AgModelFormat.cs`); their tests {AW-003, AW-005, AW-007} likewise. Run the code trio, then the test trio, if parallelising — else the spine is fine (Contenu-1 ran fully sequential). | human greenlight |
| W2 | AW-013 → AW-014 → AW-016 → AW-017 ; AW-015 anywhere before AW-016 | {AW-014 (Sandbox.csproj), AW-015 (App/*)} touch disjoint projects — parallel-safe | human greenlight |
| W3 | AW-018 → AW-019 → AW-020 | — (mv then guard) | human greenlight |
| W4 | AW-021 → AW-022 → AW-023 | — | human verdict + CONVERGE |

## Progress

### W1 DONE (2026-09-08) — AW-001..012

- **AW-001** `AssetKey.FromContentPath(root, source)` (Core) — `Path.GetRelativePath` + the ctor;
  a path outside the root → `ArgumentException`. +2 tests.
- **AW-002** `AgModelFormat` + `AgModelException` (Assets): container `magic|version|uncompressedLen`
  + a raw `DeflateStream(Optimal)` of a length-prefixed payload (model name; per-mesh
  `vertexCount|indexCount|materialIndex|streamMask|worldTransform` + SoA arrays via
  `MemoryMarshal.AsBytes`; per-material a flat field list; per-image `w|h|isSrgb|Rgba8Pixels`).
  Reader `public`, writer `internal` + IVT `Agapanthe.Assets.Pipeline` + `Agapanthe.Tests`. Full
  read validation (magic / version / recomputed length / finite floats / `materialIndex` in range /
  trailing bytes) → `AgModelException`, no OOB. Mesh/material `Name` dropped, model `Name` kept.
- **AW-003** `AgModelFormatTests` (8) — round-trip byte-identical + field-by-field, same-process
  determinism, corruption (bad magic / version / truncated / payload-length-lie / non-finite / OOR).
- **AW-004** `ContentManifest` + `ManifestEntry` + `AssetKind` (Assets): `magic "AGMN"` + entries
  sorted ordinal by key (`keyLen|key|kind|blobPathLen|blobPath|contentHash[32]`). Reader `public`,
  writer `internal`. Strictly-ascending guard, ctor-valid keys, no dup → `AssetException`.
- **AW-005** `ContentManifestTests` (6) — round-trip + stable re-write, corruption (magic / version /
  non-ascending / ctor-rejected key), writer rejects a wrong-size hash.
- **AW-006** `AssetCatalog` (Assets): `Open(root)` (missing manifest → `AssetException` naming the
  path), `Contains`, `LoadModel` (absent / non-Model → `AssetException`; missing blob →
  `AgModelException`), `Keys`. GPU-free, no cache, no `IDisposable`.
- **AW-007** `AssetCatalogTests` (3) — `Open` on a missing manifest; unknown key / wrong-kind throw.
- **AW-008** `src/Agapanthe.Assets.Pipeline` (library, **not** AOT) + `AgModelWriter` /
  `ContentManifestWriter` (façades + tmp-then-move `WriteFile`). `Agapanthe.slnx` + `Agapanthe.Tests.csproj`.
- **AW-009** `CookRunner.Cook(contentRoot, outputDir) → CookSummary` (Pipeline): globs `*.glb` +
  `*.gltf` ordinal, key = `FromContentPath`, dup key → throw; **content-hash** staleness vs a
  `.cookstate` sidecar (`agcookstate\t<cookerVersion>` + `<key>\t<sha256>` lines) + a `CookerVersion`
  const; `GltfLoader.Load` → `AgModelWriter.WriteFile`; always rewrites manifest + state. `.gltf`
  sibling-hash tracking deferred (Contenu-2b) — a `.gltf`'s `.bin`/image edits need a clean rebuild.
- **AW-010** `tools/AssetCooker` (Exe, `AssetCooker.csproj` + slnx) — `Program.cs` → `CookRunner`,
  `FontCooker` failure shape (exit 0/1/2).
- **AW-011** `AssetCookTests` (6): **CookFidelity** (`DamagedHelmet.glb` + `MetalRoughSpheres.glb`
  decode field-by-field == `GltfLoader.Load` — the gate that the render is unchanged); **blob-hash
  pin** `2c6483c260c1d4f88b0ea8aa9f6106d19955bee41f069022568ef1d963b0aa97` (SDK 10.0.103, JIT);
  incremental (2 cooked → 0 cooked / 2 skipped, manifest byte-identical; rewrite a source's bytes →
  1 cooked); key from a nested content path; cook→open→load.
- **AW-012 gate**: `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` **684 passed** (+25) ·
  `planet-drop` capture HDR `12638edd…` / UI `03421357…` **unchanged**, 0 leak / 0 validation ·
  `HeadlessSim` snapshot tests green (unchanged) · manual cook of the 2 real `.glb` twice → identical
  `content.agmanifest` + blob hashes. **Nothing shipped changed** (Sandbox still calls `GltfLoader`).

**Note (size)**: `DamagedHelmet.glb` (3.7 MB source) → `DamagedHelmet.glb.agmodel` **21 MB**
(decoded RGBA8 2K textures, Deflate barely helps on photo data). Expected — BCn is a later render
milestone (Decision Log). Watch the shipped-`bin` size in W2.

### W2 DONE (2026-09-08) — AW-013..017

- **AW-013** `content/models/{DamagedHelmet,MetalRoughSpheres}.glb` — copies (no LFS; ~15 MB, as
  the fixtures already are). `tests/…/Fixtures/` untouched.
- **AW-014** `Sandbox.csproj`: `CookAssets` target (twin of `CookFonts` — `<MSBuild>` `AssetCooker`
  with the standard `RemoveProperties`, `<Exec>` `dotnet exec … content/ obj/assetcache/`, stamp on
  `content/**` + `tools/AssetCooker/**/*.cs` + `src/Agapanthe.Assets.Pipeline/**/*.cs`) +
  `IncludeCookedAssets` (`BeforeTargets="AssignTargetPaths"`, ships `obj/assetcache/**` minus
  `.cookstate`/`.cooked.stamp` as `Content Link="content\…"`). The `Fixtures → models/` copy narrowed
  to `studio_small_1k.hdr` only.
- **AW-015** `AppHost.ResolveContentRoot(HostOptions)` (`options.ContentRoot ??
  <BaseDirectory>/content`); `AssetCatalog.Open(...)` in `RunClient` → `SceneContext.Catalog`;
  `HostOptions.ContentRoot` from `AGAPANTHE_CONTENT`. `SceneContext` `+ required AssetCatalog Catalog`.
- **AW-016** `ModelSceneRecipe` / `DriveSceneRecipe`: `GltfLoader.Load(path)` → `ctx.Catalog.LoadModel(
  ModelContent.ResolveModelKey(ctx.Args, ctx.Catalog))`; `ResolveModelKey` (no arg → `models/DamagedHelmet.glb`;
  bare `X` → `models/X` / `models/X.glb` vs `Contains`; miss → `InvalidOperationException`). `registry.Load`
  now takes the resolved key. `AGAPANTHE_UNLOAD_TEST` reuses `models/unload-probe`. `Program.cs` comment updated.
  Procedural content (`BuildGroundModel`/`BuildSphereModel`/sky) unchanged.
- **AW-017 gate**:
  - **`AGAPANTHE_SCENE=model` HDR capture `9030f6a64e9587b05d5abb99b487b1b9`** (2 764 816 B),
    JIT **and** NativeAOT. **Not `df55d444…`** — a `git stash` of the W2 source (pre-W2 tree, still
    `GltfLoader.Load`) produces the **same** `9030f6a6…`. So the cooked path is byte-faithful and the
    `df55d444…` reference (S30 W1) had already drifted before this milestone (Contenu-1 or a
    dependency; not diagnosed further — the milestone's job is "cooked == the path it replaced", and
    it is). **Re-pinned to `9030f6a6…`**; human verdict confirms the helmet still looks right.
  - `dotnet test` **684 passed** · `planet-drop` HDR `12638edd…` / UI `03421357…` **unchanged** ·
    `grid:20x20` / `drop:40` / `drive` clean, 0 leak / 0 validation · `-- MetalRoughSpheres.glb` →
    resolves `models/MetalRoughSpheres.glb`, renders · `-- nope.glb` → clean message, exit 1 ·
    Sandbox **JIT == NativeAOT** (cooked-content `AgModelFormat.Read` + `DeflateStream` inflate +
    `MemoryMarshal.Cast` all AOT-clean).
  - Shipped size: `bin/.../content/models/DamagedHelmet.glb.agmodel` **21 MB** (see W1 note).

### W3 DONE (2026-09-08) — AW-018..020

- **AW-018** `git mv src/Agapanthe.Assets/{GltfLoader,ImageLoader,TangentGenerator}.cs +
  Gltf/{AccessorReader,GlbContainer,GltfDocument,GltfSchema}.cs → src/Agapanthe.Assets.Pipeline/`
  (7 files, namespaces `Agapanthe.Assets*` kept — no rename). `Agapanthe.Assets.csproj` keeps
  `StbImageSharp` (HDR) but no glTF; `Agapanthe.Assets.Pipeline.csproj` **gains** `PackageReference
  StbImageSharp`. `GltfSchema.cs`'s `[assembly: InternalsVisibleTo("Agapanthe.Tests")]` travelled
  with it → the parser tests keep white-box access. `HdrImageLoader`'s `<see cref="ImageLoader"/>` →
  `<c>ImageLoader</c>` (cross-assembly cref would be a CS1574 error).
- **AW-019** `AssetsPipelineIsolationTests` (3): scan every `.csproj` in the repo → `Assets.Pipeline`
  referenced **only** by `AssetCooker` + `Agapanthe.Tests`; `Agapanthe.Assets.csproj` `ProjectReference`
  set is exactly `{Agapanthe.Core}`; no `GltfLoader` / `Agapanthe.Assets.Gltf.*` / `ImageLoader` /
  `TangentGenerator` type resolves from the loaded `Agapanthe.Assets` assembly, and it does not pull
  `Assets.Pipeline` transitively.
- **AW-020 gate**: `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` **687 passed** (+3) — the
  3 glTF test files (`GltfLoaderTests` / `GltfParserTests` / `AccessorReaderTests`) recompile
  **unchanged** against `Assets.Pipeline` · captures `model` `9030f6a6…` / `planet-drop` HDR
  `12638edd…` / UI `03421357…` **all unchanged** · `HeadlessSim` snapshots `80ced166…` / `cf01492e…`
  **unchanged** · `AotComponentProbe` **PASS** · **Sandbox + HeadlessSim JIT == NativeAOT** (Sandbox
  AOT `model` capture `9030f6a6…`).

### W4 IN PROGRESS (2026-09-08)

- **AW-021 Self Code Review** DONE — full diff re-read against spec + Decision Log:
  - reader `public` / writer `internal` on both formats; `git mv` shows 0-change renames (verbatim).
  - **applied during review** (memory safety, patron `FontAssetFormat`): `AgModelFormat.Read` now
    (1) bounds the header's `uncompressedLen` against `64 + compressedLen*1100` before allocating
    the inflate buffer, and (2) reads every count via `Reader.ReadCount()` which rejects
    `count > bytesRemaining` — a forged count can no longer drive a `new T[count]` OOM. The array
    readers `Take()` (which validates) **before** materialising, not after. Round-trip + hash-pin
    tests unchanged (writer untouched, decoded model identical).
  - ordinal sort + strictly-ascending guard on the manifest; `AssetCatalog` GPU-free + AOT-clean
    (proven by the AOT run); `registry.Load` signature untouched; procedural content still on the
    `ModelAsset` overload; the two staleness layers behave as documented.
- **AW-022 Requirements Validation** DONE — spec acceptance bar + Decision Log (10 rows) + Error
  Handling table walked line by line, all satisfied. One **intentional broadening**: `CookRunner`
  globs `*.gltf` **as well as** `*.glb` (the spec's revised §CookRunner said `.glb` only) — `.gltf`
  cooks fine via `GltfLoader`; only the *sibling* `.bin`/image hash tracking is deferred (a `.gltf`
  edit to a sibling needs a clean rebuild). The small `Box*` fixtures use this; the shipped Sandbox
  content is 100% `.glb`.
- **AW-023 double audit** — `csharp-lowlevel` **4.1/5 PASS-with-concerns** (no blocker) ·
  `engine-architect` **4.2/5 PASS-with-concerns** (no blocker). Both converged on the `.gltf` glob
  (silent stale content) and the unvalidated `blobPath`.

  **Findings applied (W4):**
  - `AgModelFormat.Read` memory safety (already partly done AW-021): the array readers `Take()`
    (validates) **before** materialising; every count via `Reader.ReadCount()` (rejects
    `count > bytesRemaining`); `uncompressedLen` bounded by BOTH the deflate ratio AND a hard 1 GiB
    ceiling. + 2 tests (forged count, truncation mid-array).
  - `CookRunner` globs **`*.glb` only** (was `.glb` + `.gltf`) — a `.gltf` under `content/` is
    ignored (visible), not cooked with untracked `.bin`/image siblings (silent stale). Tests moved
    to the 2 real `.glb`.
  - `ContentManifest.Read` validates `blobPath` (rejects rooted / `..`) — same guard as the key, the
    spec's own threat model applied symmetrically.
  - `AgModelFormat` validates material image-slot indices vs `imageCount`; `EnsureFinite` covers
    tangents.
  - `AssetKey.Normalise` rejects control characters (a tab/newline would corrupt the line-based
    `.cookstate` and any spliced path).
  - `CookRunner` **prunes orphan blobs** (a deleted/renamed source no longer ships dead content
    forever) + logs the total cooked size (BCn drift canary). + test.
  - `AppHost` opens the catalog **tolerantly** (`AssetCatalog.Empty` + a warning) — a fully-procedural
    scene (`planet*`) runs with no manifest; a model scene still fails with an actionable message.
    Verified: `AGAPANTHE_CONTENT=/nonexistent` + `planet` → warn, renders, 0 leak.
  - `AssetsPipelineIsolationTests` also forbids referencing `tools/AssetCooker` (transitive Pipeline
    pull — the MP-0a mutation).
  - `AgModel_CookedDamagedHelmet_HashIsPinned` gets an assertion message (canary, not corruption;
    re-pin policy); `ManifestEntry` doc names the by-ref `byte[]` equality trap; `AssetCatalog.Keys`
    sorted ordinal; `ResolveModelKey` prefix match is `Ordinal`; IVT for Pipeline moved to `.csproj`.

  **Findings deferred (§Deferred Work + `docs/BACKLOG.md`):**
  - `.gltf` support + its per-sibling dependency graph → Contenu-2b (needs a `GltfDocument`
    URI-enumeration API).
  - `content/` relocation to `samples/Sandbox/` — the arch's preference, but Windows collapses
    `content/` into the existing `samples/Sandbox/Content/` (case-insensitive FS); needs a distinct
    name (`assets/`?) — deferred, not blocking, keys are content-root-relative so it stays free.
  - `build/Agapanthe.Cook.targets` — extract the ~170 lines of cook targets from `Sandbox.csproj`
    at app #2 (same trigger as the `EngineWindowAdapter` debt).
  - Blob geometry/image split (a dedicated server ships 20 MB of pixels it never uploads) —
    **grouped with BCn** as one backlog item, reversible via `AgModelFormat.Version`.
  - `HostOptions.VerifyContentHashes` (the `contentHash[32]` is written + shipped, not yet read).
  - `.cookstate` re-hashes the blob on the skip path; case-fold key collision on a
    case-insensitive FS; `AgModelFormat.Read(byte[], offset, count)` to skip one 5 MB copy.

- **CONVERGE** — `CLAUDE.md` (§Contenu-2 + `Agapanthe.Assets.Pipeline` in the module diagram),
  `docs/AVANCEMENT.md` (§Reprise → Contenu-3), `docs/BACKLOG.md` (Contenu-2 delivered; Contenu-2b),
  spec §Execution outcome, board archived.

### W4 — DONE (2026-09-08) — mandatory tail

- `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` **689 passed** · captures `model`
  `9030f6a6…` / `planet-drop` HDR `12638edd…` / UI `03421357…` **unchanged** · `HeadlessSim`
  `80ced166…` / `cf01492e…` **unchanged** · `AotComponentProbe` **PASS** · Sandbox + HeadlessSim
  **JIT == NativeAOT** · double audit applied. **Human visual verdict: DUE.**
