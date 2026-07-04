# Absolute-Human Board — Agapanthe Session 4 (M4 : glTF)

**Status**: in-progress
**Créé**: 2026-07-04
**Spec**: docs/plans/2026-07-02-graphics-engine-design.md §3.6, §5, §6 (M4)
**Board persistence**: git-tracked
**Sessions passées**: S1 (M0+M1), S2 (M2), S3 (M3) → .absolute-human/archive/

## Intake (design déjà fixé — pas de re-brainstorm)

- **Problème**: le Sandbox rend un cube codé en dur avec un câblage manuel non réplicable. M4 apporte le chargement de vrais modèles (glTF 2.0) et la structure Rendering qui va avec.
- **Cible de sortie (spec §6 M4)**: fixtures Khronos affichées (géométrie + textures correctes), tests parsing verts. Pas d'éclairage PBR (M5) — baseColor texturée.
- **Décisions architecte S3 actées ici**:
  1. GltfLoader/ImageLoader → **DTO CPU purs, zéro dépendance Graphics** (tests sans GPU, spec §5). Graphe : Assets → Core ; Rendering → Assets + Graphics.
  2. Rendering gagne Mesh, Material, MeshInstance, Scene, Renderer/ForwardPass — le câblage manuel du Sandbox migre.
  3. Ownership : Material possède images/samplers/UBO + set ; Mesh ses buffers ; Renderer le DescriptorAllocator.
  4. **Set 1 figé en forme PBR finale (6 bindings)** dès M4 : 0 baseColor, 1 normal, 2 metallicRoughness, 3 AO, 4 emissive (combined samplers) + 5 UBO facteurs. M4 remplit baseColor + facteurs ; les autres pointent des placeholders 1×1.
  5. Cache de Sampler (dedup configs glTF) dans Rendering.
  6. Uploads per-ressource synchrones, tout le load dans un seul `using` GpuUploader.
- **Spec §3.6 périmètre glTF**: POSITION/NORMAL/TANGENT/TEXCOORD_0, indices u16 et u32, accessors entrelacés (byteStride), hiérarchie aplatie en transforms monde, matériaux metallic-roughness, alphaMode OPAQUE/MASK (alphaCutoff), KHR_materials_emissive_strength, `.gltf`+`.bin` et `.glb`. TANGENT absent → génération par triangle accumulée par sommet. **Exclus** : skinning, animations, morph targets, sparse accessors, BLEND (rendu opaque + warning), Draco.
- **Images**: StbImageSharp → RGBA8 ; sRGB (baseColor, emissive) vs linéaire (normal, MR, AO) décidé par l'usage, pas par le fichier.

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, Nullable/ImplicitUsings enable, unsafe dans Graphics/Platform uniquement.
- Aucun type Vk*/Silk.NET.Vulkan hors Graphics. Assets ne référence NI Graphics NI Silk.NET.
- Ownership déterministe, ResourceTracker, finalizers conditionnels, hot path zéro alloc.
- Run macOS: `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox`, `AGAPANTHE_MAX_FRAMES=N`.
- Fixtures Khronos : tests/Agapanthe.Tests/Fixtures/ (git-tracked, modèles légers seulement ; DamagedHelmet ~3 Mo acceptable).

## Rollback Point

`4f195a5` (fin session 3 + fixes souris)

## Task Graph

```
W1: M4-01 Assets csproj + fixtures   M4-02 Vertex+Tangent   M4-03 DTO glTF
W2: M4-04 Parser JSON + GLB [dep 01,03]
W3: M4-05 Accessors → géométrie DTO [dep 04]   M4-08 ImageLoader Stb [dep 01]
W4: M4-06 Nœuds aplatis + matériaux [dep 05]   M4-07 Génération tangentes [dep 02,05]
W5: M4-09 Rendering Mesh/Material/Scene/SamplerCache + set 1 PBR [dep 06,07,08]
W6: M4-10 Renderer/ForwardPass [dep 09]
W7: M4-11 Sandbox fixtures + shaders [dep 10]
W8 (tail): M4-12 self review · M4-13 requirements · M4-14 full verification
```

