# Absolute-Human Board — Agapanthe Session 6 (M6 : Shadow Mapping)

**Status**: in-progress
**Créé**: 2026-07-05
**Spec**: docs/plans/2026-07-02-graphics-engine-design.md §3.3 (passes), §3.4, §6 (M6)
**Board persistence**: git-tracked
**Sessions passées**: S1-S5 → .absolute-human/archive/

## Intake (10 décisions architecte S5 actées — pas de re-brainstorm)

- **Problème**: M5 éclaire mais rien ne bloque la lumière — pas d'ancrage au sol, pas de profondeur de scène. M6 = ombres portées de la lumière directionnelle.
- **Cible de sortie (spec §6 M6)**: shadow mapping directionnel — depth D32 2048², slope-scaled bias, PCF 3×3, 1 cascade (CSM = phase 2). Ombres stables caméra en mouvement, pas d'acné visible.
- **Les 10 décisions architecte S5** (détail : archive board S5, section « Revue architecte M5-07 ») :
  1. `RenderingAttachments.Color` → nullable, émission conditionnelle (passe depth-only).
  2. Pipeline depth-only : FragmentShader + ColorFormat optionnels, StageCount/blend conditionnels.
  3. `GraphicsPipelineDesc` += DepthBias{Constant,Slope}.
  4. `SamplerDesc` += CompareOp? → CompareEnable (sampler2DShadow), ClampToEdge + border blanc.
  5. `MathHelpers.OrthographicVulkan` (Y-flip, Z [0,1], testé sans GPU).
  6. Set 0 binding 2 = shadow sampler comparateur ; lightViewProj mat4 appended dans LightsUniforms (176→240, test std140 étendu).
  7. **Passe shadow sans descriptor set** : model + lightViewProj = 128 o pile en push constants.
  8. `Scene` expose une AABB monde (SceneBuilder) → fit ortho dans Renderer ; DirectionalLight reste pure ; réglages en Renderer.ShadowSettings.
  9. Shadow map 2048² D32 DepthAttachment|Sampled créée UNE fois au ctor (invariante au resize, hors EnsureTargets).
  10. Découper DrawScene en RecordShadowPass/RecordScenePass/RecordTonemapPass avant insertion.
