# Absolute-Human Board — Agapanthe Session 4 (M4 : glTF)

**Status**: completed
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

### M4-06 — Hiérarchie de nœuds + matériaux [code, M] — done (implémenté par l'orchestrateur, agents tués 2× par limite session)
**Résultat**: GltfLoader.Load(path) → ModelAsset. Matrice : les 16 floats colonne-major glTF copiés séquentiellement dans Matrix4x4 = transposée = forme row-vector correcte (documenté + testé sur la matrix Z-up→Y-up de Box : local (0,1,0) → monde (0,0,−1)) ; TRS = CreateScale·CreateFromQuaternion·CreateTranslation, monde = local·parent, garde anti-cycle profondeur 256. Indices absents → séquentiels. Matériaux : BLEND → Opaque+Log.Warn, KHR_emissive_strength, sampler du slot baseColor (fallback premier slot texturé), min filter collapse NEAREST*/LINEAR*. **ImageCatalog** : décodage à la demande dédupliqué par (image glTF, sRGB), images non référencées jamais décodées, double décodage si slot sRGB ET linéaire (documenté) ; sources : uri fichier, data: base64, bufferView GLB. **Intégration M4-07** : tangentes générées quand TANGENT absent + normal map + normals/uvs présents.

### M4-07 — Génération de tangentes [code, M] — done (orchestrateur)
**Résultat**: TangentGenerator.Generate (Lengyel : par-triangle via déterminant UV, skip si |det|<1e-8, accumulation par sommet, Gram-Schmidt, w = signe de dot(cross(N,T), B accumulé), fallback déterministe ⊥N si accumulation dégénérée — jamais de NaN). 7 tests : quad → (1,0,0,+1), U miroiré → (−1,0,0,−1), sphère UV 8×8 unit/⊥/±1, UVs dégénérés → fallback propre, faces opposées partagées sans NaN, préconditions. **Preuve intégrée** : DamagedHelmet (sans TANGENT, avec normal map) → tangentes générées, échantillonnées unit/⊥N/w±1 en test. 142 tests verts. Piège rencontré : File.WriteAllText avec Encoding.UTF8 écrit un BOM que le parser rejette (à juste titre) — tests inline sans BOM.

