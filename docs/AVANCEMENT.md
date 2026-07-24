# Agapanthe — Plan complet & état d'avancement

**Mis à jour** : 2026-07-23 (session 20 — **P3-M7 CLOS** : buffers device-local + réduction du raster d'ombre 4×→~1× ; double audit PASS, verdict visuel PASS incl. soleil bas ; A+B ~15,3 → ~8,0 ms ≈ ×2) · 2026-07-23 (session 19 — **P3-M6 CLOS** : slots persistants dirty-trackés + cull d'ombre GPU ; double audit PASS, verdict visuel PASS ; voir « Point de reprise ») · 2026-07-14 (session 14 — **vérifs humaines de la Phase 2 soldées** : banc M4 PASS with concerns [perf → P3-M1], précision M3 PASS, hot reload M1 PASS) · 2026-07-13 (session 13 — **PHASE 2 CLOSE** — frustum culling + montée en charge : 10 000 entités cullées à 10 000 km, 0 alloc/frame, en NativeAOT ; double audit signe la clôture) · **Machines de dev** : macOS (Apple M3, MoltenVK) + Windows 11 (RTX 5070 Ti, Vulkan 1.3 core) · **Cibles** : Windows / Linux / macOS

## Vision

Moteur de jeu Vulkan en C# from scratch. **Phase 1 (TERMINÉE, 8/8)** : toute la chaîne graphique 3D, d'une fenêtre vide à une scène PBR complète — glTF, metallic-roughness, multi-lumières, ombres, skybox/IBL, hot reload shaders. **Phase 2 (TERMINÉE, 5/5)** : viewer → moteur — ECS (Arch), coordonnées `double` + camera-relative à origine quantifiée, couture render-list sans types GPU, frustum culling, montée en charge (10k entités cullées à 10 000 km, 0 alloc, NativeAOT). **Phase 3 (à venir)** : gameplay — lifecycle/scheduler, physique, sérialisation, audio.

Spec Phase 1 : [docs/plans/2026-07-02-graphics-engine-design.md](plans/2026-07-02-graphics-engine-design.md) · **Spec Phase 2** : [docs/plans/2026-07-12-phase2-foundations-design.md](plans/2026-07-12-phase2-foundations-design.md) · Suivi de session : [.absolute-human/board.md](../.absolute-human/board.md) (+ archives par session)

## Phase 2 — Fondations scalables (CLOSE — 5/5)

**Cadre** (spec Phase 2) : la Phase 1 a livré un **viewer**, pas un moteur (`Mesh.WorldTransform` figé → rien ne bouge ; `Scene` mélange possession GPU et draw list). La Phase 2 pose **les fondations qui ne se retrofitent pas** : ECS (**Arch**), coordonnées monde `double` + camera-relative rendering, couture render-list (handles, pas de types GPU dans le monde), frustum culling. **Cible de sortie** : des milliers d'entités qui bougent, cullées, **à 10 000 km de l'origine sans trembler**, en NativeAOT, 0 leak. Horizon lointain (non construit ici, mais non condamné) : univers persistant streamé multi-serveurs.

**Deux règles obligatoires nouvelles (rétroactives Phase 1)** : (1) code **AOT-pur** (`IsAotCompatible` sur les libs, `PublishAot` Sandbox, warnings IL = erreurs) ; (2) **chemin SPIR-V hors-ligne** (shaderc = luxe de dev, prod = précuit).

**Jalons Phase 2** :
| # | Livrable | État |
|---|---|---|
| P2-M0 | Gate AOT + verdict Arch | ✅ **PASSÉ** (S9) — double audit PASS, Arch validé |
| P2-M1 | Chemin SPIR-V hors-ligne | ✅ **PASSÉ** (S10) — double audit PASS, prod sans shaderc (prouvé Windows AOT) |
| P2-M2 | Couture ECS : Arch + `ResourceRegistry` + 2 listes (passthrough, sans culling) — refactor **byte-identique** | ✅ **PASSÉ** (S11) — double audit PASS, capture byte-identique Debug + AOT |
| P2-M3 | `Double3` + camera-relative rendering (précision grande distance) | ✅ **PASSÉ** (S12) — double audit PASS conditionnel, **10 000 km == origine, bit-pour-bit** |
| P2-M4 | Frustum culling + montée en charge (= critère de sortie) | ✅ **PASSÉ** (S13) — **PHASE 2 CLOSE** · 10k cullées à 10 000 km, 0 alloc, AOT, double audit signe |
| P2-M5 | Audits + clôture | ✅ **absorbé par M4** — le double audit de clôture a été mené dans W4 |

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

**P2-M2 (couture ECS) — LIVRÉ (S11)** · Détail : [.absolute-human/archive/board-session11-P2M2.md](../.absolute-human/archive/board-session11-P2M2.md)

**Ce qui change** : `Scene` (qui mélangeait possession GPU + draw list + bounds) est **débranchée en deux** — `ResourceRegistry` (possession, Rendering) + **`GameWorld`** (les entités, ECS **Arch 2.1.0**, nouveau projet `Agapanthe.World`). Le helmet est dessiné **entièrement via l'ECS** : entités → systèmes → 2 listes triées → **handles** résolus au draw. **Aucun type GPU dans le monde, aucun type Arch hors de World** (`PrivateAssets="compile"` → garantie *mécanique*, pas conventionnelle).

- **Livré** : `Double3`/`Double3Bounds`/handles/`RenderItem`/`RenderList` (Core, GPU-free) · 7 composants + `ComponentRegistry` (rooting AOT) + `GameWorld` (seule API ; Arch confiné) · systèmes **1** (propagation hiérarchie + **détection de cycle**) et **2** (agrégation bounds) · `CollectRenderLists` **passthrough** (culling = M4) · `tools/AotComponentProbe` (gate AOT).
- **🎯 Critère de sortie TENU** : capture **Debug ET NativeAOT** = **byte-identique** à la baseline M8 (`24001B24…`) → le refactor est **pur**. 0 validation, 0 leak (135), **234 tests** (5/5 runs), 0 warning.
- **Rooting AOT** (le risque n°1) : `Root<T>()` roote `new T[1]` **et** enregistre le type — impossible d'enregistrer sans rooter. Le **source-gen + analyzer** prévus sont **abandonnés** (P2-M0 avait prouvé `new T[1]` suffisant) → registre à la main + test de complétude par réflexion + probe AOT. *L'architecte a validé : « le meilleur arbitrage du jalon ».* Source-gen reporté Phase 3 (avec la sérialisation).
- **Course Arch fermée par construction** : Arch attribue les ids de types dans un état **global, sans verrou**, au premier contact (`World.Create`) → 2 mondes créés en parallèle = **tableaux de composants mésalignés** (composant relu **tout à zéro**, reproductible). Fix : `Root<T>` force `Component<T>.ComponentType` **sous notre verrou**, avant tout monde. *Prouvé : tests World en parallèle **5/5** vs **3/3 d'échec** avant.* Contrat mono-thread gardé par `AssertOwnerThread` (`[Conditional("DEBUG")]`, zéro coût Release).
- **Leçon de guerre (le gate était vert par chance)** : le repli `(0,0,0)` des bounds était appliqué **par mesh** — or **zéro n'est pas l'élément neutre d'une union**. Un mesh **vide** tirait donc les bounds à l'origine : un modèle à (1000,1000,1000) aurait vu son extent **doublé** (cadrage + ombre faux). Invisible sur le casque (aucun mesh vide) → **trouvé par l'audit, pas par le gate**. Le fold vide (∞ inversés) est neutre ; le repli est désormais **global**.
- **Piège byte-identique évité** : `Scene.BoundsCenter/Diagonal` calculaient **en float** ; calculer en `double` puis narrower donne **jusqu'à 1 ULP d'écart** → caméra et matrice d'ombre décalées. Partout où les bounds sont consommées : **narrow d'abord, arithmétique float ensuite**.
- **`Span.Sort(structComparer)` alloue** (~88 B/appel : il boxe le comparateur) → tri maison `RenderList.SortByKey`. Trouvé par le test zéro-alloc.

