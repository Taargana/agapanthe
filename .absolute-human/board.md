# Absolute-Human Board — Agapanthe Session 1 (M0+M1)

**Status**: in-progress
**Créé**: 2026-07-02
**Spec**: docs/plans/2026-07-02-graphics-engine-design.md (approuvée, review 4.8/5)
**Persistance board**: git-tracked (défaut, user AFK — audit trail)

## Intake (hérité du brainstorm absolute-brainstorm — pas de re-interview)

- **Problème**: moteur de jeu Vulkan C# from scratch, phase graphique 3D. Session 1 = jalons M0 (fondations) + M1 (triangle).
- **Succès**: Sandbox affiche un triangle sur macOS/MoltenVK, resize OK, validation layers propres, rapport ResourceTracker vide.
- **Contraintes**: voir spec §2/§3 (Silk.NET, Vulkan 1.2+dynamic_rendering+sync2, portability macOS, ownership §3.2, .NET 10, xUnit).
- **Scope budget**: M0→M8 complet dépasse le budget → sessions par jalon. Session 1 = M0+M1 (~11 tâches S/M).

## Project Conventions

- .NET 10 (SDK 10.0.103 présent), xUnit, solution `Agapanthe.sln`, src/ + samples/ + tests/ + shaders/.
- Vulkan runtime machine dev: MoltenVK 1.4.1 + loader + validation layers via Homebrew (ICD auto-découvert, `VK_LAYER_PATH` non requis — layer visible par défaut).
- Nullable enable, ImplicitUsings enable, unsafe autorisé dans Graphics.

## Rollback Point

Commit initial (scaffold) — renseigné après T1: `<hash>`

## Task Graph

```
T1 (scaffold) ─► T2 (Core) ─► T3 (Platform window)
                    │      └► T4 (Instance+debug) ─► T5 (Device+queues) ─► T6 (Swapchain)
                    │                                        └──────────► T7 (Shaderc)
T6,T7 ─► T8 (Triangle: pipeline+sync+frame loop) ─► T9 (Sandbox wiring)
T9 ─► T10 (Self code review) ─► T11 (Requirements validation) ─► T12 (Full verification)
```

## Waves

| Wave | Tâches |
|---|---|
| 1 | T1 |
| 2 | T2 |
| 3 | T3, T4 |
| 4 | T5 |
| 5 | T6, T7 |
| 6 | T8 |
| 7 | T9 |
| 8 | T10, T11, T12 (tail) |

## Tâches

### T1 — Scaffold solution [type: infra, S] — status: in-progress
git init, .gitignore, Agapanthe.sln, projets Core/Platform/Graphics/Rendering/Assets/Sandbox/Tests, refs unidirectionnelles (spec §3.1), packages (Silk.NET.Windowing/Input/Vulkan/Shaderc + natives, StbImageSharp, xUnit), Directory.Build.props. AC: `dotnet build` vert, `dotnet test` vert (0 tests OK).

### T2 — Core: MathHelpers + ResourceTracker + logging [type: code, M] — status: pending
Deps: T1. `MathHelpers` (PerspectiveVulkan Y↓/Z[0,1], LookAt), `Log` minimal, `ResourceTracker` (compteurs par type, DEBUG, rapport fermeture). Tests: projections vs valeurs de référence, tracker leak/no-leak. AC: tests verts.

### T3 — Platform: EngineWindow [type: code, M] — status: pending
Deps: T2. Wrap Silk.NET.Windowing : création, boucle, resize events, extensions d'instance requises + création surface via handles opaques (pas de type Vk* public). AC: fenêtre s'ouvre/ferme (validé via Sandbox en T9).

### T4 — Graphics: Instance + validation + debug utils [type: code, M] — status: pending
Deps: T2. VkInstance avec portability_enumeration + flag (macOS), VK_LAYER_KHRONOS_validation en DEBUG, debug messenger (callback gardé vivant, ERROR = fail fast), GraphicsException (VkResult check). AC: instance créée sans message de validation.

### T5 — Graphics: PhysicalDevice + Device + queues [type: code, M] — status: pending
Deps: T4. Sélection physical device (score discrete>integrated, queues graphics+present), device avec dynamic_rendering + synchronization2 + portability_subset si présent, récupération queues. AC: device créé, features actives.

### T6 — Graphics: Swapchain [type: code, M] — status: pending
Deps: T5. Surface (via Platform), formats sRGB préférés, FIFO, image views, recréation sur resize/OUT_OF_DATE (WaitIdle, spec §3.3), semaphores render-finished per-image. AC: swapchain créée + recréée sans validation error.

### T7 — Graphics: ShaderCompiler (shaderc) + ShaderModule [type: code, M] — status: pending
Deps: T5. Compilation GLSL→SPIR-V runtime via Silk.NET.Shaderc, cache disque (hash source — includes en session M8), ShaderModule. Tests: cache hit/miss (logique hash sans GPU). AC: vert/frag du triangle compilés.

### T8 — Graphics: pipeline + frame loop + triangle [type: code, M] — status: pending
Deps: T6, T7. GraphicsPipeline (dynamic rendering), command pool/buffers, FrameContext ×2 (fence, image-available), DeletionQueue, boucle acquire→record→submit(sync2)→present. AC: triangle visible.

### T9 — Sandbox wiring [type: code, S] — status: pending
Deps: T8. samples/Sandbox : Program.cs assemblant Platform+Graphics, triangle, fermeture propre → rapport ResourceTracker. AC: run manuel OK sur macOS.

### T10 — Self code review [type: test, S] — status: pending
Deps: T9. Audit csharp-lowlevel (leaks, delegates, allocations hot path) + revue engine-architect (API Graphics, spec §7). AC: findings critiques = 0.

### T11 — Requirements validation [type: test, S] — status: pending
Deps: T10. Chaque exigence M0/M1 de la spec cochée contre le code. AC: toutes couvertes ou déférées documentées.

### T12 — Full project verification [type: test, S] — status: pending
Deps: T11. `dotnet build` (0 warning bloquant), `dotnet test`, run Sandbox : zéro validation error, ResourceTracker vide, resize OK. Sortie collée au board. AC: tout vert.

## Deferred Work

- Hot reload includes watch (M8), Mailbox present mode, timeline internes.

## Log

- 2026-07-02: env vérifié (.NET 10.0.103 ✓, MoltenVK 1.4.1 installé via brew ✓, validation layer ✓). Session démarrée.
