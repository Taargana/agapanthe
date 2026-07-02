# Agapanthe — Spec de design : Phase Graphique 3D

**Date** : 2026-07-02
**Statut** : approuvé (interview absolute-brainstorm)
**Portée** : fondations du moteur + renderer Vulkan complet jusqu'à la scène PBR (glTF, multi-lumières, ombres, IBL). Hors portée : ECS, audio, physique, scripting, éditeur, multi-backend.

## 1. Résumé

Agapanthe est un moteur de jeu C# cross-platform (Windows, Linux, macOS) construit sur Vulkan via Silk.NET. Philosophie : bindings existants, le reste from scratch — allocateur GPU, renderer, loader glTF. La phase 1 livre un renderer forward PBR complet, validé visuellement dans une app Sandbox, avec une discipline stricte de gestion mémoire (destruction déterministe, zéro leak, zéro allocation managée par frame).

## 2. Décisions et justifications

| Décision | Choix | Justification |
|---|---|---|
| Bindings | Silk.NET (Vulkan, Windowing, Input, Shaderc) | Écosystème complet .NET Foundation, API 1:1 avec la spec, cross-platform |
| Baseline GPU | Vulkan 1.2 + `VK_KHR_dynamic_rendering` + `VK_KHR_synchronization2` | Intersection MoltenVK (machine de dev macOS) / drivers desktop |
| Maths | System.Numerics + helpers | SIMD gratuit, interop direct GPU ; le from-scratch se concentre où il a de la valeur |
| Shaders | GLSL → SPIR-V runtime (shaderc) + cache disque + hot reload | Boucle d'itération renderer optimale |
| Abstraction | Couche GPU mince mono-backend | Isole Vulkan sans sur-ingénierie multi-backend |
| Images | StbImageSharp | Décoder PNG/JPEG à la main = semaines orthogonales au rendu |
| glTF | Loader from scratch | System.Text.Json + parsing binaire, faisable et formateur |
| Runtime | .NET 10, xUnit | LTS courant |
| Scène | Structure minimale, pas d'ECS | L'organisation des données est une décision de phase 2 |

**Spécifique macOS (obligatoire dès M0)** : instance créée avec `VK_KHR_portability_enumeration` + flag `VK_INSTANCE_CREATE_ENUMERATE_PORTABILITY_BIT_KHR` ; si le device expose `VK_KHR_portability_subset`, l'activer et ne pas dépendre des features absentes du subset. Sans cela : `VK_ERROR_INCOMPATIBLE_DRIVER` avec les loaders récents.

## 3. Architecture

### 3.1 Modules et dépendances

```
Sandbox ──► Rendering ──► Graphics ──► Core
              │              ▲
Assets ───────┘──────────────┘
Platform ──► Core
```

