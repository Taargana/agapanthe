# Agapanthe — contexte projet (à lire en premier)

Moteur de jeu **Vulkan en C# from scratch** (.NET 10), cross-platform (Windows / Linux / macOS).
**Phase 1 (TERMINÉE, 8/8)** : chaîne graphique 3D. **Phase 2 (TERMINÉE, 5/5)** : fondations scalables — ECS (Arch), `double` + camera-relative à origine quantifiée, culling, montée en charge (10k entités à 10 000 km, NativeAOT). **Phase 3 (en cours)** : gameplay — **P3-M1 instancing** ✅, **P3-M2 scheduler + lifecycle + `Agapanthe.Engine`** ✅, **P3-M3 physique v1** ✅, **P3-M4 rendu GPU-driven** ✅, **P3-M5 CSM** ✅, **P3-M6 slots persistants + cull d'ombre GPU** ✅, **P3-M7 device-local + raster ombre 4×** ✅, **P3-M8 premier pas planétaire (reversed-Z + scène planète/Soleil 1/2)** ✅ ; cap : **Vertical Slice** (backlog §4ter, ancre planétaire) → **VS-1 sérialisation** ; validation Linux/macOS (P3-M0) toujours dûe.

## Où est la vérité (lis ces fichiers avant d'agir)

- **`docs/AVANCEMENT.md`** — état d'avancement, jalons, métriques, plan du prochain jalon, **section « Reprise — où repartir »**. C'est le point d'entrée pour reprendre le travail.
- **`docs/BACKLOG.md`** — ce qu'on sait devoir faire *plus tard*, avec le pourquoi et **quand ça mord** (CSM, rendu GPU-driven, nuages volumétriques, atmosphère, ombres analytiques planétaires, physique…). À lire avant de choisir un jalon ; à mettre à jour quand une décision technique est instruite.
- **`docs/plans/2026-07-02-graphics-engine-design.md`** — spec de référence de la phase graphique (§ numérotés, cités partout).
- **`.absolute-human/board.md`** — board de la session courante ; sessions passées dans `.absolute-human/archive/board-sessionN-*.md`.
- **`docs/visual-checks/`** — protocoles de validation visuelle par jalon (verdicts humains).

> La mémoire persistante de Claude (`~/.claude/…/memory/`) **ne voyage pas avec le dépôt**. Sur un nouvel appareil, tout le contexte nécessaire est dans les docs ci-dessus — commence par `docs/AVANCEMENT.md`.

## Décisions structurantes (verrouillées — ne pas re-litiger)

- Bindings **Silk.NET** (Vulkan + GLFW + input) ; le reste from scratch.
- Baseline **Vulkan 1.2 + dynamic_rendering + synchronization2** (chemin 1.3 core sur MoltenVK).
- Maths **System.Numerics** (convention row-vector) + helpers clip-space Vulkan (Y-flip, Z [0,1]).
- Shaders **GLSL → SPIR-V à l'exécution (shaderc)**, cache disque.
- Couche GPU **mince mono-backend** : **aucun type `Vk*` ne sort de `Agapanthe.Graphics`**.
- Allocateur GPU from scratch ; **IDisposable partout**, destruction différée N+2 frames (DeletionQueue non-capturante), **zéro alloc managée par frame** sur le hot path, **ResourceTracker (tout leak = échec du run)**.
- **Tout message de validation layer = bug.** `.NET 10`, `TreatWarningsAsErrors`.
- MoltenVK portability : pas de comparateur mutable, pas d'imageCubeArray (vérifier chaque feature au premier VUID).

## Modules

```
Sandbox ──► Rendering ──► Graphics ──► Core   (Graphics = seul projet référençant Silk.NET.Vulkan)
                └────► Assets ──► Core         (GPU-free : parsing testable sans GPU)
Platform ──► Core                              (fenêtre GLFW, input)
```

## Commandes

```sh
# prérequis macOS : brew install molten-vk vulkan-loader vulkan-validationlayers vulkan-tools
dotnet build
dotnet test
# Sandbox (le loader Vulkan Homebrew est hors des chemins GLFW par défaut → DYLD_LIBRARY_PATH)
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox -- MetalRoughSpheres.glb
```

