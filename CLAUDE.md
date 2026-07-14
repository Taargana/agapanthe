# Agapanthe — contexte projet (à lire en premier)

Moteur de jeu **Vulkan en C# from scratch** (.NET 10), cross-platform (Windows / Linux / macOS).
**Phase 1 (TERMINÉE, 8/8)** : chaîne graphique 3D. **Phase 2 (TERMINÉE, 5/5)** : fondations scalables — ECS (Arch), `double` + camera-relative à origine quantifiée, culling, montée en charge (10k entités à 10 000 km, NativeAOT). **Phase 3 (à venir)** : gameplay — lifecycle/scheduler, physique, sérialisation, audio.

## Où est la vérité (lis ces fichiers avant d'agir)

- **`docs/AVANCEMENT.md`** — état d'avancement, jalons, métriques, plan du prochain jalon, **section « Reprise — où repartir »**. C'est le point d'entrée pour reprendre le travail.
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

**Prochain : Phase 3** (gameplay). Ordre recommandé (engine-architect) : **P3-M0 validation Linux/macOS** (les titres de la Phase 2 — AOT + SPIR-V hors-ligne — sont **prouvés Windows uniquement**), puis buffer d'instances persistant + dettes de culling, puis lifecycle/scheduler, physique, sérialisation, audio. Détail + point de reprise dans `docs/AVANCEMENT.md`.

**P3-M1 (session 14, clos)** : instancing via SSBO (draw calls **12 556 → 2** à 10k, batching par (matériau, mesh), `firstInstance`) + les **2 dettes de culling de M4 soldées** (`AggregateBounds` par frame, `ExtrudedShadowFrustum` ANDé au volume de lumière). Double audit PASS, findings appliqués. 284 tests, 0 alloc/frame, AOT PASS.

**Dette persistante** : 🔴 **Linux jamais validé** · 🔴 l'**ordre de frame vit dans le Sandbox** (dette #1 soldée à l'appel, pas dans le moteur → scheduler) · 🔴 `ShadowFit.UpstreamExtent` dérive des bounds **globales** (mord dès la physique) · 🔴 le cull GPU (P3-M2) impose `DrawIndexedIndirect` + `drawIndirectFirstInstance` — **pas** « une ligne de shader » · 🟠 `SortKey` sans profondeur · 🟠 plafond 16 bits mesh/matériau · crash au shutdown (GLFW/Silk.NET) **reproductible** via `AGAPANTHE_UNLOAD_TEST=20` (~2 runs/10, après le rapport propre).
