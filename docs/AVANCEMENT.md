# Agapanthe — Plan complet & état d'avancement

**Mis à jour** : 2026-07-12 (fin session 8 — **PHASE 1 CLOSE**) · **Machines de dev** : macOS (Apple M3, MoltenVK) + Windows 11 (RTX 5070 Ti, Vulkan 1.3 core) · **Cibles** : Windows / Linux / macOS

## Vision

Moteur de jeu Vulkan en C# from scratch. **Phase 1 (TERMINÉE)** : toute la chaîne graphique 3D, d'une fenêtre vide à une scène PBR complète — glTF, metallic-roughness, multi-lumières, ombres, skybox/IBL, hot reload shaders. **Phase 2 (à venir)** : ECS/scene graph, audio, physique, gameplay.

Spec de référence : [docs/plans/2026-07-02-graphics-engine-design.md](plans/2026-07-02-graphics-engine-design.md) · Suivi de session : [.absolute-human/board.md](../.absolute-human/board.md) (+ archives par session)

## Décisions structurantes (verrouillées)

| Sujet | Choix |
|---|---|
| Bindings | Silk.NET (Vulkan + GLFW + input) — le reste from scratch |
| Baseline GPU | Vulkan 1.2 + dynamic_rendering + synchronization2 (chemin 1.3 core sur MoltenVK) |
| Maths | System.Numerics (convention row-vector) + helpers clip-space Vulkan (Y-flip, Z [0,1]) |
| Shaders | GLSL → SPIR-V à l'exécution (shaderc), hot reload prévu M8 |
| Abstraction GPU | Couche mince mono-backend — aucun type `Vk*` ne sort de `Agapanthe.Graphics` |
| Mémoire GPU | Allocateur from scratch (free-list par blocs 64 MiB, dedicated au-delà de 32 MiB) |
| Assets | glTF 2.0 parsé from scratch, StbImageSharp pour les images ; DTO CPU sans dépendance GPU |
| Discipline mémoire | IDisposable partout, destruction différée N+2 frames (DeletionQueue non-capturante), zéro alloc managée par frame, ResourceTracker (leak = échec du run) |
| Qualité | Tout message de validation layer = bug. xUnit sans GPU pour maths/allocateur/parsing |
| Runtime | .NET 10, TreatWarningsAsErrors |

## Modules

```
Sandbox ──► Rendering ──► Graphics ──► Core
                │              (seul projet référençant Silk.NET.Vulkan)
                └────► Assets ──► Core   (GPU-free : parsing testable sans GPU)
Platform ──► Core   (fenêtre GLFW, input, capture souris)
```

## Jalons — état

| # | Livrable | État | Session |
|---|---|---|---|
| M0 | Fenêtre, instance/device/swapchain, ResourceTracker | ✅ | S1 |
| M1 | Triangle (pipeline, shaderc runtime, frames-in-flight) | ✅ | S1 |
| M2 | Mesh 3D : depth, descriptors, UBO caméra, push constants, caméra libre | ✅ | S2 |
| M3 | GpuAllocator, staging uploads, textures + mipmaps + samplers | ✅ | S3 |
| M4 | Loader glTF, tangentes, Scene/Material/Renderer, fixtures Khronos | ✅ | S4 |
| M5 | PBR Cook-Torrance + 3 lumières HDR + ACES tone mapping | ✅ validé visuellement | S5 |
| M6 | Shadow mapping directionnel (D32 2048², PCF 3×3 manuel, slope-scaled bias) | ✅ | S6 |
| M7 | IBL compute (cubemap, irradiance, prefiltered, BRDF LUT) + skybox | ✅ validé visuellement | S7 |
| M8 | Hot reload shaders (+includes), labels RenderDoc, confort souris, audit final | ✅ validé (hot reload live < 1 s) | S8 |

**→ PHASE 1 CLOSE (8/8 jalons).** Chaque jalon a clos sur : Sandbox propre (0 message validation, 0 leak), tests verts, double audit agent (csharp-lowlevel + architecte) PASS, board archivé.

## État courant (fin session 8 — Phase 1 close)