Env vars debug : `AGAPANTHE_MAX_FRAMES=N` (auto-close, sortie 0 si 0 leak) · `AGAPANTHE_CAPTURE=out.ppm` (dump HDR tonemappé) · `AGAPANTHE_VIEW="x,y,z"` (angle caméra) · `AGAPANTHE_HDRI=<path.hdr>` (environnement IBL) · `AGAPANTHE_IBL_TEST=<préfixe>` (génère l'IBL headless + dump faces/maps) · `AGAPANTHE_SHADER_RELOAD_TEST=1` (force un reload des 4 passes + logge le wall-time).

**Hot reload shaders** (M8) : éditer un fichier de `shaders/` pendant que le Sandbox tourne → recompile + recréation du pipeline en < 1 s. Échec de compile = log + ancien pipeline conservé. Limites : éditions **interface-compatibles** uniquement (changer un binding → restart) ; les 4 shaders compute IBL ne sont pas surveillés.

## Méthode de travail

Sessions pilotées via **absolute-human** (décomposition en tâches, vagues parallèles, TDD). Chaque jalon clôt sur : Sandbox propre (0 message validation, 0 leak), tests verts, **double audit agent** (`csharp-lowlevel` + `engine-architect`) PASS, protocole visuel humain quand pertinent, board archivé.

## Préférences de travail (l'humain)

- **Commits sur demande uniquement** : ne jamais `commit`/`push` sans demande explicite. L'humain pilote le rythme et valide chaque étape.
- **Travail en vagues + feu vert** : décomposer en vagues, attendre l'accord de l'humain entre les étapes, lancer les audits subagents (`csharp-lowlevel` + `engine-architect`) avant de clore un jalon.
- **Vérification visuelle headless** : captures headless (`AGAPANTHE_CAPTURE`) à chaque vague pour prouver le rendu ; **verdict visuel humain** requis avant de marquer un jalon `done`.
- **Gate 0 leak / 0 validation** : tout message de validation layer ou tout leak ResourceTracker est **bloquant**, jamais ignoré ni contourné.
- **Langue** : conversation en **français** (phrases complètes) ; **code, commits, PRs et docs techniques en anglais**. Dans la prose française, les **termes techniques du domaine restent en anglais** (shader baking, culling, frustum, shadow mapping, tie-break, radix sort, lockstep, instance buffer, server meshing…) — ne pas les traduire (« précuit » pour *baking* est à proscrire).

## État courant

**PHASE 1 CLOSE (8/8). PHASE 2 CLOSE (5/5) — P2-M0 → P2-M4 clos** (gate AOT + Arch · SPIR-V hors-ligne · couture ECS · camera-relative · **frustum culling + montée en charge**). Métriques : 275 tests, 0 warning, 0 message de validation, 0 leak, probe NativeAOT PASS. Double audit signe la clôture de la phase.

**P2-M4 (session 13)** : le monde stocke les positions en `double` à **origine quantifiée** (snap 1024 m), ne dessine que ce que voit la caméra (frustum culling), et le prouve à l'échelle — **10 000 entités cullées à 10 000 km, 0 alloc/frame, en NativeAOT**, image bit-identique à l'origine (maille alignée). Env vars : `AGAPANTHE_WORLD_ORIGIN="x,y,z"` · `AGAPANTHE_SCENE=grid:NxN` (banc) · `AGAPANTHE_CULL_STATS=1` (stats) · `AGAPANTHE_UNLOAD_TEST=N`.

**Prochain (session 22+)** : **Vertical Slice** (formalisée S21, [backlog §4ter](docs/BACKLOG.md) — preuve d'intégration, ancre planétaire, Windows d'abord). Commencer par **VS-1 sérialisation** (source-gen, partage le générateur du rooting AOT — save/load `World` round-trip), puis VS-2 spawn runtime, VS-3 glu gameplay, VS-4 HUD, VS-5 audio (stretch). **P3-M0 validation Linux/macOS** reste dûe (non bloquante pour la slice). Détail + point de reprise dans `docs/AVANCEMENT.md` § Reprise.

**P3-M1 (session 14, clos)** : instancing via SSBO (draw calls **12 556 → 2** à 10k, batching par (matériau, mesh), `firstInstance`) + les **2 dettes de culling de M4 soldées** (`AggregateBounds` par frame, `ExtrudedShadowFrustum` ANDé au volume de lumière). Double audit PASS. 284 tests, 0 alloc/frame, AOT PASS.

**P3-M2 (session 15, clos)** : nouveau projet **`Agapanthe.Engine`** (marie World + Rendering, ne référence pas Platform, ne possède rien). L'**ordre de frame quitte le Sandbox** → `FrameOrchestrator` + `SystemScheduler` à étages (`Input→Simulation→PostSimulation→Render`), `Tick` hors `DrawFrame` (D1.a). **Lifecycle** : `Spawn`/`Despawn`/`SetParent`/`IsAlive` différés à une barrière, **`Despawn` cascade**, `EntityRef` = `GlobalId` (file de commandes propre au World, pas le `CommandBuffer` d'Arch). **Cull d'ombre deux passes** : wedge borné en amont (7ᵉ plan, `ShadowCasterDistance`), `UpstreamExtent` depuis les **casters** (plus les bounds globales). Double audit PASS, findings appliqués (garde F7 `_pass1ShadowList`). Banc `grid:100x100` Release+**AOT** : draws 2+2, 0 alloc/frame, 0 leak, **311 tests**, capture bit-identique `9790D95D`. Commit `90627a5`. Env var : `AGAPANTHE_CHURN=N` (stress lifecycle).

