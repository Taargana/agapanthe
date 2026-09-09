# Contenu — sub-milestone 2: offline cook + content manifest — Design Spec

> Domain **Contenu** (backlog §4quater), **decomposed into 3 sub-milestones** (like MP-0). Interview:
> session 32, 2026-09-08. Contenu-1 (`AssetKey` + snapshot v3) closed S31 (`fa335d7`). This spec
> re-states the 3-way framing and fully designs **Contenu-2**. Spec rev 1.

## Summary

Give the engine an **offline asset cook**: a `tools/AssetCooker` turns each source glTF under a
`content/` tree into a self-contained, Deflate-compressed, deterministic `.agmodel` blob, and emits a
binary `content.agmanifest` mapping `AssetKey → blob`. At runtime an `AssetCatalog` reads the manifest
and hands a recipe a fully-decoded `ModelAsset` — **no glTF/JSON parsing, no tangent generation, no
image decode at runtime**. glTF parsing moves out of `Agapanthe.Assets` into a new cook-side
`Agapanthe.Assets.Pipeline`. The Sandbox migrates to cooked content; an arbitrary-path model load is
gone (a model is now named by key). This is the layer Contenu-3 (declarative scenes, which reference
assets *by key*) is built on.

## Context

### The domain, decomposed

| # | Sub-milestone | Delivers | Status |
|---|---|---|---|
| **Contenu-1** | stable asset identity | `AssetKey` (`Core`); `ModelKeyIndex` (`Rendering`); snapshot **v3** (`MeshRef` = key + local indices + a load-time resolver) | ✅ **closed S31** (`fa335d7`) |
| **Contenu-2** | **offline cook + content manifest** — this spec | `.agmodel` blob; `tools/AssetCooker`; `content.agmanifest`; runtime `AssetCatalog`; glTF import moves offline (`Agapanthe.Assets.Pipeline`); Sandbox on cooked content. (A per-`.gltf` source-**dependency graph** is deferred with `.gltf` support — see §Out of scope.) | **this spec** |
| Contenu-3 | declarative prefabs & scenes | `.agscene` / `.agprefab` (**authoring** format, NOT a save-game); `SceneLoader`; the 5 Sandbox `ISceneRecipe`s become data; sim-side `AssetRef` decision (Contenu-1 audit finding) | framed only |

Ordering is forced: Contenu-3 scenes reference cooked assets **by key**, resolved through Contenu-2's
manifest + catalog. Each layer is its own spec + board + double audit + human verdict, with a human
greenlight between them (MP-0 pattern).

### Why Contenu-2 now — the concrete state

- **`src/Agapanthe.Assets/GltfLoader.cs`** (`Load(string path) → ModelAsset`) parses `.glb`/`.gltf`
  (`Gltf/GltfDocument`, `Gltf/GlbContainer`, `Gltf/GltfSchema`, `AccessorReader`), generates missing
  tangents (`TangentGenerator`), and decodes referenced images through `ImageLoader` (StbImageSharp).
  All of it runs **at every launch, per model**. `Agapanthe.Assets` is `IsAotCompatible` and carries
  `PackageReference StbImageSharp` — so the glTF+JSON+image-decode surface is in every shipped
  binary's AOT closure.
- **No cooked-blob format for models.** The `.agfont` precedent exists (`FontAssetFormat` — reader
  `public`, writer `internal` + `InternalsVisibleTo("FontCooker")`, blittable, deterministic,
  versioned header) and `tools/FontCooker` / `tools/ShaderPrecompiler` are the cook-tool pattern
  (console `Exe`, **not** AOT, **not** a `ProjectReference` of a shipped project — invoked via
  `dotnet exec` from an MSBuild target with stamp/hash incrementality and tmp-then-move writes).
- **No manifest, no catalog.** A recipe today calls `GltfLoader.Load(pathResolvedByHand)` then
  `registry.Load(device, model, layout, AssetKey, worldOrigin)` (Contenu-1). `ModelContent.ResolveModelPath`
  turns a bare CLI arg or the default into a filesystem path.
- **Contenu-3 needs a `AssetKey → ModelAsset` resolver.** A declarative scene names its models by
  key; there is nothing today that turns a key into an asset without the recipe knowing a path.
- Contenu-1 left a documented debt: Sandbox derives keys from `Path.GetFileName` (directory
  discarded) — the cook establishes a **content root**, so a key is a path relative to it.

### What exists (relevant)

- **`src/Agapanthe.Assets`** (`net10.0`, `IsAotCompatible`, refs `Core` + `StbImageSharp`):
  - `Model/` DTOs — `ModelAsset` (`Meshes`, `Materials`, `Images`, `Name`), `MeshAsset` (SoA:
    `Positions Vector3[]` required, `Normals Vector3[]`, `Tangents Vector4[]`, `Uvs Vector2[]`,
    `Indices uint[]` required, `MaterialIndex int` = -1 default, `WorldTransform Matrix4x4`,
    `Name string`), `MaterialAsset` (13 scalar/enum fields + 5 image-slot `int` indices +
    `TextureSettings` + `Name`), `ImageAsset` (`Rgba8Pixels byte[]`, `Width`, `Height`, `IsSrgb`),
    `HdrImageAsset`, `TextureSettings` (wrap/filter enums).
  - `GltfLoader` + `Gltf/{GltfDocument,GlbContainer,GltfSchema}` + `AccessorReader` +
    `TangentGenerator` + `ImageLoader` + `HdrImageLoader` (**StbImageSharp**, `.hdr` RGBE →
    `HdrImageAsset`) + `AssetException`.
  - `Font/{FontAsset,FontAssetFormat,FontAssetException}` — the cooked-format precedent.
- **`src/Agapanthe.Rendering/ResourceRegistry.cs`** — `Load(GraphicsDevice, ModelAsset,
  DescriptorSetLayout, AssetKey key, Double3 worldOrigin = default)` (Contenu-1). `SceneBuilder.Build`
  interleaves the SoA into GPU vertices and uploads. `GraphicsException` is the layer's error type.
