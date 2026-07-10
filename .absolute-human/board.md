# Absolute-Human Board — Agapanthe Session 7 (M7 : IBL & Skybox)

**Status**: DONE — M7 livré (9 tâches, verdict visuel PASS 2026-07-10). À archiver → board-session7-M7.md à l'ouverture de S8.
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

### M7-03 — Loader HDR float + fixtures [code+infra, M] — done (agent mort en vérification, finalisé par l'orchestrateur)
**Résultat**: HdrImageAsset {RgbaPixels float[], W, H} linéaire, HdrImageLoader.Load/LoadFromBytes (ImageResultFloat, 4 canaux forcés, AssetException pattern), GpuUploader.BytesPerTexel += formats float. Fixtures : studio_small_03_1k.hdr (Poly Haven CC0) + MetalRoughSpheres.glb (déjà commis W1). **Fix orchestrateur** : le test « corrompu » ne corrompait rien (Encoding.ASCII écrase \xDE en '?' + stb pad les scanlines manquantes silencieusement) → test réécrit (signature invalide) + validation pixel-count ajoutée au loader pour les cas détectables. 180 tests verts (+7), run inchangé.
(architecte 5) StbImageSharp ImageResultFloat → `HdrImageAsset { float[] RgbaPixels, W, H }` (ou demi-flottants). GpuUploader.BytesPerTexel += R32G32B32A32Sfloat=16 (et/ou Rgba16Sfloat=8 si conversion half). Fixtures : HDRI equirect petite (1k-2k, CC0 Poly Haven) dans tests/Fixtures + MetalRoughSpheres.glb (Khronos) dans Fixtures. Tests : décodage HDR (dimensions, valeurs > 1 présentes), upload float. AC: tests verts.

