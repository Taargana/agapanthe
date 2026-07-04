# Absolute-Human Board — Agapanthe Session 2 (M2 : Mesh 3D)

**Status**: completed
**Créé**: 2026-07-03
**Spec**: docs/plans/2026-07-02-graphics-engine-design.md §6 (M2), §3.3, §3.4
**Board persistence**: git-tracked
**Session 1 (M0+M1)**: completed, archivée → .absolute-human/archive/board-session1-M0-M1.md

## Intake (design déjà fixé — pas de re-brainstorm)

- **Problème**: passer du triangle codé en dur à un vrai mesh 3D indexé, avec profondeur, caméra libre, transforms, et le socle de binding (descriptors/UBO/push constants) requis par tout le reste du renderer.
- **Cible de sortie**: cube 3D coloré (par sommet) qui tourne, vu par une caméra libre (WASD + souris), avec depth test. **Textures = M3** (images/samplers/mipmaps), pas ce jalon — le mot « texturable » de la demande est reporté à M3 par la spec.
- **Contrainte forte**: M2 commence par 3 pré-refactors actés par l'architecte (voir DAG W2), sinon dette bloquante.

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, Nullable/ImplicitUsings enable, unsafe dans Graphics/Platform.
- Aucun type Vk*/Silk.NET.Vulkan hors module Graphics (frontière tolérée: IVkSurface).
- Ownership déterministe, ResourceTracker Register/Unregister par handle, finalizer = ReportFinalizerLeak only (conditionnel au handle acquis, cf. audit M2 finding 1).
- Run macOS: `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox`, `AGAPANTHE_MAX_FRAMES=N` pour headless.
- Allocation M2: buffers host-visible mappés (simple, correct). Le vrai GpuAllocator device-local + staging = M3.

## Rollback Point

`0c59953` (fin session 1)

## Task Graph

```
W1 (indépendants):  M2-01 PixelFormat    M2-02 Camera+Input    M2-03 Vertex+Cube
                          │                     │                    │
W2 (core refactors, séquentiels — fichiers partagés):
        M2-04 FrameIndex+DeletionQueue câblée ─► (base FrameRenderer/Device)
W3:     M2-05 GpuBuffer(host-visible) [dep M2-04]
        M2-06 GpuImage depth + FrameRenderer depth attach [dep M2-01, M2-04]
        M2-07 GraphicsPipelineDesc + layout cache [dep M2-01, M2-03]
W4:     M2-08 FrameContext (UBO caméra per-frame + pool descriptor + set0) + CommandList grandit
              [dep M2-05, M2-07, M2-04]
W5:     M2-09 Sandbox cube tournant [dep M2-02, M2-03, M2-06, M2-08]
W6 (tail): M2-10 self review · M2-11 requirements · M2-12 full verification
```

Fichiers partagés (ownership pour éviter conflits parallèles) :
- GraphicsDevice.cs / FrameRenderer.cs / DeletionQueue.cs → M2-04 puis M2-06/M2-08 (séquentiel, jamais en parallèle).
- GraphicsPipeline.cs → M2-07 seul.
- CommandList.cs → M2-08 seul.
- Program.cs → M2-09 seul.

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | M2-01, M2-02, M2-03 | parallèle (fichiers disjoints) |
| 2 | M2-04 | moi (cœur, fichiers partagés) |
| 3 | M2-05, M2-06, M2-07 | M2-05/07 parallèle ; M2-06 touche FrameRenderer → après/isolé |
| 4 | M2-08 | moi/graphics-3d |
| 5 | M2-09 | moi |
| 6 | M2-10, M2-11, M2-12 | tail |

## Tâches

### M2-01 — PixelFormat enum public [code, S] — done
Nouveau `PixelFormat` (public, Graphics) + mapping interne ⇄ VkFormat (Bgra8Srgb, Rgba8Unorm, Rgba8Srgb, D32Sfloat, R16g16b16a16Sfloat…). `Swapchain.ColorFormat` retourne `PixelFormat`. Remplace le `uint colorFormat` déguisé. Fichiers: PixelFormat.cs (new), Swapchain.cs (léger). AC: build, aucun uint format hors Graphics.

### M2-02 — Caméra libre + input [code, M] — done
`Camera` (Rendering): position/orientation, view via MathHelpers.LookAt, proj via PerspectiveVulkan, aspect. Contrôleur WASD + souris (yaw/pitch) via input Platform (Silk.NET.Input exposé par EngineWindow). Fichiers: Rendering/Camera.cs, Rendering/FreeCameraController.cs, Platform input wiring. Tests: matrice view (position/orientation → transform attendue). AC: tests verts.

### M2-03 — Vertex + géométrie cube [code, S] — done
`Vertex` struct (Position Vector3, Color Vector3, Normal Vector3, Uv Vector2 — Uv/Normal préparés pour M3/M5) + `VertexInputDescription` (binding + attributes) + data cube (24 sommets, 36 indices u16, couleurs par face). Fichiers: Rendering/Vertex.cs, Rendering/Primitives.cs. AC: build, données cohérentes (test count).