- **`tools/FontCooker`** — `Program.cs` (arg parse, tmp-then-move, one-line failure + exit 1/2) +
  `SdfFontBaker` (the StbTrueTypeSharp-using baker, lives IN the tool). `.csproj`: `Exe`, `net10.0`,
  no AOT, `ProjectReference Agapanthe.Assets` + `Agapanthe.Core`, `PackageReference StbTrueTypeSharp`.
- **`samples/Sandbox/Sandbox.csproj`** — `CookFonts` / `PrecompileShaders` MSBuild targets:
  `<MSBuild>` the tool with `RemoveProperties="RuntimeIdentifier;SelfContained;PublishAot;PublishTrimmed;PublishSingleFile"`,
  `<Exec Command="dotnet exec &quot;@(_Tool)&quot; &quot;$(Src)&quot; &quot;$(Out)&quot;"`, stamp file,
  then an `IncludeCooked*` target (`BeforeTargets="AssignTargetPaths"`) pre-expands the output glob into
  `<Content>` items with `Link="…"` and `CopyToOutputDirectory=PreserveNewest`. Fixtures live under
  `tests/Agapanthe.Tests/Fixtures/` (`DamagedHelmet.glb`, `MetalRoughSpheres.glb`, `Box*.gltf`+`.bin`,
  `BoxTextured*`, `BoxInterleaved*`, `CesiumLogoFlat.png`, `studio_small_1k.hdr`) and are copied to
  `models/` next to the exe as Content.
- **`src/Agapanthe.App/AppHost.cs`** — `RunClient(IGame, IWindow, string[], HostOptions?)`; owns the
  `ResourceRegistry` (created `:86`), builds a `SceneContext`, has `ResolveShaderDirectory()`.
  `HostOptions.FromEnvironment`. `SceneContext` is a pure `required`-property carrier (Device, Registry,
  Renderer, Camera, Controller, Window, World, Orchestrator, Simulation, RenderList, Args).
- **`tests/Agapanthe.Tests`** — `GltfLoaderTests` / `GltfParserTests` load `Box*` **and
  `DamagedHelmet.glb`** from `tests/Agapanthe.Tests/Fixtures/` (`GltfLoaderTests.DamagedHelmet_LoadsGeometryMaterialAndEmbeddedImages`,
  `GltfParserTests.Load_DamagedHelmetGlb_ParsesHeaderChunksAndEmbeddedBuffer`); `AccessorReaderTests`
  loads `Box*`; `FontAssetTests` (`.agfont` round-trip); `EngineIsHeadlessTests` (the MP-0a static
  `[InlineData]` allowlist + assembly-closure walk + "no `PackageReference`" guard). White-box
  visibility for the parser tests comes from `[assembly: InternalsVisibleTo("Agapanthe.Tests")]` in
  `Gltf/GltfSchema.cs` — that attribute travels with the code in the W3 `git mv`.
- **Determinism references**: `AGAPANTHE_SCENE=model` HDR capture MD5 **`df55d444b74c7aa94fd0ab18d795cc9c`**
  (2 764 816 B — DamagedHelmet, the Agapanthe.App S30 W1 baseline, documented not test-pinned).
  `planet-drop` HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa`
  (pixel captures). `HeadlessSim` snapshot `80ced166fdf3076119a62970f63d683b` (1842 B) / `--drive`
  `cf01492e8a9688b666e01d2ef9d63869` (210 B) — **snapshot format unchanged by this milestone**.

### Out of scope (Contenu-2b / Contenu-3 / a render milestone)

`.gltf` sources with **external `.bin`/image siblings** — every current Sandbox model is a
self-contained `.glb`; a multi-file `.gltf` needs a per-dependency hash list in `.cookstate` and a
new `GltfDocument` URI-enumeration API, deferred to the first cooked `.gltf` (Contenu-2b); the
parser-coverage `Box.gltf` fixtures stay under `tests/`, uncooked. Standalone HDR environments /
textures / fonts in the catalog (`HdrImageLoader` stays a runtime path-load; `AGAPANTHE_HDRI`
unchanged; the procedural sky stays in code) — the catalog is designed extensibly (`LoadModel` now,
`LoadEnvironment` later). **Cross-machine byte-reproducibility of the `.agmodel` blob** — it depends
on `DeflateStream`'s output being stable for a BCL version; guaranteed here is that the *decoded*
`ModelAsset` is identical (test 9) and that a single machine's cook is deterministic (test 2, and a
literal blob-hash pin — test 3b); a `contentHash` that diverges across two dev machines on different
.NET patches is possible and is an `AgModelFormat.Version` concern if it ever bites. GPU block
compression (BC7/BC5 — changes the GPU format and `SceneBuilder`'s upload path); asset hot-reload;
packing blobs into archives; a runtime **load graph** (scene → prefab → model); **sim-side asset
identity** (`AssetRef` component — the Contenu-1 architecture-audit finding, a Contenu-3 structural
decision); cooking the in-code procedural content (it is parametric — built at scene setup from env
vars — and cannot be cooked).

## Design

### Architecture — assemblies (graph stays acyclic)

```
Core ◄── Assets ◄── Assets.Pipeline
          ▲               ▲
   Rendering, App    tools/AssetCooker  (Exe; never a ProjectReference of a shipped project)
                          ▲
                    Agapanthe.Tests   (the 3 glTF test files re-point here)