### M7-04 — Générateur IBL compute [code, L] — done — OWNER Rendering/IblGenerator.cs + IblMaps.cs (new) + 4 shaders .comp
**Résultat**: 4 kernels compute (equirect→cube 512², irradiance 32², prefiltered 128²×8 mips GGX importance-sample N=V=R, BRDF LUT 512² RG16F Karis split-sum) exécutés en UN seul SubmitImmediate. `IblMaps{Environment,Irradiance,Prefiltered,BrdfLut}` disposable ; `IblGenerator` possède pipelines/layouts/samplers réutilisables, Generate() possède le transitoire (equirect stagé en half-float, uploader, pool descripteurs). Équirect uploadé en **Rgba16Sfloat (half)** et pas F32 — MoltenVK ne filtre pas linéairement le 32-bit float. Gén. **166 ms**, 0 validation, 0 leak (82 ressources), captures env/irradiance/BRDF LUT visuellement correctes (studio cohérent, LUT forme canonique). 180 tests, capture M6 helmet intacte (86 ressources).
**Gaps Graphics comblés (justifiés, hors owner-lock)**: (1) `PixelFormat.Rg16Sfloat` (BRDF LUT) ; (2) `ImageLayoutState.ShaderReadOnlyCompute` (= ShaderReadOnlyOptimal mais stage Compute — le hand-off env→kernels lecteurs est compute→compute, un ShaderReadOnly fragment ne synchroniserait pas la lecture compute) ; (3) `TransitionImage(GpuImage)` barrière **full-subresource** (mips×layers de l'image ; no-op pour les cibles 1/1 existantes, requis pour les cubemaps 6 faces + mips) ; (4) `GpuReadback` param `baseArrayLayer` (capture d'une face de cube). Aucune régression du chemin M6 (changements no-op pour images 1 mip/1 layer).
(architecte 6) 4 kernels GLSL compute : (a) equirect→cubemap (1024² ou 512², RGBA16F, 6 layers, échantillonnage direction→uv equirect) ; (b) irradiance (32², convolution cosinus) ; (c) prefiltered specular (128² base, mips par roughness, GGX importance sampling, N=V=R approx) ; (d) BRDF LUT (512², 2 canaux RG16F, indépendante de l'environnement). Exécutés via SubmitImmediate au chargement (barriers General→ShaderReadOnly). `IblMaps { Environment, Irradiance, Prefiltered, BrdfLut }` disposables. AC: build, génération sans validation error, temps loggé ; capture debug d'une face si simple.

### M7-05 — Skybox + set 0 IBL + ambiant [code, M] — done — OWNER Renderer.cs, shaders skybox.vert/frag + mesh.frag
**Résultat**: skybox = triangle plein-écran sans VB, **FUSIONNÉ dans le scope scène** (option architecte, la plus simple — pas de depth Store ni barrière extra), dessiné APRÈS les meshes, z=w=1 (far), DepthTest LessOrEqual + DepthWrite OFF → ne peint que le fond. Direction de vue reconstruite par `inverse(proj*view)` unproject (3 verts, coût nul). Set 0 skybox propre = camera UBO (vertex) + env cubemap (fragment). mesh.frag : set 0 bindings 3/4/5 (irradiance/prefiltered/BRDF LUT), ambiant IBL = kd·irradiance·albedo + prefilteredLod(R, roughness·maxMip)·(F0·brdf.x+brdf.y), Fresnel roughness-aware, ×AO — **remplace la constante** (lights.Ambient désormais ignoré). Renderer possède IblGenerator+IblMaps+sampler+skybox pipeline ; `SetEnvironment(hdr)` génère et adopte ; DrawScene rejette un env absent. Aucun changement Graphics requis (DepthWrite toggle + DepthCompareOp LessOrEqual déjà présents ; pool FrameContext large). Sandbox charge studio_small_1k.hdr par défaut. **Capture helmet : reflets d'env + ciel visible ✓ ; MetalRoughSpheres : rangée metallic dégradé net→flou correct ✓**. 0 validation, 0 leak (136-142 ressources), 180 tests, gén. IBL ~120 ms.
Skybox pass : DANS le scope scène (option architecte « fusionner ») ou passe séparée avec depth Store — choisir le plus simple ; cube sans vertex buffer (gl_VertexIndex 36 ou fullscreen + direction reconstruite — choisir), DepthTest LessOrEqual sans Write, z=w (toujours au fond), sample la cubemap environnement. Set 0 bindings 3/4/5 : irradiance, prefiltered, BRDF LUT (+ sampler). mesh.frag : ambiant = irradiance×albedo×kd + prefiltered(R, roughness→mip)×(F0×brdf.x+brdf.y), AO appliqué, remplace la constante. Renderer charge/possède les IblMaps (`SetEnvironment(chemin hdr)` ou via ctor — design à l'agent). AC: captures — helmet avec reflets d'environnement, ciel visible.

### M7-06 — Sandbox + MetalRoughSpheres [code, S] — done — OWNER Program.cs + docs/visual-checks
**Résultat**: HDRI par défaut `studio_small_1k.hdr` (déjà copié dans models/ par la glob fixtures), override par `AGAPANTHE_HDRI=<path.hdr>`. Captures propres à cadrage/expo par défaut sauvées dans docs/visual-checks : `2026-07-10-m7-damagedhelmet-agapanthe.png` (reflets env + ciel) et `2026-07-10-m7-metalroughspheres-agapanthe.png` (grille metallic×roughness). Doc protocole `2026-07-10-m7-ibl.md` (critères + procédure viewer Khronos, verdict à remplir = M7-09). 0 leak (136/142 ressources).
HDRI par défaut (fixture copiée à l'output), arg pour en changer. Captures MetalRoughSpheres : rangée metallic (haut = miroir teinté ciel, bas = diélectrique) + helmet. AC: captures propres.

### M7-07 — Self code review [test, S] — done — 0 CRITIQUE
**csharp-lowlevel**: 0 critique, 2 MEDIUM + 1 MINEUR CORRIGÉS (M1 fuite 4 GpuImages hors try → déplacés dans try + catch null-safe ; M2 ToHalf +Inf/NaN sur HDRI brillants → clamp Half.MaxValue + scrub NaN ; m3 SetEnvironment maps pendantes si Generate throw → temp-swap). Autres MINEUR laissés (m4 11 vues retenues = gaspillage acceptable ; m5 marge pool descripteurs = auto-grow ; m6 env en ShaderReadOnlyCompute samplé fragment = layout Vulkan concordant, sync par WaitIdle). Hot path zéro-alloc CONFIRMÉ, barrière full-subresource sans régression CONFIRMÉE. Fixes vérifiés : 180 tests, capture helmet BYTE-IDENTIQUE (hardening pur), 0 leak.
**engine-architect**: PASS with CONCERNS (0 critique). F1 commentaire General périmé → CORRIGÉ. Reste = dette M8 tracée (voir Deferred Work) : F3 dédup PipelineLayoutBuilder (extraire début M8), F5 DrawScene hard-require env (acté, placeholder 1×1 si mode sans-HDRI apparaît), F6 seam par-passe pour hot reload M8, F2 overload transition sub-range si cas réel. Skybox fusionné + placement IBL = design validés.

### M7-08 — Requirements validation [test, S] — done
Spec §3.6 IBL : equirect HDR → cubemap env ✓ → irradiance diffuse ✓ → prefiltered specular (mips par roughness) ✓ → BRDF LUT (une fois, indépendante env) ✓ ; généré au chargement via compute ✓. Cache disque par hash HDRI = DÉFÉRÉ (gén. 135 ms ≪ 2 s, board). Spec §6 M7 : IBL complet compute ✓ + skybox ✓. §3.4 set 0 maps IBL : câblées bindings 3/4/5 (le placeholder 1×1 de la spec non implémenté — DrawScene exige un env à la place, déviation actée F5).

### M7-09 — Full verification + protocole visuel [test, S] — done — PASS
Verif : build OK, 180 tests verts, runs 0 validation / 0 leak (136 helmet, 142 spheres), capture byte-identique post-fixes. Protocole visuel : **PASS (revue humaine 2026-07-10, « c'est très bien »)** — helmet réfléchit l'environnement + ciel visible, MetalRoughSpheres rangée metallic net→flou correcte. Cache disque IBL déféré définitivement (135 ms). Verdict inscrit docs/visual-checks/2026-07-10-m7-ibl.md.

## Deferred Work

- Cache disque IBL par hash HDRI → DÉFÉRÉ DÉFINITIVEMENT (gén. 135 ms ≪ 2 s ; M8/phase 2 seulement si utile).
- Immutable samplers (comparateur hardware) → phase 2 avec CSM.
- KHR_lights_punctual parsing glTF (lumières depuis le fichier) → phase 2.
- Texel-snapping ombres → phase 2 (lumière statique).
- **[audit M7-07 architecte] Extraire PipelineLayoutBuilder partagé (Graphics)** → DÉBUT M8, avant le hot reload (dédup ComputePipeline/GraphicsPipeline, contrainte owner-lock M7-02 levée).
- **[audit M7-07 architecte] Seam par-passe (ShadowPass/SkyboxPass/IblResources)** → M8, pour donner au hot reload un point d'accroche par pipeline (Renderer = 845 lignes, God-object naissant).
- **[audit M7-07 architecte] Debug labels sur la génération IBL** (4 kernels + transitions du SubmitImmediate) → M8 (item RenderDoc explicite).
- **[audit M7-07 architecte] DrawScene exige un environnement (throw)** → déviation actée vs placeholder 1×1 spec §3.4 ; implémenter le placeholder si un mode sans-HDRI apparaît (éditeur/scène vide).
- **[audit M7-07 architecte] Prefilter échantillonne env single-mip** → fireflies specular possibles sur HDRI très contrasté ; mip-chain sur l'env = raffinement phase 2.
- **[audit M7-07 architecte] Overload TransitionImage sub-range (baseMip/mipCount/baseLayer/layerCount)** → seulement quand un cas réel l'exige (mip-chain par blits, phase 2) ; full-subresource reste le défaut.

## Log

- 2026-07-05: session 7 ouverte. Board S6 archivé. DAG 9 tâches, 6 vagues. Plan architecte S6 acté.
- 2026-07-10: W3/M7-04 done — générateur IBL compute (4 kernels) + IblMaps/IblGenerator. 166 ms, 0 validation, 0 leak, captures OK, 180 tests, M6 intact.
- 2026-07-10: W4/M7-05 done — skybox (fusionné scène) + set 0 IBL 3/4/5 + ambiant IBL mesh.frag. Captures helmet (reflets+ciel) et MetalRoughSpheres OK, 0 validation, 0 leak, 180 tests.
- 2026-07-10: W5/M7-06 done — override AGAPANTHE_HDRI, captures M7 sauvées dans docs/visual-checks + doc protocole.
- 2026-07-10: W6 tail — M7-07 audits (csharp-lowlevel + architecte) PASS, 0 critique ; 3 findings mémoire (M1/M2/m3) + 1 archi (F1) CORRIGÉS, reste tracé en Deferred Work. M7-08 requirements §3.6/§6 cochés. Fixes vérifiés : 180 tests, capture byte-identique, 0 leak. RESTE : M7-09 = protocole visuel HUMAIN (verdict docs/visual-checks/2026-07-10-m7-ibl.md) + validation multi-OS.