**P3-M3 → M8 (sessions 16-21, tous clos, double audit + verdict visuel PASS chacun)** : **M3** physique v1 (corps rigides linéaires déterministes, gravité, sphère↔sol/sphère, broadphase grille 0-alloc) · **M4** rendu GPU-driven (`scene_cull.comp` frustum-cull + compaction atomics, `vkCmdDrawIndexedIndirect`, offset de batch en push constant → MoltenVK-safe ; GPU visible == CPU) · **M5** CSM (4 cascades, atlas 2×2, `castsShadow` par entité) · **M6** slots persistants dirty-trackés (`PersistentInstanceBuffer`, gather+radix sort au rebuild structurel seulement, frame ordinaire O(dirty)) + cull d'ombre GPU (`shadow_cull.comp`) · **M7** buffers device-local (`CommandList.CopyBuffer` async, core `vkCmdCopyBuffer`) + raster d'ombre 4×→1× (7ᵉ plan near-cut, A+B ~15,3 → ~8,0 ms) · **M8** premier pas planétaire : **reversed-Z global** (`PerspectiveVulkanReversed`, comparateur depth **par pipeline** → passe d'ombre découplée) + `Primitives.UvSphere` + scène `AGAPANTHE_SCENE=planet` (planète + Soleil sphère emissive à **7,48e10 m**, échelle **1/2 uniforme**, **point light co-localisée avec la sphère-Soleil** = seule lumière, env noir). 333 tests, 0 alloc/frame, AOT PASS. Env vars : `AGAPANTHE_PHYSICS=1`, `AGAPANTHE_CULL_VERIFY=1`, `AGAPANTHE_SUN`, `AGAPANTHE_PLANET_{RADIUS,PHASE,ALT,FOV}`, `AGAPANTHE_SUN_{DIR,RADIUS,DISTANCE}`.

**Dette persistante** : 🔴 **Linux jamais validé** (premier item P3, non bloquant pour la Vertical Slice) · 🟠 **CSM `_casterSpheres` dans le World** (F7, une `RenderView`/frame — gardée) · 🟠 **`UpstreamExtent` par cascade** déféré (near-cut calé sur épaisseur de tranche, pas longueur d'ombre — soleil très rasant = cas limite ; backlog §2.0bis) · 🟠 **spawn de corps runtime absent** (`SpawnBody` immédiat → besoin `SpawnBodyDeferred`, = **VS-2**) · 🟠 cascade despawn O(profondeur×N) quand un despawn est en attente · 🟠 `SortKey` sans profondeur (+ compaction atomique scramble l'ordre → 2ᵉ verrou transparence) · 🟡 garde normalisation `Frustum` `1e-8` (near sub-mètre) · plafonds **16 bits** mesh/matériau et **`GlobalId < 2³²`** (clé de contact physique) à faire échouer bruyamment · device-local/transfer **non exécuté sur MoltenVK** (P3-M0) · crash shutdown GLFW/Silk.NET via `AGAPANTHE_UNLOAD_TEST=20` (~2 runs/10, après le rapport propre).