```

- **`src/Agapanthe.Assets`** (runtime, `IsAotCompatible`): the `Model/` + `Font/` DTOs, the new
  `AgModelFormat` reader, the new `ContentManifest` reader, the new `AssetCatalog`, `HdrImageLoader`
  (keeps `StbImageSharp` — HDR), `AssetException` + the new `AgModelException`. **Loses**
  `GltfLoader`, `Gltf/*`, `AccessorReader`, `TangentGenerator`, `ImageLoader`.
- **`src/Agapanthe.Assets.Pipeline`** (NEW, cook-side): the `AgModelFormat`/`ContentManifest`
  **writers** (reachable via `[assembly: InternalsVisibleTo("Agapanthe.Assets.Pipeline")]` on
  `Agapanthe.Assets`) + `AgModelWriter` + `ContentManifestWriter` + `CookRunner`. In **W3** it also
  gains the glTF code moved out of `Agapanthe.Assets` (`git mv`, verbatim) + `PackageReference
  StbImageSharp`. In W1/W2 `CookRunner` calls `Agapanthe.Assets.GltfLoader` (still in `Agapanthe.Assets`
  then). `.csproj`: **no** `IsAotCompatible` (dev tool, like `FontCooker`); refs `Agapanthe.Assets` +
  `Agapanthe.Core`. **`Agapanthe.Tests` references this project** (so tests 1–2, 7–11 reach the
  writers + `CookRunner`) — a library, not an Exe, precisely so the tests can.
- **`tools/AssetCooker`** (NEW): `Exe`, `net10.0`, no AOT, `ProjectReference Agapanthe.Assets.Pipeline`.
  A thin `Program.cs` — arg parse + call `CookRunner`. Deliberately not referenced by any shipped
  project (twin of `FontCooker.csproj`'s comment).
- **`Agapanthe.slnx`** gains both new projects (it already lists `tools/FontCooker`,
  `tools/ShaderPrecompiler`, `tools/AotComponentProbe` under a `tools` folder and every `src/*` project).
- **AOT note**: `AgModelFormat.Read`, `ContentManifest.Read`, `AssetCatalog` and the `DeflateStream`
  *inflate* path stay in `Agapanthe.Assets` and therefore in the Sandbox/HeadlessSim AOT closure —
  all AOT-safe (`DeflateStream`, `MemoryMarshal`, `SHA256` are trim/AOT-clean; no reflection, no
  source-gen). The `Optimal`-level *deflate* is Pipeline-only, never in a shipped binary.

**Static guard** (new, `AssetsPipelineIsolationTests`, patterned on MP-0a): (1) `Agapanthe.Assets.csproj`
carries no `ProjectReference` to `Assets.Pipeline` and no glTF namespace type resolves from the loaded
`Agapanthe.Assets` assembly; (2) a scan of **every `.csproj` in the repo** (`src/*`, `samples/*`,
`tools/*`, `tests/*`) shows `Assets.Pipeline` referenced **only** by `tools/AssetCooker` and
`tests/Agapanthe.Tests`.

### `src/Agapanthe.Assets/AgModelFormat.cs` (new) — the `.agmodel` blob

Pattern = `FontAssetFormat`: **reader `public`** (runtime path), **writer `internal`** +
`[assembly: InternalsVisibleTo("Agapanthe.Assets.Pipeline")]` + `InternalsVisibleTo("Agapanthe.Tests")`.
Deterministic: the same `ModelAsset` always produces the same bytes.

**Container**: `magic "AGMD" (4) | version u32 (=1) | payloadLen u32` then a **DeflateStream**
(`CompressionLevel.Optimal` — deterministic for a fixed BCL version; a version bump covers a BCL
change) wrapping the payload below. Little-endian scalars written explicitly (every target is LE; a
BE port fails at the magic).

```
payload (inside the deflate):
  modelNameLen u16 | model name UTF-8
  meshCount u32
    per mesh:
      vertexCount u32 | indexCount u32 | materialIndex i32
      streamMask u8   (bit0 Normals present, bit1 Tangents, bit2 Uvs; Positions + Indices always)
      worldTransform  f32 * 16                                  (MemoryMarshal, row-major)
      Positions       f32 * 3 * vertexCount                     (MemoryMarshal.AsBytes<Vector3>)
      [Normals        f32 * 3 * vertexCount]
      [Tangents       f32 * 4 * vertexCount]
      [Uvs            f32 * 2 * vertexCount]
      Indices         u32 * indexCount
  materialCount u32
    per material (a fixed-size blittable record written whole):
      BaseColorFactor f32*4 | MetallicFactor f32 | RoughnessFactor f32 | NormalScale f32
      OcclusionStrength f32 | EmissiveFactor f32*3 | EmissiveStrength f32
      AlphaMode u8 | AlphaCutoff f32
      BaseColorImage i32 | NormalImage i32 | MetallicRoughnessImage i32 | OcclusionImage i32 | EmissiveImage i32
      TextureSettings  (its wrap/filter enums as bytes — one blittable struct)
  imageCount u32
    per image: width u32 | height u32 | isSrgb u8 | Rgba8Pixels byte[width * height * 4]
```

- **Mesh/material `Name` are dropped** (the DTO doc-comments state they are diagnostics-only and not
  unique). The model `Name` is kept (one string, useful in logs). Rendering's `SceneBuilder` and
  `ResourceRegistry` never read a mesh/material name — verified.
- **Read validation** (patterned on `FontAssetFormat.Read`): magic; version (else a dedicated refuse,
  no auto-upgrade); after inflating, total payload length is recomputed from the counts and the
  payload must be **exactly** that long (a hostile count cannot force a huge allocation); every image
  `width > 0 && height > 0 && (long)width*height*4` fits and matches the bytes that follow; every
  float finite; `materialIndex ∈ [-1, materialCount)`; a mesh's per-stream lengths equal
  `vertexCount`. Any failure → `AgModelException` (new, `Agapanthe.Assets`) with a reason — never a
  half-built asset, never an OOB read.
- `MemoryMarshal.Cast<byte, Vector3>` etc. requires the deflated payload be materialised into a
  `byte[]` first (a `DeflateStream` is not seekable) — `Read` inflates fully into a pooled/plain
  buffer, then parses spans. `ModelAsset` is a punctual load; the allocation is fine.

### `src/Agapanthe.Assets/ContentManifest.cs` (new) — the index

**Shipped file** `content.agmanifest`, binary, deterministic, entries **sorted ordinal by key**:

```
magic "AGMN" (4) | version u32 (=1) | entryCount u32
per entry:
  keyLen u16     | key UTF-8            (AssetKey.Value)
  kind u8        (0 = Model; 1/2/… reserved for Environment/Font — the reader rejects an unknown kind
                  only when asked for it, so a forward-compat manifest still lists its Model entries)
  blobPathLen u16 | blob path UTF-8     (relative to the content root, e.g. "models/DamagedHelmet.glb.agmodel")
  contentHash byte[32]                  (SHA-256 of the .agmodel file — integrity + a cheap diff key)
```

- Reader `ContentManifest.Read(ReadOnlySpan<byte>) → IReadOnlyDictionary<AssetKey, ManifestEntry>`
  (`public`). Writer `internal` (Pipeline). Validation: magic, version, `entryCount` sane vs length,
  keys strictly ascending ordinal (a forged/reordered manifest is corruption — the Contenu-1 W4
  symmetry), each key valid through the `AssetKey` ctor (wrap `FormatException`), no duplicate key.
- **Build state is a sidecar, not shipped**: `<outputDir>/.cookstate` (format free — dev-machine-only,
  read only by the cooker). Per entry: `key`, `sourcePath`, `sourceHash[32]` (SHA-256 of the source
  file's bytes) + the cooker's own version string. The **runtime manifest never carries a source
  hash**. **Every current Sandbox model is a self-contained `.glb` with zero external
  dependencies**, so the source hash + cooker version is the whole staleness key. `.gltf` with
  external `.bin`/image siblings — which would need a per-dep hash list and a new
  `GltfDocument` API to enumerate URIs — are **out of scope** (the parser-coverage `Box.gltf`
  fixtures stay under `tests/`, uncooked); the first cooked `.gltf` extends `.cookstate` then. §Out
  of scope names this.

### `src/Agapanthe.Assets/AssetCatalog.cs` (new) — the runtime

```csharp
public sealed class AssetCatalog                 // no GPU, no Pipeline dependency
{
    public static AssetCatalog Open(string contentRoot);   // reads <contentRoot>/content.agmanifest
    public bool Contains(AssetKey key);
    public ModelAsset LoadModel(AssetKey key);
    public IReadOnlyCollection<AssetKey> Keys { get; }
}
```

- `Open`: `content.agmanifest` missing → `AssetException` naming the path ("no cooked content at
  '<root>' — run a build"). Holds the parsed dictionary + the resolved root; no file handles, no
  `IDisposable`.
- `LoadModel`: key absent → `AssetException` ("no asset '<key>' in the content manifest"); entry
  `kind != Model` → `AssetException`; then `File.ReadAllBytes(root/blobPath)` → `AgModelFormat.Read`.
  A missing/corrupt blob surfaces as `AgModelException`/`IOException` — the caller's scene build
  fails loudly. **No `contentHash` check by default** (an extra full-file SHA-256 on every load); a
  `HostOptions.VerifyContentHashes` opt-in is a documented follow-up, not this milestone.
- **No in-memory `ModelAsset` cache**: `registry.Load` is punctual, and the `LoadedModel` GPU
  resources it mints are already the cache. Matches today's behaviour exactly.

### `src/Agapanthe.Assets.Pipeline/` (new) — cook-side

- **Moved in W3** (`git mv`, zero code change): `GltfLoader.cs`, `Gltf/GltfDocument.cs`,
  `Gltf/GlbContainer.cs`, `Gltf/GltfSchema.cs` (its `[assembly: InternalsVisibleTo("Agapanthe.Tests")]`
  travels with it), `AccessorReader.cs`, `TangentGenerator.cs`, `ImageLoader.cs`. Their namespace
  stays `Agapanthe.Assets*` (no rename churn); they compile in a different assembly. `AssetException`
  — used by both the moved code and runtime `HdrImageLoader` — **stays in `Agapanthe.Assets`**
  (Pipeline → Assets, never the reverse — no cycle). Until W3, `CookRunner` calls the `GltfLoader`
  that still lives in `Agapanthe.Assets`.
- **New**:
  - `AgModelWriter.Write(Stream, ModelAsset)` — builds the payload, deflates, calls the internal
    `AgModelFormat.WriteContainer`. Deterministic.
  - `ContentManifestWriter.Write(Stream, IReadOnlyList<ManifestEntry>)` — sorts ordinal, writes.
  - `CookRunner.Cook(string contentRoot, string outputDir)` — globs `**/*.glb` under `contentRoot`
    (`.gltf` support deferred with its dep-enumeration, see §Out of scope) in **ordinal-sorted**
    order; key = path relative to `contentRoot`, separators normalised to `/` (this is
    `AssetKey.FromContentPath`, see below); fails the whole cook (exit 1) if two sources normalise to
    one key. For each source: **content-hash staleness** — SHA-256 the source bytes, and if the
    `.cookstate` entry's `sourceHash` + cooker version match **and** the `<outputDir>/<key>.agmodel`
    still exists, **skip** it; else `Agapanthe.Assets.GltfLoader.Load(source)` (moves into Pipeline in
    W3) → `AgModelWriter.Write` to `<outputDir>/<key>.agmodel` (tmp-then-move). Always rewrite
    `content.agmanifest` + `.cookstate` (tmp-then-move) from the full entry set. Returns a summary
    (cooked / skipped counts).
  - **Two staleness layers, on purpose**: the MSBuild `CookAssets` stamp is *mtime*-based and coarse
    (any file under `content/**` or the tool sources newer than the stamp → run the cooker); inside,
    `CookRunner` is *content-hash*-based and fine (an mtime touch with unchanged bytes → every blob
    skipped, manifest rewritten identically).
- **`AssetKey.FromContentPath(string contentRoot, string sourcePath)`** — new static helper in
  `Agapanthe.Core/AssetKey.cs`: `Path.GetRelativePath(contentRoot, sourcePath)` → normalise via the
  existing ctor. One place; the cooker is its only caller today, the Sandbox recipe uses literal keys.

### `tools/AssetCooker/Program.cs` (new)

```
usage: AssetCooker <contentRootDir> <outputDir>
exit: 0 = cooked or already up to date, 1 = cook failed, 2 = bad arguments
```

`FontCooker` shape exactly: validate args (dirs exist), `try { CookRunner.Cook(...); }` `catch (…
AssetException or AgModelException or IOException or UnauthorizedAccessException) { one line + return 1; }`,
`Log.Info` a summary on success.

### `samples/Sandbox` — the migration

- **`content/`** (new, repo root, like `shaders/` and `fonts/`): `content/models/DamagedHelmet.glb`,
  `content/models/MetalRoughSpheres.glb` — **copies** of the two real Sandbox models.
  `tests/Agapanthe.Tests/Fixtures/` is **left untouched**: `GltfLoaderTests` / `GltfParserTests` load
  `DamagedHelmet.glb` from there, and `AccessorReaderTests` / the parser tests load `Box*`. A `.glb`
  fixture is ~1–3 MB; the duplication is deliberate — the test tree and the content tree have
  different owners. `studio_small_1k.hdr` stays under `tests/Fixtures/` with its `models/` Content
  copy (HDR out of scope). The Sandbox no longer copies `DamagedHelmet.glb` / `MetalRoughSpheres.glb`
  into `models/` (they ship cooked, under `content/`); the `tests/Fixtures → models/` Content copy in
  `Sandbox.csproj` narrows to just the HDR.
- **`Sandbox.csproj`**: a `CookAssets` target — twin of `CookFonts` (`<MSBuild>` the cooker with the
  same `RemoveProperties`, `<Exec>` `dotnet exec … <content/> <obj/assetcache/>`, stamp) + an
  `IncludeCookedAssets` target (`BeforeTargets="AssignTargetPaths"`) shipping `obj/assetcache/**` as
  `<Content Link="content\%(RecursiveDir)%(Filename)%(Extension)">`. Stamp `Inputs`: every file under
  `content/**` **and** every `.cs` under `tools/AssetCooker/**` + `src/Agapanthe.Assets.Pipeline/**`
  (the cooker's own logic is an input — the `.agmodel` is stamp-gated, and `CookRunner`'s
  content-hash check inside handles the fine-grained skip).
- **`src/Agapanthe.App/AppHost.cs`**: `internal static string ResolveContentRoot(HostOptions)` —
  `options.ContentRoot ?? Path.Combine(AppContext.BaseDirectory, "content")` (twin of
  `ResolveShaderDirectory`); `var catalog = AssetCatalog.Open(ResolveContentRoot(options));` in
  `RunClient`, threaded into `SceneContext.Catalog`. No teardown step (no handle).
- **`src/Agapanthe.App/HostOptions.cs`**: `+ string? ContentRoot` (from `AGAPANTHE_CONTENT` in
  `FromEnvironment`).
- **`src/Agapanthe.App/SceneContext.cs`**: `+ public required AssetCatalog Catalog { get; init; }`.
- **`samples/Sandbox/Scenes/ModelSceneRecipe.cs` + `DriveSceneRecipe.cs`**: `GltfLoader.Load(path)` →
  `ctx.Catalog.LoadModel(key)`. `ModelContent.ResolveModelPath` → `ModelContent.ResolveModelKey(args,
  catalog)`: no arg → `AssetKey("models/DamagedHelmet.glb")`; a bare arg `X` → try
  `AssetKey("models/X")` then `AssetKey("models/X.glb")` against `catalog.Contains`; a `models/…` arg
  → verbatim; nothing matches → `InvalidOperationException` ("model 'X' is not in the content
  manifest — add it under content/models/ and rebuild"). The `AGAPANTHE_UNLOAD_TEST` loop loads the
  same cooked key N times (it already `Unload`s each cycle). `ModelSceneRecipe`'s existing
  `AGAPANTHE_LOAD` warn line is unaffected. **`samples/Sandbox/Program.cs`** — the header comment
  documenting `-- <model>.glb` as a bare fixture name is updated to "a content key".
- **`samples/Sandbox/Content/ModelContent.cs` + `Content/PlanetContent.cs`**: `BuildGroundModel` /
  `BuildSphereModel` / `BuildGrassImage` / `BuildSkyEnvironment` **unchanged** — procedural, still
  `registry.Load(device, ModelAsset, layout, key, origin)`.
- **`tests/Agapanthe.Tests/Agapanthe.Tests.csproj`**: `+ ProjectReference Agapanthe.Assets.Pipeline`
  (in W1 — the tests need the writers + `CookRunner`). `GltfLoaderTests` / `GltfParserTests` /
  `AccessorReaderTests` compile **unchanged** — the glTF types keep their `Agapanthe.Assets*`
  namespaces, so after the W3 `git mv` they resolve from `Assets.Pipeline` transparently; the
  `DamagedHelmet.glb` / `Box*` fixtures under `tests/Fixtures/` do not move. `EngineIsHeadlessTests`:
  the Sandbox/App/Engine `[InlineData]` closures are unchanged (they never referenced glTF types);
  the new isolation assertion lives in `AssetsPipelineIsolationTests`, not here.

## Error Handling

| Failure | Handling |
|---|---|
| `AssetCooker` bad args / missing dir | exit 2, usage line (FontCooker shape). |
| A source glTF is malformed | `GltfLoader` throws `AssetException`; `CookRunner` lets it propagate; `Program` prints one line + exit 1. **No partial blob** — tmp-then-move. |
| `content.agmanifest` missing at runtime | `AssetCatalog.Open` → `AssetException` naming the root ("no cooked content — run a build"). |
| `catalog.LoadModel(key)`, key not in the manifest | `AssetException` ("no asset '<key>' in the content manifest"). The recipe's `ResolveModelKey` catches the "bare CLI arg" case earlier with a friendlier message. |
| A `.agmodel` blob is missing / truncated / bad magic / wrong version / count mismatch | `AgModelException` (or `IOException` for missing) — the scene build fails loudly, same as any Contenu-1 resolver throw. |
| `content.agmanifest` corrupt (bad magic, non-ascending keys, a key the ctor rejects, duplicate) | `AssetException` from `ContentManifest.Read` — `AssetCatalog.Open` fails, nothing loads. |
| Manifest lists `kind = Environment` (reserved) and a recipe asks `LoadModel` for that key | `AssetException` ("asset '<key>' is an environment, not a model"). |
| `DeflateStream` produces different bytes after a BCL upgrade | The *decoded* `ModelAsset` is unaffected (test 9) — the render does not change. The literal blob-hash pin (test 3b) fails on the machine that upgraded, flagging it; the fix is a re-cook (and an `AgModelFormat.Version` bump only if the *payload layout* — not just the compression — changed). Cross-machine `contentHash` divergence on mixed .NET patches is a known, accepted limit (§Out of scope). |
| Two source files normalise to the same key (e.g. `models/x.glb` reachable twice via a symlink) | `CookRunner` detects the duplicate key and fails the cook (exit 1). |

## Testing Strategy

New: `tests/Agapanthe.Tests/{AgModelFormatTests,ContentManifestTests,AssetCatalogTests,AssetCookTests,AssetsPipelineIsolationTests}.cs`.
Modify: `Agapanthe.Tests.csproj` (+ `Agapanthe.Assets.Pipeline` ref). The 3 glTF test files are
**unchanged**. All GPU-free.

| # | Test | Cases |
|---|---|---|
| 1 | `AgModel_RoundTrip_IsByteIdentical` | build a `ModelAsset` (2 meshes — one with all streams, one positions+indices only; 2 materials — one textured, one factors-only; 2 images) → `AgModelWriter.Write` → `AgModelFormat.Read` → re-`Write` → **byte-identical**; the read-back `ModelAsset` equals the original field-by-field (positions, indices, material factors, image pixels, `MaterialIndex`, `WorldTransform`, model `Name`; mesh/material `Name` are `""`). |
| 2 | `AgModel_Write_IsDeterministic` | same `ModelAsset`, two writes (same process) → identical bytes. |
| 3 | `AgModel_Read_RejectsCorruption` | bad magic; version 2; truncated container; `imageCount` claims an image whose `w*h*4` exceeds the remaining bytes; a non-finite position; `materialIndex` out of range → each `AgModelException`, no OOB. |
| 3b | `AgModel_CookedDamagedHelmet_HashIsPinned` | `CookRunner.Cook(DamagedHelmet.glb)` → SHA-256 of the `.agmodel` **== a literal pinned hash** (recorded on the dev machine, JIT). This is the only guard that a silent `DeflateStream`/payload change is *visible*; a BCL patch that changes the compressed bytes fails here (and only here) — the intended signal. **Board caveat**: a CI runner on a different .NET patch than the pinning machine reds this test; the board records the pin machine's SDK version and the re-pin policy is "verify test 9 still passes, then re-pin + note the SDK". |
| 4 | `ContentManifest_RoundTrip` | 3 entries → write → read → same map; entries come back with keys ascending. |
| 5 | `ContentManifest_Read_RejectsCorruption` | bad magic; non-ascending keys; a key the `AssetKey` ctor rejects; duplicate key → `AssetException`. |
| 6 | `AssetCatalog_Open_MissingManifest_Throws` | `Open("<empty dir>")` → `AssetException` naming the path. |
| 7 | `AssetCatalog_LoadModel` | cook a fixture into a temp dir, `Open`, `LoadModel(key)` → a `ModelAsset` with the expected mesh/material/image counts; `Contains` true; `Keys` lists it. |
| 8 | `AssetCatalog_LoadModel_UnknownKey_Throws` / `WrongKind_Throws` | absent key → `AssetException`; a hand-written manifest entry with `kind = 1` → `LoadModel` throws. |
| 9 | `Cook_Then_Load_Equals_GltfLoader` (the **fidelity gate** — the proof the render is unchanged, esp. for `MetalRoughSpheres` which has no pixel pin) | for `DamagedHelmet.glb` **and** `MetalRoughSpheres.glb`: `CookRunner.Cook` → `AssetCatalog.LoadModel` yields a `ModelAsset` **equal field-by-field** to `Agapanthe.Assets.GltfLoader.Load(source)` — same vertex floats (bit-exact through `MemoryMarshal.AsBytes<Vector3>` ⇄ `Cast<byte,Vector3>`), same decoded RGBA8 pixels, same generated tangents, same material factors, same `MaterialIndex`/`WorldTransform`. |
| 10 | `Cook_IsIncremental` | cook a temp content dir; cook again → summary reports **0 cooked / N skipped**, `content.agmanifest` **byte-identical** to the first run; **rewrite a source file's bytes** (not just its mtime) → next cook reports **1 cooked**, only that blob's mtime changed. |
| 11 | `CookRunner_DerivesKeyFromContentRelativePath` | a source `.glb` at `<contentRoot>/models/sub/x.glb` → key `models/sub/x.glb` (not `x.glb`); a duplicate key (two sources) fails the cook. |
| 12 | `AssetsPipelineIsolation` (static, MP-0a pattern) | `Agapanthe.Assets.csproj` has no `ProjectReference` to `Assets.Pipeline`; no `Agapanthe.Assets.Gltf.*` / `GltfLoader` type resolves from the loaded `Agapanthe.Assets` assembly; a scan of **every `.csproj`** (`src/*`, `samples/*`, `tools/*`, `tests/*`) shows `Assets.Pipeline` referenced only by `tools/AssetCooker` + `tests/Agapanthe.Tests`. |
| 13 | `EngineIsHeadlessTests` (existing) | still green — the Sandbox/App/Engine closures are unchanged (they never saw glTF types). |

**Manual / CI gates**:
- `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` green (+ 5 new test files).
- `AGAPANTHE_SCENE=model` HDR capture MD5 **== the pre-W2 `GltfLoader` path** (cooked content renders
  byte-identical). If it differs, W2 stops: diagnose (stash the W2 source, capture the old path,
  compare) before re-pinning + a human verdict. **W2 outcome: `9030f6a64e9587b05d5abb99b487b1b9`,
  JIT == AOT — the S30 `df55d444…` reference had already drifted; a stash proved the cooked path
  equals the path it replaces.**
- `planet-drop` HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI `034213575932dabcff41c2e0c72addfa` —
  **unchanged** (procedural content untouched).
- `HeadlessSim` default + `--drive` snapshots **unchanged** (`80ced166…` / `cf01492e…` — no snapshot
  format change) · `AotComponentProbe` PASS · Sandbox + HeadlessSim **JIT == NativeAOT**.
- Double audit (`csharp-lowlevel` + `engine-architect`).
- **Human visual verdict**: `AGAPANTHE_SCENE=model` renders DamagedHelmet from cooked content,
  identical to before; `-- MetalRoughSpheres.glb` resolves and renders (its fidelity proof is test
  9's field-by-field equality, not a pixel pin); a bad name gives a clean message.

## Migration Path

Four waves; each builds green with the pinned pixel captures + HeadlessSim snapshots unchanged.

| Wave | Content | Gate |
|---|---|---|
| **W1** | `src/Agapanthe.Assets`: `AgModelFormat` (reader public + writer internal, `InternalsVisibleTo("Agapanthe.Assets.Pipeline")` + `"Agapanthe.Tests"`) + `AgModelException`; `ContentManifest` (reader public + writer internal) + `ManifestEntry`; `AssetCatalog`. **New `src/Agapanthe.Assets.Pipeline`** (library): `AgModelWriter`, `ContentManifestWriter`, `CookRunner` (calls `Agapanthe.Assets.GltfLoader` — still there). **New `tools/AssetCooker`** (Exe → `Program.cs` → `CookRunner`). `Agapanthe.slnx` + `Agapanthe.Tests.csproj` gain both. `AssetKey.FromContentPath` (Core). Tests 1–11. | build 0 warn · tests green · **nothing shipped changes** (Sandbox still calls `GltfLoader`; `Assets.Pipeline`/`AssetCooker` referenced by no shipped project). |
| **W2** | `content/` + **copies** of the 2 `.glb`; `CookAssets` + `IncludeCookedAssets` MSBuild targets (twin of `CookFonts`); narrow the `tests/Fixtures → models/` Content copy to the HDR; `AppHost.ResolveContentRoot` + `AssetCatalog` into `SceneContext.Catalog` + `HostOptions.ContentRoot`; `ModelSceneRecipe` / `DriveSceneRecipe` → `ctx.Catalog.LoadModel`; `ModelContent.ResolveModelKey`; `Program.cs` usage comment. | `AGAPANTHE_SCENE=model` HDR == `df55d444…` · `grid:` / `drop:` / `drive` clean · 0 leak / 0 validation · `planet-drop` captures unchanged · Sandbox JIT == AOT. |
| **W3** | `git mv` `GltfLoader` + `Gltf/*` + `AccessorReader` + `TangentGenerator` + `ImageLoader` → `Agapanthe.Assets.Pipeline` (+ `PackageReference StbImageSharp` there); `Agapanthe.Assets.csproj` keeps `StbImageSharp` (HDR) but loses the glTF code; `CookRunner`'s `GltfLoader` call now resolves in-assembly; `AssetsPipelineIsolationTests` (test 12). The 3 glTF test files + `AssetCooker` recompile unchanged (namespaces travel). Test 13. | build 0 warn · tests green · captures unchanged · Sandbox + HeadlessSim JIT == AOT · `AotComponentProbe` PASS. |
| **W4** | Mandatory tail: Self Code Review → Requirements Validation → Full Project Verification + double audit + human verdict. CONVERGE: `CLAUDE.md` (§Contenu-2, `Pipeline` in the module diagram), `docs/AVANCEMENT.md` (§Reprise → Contenu-3), `docs/BACKLOG.md` (Contenu-2 delivered; Contenu-2b = HDR/textures in the catalog; the Contenu decomposition table), this spec's §Execution outcome, archive the board, suggest a commit. | all gates; audits applied; board `completed`. |

**Rollback**: W1's tree-clean hash. Each wave is a commit-able unit.

## Open Questions

None — resolved in the session-32 interview (see Decision Log).

## Decision Log

| Decision | Options considered | Chosen | Rationale |
|---|---|---|---|
| Scope | lean (blob + cooker + thin catalog, deps later); **full pipeline**; decompose Contenu-2 further | **full pipeline** | The user wants the real thing: `.agmodel` + cooker + manifest + catalog + Sandbox migrated + glTF cook-only. It is ~4 waves, within budget. Contenu-3 needs the catalog anyway. |
| Where glTF parsing lives after | new `Agapanthe.Assets.Pipeline`; into `tools/AssetCooker` directly (like `SdfFontBaker`); keep in `Agapanthe.Assets` and rely on trimming | **`Agapanthe.Assets.Pipeline`** | glTF is heavily unit-tested (3 files) — a lib keeps the tests; `tools/AssetCooker` stays a thin Exe; one new `.csproj`; the honest version of "moves offline". `SdfFontBaker`-in-the-tool works because the baker is untested. |
| `.agmodel` granularity | self-contained blob + build-time deps; split geometry/image blobs + a runtime load graph | **self-contained + build-time deps** | A runtime key→key load graph is a Contenu-3 concern (scene → prefab → model). Build-time source deps are what incremental cook needs. One file per key = how `registry.Load` consumes a whole `ModelAsset`, and how `.agfont` works. |
| Manifest format | binary deterministic index + a cooker build-state sidecar; no runtime manifest (path by convention); JSON | **binary index + sidecar** | Matches the project's binary-deterministic discipline (`.agfont`, VS-1 snapshot, shader cache); keeps the runtime off filesystem conventions; sets up archive/packing later. The runtime manifest never carries a source hash. |
| Runtime seam | `ctx.Catalog.LoadModel(key)` then `registry.Load(model, key)`; `registry.Load(device, key, …)` (registry holds the catalog); a `ctx.LoadModel(key)` helper | **`ctx.Catalog.LoadModel` then `registry.Load`** | `registry.Load` stays exactly as Contenu-1 left it; the `ModelAsset` overload must stay for procedural content anyway; two explicit steps, matching the project's seam style; `SceneContext` stays a pure carrier. |
| HDR / textures / fonts standalone | in scope (`.agenv` + `LoadEnvironment` + move `HdrImageLoader`); **out of scope** | **out of scope** | Contenu-1 already deferred them. `HdrImageLoader` is small; the milestone stays bounded. The catalog is designed to grow a `LoadEnvironment`. |
| Cook trigger + source layout | MSBuild target + `content/` root (like the two existing cookers); a standalone CLI; both | **MSBuild target + `content/`** | Unambiguous project precedent (`CookFonts` / `PrecompileShaders` — `dotnet exec`, stamp, `RemoveProperties`, tmp-then-move). `content/` mirrors `shaders/` and `fonts/`. |
| Image compression | **Deflate the blob** (BCL); raw uncompressed; GPU block compression (BC7/BC5) | **Deflate** | RGBA8 2K textures raw = ~50 MB per model. Deflate is BCL, deterministic at a fixed level, AOT-safe, no new dep. BCn changes the GPU format + `SceneBuilder` upload — a separate render milestone. |
| CLI model arg | **becomes a key**; a dev fallback that cooks-on-the-fly / falls back to `GltfLoader` | **becomes a key** | The point of the milestone is that the runtime knows only cooked content. A path arg is resolved to a key against the manifest; an arbitrary path is no longer loadable — drop it in `content/models/` and rebuild. |
| AOT win | full (StbImageSharp leaves the runtime — needs a hand-rolled RGBE decoder or HDR in scope); **partial** | **partial, accepted** | `HdrImageLoader` (out of scope) also uses `StbImageSharp`, so it stays a runtime `PackageReference`. The real win: no glTF/JSON parsing, no tangent gen, no `ImageLoader` at runtime. A hand-rolled RGBE decoder is a Contenu-2b option. |
| `.agmodel` determinism guarantee | byte-identical cross-machine (pin the layout AND require a reproducible compressor); **same-machine deterministic + decoded-`ModelAsset` stable + one literal blob-hash pin** | **the latter** | The payload layout is fully pinned and the decoded `ModelAsset` is bit-exact (test 9) — the render never changes. `DeflateStream(Optimal)` is deterministic within a BCL version but not contractually stable across .NET patches; chasing cross-machine byte-identity would mean vendoring a compressor. The literal blob-hash pin (test 3b) makes a change *visible* without making it *impossible*, and a divergent `contentHash` across dev machines is an accepted, documented limit. |
| Cook dependency graph | full source-dep graph now (`.bin`/image siblings per `.gltf`); **source hash + cooker version only** | **source hash + cooker version** | Every current Sandbox model is a self-contained `.glb`. A `.gltf` sibling graph needs a new `GltfDocument` URI-enumeration API and is unexercised by any cooked asset — deferred to the first cooked `.gltf` (Contenu-2b), named in §Out of scope. |

## Execution outcome (2026-09-08, session 32)

Delivered in 4 waves + mandatory tail, board `.absolute-work/archive/board-session32-Contenu2.md`.

- **W1** — `AgModelFormat` (`.agmodel` container: `magic|version|uncompressedLen` + a raw
  `DeflateStream(Optimal)` of a length-prefixed payload; reader `public`, writer `internal`) +
  `AgModelException`; `ContentManifest` (`AGMN`, entries sorted ordinal, strictly-ascending guard) +
  `ManifestEntry` + `AssetKind`; `AssetCatalog` (`Open`/`Contains`/`LoadModel`/`Keys`, GPU-free, no
  cache). New `src/Agapanthe.Assets.Pipeline` (library, not AOT) — `AgModelWriter` /
  `ContentManifestWriter` (tmp-then-move) + `CookRunner`. New `tools/AssetCooker` (Exe). Nothing
  shipped changed (Sandbox still on `GltfLoader`).
- **W2** — `content/models/{DamagedHelmet,MetalRoughSpheres}.glb` (copies); `Sandbox.csproj`
  `CookAssets` + `IncludeCookedAssets` targets (twin of `CookFonts`); `AppHost.ResolveContentRoot`
  + `AssetCatalog` into `SceneContext.Catalog` + `HostOptions.ContentRoot`; `ModelSceneRecipe` /
  `DriveSceneRecipe` → `ctx.Catalog.LoadModel(ModelContent.ResolveModelKey(...))`; the CLI arg is
  now a content key. **The `AGAPANTHE_SCENE=model` capture is `9030f6a64e9587b05d5abb99b487b1b9`,
  JIT == AOT — NOT the S30 `df55d444…`**: a `git stash` of the W2 source proved the cooked path
  renders byte-identical to the `GltfLoader` path it replaces, so the S30 reference had already
  drifted (Contenu-1 or a dependency). Re-pinned with that written reason.
- **W3** — `git mv` of `GltfLoader` + `Gltf/*` + `AccessorReader` + `TangentGenerator` + `ImageLoader`
  → `Agapanthe.Assets.Pipeline` (+ `StbImageSharp`); `Agapanthe.Assets` keeps `StbImageSharp` for
  `HdrImageLoader` (HDR standalone is out of scope). `AssetsPipelineIsolationTests` (static `.csproj`
  scan of the whole repo + a reflection check on the loaded `Agapanthe.Assets` assembly). The 3
  glTF test files recompile unchanged.
- **W4** — double audit (`csharp-lowlevel` **4.1/5**, `engine-architect` **4.2/5**, both
  PASS-with-concerns, no blocker). Applied: memory-safe `.agmodel` parsing (count bounds before
  allocation, 1 GiB ceiling); `CookRunner` globs `*.glb` **only** (a `.gltf` sibling edit can't ship
  a stale blob silently); `blobPath` validated like the key; material image-slot + tangent-finite
  validation; `AssetKey` rejects control chars; orphan-blob prune + size logging; tolerant catalog
  open (`AssetCatalog.Empty` — a procedural scene runs with no manifest); isolation guard also
  forbids referencing `tools/AssetCooker`.

**Deviations from spec** (both intentional): (1) the `model` capture is re-pinned to `9030f6a6…`
(the `df55d444…` reference had drifted pre-milestone — proven, not assumed); (2) `CookRunner` began
by globbing `.gltf` too and W4 restricted it to `.glb` (spec-conforming) — `.gltf` support returns
with its dependency graph in Contenu-2b.

**Deferred** (board §Deferred Work + `BACKLOG.md §4quater`): Contenu-2b (`.gltf` + dep graph,
standalone HDR/textures/fonts in the catalog, a hand-rolled RGBE decoder to shed `StbImageSharp`
from the runtime); `build/*.targets` extraction of the cook targets (app #2); blob geometry/image
split **grouped with BCn**; `content/` relocation under `samples/Sandbox/` (blocked on a
case-insensitive-FS name clash with `Content/`).

**Gates**: `dotnet build Agapanthe.slnx` 0 warning · `dotnet test` **689 passed** · `model`
`9030f6a6…` / `planet-drop` HDR `12638edd…` / UI `03421357…` unchanged · `HeadlessSim` `80ced166…`
/ `cf01492e…` unchanged · `AotComponentProbe` PASS · Sandbox + HeadlessSim **JIT == NativeAOT** ·
`AssetsPipelineIsolationTests` green. **Human visual verdict: DUE.**
