# Agapanthe — Plan complet & état d'avancement

**Mis à jour** : 2026-07-13 (session 10 — **PHASE 2 EN COURS**, **P2-M1 CLOS** — chemin SPIR-V hors-ligne livré, double audit PASS) · **Machines de dev** : macOS (Apple M3, MoltenVK) + Windows 11 (RTX 5070 Ti, Vulkan 1.3 core) · **Cibles** : Windows / Linux / macOS

## Vision

Moteur de jeu Vulkan en C# from scratch. **Phase 1 (TERMINÉE, 8/8)** : toute la chaîne graphique 3D, d'une fenêtre vide à une scène PBR complète — glTF, metallic-roughness, multi-lumières, ombres, skybox/IBL, hot reload shaders. **Phase 2 (EN COURS)** : transformer le viewer en moteur — fondations scalables (ECS, coordonnées grande échelle, culling, AOT). Physique/audio/gameplay = phases ultérieures.

Spec Phase 1 : [docs/plans/2026-07-02-graphics-engine-design.md](plans/2026-07-02-graphics-engine-design.md) · **Spec Phase 2** : [docs/plans/2026-07-12-phase2-foundations-design.md](plans/2026-07-12-phase2-foundations-design.md) · Suivi de session : [.absolute-human/board.md](../.absolute-human/board.md) (+ archives par session)

## Phase 2 — Fondations scalables (EN COURS)

**Cadre** (spec Phase 2) : la Phase 1 a livré un **viewer**, pas un moteur (`Mesh.WorldTransform` figé → rien ne bouge ; `Scene` mélange possession GPU et draw list). La Phase 2 pose **les fondations qui ne se retrofitent pas** : ECS (**Arch**), coordonnées monde `double` + camera-relative rendering, couture render-list (handles, pas de types GPU dans le monde), frustum culling. **Cible de sortie** : des milliers d'entités qui bougent, cullées, **à 10 000 km de l'origine sans trembler**, en NativeAOT, 0 leak. Horizon lointain (non construit ici, mais non condamné) : univers persistant streamé multi-serveurs.

**Deux règles obligatoires nouvelles (rétroactives Phase 1)** : (1) code **AOT-pur** (`IsAotCompatible` sur les libs, `PublishAot` Sandbox, warnings IL = erreurs) ; (2) **chemin SPIR-V hors-ligne** (shaderc = luxe de dev, prod = précuit).

**Jalons Phase 2** :
| # | Livrable | État |
|---|---|---|
| P2-M0 | Gate AOT + verdict Arch | ✅ **PASSÉ** (S9) — double audit PASS, Arch validé |
| P2-M1 | Chemin SPIR-V hors-ligne | ✅ **PASSÉ** (S10) — double audit PASS, prod sans shaderc (prouvé Windows AOT) |
| P2-M2 | `Double3` + camera-relative + couture ECS render-list | **prochain** |
| P2-M3 | (camera-relative / ECS — voir spec §6) | à venir |
| P2-M4 | Frustum culling + montée en charge (= critère de sortie) | à venir |
| P2-M5 | Audits + clôture | à venir |

