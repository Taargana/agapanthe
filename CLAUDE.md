# Agapanthe — contexte projet (à lire en premier)

Moteur de jeu **Vulkan en C# from scratch** (.NET 10), cross-platform (Windows / Linux / macOS).
**Phase 1 (TERMINÉE, 8/8 jalons)** : toute la chaîne graphique 3D. **Phase 2 (à venir)** : ECS/scene graph, audio, physique, gameplay.

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
- **Langue** : conversation en **français** (phrases complètes) ; **code, commits, PRs et docs techniques en anglais**.

## État courant

**PHASE 1 CLOSE (8/8). PHASE 2 EN COURS — P2-M0 → P2-M3 clos** (gate AOT + Arch · SPIR-V hors-ligne · couture ECS · **camera-relative rendering**). Métriques : 257 tests, 0 warning, 0 message de validation, 0 leak, probe NativeAOT PASS.

**P2-M3 (session 12)** : le monde stocke les positions en `double` (`Double3`) et le GPU ne voit jamais que `objet − caméra`. Preuve : la capture du casque **à 10 000 km est identique bit-pour-bit** à celle prise à l'origine. Env vars : `AGAPANTHE_WORLD_ORIGIN="x,y,z"` (place le modèle en double) · `AGAPANTHE_UNLOAD_TEST=N` (cycles Load/Unload sous le gate de leak).

**Prochain : P2-M4** (frustum culling + montée en charge = **critère de sortie de la phase**). ⚠️ Trois décisions à prendre **avant** d'écrire la boucle de culling (scène large de test · `Bounds` → sphère locale · `ShadowFit` hissé *avant* `CollectRenderLists`), plus un arbitrage qui engage la Phase 3 (origine continue vs quantifiée). Détail + point de reprise dans `docs/AVANCEMENT.md`.

**Dette persistante** : 🔴 **Linux jamais validé** (AOT et chemin SPIR-V hors-ligne **prouvés Windows uniquement**) · tri `RenderList` O(n²) (radix dû en M4) · crash au shutdown (GLFW/Silk.NET) désormais **reproductible** via `AGAPANTHE_UNLOAD_TEST=20` (~2 runs/10, après le rapport de leak propre).
