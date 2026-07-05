# Absolute-Human Board — Agapanthe Session 5 (M5 : PBR & Lumières)

**Status**: in-progress
**Créé**: 2026-07-05
**Spec**: docs/plans/2026-07-02-graphics-engine-design.md §3.3 (passes), §3.4, §5 (protocole visuel), §6 (M5)
**Board persistence**: git-tracked
**Sessions passées**: S1-S4 → .absolute-human/archive/

## Intake (plan architecte S4 acté — pas de re-brainstorm)

- **Problème**: M4 rend baseColor×facteur directement dans le swapchain, mono-passe, sans éclairage. M5 = éclairage physiquement correct + chaîne HDR.
- **Cible de sortie (spec §6 M5)**: DamagedHelmet en PBR metallic-roughness complet (1 lumière directionnelle + N ponctuelles, normal mapping, AO, emissive), tone mapping — protocole visuel §5 validé (capture vs viewer Khronos même angle, image annotée docs/visual-checks/).
- **Les 7 décisions architecte S4** (détail : archive board S4, section « Revue architecte M4-12 ») :
  1. Multi-passes d'abord : CommandList.BeginRendering/EndRendering/TransitionImage/SetViewport publics ; FrameRenderer = acquire/submit/present/sync ; depth → Renderer ; callback (cmd, frame, swapchainTarget).
  2. HDR target Rgba16Sfloat (ColorAttachment|Sampled) + depth possédés par Renderer, recréés au resize.
  3. Tonemap fullscreen sans vertex buffer (Draw(3)), ACES/Reinhard + exposition, sortie swapchain sRGB, barrier HDR→ShaderReadOnly.
  4. Set 0 : binding 1 UBO lumières (Fragment), caméra Vertex|Fragment, CameraUniforms += position.
  5. mesh.vert : normal matrix = inverse-transpose.
  6. MaterialAsset += NormalScale/OcclusionStrength (schéma les parse déjà) → mrno.z/.w.
  7. Cull Back + validation visuelle du winding.