**P2-M3 (camera-relative rendering) — LIVRÉ (S12)** · Détail : [.absolute-human/archive/board-session12-P2M3.md](../.absolute-human/archive/board-session12-P2M3.md)

- **Le résultat** : la capture headless du casque **à 10 000 km de l'origine** est **identique bit-pour-bit** à celle prise à l'origine (0 canal sur 2 764 800). Le critère prévu (≤ 1 LSB/canal) est donc **dépassé**. Anti-faux-positif : le log imprime `eye at 9999999.99751842` — une valeur qu'un `float` **ne peut pas représenter** (son ULP vaut 1 m à 1e7), ce qui prouve que l'origine est réellement appliquée et qu'on ne compare pas deux runs identiques.
- **Le mécanisme** : le transform du monde est **coupé en deux** — la position vit en `Double3` (composant `WorldPosition`), la rotation/échelle reste une `Matrix4x4` float. Elles ne se recombinent que dans `CollectRenderLists`, **seul point du code** où une coordonnée monde devient une coordonnée GPU, et où la soustraction `objet − caméra` se fait **en double avant le cast**. `RenderView` (Core) porte l'**origine unique de la frame** : monde, lumières et fit d'ombre soustraient tous la même valeur, par construction.
- **Les lumières ponctuelles sont en `Double3`**, reconverties camera-relative **à chaque frame** (les différer était impossible : le shader compare les positions des lumières à des positions de surface déjà relatives). **Aucun shader n'a changé** : l'œil packé à zéro fait que `V = normalize(eyePos − worldPos)` devient `normalize(−worldPos)` tout seul.
- **Fit d'ombre sur le frustum caméra** (`ShadowFit`, GPU-free et testable), plafonné par `Renderer.ShadowDistance`, **mais jamais plus large que la scène** (min des deux sphères) : fitter le frustum sur une petite scène gaspillerait la shadow map. Sphère (et non 8 coins) pour l'invariance en rotation ; snap sur la grille de texels **ancrée au monde** pour l'invariance en translation.
- **Dettes rouges de M2 soldées en ouverture** : handles avec **génération** (un handle périmé → `GraphicsException`, jamais un draw silencieusement faux) et `ResourceRegistry` **globale** en slot-map (avant, `MeshHandle(0)` de deux modèles se collisionnaient — bloquant pour M4).
- **Ce que les audits ont attrapé** (corrigé) : le **snap texel était un no-op** (il quantifiait un centre qui, exprimé relativement à la caméra, ne bouge jamais en translation → la shadow map glissait en continu) · les **casters en amont étaient clippés** (0,5·r de marge) → ombres disparaissant sans erreur · **`Unload` fuyait un descriptor set par matériau, définitivement**, et **le gate « 0 leak » passait en mentant** (il compte les pools, pas les sets) → allocateur **par modèle**, et le chemin, qui n'avait **aucun appelant**, tourne désormais sous le gate réel (`AGAPANTHE_UNLOAD_TEST=N` : 20 cycles, 842 ressources créées **et** détruites).
- **Écarts au plan, assumés** : W2 (lumières) **absorbé par W1** · byte-identique vs M8 **perdu comme prévu** (la translation sort de la matrice de vue → l'ordre des opérations flottantes change ; 14 pixels sur 921 600 dépassent 1 LSB, tous sur le bord d'ombre) · le chemin « fit frustum » **n'est pas exercé par une capture** (la scène du casque emprunte toujours le chemin « scène ») — couvert par 9 tests unitaires, **c'est la condition posée par l'architecte** : scène large = **tâche 1 de M4**.
- **Métriques** : 257 tests · 0 warning · 0 message de validation · 0 leak · probe NativeAOT PASS (8 composants rootés).
- **Env vars nouvelles** : `AGAPANTHE_WORLD_ORIGIN="x,y,z"` (place le modèle en `double` — l'image doit être identique où qu'il soit) · `AGAPANTHE_UNLOAD_TEST=N` (N cycles Load/Unload sous le gate de leak).

**P2-M4 (frustum culling + montée en charge) — LIVRÉ (S13), CLÔT LA PHASE 2** · Détail : [.absolute-human/archive/board-session13-P2M4.md](../.absolute-human/archive/board-session13-P2M4.md)