Fichiers partagés : Vertex.cs/Primitives.cs → M4-02 seul. Assets/* → séquence 03→04→05→06/07 (07 fichier séparé TangentGenerator). Rendering nouveaux fichiers → M4-09/10 séquentiels. Program.cs+shaders → M4-11 seul.

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | M4-01 (moi), M4-02, M4-03 | parallèle |
| 2 | M4-04 | seul |
| 3 | M4-05, M4-08 | parallèle (fichiers disjoints) |
| 4 | M4-06, M4-07 | parallèle (fichiers disjoints) |
| 5 | M4-09 | seul |
| 6 | M4-10 | seul |
| 7 | M4-11 | seul |
| 8 | M4-12, M4-13, M4-14 | tail |

## Tâches

### M4-01 — Projet Assets + fixtures Khronos [infra, S] — done — OWNER moi
**Résultat**: Agapanthe.Assets créé (net10.0, TreatWarningsAsErrors, ref Core SEULEMENT — une ref Graphics parasite retirée, StbImageSharp 2.30.15), dans la solution. Fixtures Khronos (glTF-Sample-Assets main) dans tests/Fixtures/ : Box, BoxInterleaved, BoxTextured (+CesiumLogoFlat.png), DamagedHelmet.glb (3.8 Mo) — CopyToOutputDirectory. Doublon de ref Rendering dans Tests.csproj corrigé au passage.

### M4-02 — Vertex + Tangent [code, S] — done — OWNER Vertex.cs/Primitives.cs
**Résultat**: Vertex.Tangent Vector4 (offset 44, location 4, R32G32B32A32Sfloat), stride 60, ctor 4-args conservé (défaut (1,0,0,1)). Primitives.Cube : tangente analytique = arête A→B (direction U), w=+1 (CCW ⇒ cross(N,T) suit +V — vérifié par test). Tests : unit length, ⊥ normale, tangente = direction U + bitangente = +V par face. 91 tests verts, run M3 rend identique (agent interrompu par limite session — Vertex.cs était complet, j'ai fini Primitives+tests).

### M4-03 — DTO glTF [code, S] — done — OWNER Assets/Model/
**Résultat**: namespace Agapanthe.Assets.Model, sealed records + required/init, SoA : ModelAsset {Meshes, Materials, Images, Name} ; MeshAsset {Positions (required), Normals/Tangents/Uvs ([] = absent/à générer), Indices uint[] (required), MaterialIndex -1, WorldTransform, Name} ; MaterialAsset (défauts glTF : BaseColorFactor=1, Metallic=1, Roughness=1, EmissiveStrength=1, Cutoff=0.5 ; slots image -1 = absent, MR packé B/G en un slot) + enum AlphaMode ; TextureSettings readonly record struct {WrapU/V, MinFilter/MagFilter} + enums TextureWrap/TextureFilter (Assets propres, mip collapse Linear/Nearest) ; ImageAsset {Rgba8Pixels, W, H, IsSrgb} ; AssetException {AssetPath, message « Asset '{path}': {reason} »}. Zéro dép Graphics/Silk.NET. Build 0 warning.

### M4-04 — Parser glTF JSON + conteneur GLB [code, M] — done
**Résultat**: Gltf/GltfSchema.cs (POCO internal 1:1 + GltfJsonContext source-generated + InternalsVisibleTo Tests), GlbContainer.cs (magic/version/chunks stricts, slices zéro-copie, LooksLikeGlb), GltfDocument.cs (Load détecte GLB par magic, LoadFromBytes/Stream, GetBufferData avec cache — .bin relatif, data: base64, chunk BIN ; byteLength vérifié). Validation au Load : version 2.x requise, extensionsRequired hors KHR_materials_emissive_strength → AssetException, mode ≠ TRIANGLES → AssetException. **Piège STJ découvert** : le source-gen n'applique PAS les initialiseurs de propriété à la désérialisation → défauts glTF non triviaux (mode 4, metallic 1, cutoff 0.5, wrap 10497…) modélisés en RawXxx nullable + accesseur [JsonIgnore] coalescé — documenté dans le schéma ; M4-05/06 doivent consommer les accesseurs calculés, jamais les RawXxx. 109 tests verts (+18).

### M4-05 — Lecture des accessors → géométrie [code, M] — done
**Résultat**: AccessorReader internal (ReadVec2/3/4 f32, ReadIndices u8/u16/u32 → uint[]). Stride : contigu → MemoryMarshal.Cast en bloc ; entrelacé → boucle Read<T> par slice. Bounds check en long anti-overflow (accessorOffset + (count−1)·stride + elemSize ≤ view.byteLength ≤ buffer). Indices en BinaryPrimitives LE explicite ; fast-path vecteurs suppose hôte LE (documenté). Hors périmètre → AssetException : sparse, normalized non-float, type mismatch, débordement, 5122. Tests : Box == BoxInterleaved élément par élément (positions/normals/indices), min/max référence, mini-glTF inline pour les erreurs. 129 tests verts (+12).

### M4-06 — Hiérarchie de nœuds + matériaux [code, M] — pending
Aplatir scène par défaut : traversée nodes (matrix OU TRS → Matrix4x4, ordre glTF T*R*S en convention row-vector System.Numerics — ATTENTION à l'ordre de multiplication, tester avec un modèle à hiérarchie non triviale), transform monde par mesh instance. Matériaux : mapping schéma → MaterialAsset (metallic-roughness, alphaMode OPAQUE/MASK+cutoff, BLEND → opaque+warning, KHR_materials_emissive_strength), usage sRGB/linéaire déduit du slot. Textures→images (sampler glTF : wrap/filter → SamplerDesc-équivalent DTO). Tests : transforms de référence, matériau DamagedHelmet. AC: tests verts.

### M4-07 — Génération de tangentes [code, M] — pending — OWNER Assets/TangentGenerator.cs
Si TANGENT absent et normal map présente (sinon skip + tangente par défaut) : par triangle (arêtes + deltas UV → T), accumulation par sommet, Gram-Schmidt vs N, w = signe du déterminant UV (handedness), gestion UV dégénérés (fallback axe). Tests : quad plat UV standard → T=(1,0,0,1) ; UVs miroirés → w=-1 ; sphère : T⊥N partout. AC: tests verts.

### M4-08 — ImageLoader StbImageSharp [code, S] — done — OWNER Assets/ImageLoader.cs
**Résultat**: `ImageLoader.Load(path, isSrgb)` + `LoadFromBytes(span, isSrgb, sourceName)` (GLB/data: — ToArray assumé, hors hot path), RGBA8 forcé 4 canaux, isSrgb porté verbatim (décision = slot matériau M4-06/09), toutes erreurs en AssetException avec cause préservée. CesiumLogoFlat.png = 256×256 paletté → 262144 octets RGBA. 117 tests verts (+8, dont décodage d'une tranche de span simulant un chunk GLB).

### M4-09 — Rendering : Mesh/Material/Scene + SamplerCache + set 1 PBR [code, M] — pending
`Mesh` (vertex+index GpuBuffer DeviceLocal via GpuUploader, IndexFormat), `Material` (GpuImages + Sampler partagé via `SamplerCache`, UBO facteurs, set 1 persistant — layout PBR final 6 bindings, placeholders 1×1 blanc/normal-neutre/linéaire pour les slots absents), `MeshInstance { Mesh, Material, Transform }`, `Scene { Instances }`, `SceneBuilder`/factory : ModelAsset → Scene (upload via un GpuUploader unique). `MaterialUniforms` struct (facteurs). Layout set 1 défini UNE fois (constante partagée). AC: build + tests éventuels sans GPU.

### M4-10 — Renderer/ForwardPass [code, M] — pending
Absorbe le câblage : possède pipeline(s), DescriptorAllocator matériaux, set 0 per-frame (caméra UBO per-slot), record loop (bind pipeline, set 0, par instance : set 1 + push constant modèle + draw). API : `Renderer(device, swapchain, shadersDir)`, `DrawScene(Scene, Camera, FrameContext/CommandList)` branché sur FrameRenderer. Sandbox ne référence plus GraphicsPipeline/DescriptorSetLayout directement. AC: build.

### M4-11 — Sandbox fixtures + shaders [code, M] — pending — OWNER Program.cs + shaders
Charge une fixture (arg CLI, défaut DamagedHelmet ; fallback BoxTextured), GltfLoader → SceneBuilder → Renderer.DrawScene. Shaders mesh.vert/mesh.frag : position/normal/uv/tangent consommés (normal/tangent passthrough pour M5 — déclarés mais le frag sample baseColor × facteur, applique alphaCutoff MASK). Caméra cadrée sur le modèle (bounds). AC: run macOS — DamagedHelmet visible texturé, 0 validation, 0 leak.

### M4-12 — Self code review [test, S] — pending
Audits : csharp-lowlevel (parsing robuste, allocs, ownership Rendering) + graphics-3d (correctness géométrie/UV/tangentes, sRGB) + architecte (prêt M5). AC: 0 critique.

### M4-13 — Requirements validation [test, S] — pending
Spec §3.6 + §6 M4 cochées vs code.

### M4-14 — Full verification [test, S] — pending
build 0 warning, dotnet test, run Sandbox DamagedHelmet + BoxTextured clean. Sortie au board.

## Deferred Work

- Feel souris (lissage, sensibilité liée au FOV, courbe de réponse) — remonté par l'utilisateur en S3, à traiter au polish M8.
- MikkTSpace si artefacts de tangentes visibles (spec §3.6).
- alphaMode BLEND, skinning, animations, sparse accessors, Draco — hors phase 1 (spec).
- KTX2/BasisU — phase 2.

## Log

- 2026-07-04: session 4 ouverte. Board S3 archivé. DAG 14 tâches, 8 vagues. Décisions architecte S3 actées (DTO CPU, set 1 PBR figé, SamplerCache, Renderer).
