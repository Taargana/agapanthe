# Absolute-Human Board — Agapanthe Session 7 (M7 : IBL & Skybox)

**Status**: in-progress
**Créé**: 2026-07-05
**Spec**: docs/plans/2026-07-02-graphics-engine-design.md §3.3, §3.4, §3.6 (IBL), §5 (protocole visuel), §6 (M7)
**Board persistence**: git-tracked
**Sessions passées**: S1-S6 → .absolute-human/archive/

## Intake (9 points architecte S6 actés — pas de re-brainstorm)

- **Problème**: l'ambiant est une constante — les métaux n'ont rien à refléter hors des 3 lumières ponctuelles, les zones non éclairées sont plates. M7 = éclairage d'environnement complet.
- **Cible de sortie (spec §6 M7)**: IBL complet généré par compute (equirect HDR → cubemap → irradiance (diffus) → prefiltered specular (mips par roughness) → BRDF LUT) + skybox. Protocole visuel §5 sur **MetalRoughSpheres** : rangée metallic correcte vs viewer Khronos.
- **Chemin critique (architecte)**: Graphics d'abord (API à figer), puis Assets float, puis Rendering.
- **Leçons appliquées**: captures headless à chaque vague ; validation layer + capture = juges de paix ; MoltenVK portability (vérifier chaque feature — imageCubeArray, etc. au premier VUID).

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, aucun type Vk* hors Graphics, zéro alloc managée par frame (la GÉNÉRATION IBL est au chargement — allocs OK), ResourceTracker.
- Run : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox` (+ AGAPANTHE_MAX_FRAMES/CAPTURE/VIEW).
- FrontFace CounterClockwise. PCF manuel (pas de comparateur mutable). Skybox = passe dans la HDR entre scène et tonemap.

## Rollback Point

`babac7a` (fin session 6)

## Task Graph

```
W1: M7-01 Storage images + GpuImage cubemap (agent A — GpuImage/descriptors)
    M7-02 ComputePipeline + Dispatch + SubmitImmediate (agent B — CommandList/GraphicsDevice)
W2: M7-03 Loader HDR float + fixtures (HDRI + MetalRoughSpheres) [dep aucune — parallèle possible mais séquencé après W1 pour la charge]
W3: M7-04 Générateur IBL compute [dep 01, 02, 03]
W4: M7-05 Skybox pass + set 0 IBL + mesh.frag ambiant IBL [dep 04]
W5: M7-06 Sandbox + captures MetalRoughSpheres
W6 (tail): M7-07 audits · M7-08 requirements · M7-09 verif + protocole visuel (+ cache disque si temps — déférable)

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | M7-01 (A), M7-02 (B) | parallèle (fichiers disjoints) |
| 2 | M7-03 | agent + fixtures orchestrateur |
| 3 | M7-04 | agent graphics-3d |
| 4 | M7-05 | agent graphics-3d |
| 5 | M7-06 | orchestrateur |
| 6 | M7-07/08/09 | tail |

## Tâches