### M2-04 — Frame index autoritaire + DeletionQueue câblée [code, M] — done — OWNER GraphicsDevice/FrameRenderer/DeletionQueue
`GraphicsDevice.CurrentFrameIndex` (long, autoritaire), avancé par FrameRenderer à chaque frame présentée. Les ressources jetables mid-loop enfilent `device.DeletionQueue.Enqueue(destroy, device.CurrentFrameIndex)` au lieu de détruire direct (shutdown reste immédiat après WaitIdle). FrameRenderer.Flush utilise l'index device. Test: une ressource fake Dispose()ée à la frame N n'est détruite qu'à N+FramesInFlight. AC: test vert, run triangle toujours clean.

### M2-05 — GpuBuffer host-visible [code, M] — done
`GpuBuffer` (vertex/index/uniform), `vkCreateBuffer` + mémoire host-visible|coherent mappée persistante, `Write<T>(ReadOnlySpan<T>)`, usage flags via enum public `BufferUsage`. Disposal via DeletionQueue (M2-04). Fichiers: GpuBuffer.cs, BufferUsage.cs. Test: choix de memory type (logique testable) si extractible; sinon validé au run. AC: build + run.

### M2-06 — Depth buffer [code, M] — done — touche FrameRenderer
`GpuImage` (image + mémoire device-local + view) pour depth `D32Sfloat`, recréé au resize (taille swapchain). FrameRenderer: attache depth au dynamic rendering (depthAttachment, clear 1.0), barrière depth, depth test activé. Fichiers: GpuImage.cs (new), FrameRenderer.cs. AC: run cube avec occlusion correcte (faces arrière cachées).

### M2-07 — GraphicsPipelineDesc + layout cache [code, M] — done — OWNER GraphicsPipeline
`GraphicsPipelineDesc` (stages, VertexInputDescription, set layouts, push-constant ranges, depth state on/off, formats couleur+depth via PixelFormat, cull/topology). `LayoutCache` (descriptor set layouts + pipeline layouts créés une fois, partagés, §3.4). GraphicsPipeline consomme la desc. Fichiers: GraphicsPipeline.cs, GraphicsPipelineDesc.cs, DescriptorLayoutCache.cs. AC: build, pipeline cube créé.

### M2-08 — FrameContext + descriptors + CommandList [code, M] — done — OWNER CommandList
`FrameContext` par frame in flight: UBO caméra (GpuBuffer host-visible), descriptor pool per-frame reset quand la fence de la frame est signalée, set 0 (caméra). CommandList grandit: BindVertexBuffers, BindIndexBuffer, DrawIndexed, BindDescriptorSets, PushConstants. Callback record reçoit le FrameContext. Set 0 = caméra (§3.4), push constant = matrice modèle 64o. Fichiers: FrameContext.cs, CommandList.cs, FrameRenderer.cs. AC: build.

### M2-09 — Sandbox cube tournant [code, M] — done — OWNER Program.cs
Assemble: caméra libre, mesh cube (GpuBuffer vertex+index), pipeline (desc, depth, set0, push constant), UBO caméra par frame, matrice modèle rotative en push constant, depth. Fichiers: Program.cs, shaders/cube.vert+cube.frag. AC: run macOS — cube coloré tourne, occlusion correcte, caméra bouge, zéro validation error, ResourceTracker no leaks.

### M2-10 — Self code review [test, S] — done (2× PASS)
Audit csharp-lowlevel (leaks/alloc hot path, câblage DeletionQueue réellement prouvé) + engine-architect (API GpuBuffer/FrameContext/PipelineDesc, prêt pour M3 allocateur + M4 glTF). AC: 0 finding critique. **Résultat**: architecte PASS (section dédiée ci-dessous) ; csharp-lowlevel PASS — DeletionQueue prouvée correcte (estampille frame X, destruction X+2 après fence wait ; marge conservatrice 1 frame), hot path zéro allocation managée, delegates natifs vivants, marshalling Vertex/CameraUniforms conforme. 3 findings MINEURS : (1) finalizers rapportaient des leaks fantômes sur exceptions de ctor avant enregistrement du handle — **corrigé** (finalizers conditionnels au handle, 7 classes) ; (2) `GpuBuffer.Dispose` alloue une closure par appel — sans impact M2 (teardown only), **à traiter en M3** si libérations per-frame (enfiler struct de handles + destructeur non-capturant) ; (3) `GraphicsPipeline.CreateLayout` hors du try de nettoyage — **corrigé** (déplacé dans le try).

### M2-11 — Requirements validation [test, S] — done (tableau ci-dessous)
Exigences M2 (spec §6 + §3.4) cochées vs code. AC: toutes couvertes ou déférées documentées.