**Ce qui tourne** : `dotnet run --project samples/Sandbox` → DamagedHelmet en **PBR complet + IBL + ombres** (Cook-Torrance GGX, normal mapping, AO, emissive, 3 lumières HDR, shadow mapping directionnel PCF, **IBL image-based** — irradiance diffuse + prefiltered specular + BRDF LUT — **skybox** environnement, tone mapping ACES), caméra libre 6DOF (lissage exponentiel dt-indépendant), capture souris OS-confinée, **hot reload des shaders à chaud (< 1 s)**, **labels RenderDoc** sur les passes. Contrôles : +/− exposition, L pivote la lumière clé, N cycle 9 vues debug, PageUp/Down/Home/End sensibilité. `dotnet run … -- MetalRoughSpheres.glb` pour la grille metallic×roughness ; `AGAPANTHE_HDRI=<path.hdr>` change l'environnement.
*(macOS : préfixer `DYLD_LIBRARY_PATH=/opt/homebrew/lib`.)*

**Debug headless** : `AGAPANTHE_CAPTURE=sortie.ppm` dump le target HDR tonemappé, `AGAPANTHE_VIEW="x,y,z"` reproduit un angle caméra, `AGAPANTHE_MAX_FRAMES=N` auto-ferme, `AGAPANTHE_IBL_TEST=<préfixe>` génère l'IBL et dump les faces/maps, `AGAPANTHE_SHADER_RELOAD_TEST=1` force un reload des 4 passes et logge le wall-time (mesure du budget < 1 s sans fenêtre). GpuReadback + Renderer.SaveHdrCapture.

**Métriques (fin Phase 1)** : 58 commits · 100 fichiers C# · ~13 200 lignes (src+samples) · **205 tests xUnit** · 15 shaders GLSL · **14 audits agents (2 par jalon M2-M8), tous PASS** · gate permanent 0 warning / 0 message de validation / 0 leak.