### M7-01 — Storage images + GpuImage cubemap [code, L] — done — OWNER GpuImage.cs, DescriptorTypes.cs, DescriptorAllocator.cs, FrameContext.cs (+1 ligne DescriptorSetLayout.ToVkType, justifié)
**Résultat**: ImageUsage.Storage (bit 5), DescriptorKind.StorageImage + pools 16 + DescriptorWrites.StorageImage (layout General, sans sampler) sur DescriptorAllocator ET FrameContext (surcharges image/vue). GpuImage : ctor + arrayLayers/ImageViewKind {Color2D, Cube (6 layers requis + CubeCompatibleBit), Array2D}, vue par défaut tous mips/layers, `CreateMipView(mip, baseLayer, layerCount)` → ImageMipView (readonly struct, possédée par GpuImage : List interne, différé = une entrée non-capturante PAR vue (Handle0 seul) avant le payload principal — zéro registre, zéro packing). GpuUploader volontairement non étendu (layer 0 only — cubemaps remplies par compute, documenté). 173 tests, run M6 : capture helmet intact, 0 validation, 0 leak (86 ressources).
(architecte 1+2) ImageUsage.Storage → VkImageUsageFlags.StorageBit ; DescriptorKind.StorageImage → VkDescriptorType.StorageImage + tailles pools (DescriptorAllocator + FrameContext) + DescriptorWrites.StorageImage (layout General). GpuImage : arrayLayers (défaut 1), ImageViewKind {2D, Cube, 2DArray} (défaut 2D), CubeCompatibleBit quand Cube, **vues additionnelles** `CreateMipView(mip, layerCount)` à cycle de vie PROPRE (classe/struct disposable séparée ou liste possédée par GpuImage détruite dans le même payload… le payload = 4 handles : les vues extra dans une liste, détruites via chemin séparé — design à l'agent, contrainte : zéro régression du chemin 2D, deletion correcte). AC: build, tests existants verts, run M6 byte-identique (capture).

### M7-02 — ComputePipeline + Dispatch + SubmitImmediate [code, M] — done — OWNER ComputePipeline.cs (new), CommandList.cs, GraphicsDevice.cs (+RenderingTypes.cs enum, justifié)
**Résultat**: ComputePipelineDesc {ComputeShader required, SetLayouts, PushConstants} + ComputePipeline (pattern GraphicsPipeline, layout-builder dupliqué proprement — GraphicsPipeline.cs verrouillé, cleanup PipelineLayoutBuilder noté) ; CommandList : BindPipeline/BindDescriptorSet/PushConstants surcharges compute + Dispatch(x,y,z) ; ImageLayoutState.General → (General, ComputeShader, Read|Write) — hazards compute→compute couverts par General→General, IBL garde ses intermédiaires en General entre kernels (valide storage ET sampled), hand-off final General→ShaderReadOnly ; SubmitImmediate(record) pool-par-appel (pas d'état device, load-time), finally équilibré, QueueSubmit2+fence. Vérif exemplaire : baseline HEAD vs code restauré → captures SHA-256 identiques. 173 tests, 0 validation 0 leak.
(architecte 3+4) ComputePipeline (desc : ComputeShader + SetLayouts + PushConstants — miroir GraphicsPipeline.CreateLayout + VkComputePipelineCreateInfo). CommandList : BindPipeline(ComputePipeline) (bind point Compute), BindDescriptorSet surcharge compute, `Dispatch(x, y, z)`, PushConstants surcharge compute, ImageLayoutState.General (layout General, stage ComputeShader, access ShaderRead|Write) + transitions compute-aware. `GraphicsDevice.SubmitImmediate(Action<CommandList>)` : pool transient + cmd one-shot + fence wait (généralise GpuUploader — le refactorer pour l'utiliser est BONUS, pas requis). AC: build, tests verts, run M6 identique.

### M7-03 — Loader HDR float + fixtures [code+infra, M] — pending — OWNER Assets/HdrImageLoader ou ImageLoader étendu
(architecte 5) StbImageSharp ImageResultFloat → `HdrImageAsset { float[] RgbaPixels, W, H }` (ou demi-flottants). GpuUploader.BytesPerTexel += R32G32B32A32Sfloat=16 (et/ou Rgba16Sfloat=8 si conversion half). Fixtures : HDRI equirect petite (1k-2k, CC0 Poly Haven) dans tests/Fixtures + MetalRoughSpheres.glb (Khronos) dans Fixtures. Tests : décodage HDR (dimensions, valeurs > 1 présentes), upload float. AC: tests verts.

### M7-04 — Générateur IBL compute [code, L] — pending — OWNER Rendering/IblGenerator.cs (new) + shaders compute
4 kernels GLSL compute : (a) equirect→cubemap (1024² ou 512², RGBA16F, 6 layers, échantillonnage direction→uv equirect) ; (b) irradiance (32², convolution cosinus) ; (c) prefiltered specular (128² base, mips par roughness, GGX importance sampling, N=V=R approx) ; (d) BRDF LUT (512², 2 canaux RG16F, indépendante de l'environnement). Exécutés via SubmitImmediate au chargement (barriers General→ShaderReadOnly). `IblMaps { Environment, Irradiance, Prefiltered, BrdfLut }` disposables. AC: build, génération sans validation error, temps loggé ; capture debug d'une face si simple.

### M7-05 — Skybox + set 0 IBL + ambiant [code, M] — pending — OWNER Renderer.cs, shaders skybox.* + mesh.frag
Skybox pass : DANS le scope scène (option architecte « fusionner ») ou passe séparée avec depth Store — choisir le plus simple ; cube sans vertex buffer (gl_VertexIndex 36 ou fullscreen + direction reconstruite — choisir), DepthTest LessOrEqual sans Write, z=w (toujours au fond), sample la cubemap environnement. Set 0 bindings 3/4/5 : irradiance, prefiltered, BRDF LUT (+ sampler). mesh.frag : ambiant = irradiance×albedo×kd + prefiltered(R, roughness→mip)×(F0×brdf.x+brdf.y), AO appliqué, remplace la constante. Renderer charge/possède les IblMaps (`SetEnvironment(chemin hdr)` ou via ctor — design à l'agent). AC: captures — helmet avec reflets d'environnement, ciel visible.

### M7-06 — Sandbox + MetalRoughSpheres [code, S] — pending — OWNER Program.cs
HDRI par défaut (fixture copiée à l'output), arg pour en changer. Captures MetalRoughSpheres : rangée metallic (haut = miroir teinté ciel, bas = diélectrique) + helmet. AC: captures propres.

### M7-07 — Self code review [test, S] — pending
Audits csharp-lowlevel (cycle de vie vues cubemap, SubmitImmediate leaks, hot path inchangé) + architecte (prêt M8). AC: 0 critique.

### M7-08 — Requirements validation [test, S] — pending
Spec §3.6 IBL + §6 M7 cochées.

### M7-09 — Full verification + protocole visuel [test, S] — pending
build/tests/runs + captures MetalRoughSpheres vs viewer Khronos (IBL ON cette fois — même environnement impossible, juger le CARACTÈRE metallic/roughness) annotées docs/visual-checks/. Cache disque IBL : si le temps de génération loggé est < ~2 s, déférer définitivement (M8/phase 2).

## Deferred Work

- Cache disque IBL par hash HDRI → M8/phase 2 si génération rapide.
- Immutable samplers (comparateur hardware) → phase 2 avec CSM.
- KHR_lights_punctual parsing glTF (lumières depuis le fichier) → phase 2.
- Texel-snapping ombres → phase 2 (lumière statique).

## Log

- 2026-07-05: session 7 ouverte. Board S6 archivé. DAG 9 tâches, 6 vagues. Plan architecte S6 acté.