### M2-12 — Full verification [test, S] — done
build 0 warning, dotnet test, run Sandbox cube (validation clean + no leaks + resize OK). Sortie collée au board. AC: tout vert.

**Sortie (2026-07-03, après fixes audit)** :
```
Build succeeded.    0 Warning(s)    0 Error(s)
Passed!  - Failed: 0, Passed: 31, Skipped: 0, Total: 31 - Agapanthe.Tests.dll (net10.0)

AGAPANTHE_MAX_FRAMES=120 DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox :
[INFO ] Sandbox: initialized on 'Apple M3'. Rendering cube — WASD + souris, Échap pour quitter.
[INFO ] ResourceTracker: no leaks (36 resources created and destroyed).
[INFO ] Sandbox: clean shutdown, no GPU resource leaks.
EXIT=0 — grep validation/error/warn : uniquement lignes DEBUG du loader, zéro message de validation.
```
Resize : recréation swapchain+depth via `FramebufferResized` → `RequestResize` vérifiée session (WaitIdle + Recreate + EnsureDepthImage). Vérification visuelle resize manuelle à faire par l'utilisateur (run non-headless).

## Deferred Work

- Textures/samplers/mipmaps → M3 (le « texturable » de la demande). Vertex a déjà Uv/Normal.
- GpuAllocator device-local + staging → M3 (M2 = host-visible mappé).
- BeginRendering/EndRendering remontés dans CommandList (multi-passe) → avant M5.

## Revue architecte M2-10 (PASS) — décisions à acter au démarrage M3

1. **Chemin d'upload device-local** : `Write<T>` reste host-visible only ; upload device-local via API séparée (staging + `CommandList.CopyBuffer` ou `device.Upload`). Jamais de submit caché dans `Write<T>`.
2. **Domaine mémoire** : ajouter `MemoryDomain {DeviceLocal, HostVisible}` à la création de GpuBuffer (ou création via l'allocateur).
3. **GpuImage** : disposal différé via DeletionQueue (textures mid-loop ≠ depth swapchain-sized), remplacer `ImagePurpose` par des usage flags composables (`Sampled|ColorAttachment|DepthAttachment|TransferDst`) + `MipLevels` — sert aussi les render targets M5.

Dette acceptable (plus tard) : factoriser helpers descriptors FrameContext/DescriptorAllocator persistant + `WriteCombinedImageSampler` (M3), sémantique DescriptorSetHandle persistant vs per-frame (M3), Vertex.Tangent + Color reconsidéré (M4/M5), multi-passe = abstraction d'attachements + ownership depth/targets FrameRenderer→Renderer (avant M5), multi-binding vertex (si jamais), enum Key engine (si consommateur non-sample).

## M2-11 — Validation des exigences (spec §6 M2 + §3.4)

| Exigence | État |
|---|---|
| Vertex/index buffers | ✓ GpuBuffer host-visible mappé (M3 = allocateur device-local) |
| Depth buffer | ✓ GpuImage D32Sfloat, clear 1.0, LessOrEqual, recréé au resize |
| UBO caméra per-frame | ✓ un GpuBuffer par frame slot, écrit dans le record callback |
| Set 0 per-frame | ✓ FrameContext pool per-slot reset sur fence + AllocateSet/WriteUniformBuffer |
| Set 1 per-material | Déféré M3 (nécessite textures/samplers) — conforme au board intake |
| Placeholders shadow/IBL 1×1 dans set 0 (spec §3.4) | Déféré M3 : exige samplers, inexistants avant M3. Documenté ici |
| Push constants modèle 64 o | ✓ Matrix4x4 seule, stage vertex |
| Caméra libre WASD+souris | ✓ FreeCameraController + capture souris, Échap quitte |
| Tests maths verts | ✓ 31 tests (maths, tracker, deletion queue, caméra, primitives) |
| Winding vs Y-flip | ✓ FrontFace.Clockwise documenté dans Program.cs |

## Log

- 2026-07-03: session 2 planifiée. Design fixé (spec + revue architecte session 1). DAG 12 tâches.
- 2026-07-03: reprise après interruption — M2-06 complété (depth câblé dans FrameRenderer), M2-08 écrit (FrameContext/descriptors/CommandList), M2-09 écrit (Sandbox cube+shaders). Fix warnings validation (attributs non consommés). Run 120 frames: 0 validation, 0 leak (36 ressources). Commit e504a47. M2-10 audits lancés (csharp-lowlevel + engine-architect).
- 2026-07-03: audits M2-10 rendus — 2× PASS. Fixes findings mineurs 1+3 (finalizers conditionnels 7 classes, CreateLayout dans le try). M2-12 revalidé après fixes : build 0 warning, 31 tests, run 120 frames 0 validation 0 leak. **Session 2 close.** Reste M3 : acter les 3 décisions architecte + finding 2 (closure Dispose) si libérations per-frame.