**Acquis techniques clés** :
- Allocateur GPU testé sans GPU (seam `IMemoryBackend`), stats mémoire au shutdown
- DeletionQueue zéro-allocation (payload 4×ulong + destructeurs statiques, offset+memType bit-packés 40/24) — **tout** passe par elle : images, buffers, pipelines, shader modules
- Upload staging synchrone explicite (jamais de submit caché) + chaîne de mips par blits
- Assets 100 % CPU : glTF/GLB source-gen STJ, matrices colonne-major→row-vector prouvées par test, génération de tangentes Lengyel prouvée sur DamagedHelmet
- Multi-passes : CommandList.BeginRendering/TransitionImage publics, FrameRenderer = pur frame-sync, chaîne scène→HDR Rgba16Sfloat→ACES→swapchain sRGB (fix WAR sur l'HDR partagée entre frames in flight)
- Shader PBR : GGX + Smith height-correlated + Schlick, TBN avec fallback anti-NaN, atténuation KHR_lights_punctual, std140 triple-vérifié (C# ↔ GLSL par réflexion)
- **Seam par-passe** (M8) : `Passes/` — chaque passe possède le volatil (shaders + pipeline + desc-template + fichiers source résolus), le Renderer garde le stable + les `Record*`. Le God-object est résorbé ; c'est ce qui rend le hot reload possible.
- **Hot reload** (M8) : résolveur `#include` maison → cache disque keyé par le hash du source **résolu** (atomique + self-healing) → watcher sur le dossier source → recréation du pipeline au bord de frame, ancien en DeletionQueue. Échec de compile = log + ancien pipeline conservé.

**Leçon de guerre M5 (front face)** : le culling supprimait les faces avant — `FrontFace.Clockwise` venait d'un calcul de winding omettant le signe moins de la formule Vulkan (qui compense le Y-down du framebuffer). Avec le Y-flip baké dans la projection, glTF CCW = CCW visuel = CCW Vulkan → `CounterClockwise`. Invisible sur M2 (cube convexe fermé non éclairé ≈ identique en culling inversé) et M4 (Cull None). Diagnostic par captures headless comparées (bug → Cull None → fix).

**Leçon de guerre M8 (le protocole humain trouve ce que les audits ratent)** : deux audits agents PASS n'avaient pas vu que le Renderer loggait « hot-reloaded » **même quand la compilation échouait** (`Reload` était `void` et avalait l'exception → l'appelant ne pouvait pas distinguer succès et échec). Il a fallu une **session réelle à la fenêtre**, avec un vrai shader cassé, pour que le log se contredise à l'écran. Le comportement était correct ; c'est l'*observabilité* qui mentait. Les audits lisent le code, le protocole humain lit les symptômes.

## Fin session 7 — M7 livré (IBL & skybox)

**Verdict visuel PASS** (revue humaine 2026-07-10) : helmet réfléchit l'environnement + ciel visible, MetalRoughSpheres rangée metallic net→flou correcte. L'IBL remplace l'ambiant constant — remède au métal sombre en place.

**Livré en 5 vagues** :
- Graphics (S6) : ImageUsage.Storage, DescriptorKind.StorageImage, GpuImage cubemap (ViewType.Cube + vues par mip/face via CreateMipView, possédées), ComputePipeline + CommandList.Dispatch, GraphicsDevice.SubmitImmediate.
- Assets : HdrImageLoader (Radiance .hdr float via StbImageSharp), HdrImageAsset.
- W3 : **IblGenerator** (4 kernels compute equirect→cube 512² / irradiance 32² / prefiltered 128²×8 mips GGX importance-sample / BRDF LUT 512² RG16F Karis) en un seul SubmitImmediate, **IblMaps** disposable. Gén. ~135 ms.
- W4 : **skybox** (triangle plein-écran far-plane fusionné dans le scope scène, DepthTest LessOrEqual sans Write), set 0 bindings 3/4/5 + **ambiant IBL** dans mesh.frag (kd·irradiance·albedo + prefilteredLod(R, roughness·maxMip)·(F0·brdf.x+brdf.y), Fresnel roughness-aware, ×AO).
- W5/W6 : override AGAPANTHE_HDRI, captures docs/visual-checks, audits (0 critique, 3 findings mémoire + 1 archi corrigés).

**Acquis techniques M7** :
- Half-float pour l'équirect stagé (MoltenVK ne filtre pas linéairement le 32-bit float) ; ToHalf clampe Half.MaxValue + scrub NaN (HDRI brillants → +Inf sinon).
- `ImageLayoutState.ShaderReadOnlyCompute` = ShaderReadOnlyOptimal mais stage compute (hand-off env→kernels lecteurs compute→compute).
- `TransitionImage(GpuImage)` full-subresource (mips×layers) ; no-op pour les cibles 1/1 pré-M7.
- IblGenerator réutilisable (pipelines/layouts/samplers) ; Generate() possède le transitoire (equirect, uploader, pool descripteurs) ; try interne libère sur échec (finding M1).

**Découverte plateforme** : rien de nouveau côté MoltenVK au-delà du half-float ; imageCubeArray évité (vues 2D-array par face, un seul cube).

## Fin session 8 — M8 livré (hot reload, labels, confort, audit final) → **PHASE 1 CLOSE**

**Verdict humain PASS with concerns** (2026-07-12, Windows/RTX 5070 Ti) — protocole : [docs/visual-checks/2026-07-12-m8-hot-reload.md](visual-checks/2026-07-12-m8-hot-reload.md).

**Critère de sortie (spec §6) TENU** : édition d'un shader à chaud, app tournante → **224 ms au pire (1re compile, cache froid), ~2 ms ensuite** (≪ 1 s) · audit csharp-lowlevel **0 finding critique**. L'échec de compilation se comporte comme exigé (§4) : l'app ne crashe pas, le rendu est conservé, l'erreur shaderc est précise, la correction recharge normalement. 0 validation, 0 leak (157 ressources) malgré 4 reloads.

**Livré en 4 vagues + tail** :
- W0 (archi) : `PipelineLayoutBuilder` partagé · **destruction différée des pipelines et shader modules** (ils étaient détruits immédiatement — prérequis non anticipé du hot reload) · résolveur `#include` + clé de cache = hash du source résolu · API `CommandList` debug labels (no-op Release-safe, UTF-8 alloc-free).
- W1 (le nœud) : **seam par-passe** — `Passes/` avec `IReloadablePipeline` + base `ReloadablePass` + Shadow/Scene/Skybox/Tonemap + `IblResources`. Ctor du Renderer : 178 → 90 lignes. **Refactor byte-identique.**
- W2 : **hot reload** — `ShaderHotReloader` (watcher sur le dossier *source*, callback sans Vulkan, debounce) + `Renderer.PollShaderReload()` (early-out zéro-alloc) appelé avant `DrawFrame`.
- W3 : debug labels sur les 4 passes + les 4 kernels IBL · confort souris (lissage exponentiel dt-indépendant, sensibilité ∝ FovY).
- Tail : 2 audits **PASS** · durcissement des 3 findings MEDIUM · fix du log mensonger trouvé par le protocole humain.

**Acquis techniques M8** :
- L'invariant de sûreté du reload a été **démontré** (audit) : l'ancien pipeline est détruit à N+2 alors que son dernier usage possible est N-1 → **marge d'une frame entière**. Il tient *parce que* le reload se fait au bord de frame, avant tout recording.
- Cache `.spv` **atomique** (write-tmp + move) et **self-healing** (blob tronqué → recompile) : un process tué pendant l'écriture ne peut plus empoisonner tous les runs suivants.
- Un seul comparateur de chemins OS-aware pour tout le système (le hot reload en dépend sur les FS sensibles à la casse).

## Dette d'ouverture Phase 2 (issue de M8)

Détail complet : [.absolute-human/archive/board-session8-M8.md](../.absolute-human/archive/board-session8-M8.md) → « Dette d'ouverture PHASE 2 ».

**Validations manquantes** (trous de preuve, pas des défauts constatés) :
- 🔴 **Linux jamais validé** (rattrapage M4, toujours dû). Pas de machine disponible le 2026-07-12 → trou **assumé** pour ne pas bloquer la clôture. Le fix du comparateur de chemins OS-aware est couvert par un test unitaire qui *simule* la sensibilité à la casse, mais **n'a jamais tourné sur un vrai Linux**, pas plus que le watcher inotify. **Premier item dès qu'une machine Linux est disponible.**
- 🟠 Labels RenderDoc **non observés** dans RenderDoc (émission garantie par construction).
- 🟠 Feel souris **non jugé** à la main (lissage correct par construction).

**Findings d'audit non corrigés (hors périmètre M8)** :
- Invariant du reload garanti par **convention** seulement : `PollShaderReload()` est public ; un appelant qui l'invoquerait en cours de recording détruirait un pipeline in-flight silencieusement → poser une garde debug.
- **Crash rare au shutdown** (`0xC0000005` dans `GlfwEvents.Dispose` de Silk.NET) : observé 1 fois, **non reproductible** (12 runs → 0 repro). N'affecte ni le rendu ni les ressources GPU mais **masque le rapport ResourceTracker** quand il frappe. Ne pas patcher à l'aveugle.

**Limites actées du hot reload** : éditions **interface-compatibles** uniquement (changer un binding → set-layout figé → validation error, restart requis) · les 4 shaders **compute IBL ne sont pas surveillés**.

**Dette héritée (phase 2)** : immutable samplers (comparateur hardware) avec CSM · parsing `KHR_lights_punctual` depuis le glTF · texel-snapping des ombres · prefilter env single-mip (fireflies possibles sur HDRI contrasté) · instancing multi-mesh · MikkTSpace si artefacts · auto-exposure · upload async.

## Reprise — où repartir

**Point de reprise** : **Phase 1 close, 8/8 jalons.** M8 livré (session 8), board archivé → `.absolute-human/archive/board-session8-M8.md`. Rien en cours.

**⚠️ L'arbre de travail M8 n'est PAS committé** (l'humain pilote les commits) : 14 fichiers modifiés + 6 nouveaux (`PipelineLayoutBuilder.cs`, `ShaderIncludeResolver.cs`, `ShaderHotReloader.cs`, `Passes/`, 2 fichiers de tests). Premier geste d'une reprise : décider du/des commit(s) M8.

**Run de sanity** (doit donner 0 validation / 0 leak) :
```powershell
$env:AGAPANTHE_MAX_FRAMES=3; $env:AGAPANTHE_CAPTURE="check.ppm"; dotnet run --project samples/Sandbox
```
*(macOS : `DYLD_LIBRARY_PATH=/opt/homebrew/lib AGAPANTHE_MAX_FRAMES=3 AGAPANTHE_CAPTURE=/tmp/check.ppm dotnet run --project samples/Sandbox`)*

**Pour ouvrir la Phase 2** : ECS/scene graph, audio, physique, gameplay. Commencer par un passage `engine-architect` sur le découpage ECS vs scene graph et son articulation avec le `Renderer` actuel (qui prend aujourd'hui une `Scene` en entrée). Traiter la dette d'ouverture ci-dessus en préalable — **en priorité la validation Linux** si une machine devient disponible.