- **Agapanthe.Core** : `MathHelpers` (projections clip-space Vulkan Y↓/Z[0,1], frustum, look-at), logging, `ResourceTracker` (debug).
- **Agapanthe.Platform** : `EngineWindow` (wrap Silk.NET.Windowing), input, boucle de frame avec timing. Ne connaît pas Vulkan (expose les extensions d'instance requises et la création de surface via callbacks opaques).
- **Agapanthe.Graphics** : seul projet référençant Silk.NET.Vulkan. Types publics : `GraphicsDevice`, `Swapchain`, `GpuBuffer`, `GpuTexture`, `Sampler`, `GraphicsPipeline`, `ComputePipeline`, `CommandList`, `GpuAllocator`, `FrameContext`, `DeletionQueue`, `ShaderModule`, `DescriptorAllocator`. Aucun type `Vk*` dans les signatures publiques.
- **Agapanthe.Rendering** : `Renderer` (orchestration des passes), passes forward PBR / shadow / skybox / tone map, générateur IBL, `Material`, `Mesh` (GPU), `Camera`, `Light`, `Scene` (listes de `MeshInstance`, lumières, caméra).
- **Agapanthe.Assets** : `GltfLoader` (from scratch), `ImageLoader` (StbImageSharp), `ShaderCompiler` (shaderc + cache SPIR-V + hot reload).
- **samples/Sandbox** : app de validation visuelle, caméra libre, chargement de modèles Khronos samples.
- **tests/Agapanthe.Tests** : xUnit.

### 3.2 Cycle de vie et mémoire (règles non négociables)

1. Toute ressource GPU implémente `IDisposable`. **`Dispose()` ne détruit jamais immédiatement un handle potentiellement utilisé par une frame en vol : il l'enfile dans la `DeletionQueue` du device**, qui détruit à `frameIndex + FramesInFlight`. « Déterministe » = déterministe à N+2 frames, jamais dépendant du GC. Les destructions au shutdown (après `WaitIdle`) sont immédiates.
2. Aucun finalizer ne détruit de handle Vulkan ; en debug, un finalizer signale la fuite (log + compteur) sans rien détruire.
3. `ResourceTracker` (compilé en debug) : enregistre création/destruction par type, rapport à la fermeture — un objet vivant = échec du run.
4. Ownership : `GraphicsDevice` possède allocateur, swapchain, pools, DeletionQueue ; `Scene`/`Material` possèdent leurs ressources GPU ; le Renderer ne possède que ses targets internes (HDR, shadow maps, maps IBL).
5. Hot path (par frame) : zéro allocation managée. Buffers réutilisés, structs, `Span<T>`. Vérifié par audit csharp-lowlevel à chaque jalon.
6. Delegates passés au natif (debug callback) : gardés vivants explicitement.

### 3.3 Modèle de frame et synchronisation

- 2 frames in flight ; `FrameContext` par frame : command buffer, uniform buffers, semaphore image-available, fence in-flight.
- **Semaphores render-finished : un par image de swapchain** (pas par frame), pour respecter les règles de réutilisation WSI.
- Semaphores binaires partout ; pas de timeline semaphores en phase 1 (YAGNI — acquire/present les interdisent de toute façon).
- `synchronization2` pour toutes les barriers/submits.
- Swapchain : sur `OUT_OF_DATE`/resize → `vkDeviceWaitIdle` puis recréation complète (simple et suffisant en phase 1 ; destruction différée = optimisation ultérieure). Formats sRGB préférés, present mode FIFO par défaut (Mailbox en option).
- Rendu via `dynamic_rendering` uniquement (pas de `VkRenderPass`).
- Passes de la frame : shadow pass (depth-only) → forward PBR (HDR `R16G16B16A16_SFLOAT`) → skybox → tone mapping vers swapchain.
- `CommandList` supporte dispatch compute (requis pour la génération IBL, §3.6).

### 3.4 Descriptors et binding

- **Set 0 — per-frame** (rebindé une fois par frame) : UBO caméra (view/proj/position), UBO lumières, maps IBL + shadow map (samplers). Le layout complet existe dès M2 ; shadow map et maps IBL sont des textures placeholder 1×1 jusqu'à M6/M7.
- **Set 1 — per-material** : textures baseColor/normal/metallicRoughness/AO/emissive + UBO facteurs matériau. Alloué au chargement du matériau, immuable ensuite.
- **Push constants — per-draw** : matrice modèle seule (64 octets) ; la normal matrix est dérivée dans le vertex shader — garde la moitié du budget minimum garanti (`maxPushConstantsSize` = 128). Pas de set per-object en phase 1.
- `DescriptorAllocator` : pools créés à la demande (grow). **Un pool per-frame par `FrameContext`**, reset quand la fence de cette frame est signalée (jamais de reset d'un pool encore utilisé par une frame en vol) ; les sets per-material vivent dans un pool persistant.
- Layouts de sets et pipeline layouts créés une fois et cachés (dictionnaire par description).

### 3.5 GpuAllocator (from scratch)

- Suballocation : gros blocs `vkAllocateMemory` (64-256 Mo) découpés en allocations alignées (free-list par bloc), par memory type.
- Trois usages : `DeviceLocal`, `HostVisible` (uniform/staging persistants mappés), `Staging` (transient).
- **Seam de test : la logique free-list/blocs est implémentée contre une interface `IMemoryBackend`** (alloue/libère des « blocs » opaques) — implémentation Vulkan en prod, mock en tests xUnit sans GPU.
- Stats debug : octets alloués/utilisés par heap, nombre de blocs, fragmentation.
- Dedicated allocation pour les grosses images (render targets).

### 3.6 Chemin des assets

**glTF 2.0** (`.gltf` + `.bin`, `.glb`) :
- Supporté : POSITION/NORMAL/TANGENT/TEXCOORD_0, indices u16 **et** u32, accessors entrelacés (`byteStride`), hiérarchie de nœuds aplatie en transforms monde, matériaux metallic-roughness, `alphaMode` OPAQUE et MASK (`alphaCutoff`), `KHR_materials_emissive_strength`.
- **TANGENT absent** (fréquent dans les modèles Khronos) : génération de tangentes à l'import (méthode par triangle accumulée par sommet, suffisant en phase 1 ; MikkTSpace si artefacts).
- Exclu explicitement : skinning, animations, morph targets, sparse accessors, `alphaMode` BLEND (rendu opaque avec warning), Draco.

**Images / espaces colorimétriques** (critique pour la conformité PBR) :
- baseColor, emissive → formats **sRGB** (`R8G8B8A8_SRGB`).
- normal, metallicRoughness, AO → formats **linéaires** (`R8G8B8A8_UNORM`).
- Décodage StbImageSharp → staging → image device-local + mipmaps générées par blits.

**Shaders** :
- GLSL, `#include` maison simple. Cache disque keyé par **hash du source résolu après expansion des includes**.
- Hot reload : watch du shader **et de tous ses includes** (le résolveur d'includes retourne la liste des fichiers touchés) → recompilation → recréation pipeline à la frame suivante, l'ancien part en DeletionQueue. Échec de compilation : log, l'ancien pipeline reste actif.

**IBL** (généré au chargement de l'environnement, via compute shaders, mis en cache disque keyé par hash de la HDRI) :
- equirect HDR → cubemap environnement → irradiance map (diffuse) → prefiltered specular (mips par roughness) → BRDF LUT (générée une fois, indépendante de l'environnement).

## 4. Gestion des erreurs

- Tout `VkResult` non-success → exception `GraphicsException` avec le nom de l'appel (pas de codes ignorés). `SUBOPTIMAL_KHR`/`OUT_OF_DATE_KHR` : chemin de recréation swapchain, pas une erreur.
- Validation layers en debug : messages routés vers le log ; `ERROR` = crash immédiat en debug (fail fast).
- Échec de compilation shader en hot reload : log de l'erreur shaderc, l'ancien pipeline reste actif (pas de crash).
- Asset introuvable/corrompu : exception au chargement avec chemin + raison ; pas de fallback silencieux.
- Device lost : non récupéré en phase 1 — crash propre avec diagnostic.

## 5. Stratégie de test

- **Unitaires (xUnit, sans GPU)** : maths (projections Vulkan vs valeurs de référence, frustum), GpuAllocator (free-list via mock `IMemoryBackend` : alignement, coalescing, out-of-block), parsing glTF (fixtures : Box, BoxInterleaved, DamagedHelmet, modèles sans TANGENT).
- **Validation runtime** : chaque jalon = Sandbox lancé sur macOS, zéro message de validation, rapport ResourceTracker vide à la fermeture.
- **Multi-OS** : machines perso Windows/Linux au minimum aux jalons M1, M4, M8.
- **Visuel (M5+)** : protocole — modèles DamagedHelmet, FlightHelmet, MetalRoughSpheres ; capture Sandbox et capture du viewer glTF de référence Khronos au même angle/exposition, comparaison côte à côte, revue humaine documentée dans `docs/visual-checks/` (une image annotée par jalon).

## 6. Jalons

Chaque jalon se termine par : Sandbox tourne, validation layers propres, rapport ResourceTracker vide.

| # | Livrable | Critère de sortie |
|---|---|---|
| M0 | Solution, fenêtre, instance (+portability macOS) + device + swapchain, ResourceTracker | Fenêtre s'ouvre/resize/ferme proprement sur macOS |
| M1 | Triangle (pipeline, shaderc, sync frames-in-flight) | Triangle affiché, resize OK, zéro leak |
| M2 | Mesh 3D, depth, UBO, descriptors (sets 0/1, push constants), caméra libre | Cube en 3D, tests maths verts |
| M3 | GpuAllocator, staging, textures+mipmaps (sRGB/linéaire) | Mesh texturé, tests allocateur verts, stats mémoire visibles |
| M4 | Loader glTF, scène, génération tangentes | Fixtures Khronos affichées (géométrie+textures correctes) |
| M5 | PBR + lumières (1 dir. + N ponctuelles) + tone mapping | Protocole visuel §5 validé sur DamagedHelmet |
| M6 | Shadow mapping directionnel : depth `D32_SFLOAT` 2048², slope-scaled bias, PCF 3×3, 1 cascade (CSM = phase 2) | Ombres stables caméra en mouvement, pas d'acné visible |
| M7 | IBL complet (compute) : cubemap, irradiance, prefiltered, BRDF LUT + skybox | Protocole visuel §5 sur MetalRoughSpheres (rangée metallic correcte) |
| M8 | Hot reload shaders (+includes), debug labels RenderDoc, audit perf/leaks | Édition shader à chaud < 1s, audit csharp-lowlevel sans finding critique |

## 7. Exécution

Agents : `engine-architect` (revues M0/M3/M8, API Graphics), `graphics-3d` (M1→M7), `csharp-lowlevel` (interop M0, allocateur M3, audits). Exécution recommandée via absolute-human (décomposition, vagues parallèles, TDD).