### M4-08 — ImageLoader StbImageSharp [code, S] — done — OWNER Assets/ImageLoader.cs
**Résultat**: `ImageLoader.Load(path, isSrgb)` + `LoadFromBytes(span, isSrgb, sourceName)` (GLB/data: — ToArray assumé, hors hot path), RGBA8 forcé 4 canaux, isSrgb porté verbatim (décision = slot matériau M4-06/09), toutes erreurs en AssetException avec cause préservée. CesiumLogoFlat.png = 256×256 paletté → 262144 octets RGBA. 117 tests verts (+8, dont décodage d'une tranche de span simulant un chunk GLB).

### M4-09 — Rendering : Mesh/Material/Scene + SamplerCache + set 1 PBR [code, M] — done
**Résultat**: MaterialLayout (set 1 figé : 0-4 combined samplers baseColor/normal/MR/AO/emissive + 5 UBO, constantes + CreateLayout) ; MaterialUniforms 64 o std140 testé aux offsets (BaseColor ; metallic/roughness/normalScale=1/aoStrength=1 ; emissive.rgb+strength ; cutoff+mode ordinal) ; Mesh.Create (SoA→Vertex entrelacé, Color=blanc, défauts N/+Y UV/0 T/(1,0,0,1), narrowing u16 si max<65536 — borné testé) ; Material (UBO seul possédé, set persistant) ; SamplerCache (AddressMode←WrapU documenté, Filter←Mag, MipFilter←Min collapsé) ; Scene possède TOUT (images dédupliquées par l'ImageCatalog → un seul owner, raffinement assumé de la décision 3 : Material ne possède que son UBO — évite le double-free) ; placeholders 1×1 WhiteSrgb/NormalNeutral/LinearWhite ; SceneBuilder.Build avec un GpuUploader unique + cleanup sur échec. Rendering.csproj → ref Assets ajoutée (graphe acté). 154 tests verts (+12), run M3 inchangé.

### M4-10 — Renderer/ForwardPass [code, M] — done
**Résultat**: Renderer(device, swapchain, shaderDir) — possède pipeline mesh (5 attributs, tous consommés par mesh.vert → 0 warning validation), set 0 caméra (Vertex stage, UBO per-slot), MaterialAllocator/MaterialSetLayout exposés pour SceneBuilder, DrawScene(scene, camera, cmd, frame) zéro-alloc (for indexé — l'indexer IReadOnlyList renvoie le struct sans enumerator boxé). **Cull None en M4** (winding glTF à valider par modèle sans éclairage — retour à Back en M5, documenté). mesh.vert : worldNormal = mat3(model) approximation documentée (inverse-transpose → M5) ; mesh.frag : set 1 complet déclaré (superset légal, tous les descripteurs écrits), baseColor×factor×vertexColor + discard MASK. glslangValidator vulkan1.3 OK sur les deux. 154 tests, run M3 inchangé.

### M4-11 — Sandbox fixtures + shaders [code, M] — done — OWNER Program.cs + shaders
**Résultat**: Program réécrit — GltfLoader.Load → SceneBuilder.Build → Renderer.DrawScene (callback hoisté), plus AUCUN objet Vulkan câblé à la main dans le Sandbox. Fixtures copiées à l'output via glob Sandbox.csproj (tests/Fixtures → models/, à plat — uris relatifs des .gltf valides) ; arg CLI = chemin ou nom nu sous models/, défaut DamagedHelmet.glb, exit 2 si introuvable avant toute ressource GPU. Caméra cadrée AABB monde (distance 1.5× diagonale, front surélevé, near/far à l'échelle). Contrôles M3 conservés. **Runs** : DamagedHelmet 120 frames — 15 452 triangles, 5 images, 0 validation, 0 leak (67 ressources), 256 MiB alloués (192 device-local) ; BoxTextured — 12 triangles, 0 validation, 0 leak (53 ressources). EXIT=0 les deux.

### M4-12 — Self code review [test, S] — done (2× PASS, findings MOYENS corrigés)
**csharp-lowlevel PASS (0 critique)** : parsing GLB/accessors robuste prouvé (arithmétique long, counts bornés par la taille réelle du buffer, AssetException systématique), conversion matricielle/narrowing/entrelacement/std140 corrects, ownership sain, DrawScene zéro-alloc confirmé, teardown Sandbox légal. **3 MOYENS corrigés dans la foulée** : (M1) fan-out exponentiel de nœuds — HashSet de visite, arbre strict glTF enforced ; (M2) leak placeholders sur échec partiel — tableau pré-alloué rempli en place ; (M3) indices hors-borne → IndexOutOfRange brute — validation max(indices) < vertexCount avec AssetException ; + (N1) check bufferView image en long + signes. Restent MINEURS documentés : NaN source non filtrés (tangentes), allocations non bornées sur assets non fiables (N4 — à traiter si contenu tiers), LoadFromStream >2 Gio. Revalidé après fixes : 154 tests, run DamagedHelmet 0 validation 0 leak.
**Architecte PASS conditionnel** : axe asset/material/mesh M5-ready ; section « plan M5 » ci-dessous.

### M4-13 — Requirements validation [test, S] — done (tableau ci-dessous)

### M4-14 — Full verification [test, S] — done
**Sortie (2026-07-04)** :
```
Build succeeded.    0 Warning(s)    0 Error(s)
Passed!  - Failed: 0, Passed: 154, Total: 154

DamagedHelmet (défaut, 180 frames) : 1 mesh, 1 material, 5 images, 15452 triangles.
  total: 256.00 MiB allocated across 4 block(s). ResourceTracker: no leaks (67 resources).
BoxTextured.gltf (arg, 120 frames) : 1 mesh, 1 material, 1 image, 12 triangles.
  ResourceTracker: no leaks (53 resources).
Zéro ERROR/WARN/VUID sur les deux runs. EXIT=0.
```
Vérification visuelle (helmet texturé, caméra cadrée, resize) : manuelle par l'utilisateur.

## M4-13 — Validation des exigences (spec §3.6 + §6 M4)

| Exigence | État |
|---|---|
| POSITION/NORMAL/TANGENT/TEXCOORD_0 | ✓ AccessorReader + GltfLoader |
| Indices u16 ET u32 (+u8) | ✓ élargis u32 en DTO, narrowing u16 au GPU si max<65536 |
| Accessors entrelacés (byteStride) | ✓ testé Box vs BoxInterleaved élément par élément |
| Hiérarchie de nœuds aplatie en transforms monde | ✓ matrix (colonne-major→row-vector, testé sur Box Z-up→Y-up) + TRS (S·R·T), monde = local·parent |
| Matériaux metallic-roughness | ✓ MaterialAsset complet, set 1 PBR figé 6 bindings |
| alphaMode OPAQUE et MASK (cutoff) | ✓ + discard dans mesh.frag ; BLEND → opaque + warning (spec) |
| KHR_materials_emissive_strength | ✓ parsé + packé dans MaterialUniforms |
| .gltf+.bin ET .glb | ✓ GLB par magic, buffers fichier/data:/chunk BIN |
| TANGENT absent → génération | ✓ Lengyel accumulé + Gram-Schmidt + handedness, prouvé sur DamagedHelmet (unit/⊥N/±1 en test) |
| Exclusions (skinning/anim/morph/sparse/Draco) | ✓ AssetException explicites (sparse, mode≠4, extensions requises inconnues) |
| sRGB (baseColor/emissive) vs linéaire (normal/MR/AO) | ✓ décidé par slot, dédupliqué par (image, sRGB) |
| Décodage StbImageSharp → device-local + mips blits | ✓ chemin M3 réutilisé (FullMipChain) |
| Fixtures Khronos affichées | ✓ DamagedHelmet + BoxTextured, 0 validation, 0 leak |
| Tests parsing sans GPU (spec §5) | ✓ Assets sans réf Graphics, 154 tests dont parser/accessors/loader/tangentes/images |

## Revue architecte M4-12 (PASS) — plan M5 acté

Axe asset/material/mesh **M5-ready** (set 1 figé, 5 textures bindées, slots UBO réservés — « remplir le shader »). Seul trou : structure de frame mono-passe (identifié dès M3). Décisions démarrage M5, dans l'ordre :

1. **Chantier multi-passes d'abord (le titre de M5)** : `CommandList.BeginRendering/EndRendering` + `TransitionImage` + `SetViewport/Scissor` publics (attachments typés moteur) ; FrameRenderer réduit à acquire/submit/present/sync (garde la transition PresentSrc) ; **depth déplacé au Renderer** (ressource de technique, pas de present). Callback devient `(cmd, frame, swapchainTarget)`. Tonemap dans Rendering, jamais dans Graphics (frontière de couche). Coût estimé ~1-1.5 session, additif, zéro churn M4.
2. **HDR target** Rgba16Sfloat (ColorAttachment|Sampled, taille swapchain) + depth possédés par Renderer, recréés ensemble au resize, clear color migre.
3. **Tonemap fullscreen** : pipeline sans vertex buffer (VertexLayout nullable déjà supporté), Draw(3), ACES/Reinhard + exposition, sortie swapchain sRGB (OETF par le format, pas de gamma manuelle), barrier HDR→ShaderReadOnly entre passes.
4. **Set 0 étendu** : binding 1 UBO lumières (Fragment), caméra Vertex|Fragment, CameraUniforms += position (+16 o). Coût quasi nul — set 0 transient réécrit chaque frame (le YAGNI M2 était le bon call).
5. **mesh.vert** : normal matrix = inverse(transpose(mat3(model))) — 1 ligne, requis avant de valider l'éclairage (mat3(model) faux sous scale non-uniforme ; DamagedHelmet uniforme donc invisible en M4).
6. **MaterialAsset** += NormalScale/OcclusionStrength (schéma les parse déjà), packés dans mrno.z/.w (~4 lignes / 3 fichiers).
7. **Cull Back** (1 ligne) + protocole visuel §5 (docs/visual-checks/) sur DamagedHelmet/FlightHelmet. Risque noté : scale négatif (det<0) inverse le winding — différé tant qu'aucune fixture miroitée.

Dette différable : WorldTransform sur Mesh (instancing → phase 2), SamplerCache WrapV ignoré (si artefact), double décodage sRGB/linéaire (négligeable).

## Deferred Work

- Feel souris (lissage, sensibilité liée au FOV, courbe de réponse) — remonté par l'utilisateur en S3, à traiter au polish M8.
- MikkTSpace si artefacts de tangentes visibles (spec §3.6).
- alphaMode BLEND, skinning, animations, sparse accessors, Draco — hors phase 1 (spec).
- KTX2/BasisU — phase 2.

## Log

- 2026-07-04: session 4 ouverte. Board S3 archivé. DAG 14 tâches, 8 vagues. Décisions architecte S3 actées (DTO CPU, set 1 PBR figé, SamplerCache, Renderer).
- 2026-07-04/05: W1-W7 exécutées (commits ae8761d, 064d96e, 20f6632, 6ccc6fd, de7c7d3, e70592f, f7cfdd0). Deux vagues (W1 partiel, W4) réimplémentées par l'orchestrateur après agents tués par limites de session. M4-12 : 2× PASS, 3 findings MOYENS corrigés immédiatement. M4-13/14 : 154 tests, DamagedHelmet 15 452 tris + BoxTextured, 0 validation, 0 leak. **Session 4 close — M4 atteint.** M5 : plan multi-passes HDR+tonemap acté (7 décisions, section revue architecte).
