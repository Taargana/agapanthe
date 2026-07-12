# Agapanthe — contexte projet (à lire en premier)

Moteur de jeu **Vulkan en C# from scratch** (.NET 10), cross-platform (Windows / Linux / macOS).
Phase 1 (en cours) : toute la chaîne graphique 3D. Phase 2 (plus tard) : ECS/scene graph, audio, physique, gameplay.

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

Env vars debug : `AGAPANTHE_MAX_FRAMES=N` (auto-close, sortie 0 si 0 leak) · `AGAPANTHE_CAPTURE=out.ppm` (dump HDR tonemappé) · `AGAPANTHE_VIEW="x,y,z"` (angle caméra) · `AGAPANTHE_HDRI=<path.hdr>` (environnement IBL) · `AGAPANTHE_IBL_TEST=<préfixe>` (génère l'IBL headless + dump faces/maps).

## Méthode de travail

Sessions pilotées via **absolute-human** (décomposition en tâches, vagues parallèles, TDD). Chaque jalon clôt sur : Sandbox propre (0 message validation, 0 leak), tests verts, **double audit agent** (`csharp-lowlevel` + `engine-architect`) PASS, protocole visuel humain quand pertinent, board archivé.

## État courant

**Phase 1 : 7/8 jalons.** M0–M7 livrés (M7 = IBL compute + skybox, validé visuellement). **Prochain : M8** (hot reload shaders + includes, debug labels RenderDoc, confort souris, audit perf/leaks final, validation multi-OS) — dernier jalon avant la close Phase 1. Détail + point de reprise dans `docs/AVANCEMENT.md`.