**P2-M0 (gate AOT) — acquis clés** :
- Le Sandbox **publie et tourne en NativeAOT** (binaire natif 4,3 Mo, capture **byte-identique à M8**). Prérequis : PATH avec `vswhere` (VS Installer) sinon le linker ILC échoue (code 123, non lié à l'AOT).
- **Risque n°1 (Silk.NET.Windowing par réflexion) matérialisé puis résolu** : trimmé par l'AOT → `PlatformNotSupportedException` → fix par enregistrement explicite `GlfwWindowing/GlfwInput.RegisterPlatform()` (ctor statique EngineWindow).
- **Verdict Arch : GO** pour l'ECS (World/Query/ParallelQuery/System validés en vrai AOT). **Arch.Persistence : NO-GO** (incompat binaire + MessagePack alpha/CVE + Utf8Json abandonné) → sérialisation **maison source-gen** en phase ultérieure.
- **Contrainte AOT dure pour P2-M2 (les 2 audits convergent, pas de la dette molle)** : Arch instancie les tableaux de composants `T[]` par voie générique que l'ILC ne pré-génère pas → échec runtime `'T[]' is missing native code` **SANS warning au publish**, pouvant frapper `Add`/`CommandBuffer` et **corrompre l'état partiellement**. → le **registre de composants doit être source unique** et **générer lui-même le rooting** (`new T[1]` par type, source-gen), gardé par un **test qui tourne sous AOT**. Converge avec la sérialisation maison → **un seul générateur**.
- EngineWindow **retiré du ResourceTracker** (objet plateforme ≠ ressource GPU) + rapport 0-leak émis **avant** le teardown GLFW → le crash Silk.NET au shutdown (M8-14, upstream) ne peut plus masquer le gate.
- **AOT prouvé Windows UNIQUEMENT** → à re-prouver Linux/macOS.

**P2-M1 (SPIR-V hors-ligne) — LIVRÉ (S10)** :
- **W1** : `ShaderCompiler` charge shaderc **paresseusement** (au 1er vrai miss, pas au ctor) + fix collatéral d'un gate de fuites flaky (collection xUnit non-parallélisable). Les 2 instances (Renderer + IblGenerator) couvertes.
- **W2** : mode **cache-only** (`precompiledOnly` — miss = `GraphicsException` explicite, pas de repli shaderc, §4) + fabrique `ShaderCompiler.CreateForBuild()` (source unique du `#if DEBUG` : Debug=full / Release=cache-only) ; `ShaderHotReloader` gardé `#if DEBUG` (Release : 0 watcher).
- **W3** : précompilateur build `tools/ShaderPrecompiler` (réutilise `ShaderCompiler`+`ShaderIncludeResolver` → clés **identiques** au runtime), 2 targets MSBuild (pré-cook incrémental → staging `obj/` ; ship `.spv` en Content vers `.shadercache/`), **non-ProjectReference** du Sandbox (pas de contamination AOT). `StripShadercFromRelease` retire la lib native shaderc du Release.
- **Preuve** : publish **Release/AOT win-x64** → binaire 4,47 Mo, **shaderc absent**, 15 `.spv` livrés, run cache-only → aucun miss / aucune exception (clés matchent), 0 leak, **capture byte-identique M8 (24001B24…)**. Debug : hot reload conservé, aucun « loading shaderc » (cache chaud). 207 tests, 0 warning.
- **Double audit PASS** (csharp-lowlevel + engine-architect, 0 critique) + 8 durcissements appliqués (ctor internal, garde `EnsureShaderc`, `_shaderc` volatile ARM64, IblGenerator→`CompileFileResolved`, Inputs MSBuild `**\*`, strip cross-platform, tool récursif + catch élargi). Détail : board session 10.

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

**Point de reprise (2026-07-13, session 10)** : Phase 2 en cours. **P2-M0 et P2-M1 clos** (P2-M1 : W1/W2/W3 committés ; durcissements post-audit + docs de clôture **à committer**). Board courant : [.absolute-human/board.md](../.absolute-human/board.md) (session 10, à archiver en `board-session10-P2M1.md`) ; prochaine tâche = **ouvrir P2-M2**.

**Branche** : `phase2-foundations` (partie de `main` à `264772e`). Commits P2-M1 : `2579389`/`89ddf1e` (W1 shaderc lazy + fix flaky) · `b202b92` (W2 cache-only + sélection Debug/Release) · `e79925b` (W3 précompilateur + strip shaderc). Durcissements post-audit + docs de clôture : arbre de travail à committer.

**Prochaine tâche : P2-M2** (spec §6) — `Double3` (monde `double`) + camera-relative rendering + couture ECS render-list (handles, pas de types GPU dans le monde). ⚠️ **Contrainte AOT dure** (les 2 audits P2-M0 convergent) : le rooting des tableaux de composants Arch doit venir d'un **registre source-unique** (source-gen `new T[1]` par type) **gardé par un test tournant sous AOT** — sinon échec runtime silencieux `'T[]' is missing native code`, corruption partielle.

**Vérif humaine encore due (non bloquante, P2-M1)** : hot reload Debug **live** à la fenêtre (edit shader → recompile < 1 s) — non re-testé depuis M8 ; le headless ne le couvre pas.

**Run de sanity Debug** (0 validation / 0 leak) :
```powershell
$env:AGAPANTHE_MAX_FRAMES=3; $env:AGAPANTHE_CAPTURE="check.ppm"; dotnet run --project samples/Sandbox
```
**Publish + run NativeAOT** (Windows ; `vswhere` sur le PATH requis) :
```powershell
$env:PATH="C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish samples/Sandbox/Sandbox.csproj -r win-x64 -c Release
samples/Sandbox/bin/Release/net10.0/win-x64/publish/Sandbox.exe   # 0 validation, 0 leak, capture byte-identique 24001B24…
```
*(macOS : préfixer `DYLD_LIBRARY_PATH=/opt/homebrew/lib` pour les runs Debug ; NativeAOT non validé hors Windows — voir dette.)*

**Contexte hors dépôt** : les projets de smoke-test/probe Arch (P2-M0) sont dans le scratchpad de session (non versionnés) — jetables, à recréer si besoin depuis la spec.

## Dette d'ouverture Phase 2 — mise à jour session 10

Détail P2-M0 : [.absolute-human/archive/board-session9-P2M0.md](../.absolute-human/archive/board-session9-P2M0.md) → « Dette issue de P2-M0 ». En bref, à traiter dans les jalons Phase 2 :
- 🔴 **Rooting AOT des composants = contrainte de conception P2-M2** (registre source-unique → source-gen du rooting → test AOT). Pas de la dette molle : échec runtime silencieux au publish, corruption partielle possible.
- 🔴 **Linux jamais validé** (dette M4) **+ AOT prouvé Windows-only** → re-prouver sur Linux/macOS dès qu'une machine est dispo.
- **Sérialisation maison source-gen** (Arch.Persistence NO-GO) → phase ultérieure, même générateur que le rooting.
- **CI** : le gate 0-leak doit keyer sur la **ligne de rapport**, pas l'exit code (otage du crash Silk.NET au shutdown).
- ~~**shaderc encore embarqué** sous AOT~~ → **retiré de la prod par P2-M1** (mode cache-only + `StripShadercFromRelease`).
- **Dette issue de P2-M1** (détail : board session 10) : (a) pas d'**assertion automatique** du critère de sortie §6 (lib native absente / shaderc jamais chargé reposent sur l'œil humain) → test + gate CI ≤ P2-M5 ; (b) chemin hors-ligne + strip **prouvés Windows uniquement** → re-prouver Linux/macOS (nom de lib natif `.so`/`.dylib` déjà couvert dans le target, non testé) ; (c) **includes non exercés** (resolver + clé include-aware en place, corrects par construction mais aucun shader n'a de `#include` → ajouter un cas avant de s'y fier).
- Reste hérité de M8 (voir « Dette d'ouverture Phase 2 (issue de M8) » plus haut) : invariant du reload par convention, feel souris/labels RenderDoc non observés, crash GLFW shutdown upstream, etc.