- **Lumières M5**: 1 directionnelle + jusqu'à 4 ponctuelles (UBO fixe, count actif). Intensités HDR (> 1 autorisé).
- **Anisotropie sampler**: activer la feature device (samplerAnisotropy) si supportée + flip Sampler.AnisotropyFeatureEnabled — dette S3, pertinente maintenant (qualité visuelle M5).

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, aucun type Vk* hors Graphics, zéro alloc managée par frame, ResourceTracker, validation layer = juge de paix.
- Run : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox` (+ `AGAPANTHE_MAX_FRAMES=N`).
- std140 : MaterialUniforms/CameraUniforms testés aux offsets — étendre les tests avec les nouveaux membres.

## Rollback Point

`07f413e` (fin session 4 + doc avancement)

## Task Graph

```
W1: M5-01 CommandList passes publiques + FrameRenderer réduit (couplés, un seul agent)
W2: M5-02 Renderer multi-passes : HDR+depth ownership, passe scène → HDR, passe tonemap → swapchain
W3: M5-03 Set 0 lumières + CameraUniforms position [dep 02]   M5-04 MaterialAsset scale/strength + aniso device [fichiers disjoints]
W4: M5-05 Shader PBR complet (GGX, TBN, AO, emissive) + inverse-transpose + Cull Back [dep 03, 04]
W5: M5-06 Sandbox lumières + exposition [dep 05]
W6 (tail): M5-07 audits · M5-08 requirements · M5-09 verif + protocole visuel (partie humaine : capture utilisateur)
```

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | M5-01 | agent graphics-3d (CommandList + FrameRenderer couplés) |
| 2 | M5-02 | agent graphics-3d (Renderer + shaders tonemap) |
| 3 | M5-03, M5-04 | parallèle (Rendering/set0 vs Assets+Graphics/sampler) |
| 4 | M5-05 | agent graphics-3d (shaders PBR) |
| 5 | M5-06 | Sandbox |
| 6 | M5-07/08/09 | tail |

## Tâches

### M5-01 — CommandList passes publiques + FrameRenderer réduit [code, M] — done — OWNER CommandList.cs + FrameRenderer.cs
**Résultat**: RenderingTypes.cs (ImageLayoutState, AttachmentLoadAction, RenderTargetView — Vk internals cachés, ColorAttachmentInfo/DepthAttachmentInfo/RenderingAttachments, SwapchainTarget) ; CommandList.BeginRendering/EndRendering/SetViewportScissor/TransitionImage (mapping barrières byte-identique au M4, Undefined aspect-aware) ; FrameRenderer réduit (garde acquire/fences/flush/reset/submit/present + transitions swapchain), callback (cmd, frame, SwapchainTarget) ; depth + DepthFormat migrés au Renderer (EnsureDepth par comparaison d'extent, libération via Dispose différé — DestroyImmediately internal inaccessible depuis Rendering, drainé par FlushAll). FrameRenderer.ClearColor conservé mais superseded (contrainte signature-only Program) — **à nettoyer en M5-02**. Runs M4 intacts : 154 tests, helmet+box 0 validation 0 leak.
CommandList gagne : `BeginRendering(in RenderingAttachments)` (struct moteur : color view/loadOp/clear, depth optionnel), `EndRendering()`, `TransitionImage(GpuImage, ImageLayoutState from, to)` (états typés moteur : Undefined/ColorAttachment/DepthAttachment/ShaderReadOnly/TransferSrc/TransferDst — mapping stages/access interne), `SetViewportScissor(w, h)`. Swapchain : la transition present reste dans FrameRenderer (il possède l'image swapchain — expose un type `SwapchainTarget { view, extent }` au callback). FrameRenderer : supprime depth (_depthImage, EnsureDepthImage, DepthFormat), BeginRenderingAndDraw, transitions depth ; callback devient `Action<CommandList, FrameContext, SwapchainTarget>` ; garde acquire/fence/submit/present/transitions swapchain (Undefined→ColorAttachment avant callback, →PresentSrc après). AC: build — Sandbox/Renderer M4 cassés temporairement INTERDIT : adapter Renderer.DrawScene + Program minimalement (passe unique vers swapchain via les nouvelles APIs, depth temporairement dans Renderer ou recréé à l'identique) pour que le run M4 reste clean en fin de tâche. Tests + run 0 validation 0 leak.

### M5-02 — Renderer multi-passes HDR + tonemap [code, L→M] — pending — OWNER Renderer.cs + shaders tonemap
Renderer possède : HDR GpuImage Rgba16Sfloat (ColorAttachment|Sampled, dedicated via seuil), depth D32 (déplacé), recréation au resize (WaitIdle), pipeline tonemap (VertexLayout null, ColorFormat swapchain, ni depth), layout set tonemap (1 combined sampler), sampler clamp, pipeline scène retargeté ColorFormat=Rgba16Sfloat. `DrawScene(scene, camera, cmd, frame, swapchainTarget)` : transition HDR→ColorAttachment, passe 1 (HDR+depth, clear) DrawScene existant, transition HDR→ShaderReadOnly, passe 2 (swapchain, no depth) bind tonemap + set (alloué per-frame via FrameContext.WriteCombinedImageSampler sur l'HDR) + Draw(3). shaders/tonemap.vert (fullscreen triangle par gl_VertexIndex) + tonemap.frag (exposition + ACES fitted ou Reinhard étendu — choisis ACES, documente ; PAS de pow gamma — le format swapchain sRGB s'en charge). Exposition : push constant ou UBO léger (choisis push constant float). AC: run M4 visuel identique (tonemap sur du LDR ≈ passthrough sombre léger acceptable — vérifie visuellement toi-même via headless + validation) 0 validation 0 leak, resize OK.

### M5-03 — Set 0 lumières + caméra position [code, M] — pending — OWNER Renderer.cs (set 0) + Rendering/Lights.cs
`Lights.cs` : `DirectionalLight { Direction, Color, Intensity }`, `PointLight { Position, Color, Intensity, Range }`, `SceneLights` (1 dir + max 4 point, count). `LightsUniforms` std140 (vec4-packé, testé offsets). Renderer : binding 1 set 0 (UBO lumières per-slot, Fragment), caméra stages Vertex|Fragment, `CameraUniforms` += Position (vec4, 144 o — test offsets adapté), API `DrawScene(..., in SceneLights lights)` ou propriété Renderer.Lights (choisis propriété — moins de churn). AC: build, tests uniforms, run clean.

### M5-04 — Matériau scale/strength + anisotropie device [code, S] — pending — OWNER Assets/MaterialAsset+GltfLoader, Graphics/GraphicsDevice+Sampler
(a) MaterialAsset += NormalScale (défaut 1), OcclusionStrength (défaut 1) ; GltfLoader les lit (normalTexture.scale, occlusionTexture.strength — accesseurs calculés existants) ; SceneBuilder les packe dans mrno.z/.w ; test loader. (b) GraphicsDevice.CreateLogicalDevice : active samplerAnisotropy si le physical device la supporte ; Sampler : flip AnisotropyFeatureEnabled → détection runtime (propriété device), clamp maxSamplerAnisotropy (chemin dormant existant) ; SamplerCache/SceneBuilder : MaxAnisotropy 8 par défaut sur les textures de matériaux. AC: build, tests, run clean (aniso visible = mips moins baveux en angle rasant).

### M5-05 — Shader PBR complet [code, M] — pending — OWNER shaders/mesh.* + Renderer (Cull)
mesh.vert : normal matrix inverse-transpose (mat3), passe worldPos aux varyings. mesh.frag : Cook-Torrance GGX (D=GGX, G=Smith height-correlated, F=Schlick), diffus Lambert énergie-conservé (kd=(1-F)(1-metallic)), normal mapping TBN (tangente vertex + handedness w, normalScale appliqué), metallicRoughness (B=metallic, G=roughness), AO (R, strength), emissive (×factor×strength), lumières : 1 dir + boucle ponctuelles (atténuation inverse-carré avec Range), ambiant constant faible (placeholder IBL M7, p.ex. 0.03), alpha MASK conservé, sortie HDR linéaire (pas de clamp). Renderer : Cull = Back. AC: glslangValidator OK, run DamagedHelmet 0 validation — l'éclairage se juge au protocole visuel M5-09.

### M5-06 — Sandbox lumières + exposition [code, S] — pending — OWNER Program.cs
Lumières par défaut : 1 directionnelle (soleil chaud, intensité ~3, direction oblique), 2 ponctuelles d'appoint. Touches : +/- exposition (log2, push constant tonemap), L pour tourner la directionnelle (debug). Log des réglages. AC: run clean, contrôles fonctionnels.

### M5-07 — Self code review [test, S] — pending
Audits : csharp-lowlevel (hot path DrawScene 2 passes, transitions/barriers, resize, leaks) + architecte (prêt M6 ombres : insertion passe depth-only, ownership targets). AC: 0 critique.

### M5-08 — Requirements validation [test, S] — pending
Spec §3.3/§3.4/§6 M5 cochées vs code.

### M5-09 — Full verification + protocole visuel [test, S] — pending
build 0 warning, tests, runs DamagedHelmet+BoxTextured clean, resize. **Partie humaine** : capture Sandbox vs viewer Khronos (https://github.khronos.org/glTF-Sample-Viewer-Release/) même angle/exposition, image annotée dans docs/visual-checks/2026-07-XX-m5-damagedhelmet.md — préparer le gabarit, l'utilisateur capture.

## Deferred Work

- Ombres → M6 (passe depth-only : l'infrastructure multi-passes M5-01/02 la rend triviale à insérer).
- IBL/skybox → M7 (l'ambiant constant 0.03 est le placeholder).
- Feel souris, upload async, hot reload → M8.
- Instancing (transform sur MeshInstance) → phase 2.

## Log

- 2026-07-05: session 5 ouverte. Board S4 archivé. DAG 9 tâches, 6 vagues. Plan architecte S4 acté intégralement (7 décisions + aniso S3).