- **Le résultat** (critère de sortie §6.2, chaque gate vérifié) : **10 000 entités** (un seul upload) · **2556 visibles** après cull caméra · **0 B alloc/frame** (animation incluse) · **bit-identique à 10 000 km** avec caméra ET entités en mouvement (maille alignée) · **tourne en NativeAOT** · 0 validation · 0 leak.
- **Ce qui a été bâti** : `Frustum` (Core, GPU-free, 6 plans Gribb-Hartmann) · `Bounds` → **sphère locale** transformée par frame · **origine quantifiée** (snap 1024 m dans `RenderView` — l'œil vit à `EyeRelative` dans la cellule ; débloque le buffer d'instances persistant et la stabilité physique de la Phase 3) · ordre de frame inversé (`ShadowFit` avant la collecte, pour culler les casters contre le **volume de lumière**) · skybox reconstruit depuis la rotation de vue seule (origin-exact) · culling linéaire (sphère vs 2 frustums) · `SortKey` matériau + tie-break + **tri radix LSD** · `AnimateDrawables<T>` (écriture directe, zéro-alloc, AOT-safe).
- **Précision reformulée (D3, mesurée)** : « loin == origine » est **bit-exact ssi le déplacement est un multiple de la maille**, sinon visuellement indiscernable (le « ≤ 1 LSB » du plan est faux pris à la lettre sur une scène spéculaire — propriété de rendu, pas faute de précision).
- **Audits de clôture** : `engine-architect` PASS sans réserve ; `csharp-lowlevel` FAIL conditionnel **levé** — M1 (`MaxAxisScale` sous-couvrait le rayon sous shear → faux négatif de culling) corrigé par la **σ_max exacte** (`MathHelpers.MaxStretch`), tight (casque bit-identique), 3 tests de régression.
- **Écart assumé** : cull+collect **3,7 ms JIT-Release / ~6 ms AOT** à 10k, > cible **indicative** 1 ms — dette perf comprise (~80 % = liste d'ombres à 10 000 casters ; cull lumière conservateur sur scène plate, safe).
- **Métriques finales de phase** : 275 tests · 0 warning · 0 message de validation · 0 leak · probe NativeAOT PASS.

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
   │            │              (seul projet référençant Silk.NET.Vulkan)
   │            └────► Assets ──► Core   (GPU-free : parsing testable sans GPU)
   └──────► World ─────────────► Core   (ECS Arch — SEUL projet référençant Arch, P2-M2)
Platform ──► Core   (fenêtre GLFW, input, capture souris)

tools/ShaderPrecompiler (P2-M1, SPIR-V hors-ligne) · tools/AotComponentProbe (P2-M2, gate rooting AOT)
```
**Rendering ne référence PAS World** : le monde remplit une `RenderList` (type **Core**, GPU-free) que le Renderer consomme → le Renderer ne connaît pas l'ECS, et Arch (`PrivateAssets="compile"`) ne fuit chez personne.

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

## P3-M1 — Instancing (SSBO) + solde des 2 dettes de culling (2026-07-14, session 14)

Spec : [2026-07-14-p3m1-instancing-culling-design.md](plans/2026-07-14-p3m1-instancing-culling-design.md).

Les transforms des entités visibles sont **compactées chaque frame dans un storage buffer host-visible** (un par frame-in-flight, `InstanceBufferRing`) que le vertex shader indexe par `gl_InstanceIndex` ; la liste triée est **batchée par (matériau, mesh)** → **un draw instancié par batch**, `firstInstance` servant d'offset. Les deux dettes de culling de M4 sont soldées : `AggregateBounds()` est recalculé **par frame**, et les shadow casters sont testés contre un **`ExtrudedShadowFrustum`** (frustum caméra étendu vers la lumière, ANDé avec le volume de lumière).

**Mesures (banc `grid:100x100`, 10 000 entités)** : draw calls **12 556 → 2** (1 scène + 1 ombre) · shadow casters **10 000 → ~5 000** · cull+collect **Release JIT 3,7 → ~2,0 ms**, **NativeAOT ~6 → ~2,2 ms** · **0 alloc/frame**, 0 leak, 0 message de validation, 0 warning, **284 tests verts**, AOT PASS.

**Corrections issues du double audit de clôture** (findings appliqués, pas reportés) :
- 🔴 **Règle ε du wedge inversée** : le code jetait les plans **exactement parallèles** au rayon — or ce sont eux qui ferment le wedge latéralement. Avec un **soleil au zénith et une caméra à plat** (la config la plus banale du moteur), les 4 plans latéraux tombaient et le wedge **ne cullait plus rien** ; le banc y échappait par accident (soleil non aligné sur un axe). Corrigé (garde à la borne, biais keep) + test zénith qui l'épingle.
- 🔴 **Fit d'ombre redevenu instable** : `ShadowFit` ne snappait pas la branche « scène » (« une scène statique ne peut pas shimmer » — hypothèse tuée par les bounds désormais recalculées par frame). Le rayon est maintenant **quantifié** (16 crans par octave) et le centre **snappé au texel dans les deux branches** → le fit est une fonction en escalier, plus de crawl des bords d'ombre. Conséquence assumée : la capture casque n'est **plus bit-identique** à P2 (0,25 % de canaux, décalage sub-texel des ombres) — rendu vérifié intact.
- Clé de tri **mesh-major** pour la liste d'ombre (la passe depth ne lie aucun matériau → plus de sur-découpe) · SSBO qui **rétrécissent** après 60 frames sous le quart de leur capacité · rebind du set 1 seulement au changement de matériau · pool de descripteurs persistant déclarant `StorageBuffer` · `GpuBuffer.Write` en multiplication 64 bits.

**Dette ouverte par P3-M1 (→ P3-M2, rendu GPU-driven)** :
- 🔴 Le cull GPU **n'est pas « une ligne de shader »** : il impose `DrawIndexedIndirect` + `BufferUsage.Indirect` + la feature **`drawIndirectFirstInstance`** (qui, elle, n'est pas gratuite). Piste : porter l'offset de batch en **push constant** → neutralise du même coup le risque `baseInstance` sur MoltenVK.
- 🔴 **L'ordre des systèmes vit dans le Sandbox** (`PropagateTransforms → AggregateBounds → ComputeLightViewProj → CollectRenderLists`) : dette #1 soldée *à l'appel*, pas dans le moteur → premier client du **scheduler**.
- 🔴 `ShadowFit.UpstreamExtent` dérive des **bounds globales** : une entité qui bouge à 10 000 km fera vibrer la plage de profondeur de la shadow map de tout le monde (mord dès la physique) → la dériver de la **liste de casters**.
- 🟠 Slots persistants dirty-trackés (les 2 SSBO fusionnent, `RenderItem.WorldTransform` devient mort) · cull CPU O(n) ~2 ms AOT à 10k (c'est ce que le cull GPU rembourse) · plafond **16 bits** mesh/matériau (limite dure documentée) · `SortKey` sans profondeur.

## P3-M2 — Scheduler de systèmes + lifecycle d'entités (`Agapanthe.Engine`) (2026-07-14, session 15)

Spec : [2026-07-14-p3m2-scheduler-lifecycle-design.md](plans/2026-07-14-p3m2-scheduler-lifecycle-design.md). Commit socle : `90627a5`.

**Nouveau projet `Agapanthe.Engine`** — la seule couche qui marie World + Rendering (ne référence pas Platform, ne possède rien). L'**ordre de frame a quitté le Sandbox** : l'invariant `propagate → aggregate → fit → cull → draw` vit dans `FrameOrchestrator` + `SceneViewSystem`, exécuté par un `SystemScheduler` à étages (`Input → Simulation → PostSimulation → Render`), ordre = donnée testable. Deux interfaces disjointes (`ISystem`/`TickContext` sans GPU, `IRenderSystem`/`RenderContext`). `Tick` tourne **hors** de `DrawFrame` (sinon un resize sauterait la simulation — D1.a). Le spin/churn du banc sont devenus des `ISystem` applicatifs.

**Lifecycle d'entités (D2)** : `Spawn`/`SpawnDeferred`/`Despawn`/`SetParent`/`IsAlive` publics sur `GameWorld`, tout différé à une **barrière de fin d'étage**, **`Despawn` cascade** sur les descendants (scan `Parent` à point fixe). `EntityRef` porte désormais le **`GlobalId`** (`ulong`, identité durable qui précède la création), résolu via une map `_live` — le hot path ne la touche jamais. **Pas de `CommandBuffer` d'Arch** : file de commandes propre au World (le buffer d'Arch 2.1.0 invalide ses handles au playback, ne se reset pas, ne résout pas les refs dans les composants — correction D2 v3 après audit du code décompilé). Scope resserré (YAGNI) : pas d'`AddComponent<T>` générique public, `SetParent` seul (pooling/prefabs → backlog).

**Cull d'ombre deux passes (D3)** : le wedge extrudé est **infini vers l'amont** → un caster à 10 000 km piloterait `UpstreamExtent` et ferait exploser la précision de profondeur. Corrigé par un **7ᵉ plan de coupe** bornant le wedge à `ShadowCasterDistance` (ancré sur la sphère du frustum). Circularité fit↔cull cassée en deux passes : passe 1 (`CollectRenderLists`) cull wedge borné + `casterBounds` + tableau parallèle de sphères ; fit (footprint sur `sceneBounds`, profondeur sur `casterBounds`) ; passe 2 (`CompactShadowCasters`) compaction contre le volume de lumière puis tri.

**Mesures** : banc `grid:100x100` **Release JIT ET NativeAOT** — draws **2+2**, **0 alloc/frame** (banc + mode churn), 0 leak, 0 validation · **311 tests** · 0 warning · **NativeAOT PASS** (probe `AotComponentProbe` + Sandbox) · **capture bit-identique `9790D95D`** (D3 est un no-op observable sur la scène par défaut ; le fix est prouvé à l'échelle par test unitaire — `eyeDistance` reste borné vs >1e6 sinon).

**Double audit de clôture PASS** (`csharp-lowlevel` + `engine-architect`, aucun FAIL/MAJEUR) — findings mineurs appliqués : **garde F7** (`_pass1ShadowList` : `CompactShadowCasters` refuse une liste qui n'est pas celle du dernier `CollectRenderLists` — un contrat non gardé serait violé au premier split-screen/CSM), **test D3.a resserré** (borne liée au mécanisme, plus `*10` arbitraire), **spec nettoyée** (§2/§3.2 et récit `Stage.Input`).

**Dette ouverte / non-bloquants notés** :
- 🔴 **Linux/macOS toujours jamais validés** (AOT + SPIR-V hors-ligne Windows-only) — **P3-M0**, toujours le premier item.
- 🟠 **CSM devra sortir l'état de passe-1 (`_casterSpheres`) du World** (contrainte F7 : une seule `RenderView`/frame, désormais **gardée**).
- 🟠 Cascade despawn en **O(profondeur × N_parent)** *quand un despawn est en attente* (re-scan complet par itération du point fixe) — invisible à l'échelle actuelle, à surveiller avec des hiérarchies profondes (physique/gameplay). Corrigeable par une file de travail BFS, au prix d'une liste d'enfants (refusée par le design).
- 🟠 Verdict visuel humain P3-M1 **et** P3-M2 encore dus (P3-M2 bit-identique → non bloquant pour la clôture technique).
- (Report P3-M1 : cull GPU = `DrawIndexedIndirect` + `drawIndirectFirstInstance` ; slots persistants dirty-trackés ; `SortKey` sans profondeur ; plafond 16 bits mesh/matériau.)

## P3-M8 — Premier pas planétaire : reversed-Z + sphère + scène planète/Soleil à l'échelle (2026-07-24, session 21)

Spec : [2026-07-23-p3m8-planetary-first-step-design.md](plans/2026-07-23-p3m8-planetary-first-step-design.md). La **seconde scène de référence** (à côté de la grille de casques) : une planète et un Soleil à l'échelle **1/2 uniforme** (réel÷2 : planète 3 185,5 km, Soleil 348 170 km, distance **7,48e10 m**), qui met enfin à l'épreuve ce pour quoi les fondations `double`/camera-relative ont été bâties — surface planétaire (~1e7 m) et Soleil (7,48e10 m) **dans un seul frustum, sans z-fighting**.

**Le blocage structurel soldé — reversed-Z global.** `MathHelpers.PerspectiveVulkanReversed` (dérivation clip-space exacte `z→w−z` : `M33=−1−M33 ; M43=−M43`) mappe near→NDC 1, far→NDC 0 ; couplé au depth **D32 float** cleared à 0 et au test **`GreaterOrEqual`**, il répartit la précision quasi-uniformément sur un ratio near/far planétaire. **Comparateur depth par pipeline** (`GraphicsPipelineDesc.DepthCompare`, défaut `LessOrEqual` back-compat) : passes caméra (Scene, Skybox) en `GreaterOrEqual`, **passe d'ombre laissée en `LessOrEqual`** → **le CSM est totalement découplé** (ShadowFit reconstruit ses coins depuis les scalaires `FovY/near/far`, jamais depuis la matrice ; `Frustum` label-swap near↔far mais volume identique → culling invariant, prouvé par audit).

**La scène (`AGAPANTHE_SCENE=planet`).** `Primitives.UvSphere` (sphère unité, normales analytiques, tangentes longitude, winding CCW, `ushort` avec garde d'overflow) → `BuildSphereModel` (rayon baké dans les positions locales) → planète (albedo bleu-vert) + Soleil (emissive fort) en `Double3`. **Sun-only, physiquement fidèle** : le Soleil est une **sphère de plasma = seule lumière**, donc une **point light co-localisée avec l'entité Soleil** (inverse-carré, `I = irradiance·d²` ; la lumière part physiquement de l'étoile, pas d'un vecteur abstrait) ; **env noir** (`BuildBlackEnvironment` → IBL 0 + skybox noir) ; ambient 0. À 7,48e10 m les rayons arrivent quasi-parallèles → terminateur net, mais lié à la position du Soleil. La surface du Soleil reste purement émissive (ses normales pointent dehors → `dot(N,L)<0` depuis son centre → la point light ne l'éclaire pas). Échelle 1/2 uniforme = **taille angulaire réelle du Soleil (~0,53°)**.

**Mesures / gates** : **333 tests verts**, 0 warning, 0 message de validation, 0 leak, **NativeAOT PASS**, **GPU==CPU MATCH** (casque/grille 401, planète 2), **0 alloc/frame**. Captures headless casque + grille (rebaselinées sous reversed-Z, verdict visuel humain PASS — pas de régression) + scène planète (croissant + terminateur lisse, nuit noire, Soleil disque lointain sans z-fighting, fond noir). **Rebaseline assumé** : le hash mono/grille change sous reversed-Z ; équivalence prouvée par **audit du diff** (culling invariant, aucun z-test inversé) + verdict visuel, jamais masquée. Env vars : `AGAPANTHE_PLANET_{RADIUS,PHASE,ALT,FOV}`, `AGAPANTHE_SUN_{DIR,RADIUS,DISTANCE}`.

**Double audit de clôture** — les deux **PASS**, 0 🔴/🟠 :
- `csharp-lowlevel` **PASS** : 0 alloc/frame préservé (tout le code allocateur est load-time), chemins AOT-purs, ressources GPU possédées/libérées, **précision point light saine** (I≈2e22 sans overflow, 1/d²≈1.8e-22 sans underflow, `Range=0` désactive le cutoff). 2 🟡 **appliqués** (garde overflow `ushort` dans `UvSphere` + test ; fallback vecteur nul dans `EnvVector3`).
- `graphics-3d` **PASS** : matrice reversed-Z exacte, comparateur par pipeline correct, **culling invariant sous reversed-Z** (labels near/far échangés, volume identique, AND symétrique → 0 faux-négatif — le risque n°1 écarté), CSM confirmé insensible, skybox/z-fighting sains. 1 🟡 **appliqué** (commentaires d'échelle périmés `7,48e10`/`1/2 uniforme`) ; 1 🟡 **noté, aucune action** (garde normalisation `Frustum` `1e-8` conservatrice + pré-existante, hors régime de la scène — mord seulement à un near sub-mètre).

**Dette ouverte / notée** :
- 🔴 **Linux/macOS toujours jamais validés** (P3-M0), premier item de fond.
- 🟡 **Garde normalisation `Frustum` (`Frustum.cs:105`)** : un near sub-mètre avec far≈1e11 rendrait le plan far non normalisé (conservateur — jamais de faux-négatif) ; à traiter si un near < 1 m apparaît.
- 🟠 **Ombres planétaires analytiques** (backlog §2.2) : au pas 1 le CSM se no-op à 3e6 m (nuit = `dot(N,L)`) ; les éclipses/terminateur avancé viennent avec l'atmosphère (backlog §3, §4bis pas 3).
- Suite planétaire (backlog §4bis) : orbites képlériennes (pas 2), LOD sphérique + atmosphère (pas 3).

## VS-1 — Sérialisation du World (save/load snapshot) (2026-07-24, session 22)

Spec : [2026-07-24-vs1-world-serialization-design.md](plans/2026-07-24-vs1-world-serialization-design.md). **Premier jalon de la Vertical Slice** (backlog §4ter) — la **preuve de persistance** (DoD item 3) : `world.Save(Stream)` / `world.Load(Stream)`, round-trip **byte-identique, déterministe, AOT-pur, 0 leak**, et la scène planète qui se recharge à l'identique dans un **process neuf**.

**Format binaire blittable** (`WorldSerialization.cs`, partial `GameWorld` — GPU-free, aucun type Arch ne fuit) : header `AGWD`/version/componentCount/nextGlobalId/entityCount, puis chaque entité **triée par GlobalId** (ordre total → save déterministe) avec un `presenceMask` u32 sur l'ordre `ComponentRegistry.All` et les octets blittables de chaque composant présent, **sauf `InstanceSlot`** (runtime, réassigné au rebuild) et **`Parent` écrit comme le GlobalId du parent** (l'`Entity` Arch est un handle mémoire non-persistable). Dispatch par-composant = 3 switches concrets (Has/Write/ReadAdd) → **AOT-safe** (instanciations statiquement visibles), `MemoryMarshal` + `BinaryPrimitives` LE. **Décisions** (spec §3, déléguées à Claude) : **seam GPU = handles reproductibles** (Option 1 : le caller recharge les mêmes assets d'abord ; clés d'asset stables déférées), **pas de générateur** (blittable → ni réflexion ni source-gen — le « un seul générateur » du backlog supposait un rooting source-generated inexistant), style écrit-à-la-main de `ComponentRegistry`.

**Load** (World frais) : header vérifié → `_nextGlobalId` restauré **sans bump** ; passe 1 create avec le GlobalId sérialisé + `InstanceSlot=-1` sur les drawables ; passe 2 `LinkParent` par GlobalId (réutilise l'existant) ; `_structuralDirty=true`. Arch clé les archétypes par **ensemble** de composants → `Create+Add` reconstruit exactement l'archétype d'origine. Robustesse : magic/version/count/**tronqué (EOF)**/**mask hors-plage**/World non vide/**GlobalId dupliqué** → `WorldSerializationException` typée.

**Intégration Sandbox** : `AGAPANTHE_SAVE=<path>` (snapshot après build de scène) / `AGAPANTHE_LOAD=<path>` (force scène planète, charge les assets puis `SetupPlanetScene(spawnEntities:false)` + `world.Load` — contrat Option 1). `GameWorld.LiveEntityCount` public.

**Gates** : **346 tests** (round-trip tous archétypes, **byte-identique `Save(Load(bytes))==bytes`**, remap Parent, déterminisme, 7 cas d'erreur, garde d'ordre + garde ≤32), 0 warning, 0 validation, 0 leak, **AOT probe PASS** (`AotSerializationSmoke`, `IsDynamicCodeSupported=False`), **round-trip cross-process byte-identique JIT+AOT** (`0034af33…`, save planète 2 entités / 314 o → reload process neuf). **Double audit PASS** (`csharp-lowlevel` 0 🔴/🟠 · `engine-architect` 1 🟠 + 6 🟡, tous appliqués : garde ≤32, garde GlobalId dupliqué, doc endianness, invariants commentés, `.gitignore`). **Verdict humain fonctionnel PASS.**

**Dette / notée** : source-unique émergente (le triple switch DUPLIQUE l'ordre du registre — gardé par le test d'ordre figé + `Save` qui lève sur index inconnu, mais pas une table unique) · `GlobalId` sérialisé ×2 (clé + composant 0, inoffensif) · **Generation d'Option 1 non exploitée** (un ordre de chargement d'assets différent casse en silence — futur *fingerprint d'assets* fourni par le caller, backlog) · garde fresh-world plus permissive que « fraîchement construit » (inoffensif, `Load` écrase l'état stale).

## Reprise — où repartir

> Trajectoire long terme (CSM, rendu GPU-driven, nuages volumétriques, atmosphère, ombres planétaires analytiques,
> physique) : **[BACKLOG.md](BACKLOG.md)** — chaque item dit *ce qui casse sans lui* et *à quelle échelle il devient
> obligatoire*.

> ### ▶️ PROCHAIN — Vertical Slice, jalon **VS-2 (spawn runtime)**
> **VS-1 sérialisation CLOSE** (session 22, double audit PASS, verdict humain PASS ; bloc ci-dessus). Cap :
> **[Vertical Slice, backlog §4ter](BACKLOG.md)** — preuve d'intégration, ancre planétaire, Windows d'abord.
> Prochain dans l'ordre de dépendance : **VS-2 spawn runtime** — `SpawnBodyDeferred` + `CommandKind.SpawnBody` (le
> `StructuralCommand` fat portant vitesse/masse/restitution/rayon), dette P3-M3 : aujourd'hui `SpawnBody` est
> **immédiat** (seam load-time), il faut un spawn de corps **différé** applicable en cours de simulation (projectiles/
> débris/sonde larguée). Puis VS-3 glu gameplay → VS-4 HUD → VS-5 audio (stretch). **P3-M0 (Linux) non bloquant.**
> Ouvrir l'interview de conception (absolute-brainstorm) sur VS-2.

**Point de reprise (2026-07-23, session 20)** : **P3-M7 buffers device-local + réduction du raster d'ombre 4× — CLOS.**
Double audit PASS (`csharp-lowlevel` PASS · `graphics-3d` PASS with concerns ; 0 🔴/🟠, findings 🟡 appliqués),
**verdict visuel humain PASS** (incl. le cas **soleil bas**, exigé par l'audit). Referme les **deux dettes perf
déférées de P3-M6**. **(A) device-local** : nouveau `CommandList.CopyBuffer` async intra-frame (core `vkCmdCopyBuffer`,
pas `CmdCopyBuffer2` = KHR/1.3, risque MoltenVK) ; le buffer de candidats persistant garde un **staging host-visible
= miroir** et copie les ranges dirty vers un **device-local** ; les buffers d'instances (scène+ombre) passent
device-local sans staging (GPU write+read). **(B) raster d'ombre** : un **7ᵉ plan de coupe near-side en profondeur-vue**
par cascade fait **tuiler** les cascades au lieu de s'emboîter → chaque caster dans ~1 cascade au lieu de ~4
(**cascade 0 exemptée** pour préserver l'anti-popping P3-M6 ; marge 25% de tranche > bande de fondu 10%). **Bonus** :
`ReadBackShadowVisible` (comptage d'ombre par cascade) ferme la dette de gate GPU==CPU de l'ombre de P3-M6.
Métriques (AOT `grid:100x100`) : **A+B ~15,3 → ~8,0 ms (≈ ×2)** · shadow-verify **total ≈ 1×/caster** (par cascade
`[28,123,701,4092]`, vs ~4× avant) · draws **2+4** · **0 alloc/frame** · GPU scène == CPU (`2576 MATCH`) · mono
**bit-identical `4848F93F`** · 0 leak · 0 validation · **325 tests** · NativeAOT PASS.
Spec : [2026-07-23-p3m7-device-local-shadow-raster-design.md](plans/2026-07-23-p3m7-device-local-shadow-raster-design.md) ·
protocole visuel : [visual-checks/2026-07-23-p3m7-device-local-shadow-raster.md](visual-checks/2026-07-23-p3m7-device-local-shadow-raster.md).
Env var nouvelle : `AGAPANTHE_SUN="x,y,z"` (direction du soleil ; petit `|y|` = soleil rasant, pour le test de light leak).
**Dette léguée** : `UpstreamExtent` par cascade complet reste déféré ([backlog §2.0bis](BACKLOG.md)) — la marge du
near-cut est calée sur l'épaisseur de tranche, pas la longueur d'ombre ; un soleil **très** rasant reste le cas
limite (jugé PASS au protocole). Chemin device-local/transfer **non exécuté sur MoltenVK** (dette P3-M0).

**Point de reprise antérieur (2026-07-23, session 19)** : **P3-M6 slots persistants + cull d'ombre GPU — CLOS.** Double audit
PASS (`graphics-3d` PASS · `csharp-lowlevel` PASS with concerns ; 0 🔴/🟠, findings 🟡 appliqués), **verdict visuel
humain PASS**. Referme **deux dettes fraîches** : (1) la régression sort/upload O(n) de P3-M4 ([backlog §1](BACKLOG.md)) —
le buffer de candidats est désormais **persistant** (`PersistentInstanceBuffer`, F copies host-visible + miroir CPU
autoritatif + sync-before-use), le gather + radix sort ne tournent qu'au **rebuild structurel** (spawn/despawn/edit
mesh-matériau/re-snap d'origine) et une frame ordinaire ne patche que les slots **dirty** (O(dirty), marqués aux 3
surfaces de mutation du World : animation, physique, propagation) ; (2) le cull par cascade CPU de P3-M5 ([§2.0bis](BACKLOG.md))
passe **en compute** (`shadow_cull.comp`, compaction atomique par région (cascade, mesh-batch)) → scan CPU O(n×4) et
**4 `RenderList` managées (~12 Mo) supprimés**, `CollectShadowCasters` retiré. **~200 lignes de code mort du wedge**
retirées. Métriques : **322 tests** · 0 warning · 0 validation · 0 leak · **0 alloc/frame @10k AOT** (draws **2+4**) ·
GPU scène visible == CPU (`ReadBackSceneVisible` MATCH) · mono **bit-identical `4848F93F`** · NativeAOT PASS.
Spec : [2026-07-23-p3m6-persistent-slots-gpu-shadow-cull-design.md](plans/2026-07-23-p3m6-persistent-slots-gpu-shadow-cull-design.md).
**Slot stable ssi `SortKey` sans profondeur** (condition de validité inscrite dans `InstanceSlot` — casse le jour où la
transparence ajoute la profondeur, backlog §0). **Dette léguée** ([backlog §1/§2.0bis](BACKLOG.md)) : le **raster ombre 4×**
et les **buffers host-visible → device-local** restent (déférés exprès, risque gate visuel / principal levier perf restant) ;
le cull d'ombre n'a pas de readback GPU==CPU (asymétrie avec la scène, dette de test notée). Banc animé ~15 ms (10k spinnés
= pire cas ; le gain se voit sur scène statique → dirty vide).

**Point de reprise antérieur (2026-07-19, session 18)** : **P3-M5 CSM — CLOS.** Double audit PASS with concerns (`csharp-lowlevel` + `graphics-3d`), findings majeurs appliqués, verdict visuel humain PASS. Quatre cascades dans un **atlas 2×2** de la carte 4096² existante (2048²/cascade, mémoire d'ombre **inchangée**) : split pratique (λ=0.5) sur `Cascades.MaxDistance` (200 m), fit par tranche de frustum — **caméra seule, donc plus de circularité fit↔casters** → le **wedge two-pass P3-M2 est retiré** (`CompactShadowCasters`/`_casterSpheres`/garde F7 supprimés) au profit d'un cull simple par cascade. `mesh.frag` : sélection par profondeur vue, PCF 5×5 **clampé à la tuile**, fondu inter-cascade 10 %, **fondu de distance 20 %**, debug `DEBUG_CASCADE` (touche N). Nouveau composant **`NoShadowCast`** (le sol reçoit mais ne projette pas). Métriques : **334 tests** · 0 warning · 0 validation · 0 leak · **0 alloc/frame @10k AOT** (draws 2+4) · **11,4 ms/frame** vs ~8 ms en P3-M4 (**coût CSM ~3,4 ms, assumé**) · NativeAOT PASS. Commit `aec752b`. **Ce que ça corrige** : les 4 artefacts du constat visuel P3-M4 (zone rectangulaire, anneaux d'acné, coupure d'ombre franche, ombres bridées) — [protocole](visual-checks/2026-07-19-p3m5-csm.md). **Deux calibrations nées du protocole** : plafond `ShadowDistance = 50 m` de la session 16 **levé** (il bridait le CSM au quart de sa portée — le workaround avait survécu à sa justification) et **fondu de distance** (supprime l'« horizon d'ombre »). **Dette** : portée finie par construction → [backlog §2.2bis](BACKLOG.md) (plus de cascades / RT hors macOS — **bloqué MoltenVK, sources citées** / ray marching, qui attend un terrain) · depth bias unique pour des cascades de densités très différentes (à surveiller).

**Point de reprise antérieur (2026-07-19, session 17)** : **P3-M4 rendu GPU-driven — CLOS.** Cull compute de la scène (`scene_cull.comp`, frustum-cull + compaction atomics) + draw indirect (`vkCmdDrawIndexedIndirect`, offset de batch en push constant → MoltenVK-safe). Gate : **GPU visible == CPU (2557 @10k AOT)**, mono bit-identique `9790D95D`, 0 alloc/frame @10k AOT, draws 2+2, NativeAOT PASS, 321 tests, double audit PASS. Env vars : `AGAPANTHE_CULL_VERIFY=1` (compte GPU vs CPU). **Dette P3-M4 → [backlog §1](BACKLOG.md)** : (C) slots persistants dirty-trackés (prochain jalon GPU-driven, rembourse la régression A+B sort/upload O(n)) · buffers GPU-produits en device-local · compaction atomique = 2ᵉ verrou transparence. **Verdict visuel PASS avec dette d'ombre** : artefacts sol sur grille (empreinte shadow map + acné sur sol plat, `eyeDistance` 248 m car plan de sol = caster ; moiré herbe) diagnostiqués **préexistants, pas le cull** — correctif = CSM ([backlog §2]) ou mitigation ground-non-caster. Bonus : **HUD debug barre de titre** (fps/ms/draws/candidates/GC MB). **Prochaine tâche : voir le choix ci-dessous** (CSM / slots persistants (C) / mitigation ombre / autre).

**Point de reprise antérieur (2026-07-19, session 16)** : **P3-M3 physique v1 — CLOS. W1→W4 livrés, double audit PASS, findings appliqués, verdict visuel humain PASS** (le glissement des casques — pas de roulement — est la limite v1 linéaire attendue, rotation/inertie au backlog §4). Corps rigides linéaires déterministes (gravité, intégration à dt fixe, collision sphère↔sol + sphère↔sphère, broadphase grille uniforme 0-alloc, résolution triée `(GlobalId)`). Métriques : 320 tests · 0 warning · 0 validation · 0 leak · **0 alloc/frame @1000 corps AOT** · NativeAOT PASS · reproductible run-à-run ET Debug≡AOT (`19D1A629`). `UpstreamExtent` exercé sous mouvement réel, wedge borné (P3-M2 D3) tient. Env vars : `AGAPANTHE_SCENE=drop:N` · `AGAPANTHE_PHYSICS=1`. Détail : board S16 + spec [2026-07-19-p3m3-physics-design.md](plans/2026-07-19-p3m3-physics-design.md). **Dette P3-M3 → [backlog §4](BACKLOG.md)** (SpawnBodyDeferred, plafond `GlobalId<2³²`, accumulateur, solver quality). **Prochaine tâche = le rendu GPU-driven** reporté de P3-M1 (cull compute + draw indirect, [backlog §1](BACKLOG.md)). **P3-M0 — validation Linux/macOS repoussée** (décision humaine : pas de machine, plus tard).

**Fix Sandbox (session 16, appliqué + validé)** — constat visuel humain de fin de session 15 sur une **grille de casques** (`AGAPANTHE_SCENE=grid:20x20`) : (a) « chaque casque a sa propre lumière » et (b) artefacts d'ombre en anneaux sur le sol. **Diagnostic : configuration du Sandbox, pas une régression moteur** (la capture de référence reste bit-identique, un casque seul est correct). **Corrigé dans `samples/Sandbox/Program.cs` ; verdict visuel humain PASS (2026-07-19, `grid:5x5` : éclairage uniforme, ombres nettes, 0 anneau).**
- (a) `SetupLights` montait un rig studio 3-points **mis à l'échelle sur la diagonale de la scène** (gonflée par le plan de sol) → point lights à ~450 m avec atténuation en carré inverse → dégradé de luminosité à travers la foule. **Fix appliqué** : `SetupLights(..., multiInstance: rows*cols > 1)` → sur scène multi-instances, `PointCount = 0` (soleil + IBL seuls) ; rig conservé pour le showcase mono-modèle.
- (b) `FrameCamera` faisait `renderer.ShadowDistance = diagonal * 4f` → la shadow map 4096² s'étalait sur ~500 m (~0,12 m/texel) → aliasing rasant sur le sol plat. **Fix appliqué** : `ShadowDistance = Min(Max(diagonal*4, 1), 50)` — plafond universel (no-op sur un casque 2 m : `diagonal*4 ≈ 14 m < 50`, capture mono-modèle **bit-identique `9790D95D`**) ; les casters lointains cessent de projeter, ce que `ShadowCasterDistance` (D3) gère sans popping.
- **Ce que ça révèle côté moteur** : une **cascade d'ombre unique ne peut pas être à la fois nette et longue portée** — plafond structurel, le vrai correctif est le **CSM**, déjà au [backlog §2](BACKLOG.md). À surveiller après le plafond : si de l'acné d'ombre subsiste, le depth bias (calibré pour une scène de 2 m) devra s'exprimer en espace texel de shadow map — durcissement moteur mineur, à constater, pas à postuler.

**Branche** : `phase2-foundations`. Commits P2-M4 : `12a07e3` (W0 Frustum+sphère+banc) · `7d9428a` (W1 origine quantifiée + ordre de frame + skybox origin-exact) · `c5b7da7` (W2 culling) · `458e017` (W3 radix + SortKey) · `99076c1` (W4 banc + AnimateDrawables) · `2827777` (durcissements audits : σ_max exact).

## Roadmap Phase 3 — état au 2026-07-19 (session 18)

**Livrés :**
| # | Jalon | Session | Ce qu'il a prouvé |
|---|---|---|---|
| P3-M1 | Instancing (SSBO) + 2 dettes de culling | S14 | 12 556 → 2 draws à 10k |
| P3-M2 | Scheduler + lifecycle + `Agapanthe.Engine` | S15 | L'ordre de frame est un invariant du moteur, pas du Sandbox |
| P3-M3 | Physique v1 (corps rigides linéaires) | S16 | Simulation déterministe, reproductible run-à-run, 0 alloc |
| P3-M4 | Rendu GPU-driven (cull compute + draw indirect) | S17 | Le cull quitte le CPU ; GPU visible == CPU (2557 @10k) |
| P3-M5 | **CSM** (4 cascades, atlas 2×2, `NoShadowCast`) | S18 | Ombres nettes près **et** loin ; les 4 artefacts du constat P3-M4 corrigés |
| P3-M6 | **Slots persistants dirty-trackés + cull d'ombre GPU** | S19 | Le CPU ne re-trie/re-upload plus tout (chemin incrémental O(dirty)) ; le cull d'ombre quitte le CPU (12 Mo managés disparus) — double audit PASS, verdict visuel PASS |
| P3-M7 | **Buffers device-local + réduction raster d'ombre 4×** | S20 | Les buffers GPU quittent l'host-visible (PCIe) ; le raster d'ombre passe de ~4× à ~1×/caster — A+B ~15,3 → ~8,0 ms (≈ ×2), double audit PASS, verdict visuel PASS (incl. soleil bas) |
| P3-M8 | **Premier pas planétaire (reversed-Z + sphère + scène planète/Soleil 1/2)** | S21 | Surface planétaire + Soleil à 7,48e10 m dans **un** frustum sans z-fighting ; reversed-Z global découplé du CSM ; Soleil = sphère de plasma = seule lumière (point light co-localisée) — double audit PASS, verdict visuel PASS |

**Ouverts, par ordre de recommandation** (chaque ligne dit *ce qui casse sans lui*) :

1. 🔴 **P3-M0 — Validation Linux/macOS.** Toujours le premier item sur le fond : AOT et SPIR-V hors-ligne sont
   **prouvés Windows uniquement**, donc « fondations cross-platform » reste une hypothèse. **Bloqué** : pas de
   machine (décision humaine, reporté S16). *Le seul item dont l'ancienneté grandit sans qu'on puisse agir.*
2. 🟠 **GPU-driven shadow cull + slots persistants** ([backlog §1](BACKLOG.md), §2.0bis). Le meilleur rapport
   valeur/effort aujourd'hui : il rembourse **deux** dettes d'un coup — le cull par cascade quasi inopératoire de
   P3-M5 (~4× les casters rasterisés) **et** la régression sort/upload O(n) de P3-M4 (le CPU trie/upload encore
   les 10k candidats par frame). *Mord : déjà au banc, et à 40k il domine.*
3. 🟠 **Terrain** ([backlog §5](BACKLOG.md)) — le sol est un quad plat. Prérequis de **beaucoup** : ray marching
   pour les ombres lointaines (§2.3, la piste retenue avec l'humain), relief au soleil rasant, scènes crédibles.
4. ~~🟠 **Scène de test « planète / système solaire à l'échelle 1/2 »**~~ ✅ **pas 1 livré en P3-M8** (S21) : sphère
   planétaire à l'échelle **1/2 uniforme** + **reversed-Z** (le depth range à `near/far ≈ 1e11` soldé) + jour/nuit
   par `dot(N,L)` d'une point light co-localisée avec la sphère-Soleil. La précision `double` a tenu (ULP ≈ 17 µm à
   `7,5e10` m), c'était bien le depth qui cassait — et il est réglé. **Suite** (backlog §4bis) : orbites képlériennes
   (pas 2), LOD sphérique + atmosphère (pas 3). *La scène planète est désormais l'ancre de la [Vertical Slice §4ter](BACKLOG.md).*
5. 🟡 **PCSS** ([backlog §2.1bis](BACKLOG.md)) — pénombre à largeur variable, partage la sélection de cascade
   avec le CSM tout juste livré. Qualité pure, pas de dette remboursée.
6. 🟡 **Sérialisation source-gen** (partage le générateur du rooting AOT ; parallélisable). **Audio** en dernier.

> **Cap moyen terme (formalisé S21) — [Vertical Slice, backlog §4ter](BACKLOG.md).** Le premier chemin de bout en bout
> (preuve d'intégration, **ancre planétaire**, Windows d'abord) : free-fly autour de la planète/Soleil P3-M8 + un
> élément **spawné au runtime** + **save/load** du monde + HUD minimal. Consomme, dans l'ordre : **VS-1 sérialisation**
> (la grosse pièce, item 6 ci-dessus), **VS-2 spawn runtime** (`SpawnBodyDeferred`, dette P3-M3), VS-3 glu gameplay,
> VS-4 HUD, VS-5 audio (stretch). P3-M0 (Linux) reste un prérequis **non bloquant**. C'est le jalon qui fait passer de
> « moteur avec fondations » à « moteur qui a fait tourner un monde de bout en bout ».

## Reprise — recommandations immédiates (écrit 2026-07-20, fin session 18)

**Où on en est** : arbre propre, tout commité (`9aa8b3f`), 334 tests verts, 0 warning / 0 validation / 0 leak.
Rien n'est en cours, aucun jalon ouvert. Le board S18 est archivé.

**Les trois candidats, avec ce qu'ils coûtent vraiment :**

- **A. GPU-driven shadow cull + slots persistants** ([backlog §1](BACKLOG.md) + [§2.0bis](BACKLOG.md)) —
  **le meilleur rapport valeur/effort**, et le seul qui rembourse **deux dettes d'un coup** : le cull par cascade
  quasi inopératoire de P3-M5 (~4× les casters rasterisés) et la régression sort/upload O(n) de P3-M4. Les deux
  mordent **déjà** au banc (part des 11,4 ms) et dominent à 40k. *Terrain connu, risque faible, gain mesurable.*
- **B. Premier pas planétaire** ([backlog §4bis](BACKLOG.md)) — le plus **excitant** et le plus **révélateur** :
  il valide (ou infirme) la thèse « fondations pour un univers persistant ». Mais il ouvre un problème
  **structurel non résolu** (le depth range) → prévoir une vraie phase d'instruction avant de coder.
  ✅ **Échelle tranchée (humain, S18)** : **deux facteurs distincts** — tailles et distances. Départ proposé :
  tailles **1/2**, distances **1/10** (Terre 3 186 km, 1 UA → `1,5e10` m). Garde de grandes coordonnées — la valeur
  de test — tout en rendant la planète atteignable. Détail et justification : [backlog §4bis](BACKLOG.md).
- **C. Terrain** ([backlog §5](BACKLOG.md)) — prérequis de B (LOD sphérique) *et* du ray marching pour les ombres
  lointaines (§2.3). Le plus gros morceau des trois.

**Ma recommandation si on reprend à froid** : **A d'abord** (court, referme deux dettes fraîches, laisse le moteur
plus propre qu'on ne l'a trouvé), **puis B** en commençant par le pas 1 isolé. Faire B avant A revient à empiler
une nouvelle échelle de problèmes sur un chemin de rendu dont on sait déjà qu'il gaspille 4× le travail d'ombre.

**Avant d'ouvrir quoi que ce soit** : relire [backlog §2.0bis](BACKLOG.md) (dette fraîche P3-M5, dont ~200 lignes
mortes à supprimer ou documenter) et la note « **ne pas ajouter de bias par cascade** » — c'est un piège dans lequel
il serait naturel de tomber en retouchant les ombres.

**Dette transverse à ne pas perdre de vue** : rotation/friction physique ([§4](BACKLOG.md)) · transparence
**doublement verrouillée** (`SortKey` sans profondeur **+** compaction atomique qui scramble l'ordre) ·
~200 lignes mortes laissées par le retrait du wedge (§2.0bis) · crash shutdown Silk.NET (upstream).

**Dette léguée par la Phase 2 (détail : board S13), par « quand ça mord »** :
- ~~🔴 `AggregateBounds` plié une fois~~ ✅ **soldé en P3-M1** (recalcul par frame) ; ~~ordre de frame dans le Sandbox~~ ✅ **soldé en P3-M2** (`FrameOrchestrator` + scheduler).
- ~~🔴 Cull du volume de lumière conservateur~~ ✅ **soldé en P3-M1** (`ExtrudedShadowFrustum` ANDé au volume de lumière ; CSM = vrai correctif plus tard).
- 🔴 **Linux/macOS jamais validés** (AOT + SPIR-V hors-ligne Windows-only) — premier item P3.
- 🟠 `SortKey` sans profondeur (pas de front-to-back opaque ; transparence future **fausse** sans tri profondeur) · déterminisme du tri exige `(matériau, RenderOrder)` globalement unique · propagation O(n·d) déférée (hiérarchies profondes) · pas d'API `Despawn`.
- 🟡 `AssertOwnerThread` Debug-only vs futur job system · **crash shutdown Silk.NET reproductible** (`AGAPANTHE_UNLOAD_TEST=20`, ~2/10, après le rapport propre — garder le gate CI keyé sur la ligne de rapport, pas l'exit code) · pas d'assertion CI du critère de sortie.

**Vérifs humaines de la Phase 2 — SOLDÉES (2026-07-14, session 14, Windows/RTX 5070 Ti)** · protocoles : [p2m4](visual-checks/2026-07-14-p2m4-bench-skybox.md) · [p2m3](visual-checks/2026-07-14-p2m3-precision-camera.md) · [p2m1](visual-checks/2026-07-14-p2m1-hot-reload-live.md).
- (a) **P2-M4** — banc `grid:100x100` + skybox W1 : **PASS with concerns**. Cull + skybox corrects et stables ; concern = **FPS bas sur la grande grille** → dette perf **assumée** de M4 (amplifiée par le run Debug + validation layers : 74–92 ms/frame vs 3,7 ms JIT-Release / ~6 ms AOT). **Remboursement = P3-M1.**
- (b) **P2-M3** — précision + feel caméra : **PASS**. Découverte des preuves headless : **l'alignement sur la maille (snap 1024 m) gouverne le bit-exact, pas la magnitude** — offset aligné à 10 M km (0,029 % de canaux ≠) plus proche du bit-exact qu'offset non aligné à 10 000 km (0,925 %) ; les deux indiscernables à l'œil. 1e15 visiblement dégradé (double qui casse).
- (c) **P2-M1** — hot reload Debug live : **PASS** (reload < 1 s confirmé en fenêtre, budget headless 0,9–2,2 ms/passe).

**Env vars du banc (P2-M4)** : `AGAPANTHE_SCENE=grid:NxN` (réplique le modèle en grille — un upload) · `AGAPANTHE_CULL_STATS=1` (mode banc : caméra dans la scène, spin déterministe, log visibles/total + temps + alloc).

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

**Dette issue de P2-M2** (détail : board session 11) — **à traiter en ouverture de M3/M4** :
- 🔴 **Handles sans génération** : `MeshHandle(int)` est un index nu → après un unload/reload (streaming), un handle périmé résoudra **silencieusement une autre ressource** (contredit §3.2 « handle déchargé = erreur »). Types **publics** → corriger **avant** que gameplay/sérialisation s'y accrochent.
- 🔴 **`ResourceRegistry` mono-modèle** : les handles sont des index **relatifs à un registre** → `MeshHandle(0)` de deux modèles se **collisionnent**. **Bloquant pour les 10 000 entités de M4.** Slot-map global (free-list + génération) = **le même changement** que le point ci-dessus. À trancher **avant** d'écrire le culling.
- 🔴 **Tie-break du tri en M4** (*le vrai piège*) : quand `SortKey` portera matériau/pipeline/profondeur, les **ex æquo deviendront la norme** et leur ordre suivra l'itération Arch (non déterministe) → **le déterminisme byte-identique se perdra silencieusement**. La clé 64-bit doit **inclure un tie-break stable en bits de poids faible** (`RenderOrder`/`GlobalId`) — un tri stable ne suffit pas.
- 🟠 **Séquencement M4 contre-intuitif** : le fit d'ombre sur **frustum caméra** (§3.5) doit précéder (ou accompagner) la bascule `Bounds` monde→locale — sinon la boîte (plus lâche) **déplace silencieusement la matrice d'ombre**. `ImportedEntitySpec` devra porter une AABB **locale**.
- 🟠 **Propagation O(n·d)** : chaque entité re-walke toute sa chaîne parent (+ scan linéaire du walk-stack). N'alloue pas (gate vert) mais **catastrophique à 10k** → passe unique ordonnée par profondeur en M4.
- 🟠 **`Parent` pendant** : dès que la destruction d'entités arrivera (M3), un `Parent` vers une entité morte → exception, ou **pire** : lecture silencieuse d'une **autre** entité (recyclage d'ids). À traiter avec l'API de destruction.
- 🟠 **`SortByKey` = insertion sort O(n²)** → radix LSD 64-bit (scratch réutilisé) en M4.
- 🟡 **`DrawScene` à 8 paramètres**, dont `sceneBounds` **transitoire** → introduire un agrégat **`RenderView`** en M3 : il portera l'**origine camera-relative**, le point le plus facile à désynchroniser (lumières, caméra et monde doivent soustraire **exactement** la même origine, sinon dérive silencieuse).
- 🟡 **`AggregateBounds` ne doit PAS devenir un système par frame** : après le fit caméra, plus aucun lecteur → **requête à la demande**, pas un maillon de la chaîne §3.5 (défaut de la spec, pas du code).
- 🟡 **Gates CI manquants** : assertion **automatique** du byte-identique (SHA vérifié à la main aujourd'hui) ; `publish -p:TrimmerSingleWarn=false` périodique **assertant que les assemblies fautives sont exactement `{Collections.Pooled}`** (sinon `NoWarn IL3053` masquera un futur tiers).
- 🟡 **Zéro-alloc à re-mesurer en M3** : le test valide un monde à **archétypes figés** ; créer/détruire des entités en jeu allouera côté Arch. · `AotRootingSmoke` exerce les types **en dur** → le piloter par `All` avant d'ajouter un composant.
- Reste hérité de M8 (voir « Dette d'ouverture Phase 2 (issue de M8) » plus haut) : invariant du reload par convention, feel souris/labels RenderDoc non observés, crash GLFW shutdown upstream, etc.
