# Absolute-Human Board — Agapanthe Session 1 (M0+M1)

**Status**: completed (2026-07-03)
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

`c78c8c9` (scaffold initial)

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

### T1 — Scaffold solution [type: infra, S] — status: done (commit c78c8c9, build+test verts)
git init, .gitignore, Agapanthe.sln, projets Core/Platform/Graphics/Rendering/Assets/Sandbox/Tests, refs unidirectionnelles (spec §3.1), packages (Silk.NET.Windowing/Input/Vulkan/Shaderc + natives, StbImageSharp, xUnit), Directory.Build.props. AC: `dotnet build` vert, `dotnet test` vert (0 tests OK).

### T2 — Core: MathHelpers + ResourceTracker + logging [type: code, M] — status: done (9 tests verts)
Deps: T1. `MathHelpers` (PerspectiveVulkan Y↓/Z[0,1], LookAt), `Log` minimal, `ResourceTracker` (compteurs par type, DEBUG, rapport fermeture). Tests: projections vs valeurs de référence, tracker leak/no-leak. AC: tests verts.

### T3 — Platform: EngineWindow [type: code, M] — status: done (build propre, run validé en T9)
Deps: T2. Wrap Silk.NET.Windowing : création, boucle, resize events, extensions d'instance requises + création surface via handles opaques (pas de type Vk* public). AC: fenêtre s'ouvre/ferme (validé via Sandbox en T9).

### T4 — Graphics: Instance + validation + debug utils [type: code, M] — status: done (commit, run clean)
Deps: T2. VkInstance avec portability_enumeration + flag (macOS), VK_LAYER_KHRONOS_validation en DEBUG, debug messenger (callback gardé vivant, ERROR = fail fast), GraphicsException (VkResult check). AC: instance créée sans message de validation.

### T5 — Graphics: PhysicalDevice + Device + queues [type: code, M] — status: done
Apple M3, 1.3 core features (dynamic_rendering+sync2), portability_subset actif.

### T6 — Graphics: Swapchain [type: code, M] — status: done
B8G8R8A8Srgb, FIFO, 3 images, per-image render-finished semaphores, recreate WaitIdle.

### T7 — Graphics: ShaderCompiler (shaderc) + ShaderModule [type: code, M] — status: done (4 tests cache/erreur verts)

### T8 — Graphics: pipeline + frame loop + triangle [type: code, M] — status: done
GraphicsPipeline dynamic rendering, FrameRenderer (2 frames in flight, sync2 barriers+submit2), DeletionQueue.

### T9 — Sandbox wiring [type: code, S] — status: done
Run macOS: 120 frames, zéro validation error, ResourceTracker no leaks (22 res), exit 0.

### T10 — Self code review [type: test, S] — status: done
Audits csharp-lowlevel + engine-architect : aucun finding CRITIQUE, fondations saines.
Corrigés : closure de draw hoistée (zéro-alloc hot path §3.2.5) ; throw dans debug callback natif → Environment.FailFast (§4, évite UB reverse-P/Invoke) ; SuppressFinalize sur échec de ctor (faux positif leak) sur les 6 types ; allocs shaderc déplacées dans le try (OOM) ; try/finally teardown Sandbox. Ignoré : pinning fence par-frame (négligeable à N=2). Reporté : câblage DeletionQueue → entrée M2 (voir "Pré-refactor avant M2").

### T11 — Requirements validation [type: test, S] — status: done

Exigences M0/M1 (spec §6) vs code :
- Solution .NET 10 multi-projets + refs unidirectionnelles ✓
- Fenêtre (ouverture/resize/close) ✓ (EngineWindow, resize → recreate swapchain validé)
- Instance + portability_enumeration ✓ | device + queues ✓ | swapchain sRGB ✓
- ResourceTracker (compteurs + finalizer leak) ✓, rapport vide au run ✓
- Triangle affiché ✓ (dynamic rendering) | shaderc runtime + cache ✓ | 2 frames in flight + sync2 ✓
Contraintes transverses (§2/§3.2/§3.3/§4) :
- Baseline 1.2 + dynamic_rendering + sync2 ✓ (1.3 core sur MoltenVK) | portability_subset ✓
- Validation ERROR = fail fast ✓ | ownership déterministe, pas de GC pour GPU ✓
- DeletionQueue N+FramesInFlight ✓ | render-finished per-image ✓
Déféré (documenté) :
- Debug utils object names → M8 (spec : "debug labels RenderDoc")
- Hot reload includes → M8 | zéro-alloc hot path → confirmé/corrigé en T10

### T12 — Full project verification [type: test, S] — status: done (build 0 warning, 13 tests verts, Sandbox exit 0 — voir Log)

## Deferred Work

- Hot reload includes watch (M8), Mailbox present mode, timeline internes.

## Pré-refactor à faire AVANT M2 (revue engine-architect — décisions d'API publique)

Fondations jugées saines, aucun rewrite. Mais 3 surfaces coûteuses à changer une fois M2 posé, à trancher avant la 1re ligne de M2 :
1. **Index de frame autoritaire → GraphicsDevice + câbler DeletionQueue pour de vrai** : aujourd'hui la queue N+2 est correcte mais jamais alimentée (tout se détruit au shutdown après WaitIdle). Une ressource jetable doit stamper `device.DeletionQueue.Enqueue(destroy, device.CurrentFrameIndex)`. Contrat §3.2 non prouvé tant que non câblé. (FrameRenderer/DeletionQueue/GraphicsDevice)
2. **FrameContext** (UBO per-frame + pool descriptor per-frame reset sur fence, §3.3/§3.4) + changer la signature du callback record pour exposer le contexte de frame. (FrameRenderer/CommandList)
3. **GraphicsPipelineDesc + cache de set/pipeline layouts partagés (§3.4) + enum PixelFormat public** (remplace le `uint colorFormat` = VkFormat déguisé, bloque choix de format hors Graphics dès depth M2). (GraphicsPipeline/Swapchain)

Additif propre (pas de casse) : croissance CommandList (vertex/index/descriptors/push), attache depth M2. Avant M5 : remonter BeginRendering/EndRendering dans CommandList pour le multi-passe offscreen (direction actée, implémentation différée).

## Log

- 2026-07-02: env vérifié (.NET 10.0.103 ✓, MoltenVK 1.4.1 installé via brew ✓, validation layer ✓). Session démarrée.
- 2026-07-03: session 1 terminée. 12 tâches done. Preuves de sortie :
  - `dotnet build` : 0 warning (TreatWarningsAsErrors), 0 error.
  - `dotnet test` : 13/13 verts (maths clip-space Vulkan, ResourceTracker, shaderc cache/erreur).
  - Sandbox macOS/MoltenVK (Apple M3) : triangle rendu, 120 frames, ZÉRO erreur de validation, `ResourceTracker: no leaks (22 resources)`, exit 0. Re-validé après fixes d'audit.
  - Commits sur `main` : c78c8c9 (scaffold), 187d6fa (Core/Platform/shaders), + Graphics bootstrap, + shaders/triangle, + fixes audit.
  - Rollback point : c78c8c9.
- Prochaine session (M2) : commencer par les 3 pré-refactors listés ci-dessus, puis mesh 3D + depth + UBO per-frame + descriptors + caméra libre.