- **Leçon M5 à appliquer** : vérifier chaque étape visuellement via `AGAPANTHE_CAPTURE`/`AGAPANTHE_VIEW` — les captures headless sont désormais l'outil de validation de premier rang, avant même la validation layer.

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, aucun type Vk* hors Graphics, zéro alloc managée par frame, ResourceTracker, validation layer + capture headless = juges de paix.
- Run : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox` (+ `AGAPANTHE_MAX_FRAMES`, `AGAPANTHE_CAPTURE=x.ppm`, `AGAPANTHE_VIEW="x,y,z"`).
- FrontFace = CounterClockwise (leçon M5) — la passe shadow utilise le MÊME winding (front-face culling standard pour les ombres = garder Back… décision : Cull Back aussi en shadow pass, le bias gère l'acné ; Cull Front est une alternative si peter-panning — à trancher au run).

## Rollback Point

`380bcf0` (fin session 5)

## Task Graph

```
W1: M6-01 Extensions Graphics (décisions 1-4)   M6-02 OrthographicVulkan + Scene AABB (décisions 5+8)
W2: M6-03 Renderer passe shadow + shaders (décisions 6, 7, 9, 10) [dep 01, 02]
W3: M6-04 Sandbox réglages ombre + captures headless [dep 03]
W4 (tail): M6-05 audits · M6-06 requirements · M6-07 verif + protocole visuel
```

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | M6-01 (Graphics), M6-02 (Core+Rendering data) | parallèle (fichiers disjoints) |
| 2 | M6-03 | agent graphics-3d |
| 3 | M6-04 | orchestrateur (+captures) |
| 4 | M6-05/06/07 | tail |

## Tâches

### M6-01 — Extensions Graphics depth-only [code, M] — done — OWNER RenderingTypes/CommandList/GraphicsPipeline*/Sampler
**Résultat**: (1) RenderingAttachments.Color nullable, émission conditionnelle. (2) FragmentShader nullable + ColorFormat défaut Undefined, stageCount 1/2, blend/rendering-info conditionnels. (3) DepthBiasConstant/Slope statiques (enable si ≠ 0). (4) SamplerCompare {None, LessOrEqual…} + ClampToBorder ajouté à SamplerAddressMode, bordure blanche forcée si comparateur (hors-frustum = lit 1.0 = éclairé). Capture avant/après **identique au byte près** (md5). **Gap flaggé par l'agent et réglé par l'orchestrateur** : depth StoreOp était DontCare en dur — `DepthAttachmentInfo.Store` ajouté (défaut false = scratch, shadow pass mettra true). Vérif W1 intégrée : 172 tests, run 0 validation 0 leak, capture propre.
(1) `RenderingAttachments.Color` → `ColorAttachmentInfo?`, CommandList.BeginRendering émet ColorAttachmentCount=0/PColorAttachments=null si absent. (2) GraphicsPipelineDesc.FragmentShader + ColorFormat optionnels ; GraphicsPipeline : StageCount 1/2 conditionnel, blend/ColorAttachmentCount conditionnels, PipelineRenderingCreateInfo sans color format si absent. (3) GraphicsPipelineDesc += `float DepthBiasConstant`, `float DepthBiasSlope` (0 = off) → rasterizer DepthBiasEnable/ConstantFactor/SlopeFactor. (4) SamplerDesc += `SamplerCompareOp? Compare` (enum moteur : LessOrEqual suffit M6 + Never/Less/Greater… minimal) → CompareEnable/CompareOp + BorderColor FloatOpaqueWhite + doc (hors-frustum = pas d'ombre). AC: build 0 warning, tests existants verts, run M5 inchangé (aucun client des nouveautés).

### M6-02 — OrthographicVulkan + Scene AABB [code, S] — done — OWNER MathHelpers.cs + Scene/SceneBuilder
**Résultat**: `OrthographicVulkan(width, height, near, far)` centré = CreateOrthographic + M22 négé (même geste que PerspectiveVulkan). Découverte : System.Numerics CreateOrthographic produit DÉJÀ Z [0,1] (convention DX native) — seul le Y-flip manquait. Test croisé : même point via ortho et perspective → même signe Y. Scene.BoundsMin/Max/Center/Diagonal, fold pur `SceneBuilder.ComputeWorldBounds(IReadOnlyList<MeshAsset>)` testable sans GPU, calculé avant upload. Program : ModelBounds supprimé, FrameCamera/SetupLights lisent scene.Bounds*. 172 tests (+13), capture headless : cadrage identique.
(5) `MathHelpers.OrthographicVulkan(width, height, near, far)` ou (left,right,bottom,top,near,far) — row-vector, Y-flip, Z [0,1] ; tests sans GPU (coins connus → NDC attendus, cohérence avec PerspectiveVulkan sur le Y). (8) `Scene.BoundsMin/BoundsMax` (ou struct Aabb) calculée par SceneBuilder depuis les MeshAsset (positions × WorldTransform — le calcul existe dans Program.ModelBounds : le MIGRER dans SceneBuilder, Program consomme Scene.Bounds). AC: tests verts, Program simplifié.

### M6-03 — Renderer passe shadow + PCF [code, L→M] — done — OWNER Renderer.cs + shaders
**Résultat**: DrawScene découpé (ComputeLightViewProj → RecordShadowPass → RecordScenePass → RecordTonemapPass), shadow map D32 2048² au ctor (hors EnsureTargets), pipeline depth-only (shadow.vert seul, VertexLayout position-seule stride 60 — warning attribut évité), push constants 128 o (lightViewProj + model, aucun set), bias 1.25/1.75 (spec — captures sans acné ni peter-panning, conservés), WAR _shadowInitialized (pattern HDR), LightsUniforms 176→240 (LightViewProj offset 176, testé). **DÉCOUVERTE PLATEFORME** : MoltenVK portability_subset → mutableComparisonSamplers=FALSE — le sampler comparateur écrit par descriptor est REJETÉ (VUID-04450 capturé) → PCF 3×3 MANUEL (sampler2D Nearest ClampToEdge, comparaison dans le shader). Qualité identique en 3×3. **À arbitrer M7** : comparateur hardware via immutable samplers = extension DescriptorSetLayout. Preuves capture : auto-ombrage cohérent (oreillette droite, creux visière, sous les tuyaux — source haut-gauche-avant), dôme sans acné, 2 angles. 173 tests, 2 fixtures 0 validation 0 leak.
(10) Découper DrawScene : RecordShadowPass / RecordScenePass / RecordTonemapPass. (9) Shadow map D32 2048² DepthAttachment|Sampled au ctor + sampler comparateur (ClampToEdge, border blanc, CompareOp LessOrEqual) + pipeline shadow (shadow.vert seul, VertexLayout position seule ? NON — stride complet 60, déclarer position uniquement = attribut 0 : OK validation car les autres ne sont pas déclarés au pipeline ; push constants 128 o = lightViewProj (0-64, Vertex) + model (64-128, Vertex) — ATTENTION : réutiliser le MÊME buffer vertex). (6) lightViewProj dans LightsUniforms (176→240, mat4 après les points ; test std140 étendu) + set 0 binding 2 shadow map (comparateur). (7) Passe shadow : BeginRendering depth-only 2048², SetViewportScissor(2048), par instance push model+lightVP, DrawIndexed ; transition shadow DepthAttachment→ShaderReadOnly avant la passe scène ; acquire ShaderReadOnly→DepthAttachment frame suivante (même pattern WAR que l'HDR). Fit ortho : centre AABB scène, rayon = demi-diagonale, vue = LookAt(centre − dir·rayon, centre), ortho couvrant le rayon, near/far serrés. mesh.frag : sampler2DShadow binding 2, coords = worldPos × lightViewProj → NDC → uv (x*0.5+0.5, y*0.5+0.5 — ATTENTION Y-flip déjà dans l'ortho), PCF 3×3 textureProj/textureOffset, ombre appliquée à la directionnelle seule. ShadowSettings {Resolution=2048, DepthBiasConstant~1.25, DepthBiasSlope~1.75} sur Renderer. AC: build, tests, run 0 validation, captures headless : ombre visible sous le casque (ajouter un sol ? NON — M6 minimal : l'auto-ombrage du casque suffit à valider ; le sol viendra avec une scène de test si besoin). Vérifier l'auto-ombrage par capture.

### M6-04 — Sandbox réglages + captures [code, S] — done (orchestrateur) — OWNER Program.cs
**Résultat**: cycle debug N étendu à 10 vues (« shadow factor » = DEBUG_SHADOW 9, ajouté par M6-03), titre « Agapanthe — M6 Shadows ». L pivote déjà la clé (l'ombre suit — lightViewProj recalculé chaque frame depuis Lights.Directional). Capture angle face : casque intact, ombrage sous les tuyaux/menton cohérent. 173 tests, 0 validation.

### M6-05 — Self code review [test, S] — pending
Audits csharp-lowlevel (hot path 3 passes, transitions shadow WAR, leaks) + architecte (prêt M7 : skybox entre scène et tonemap, gaps cubemap/compute tracés). AC: 0 critique.

### M6-06 — Requirements validation [test, S] — pending
Spec §6 M6 : D32 2048² ✓, slope-scaled bias ✓, PCF 3×3 ✓, stable caméra mouvement (capture 2 angles), pas d'acné (captures).

### M6-07 — Full verification + protocole visuel [test, S] — pending
build/tests/runs 2 fixtures + captures headless annotées dans docs/visual-checks/ (l'utilisateur confirme à l'écran).

## Deferred Work

- CSM (cascades) → phase 2 (spec §6 : « 1 cascade, CSM ensuite »).
- Sol/plan de test pour ombres portées inter-objets → si l'auto-ombrage ne suffit pas à valider.
- Ombres des ponctuelles (cube maps) → phase 2.
- Peter-panning vs acné : Cull Back + bias d'abord ; Cull Front en shadow pass si problème.

## Log

- 2026-07-05: session 6 ouverte. Board S5 archivé. DAG 7 tâches, 4 vagues. Les 10 décisions architecte S5 actées.
