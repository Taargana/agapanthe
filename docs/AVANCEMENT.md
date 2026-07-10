# Agapanthe — Plan complet & état d'avancement

**Mis à jour** : 2026-07-10 (fin session 7) · **Machine de dev** : macOS (Apple M3, MoltenVK) · **Cibles** : Windows / Linux / macOS

## Vision

Moteur de jeu Vulkan en C# from scratch. Phase 1 (en cours) : toute la chaîne graphique 3D, jusqu'à une scène PBR complète — glTF, metallic-roughness, multi-lumières, ombres, skybox/IBL, hot reload shaders. Phase 2 (plus tard) : ECS/scene graph, audio, physique, gameplay.

Spec de référence : [docs/plans/2026-07-02-graphics-engine-design.md](plans/2026-07-02-graphics-engine-design.md) · Suivi de session : [.absolute-human/board.md](../.absolute-human/board.md) (+ archives par session)

## Décisions structurantes (verrouillées)

| Sujet | Choix |
|---|---|
| Bindings | Silk.NET (Vulkan + GLFW + input) — le reste from scratch |
| Baseline GPU | Vulkan 1.2 + dynamic_rendering + synchronization2 (chemin 1.3 core sur MoltenVK) |
| Maths | System.Numerics (convention row-vector) + helpers clip-space Vulkan (Y-flip, Z [0,1]) |
| Shaders | GLSL → SPIR-V à l'exécution (shaderc), hot reload prévu M8 |
| Abstraction GPU | Couche mince mono-backend — aucun type `Vk*` ne sort de `Agapanthe.Graphics` |
| Mémoire GPU | Allocateur from scratch (free-list par blocs 64 MiB, dedicated au-delà de 32 MiB) |
| Assets | glTF 2.0 parsé from scratch, StbImageSharp pour les images ; DTO CPU sans dépendance GPU |
| Discipline mémoire | IDisposable partout, destruction différée N+2 frames (DeletionQueue non-capturante), zéro alloc managée par frame, ResourceTracker (leak = échec du run) |
| Qualité | Tout message de validation layer = bug. xUnit sans GPU pour maths/allocateur/parsing |
| Runtime | .NET 10, TreatWarningsAsErrors |

## Modules

```
Sandbox ──► Rendering ──► Graphics ──► Core
                │              (seul projet référençant Silk.NET.Vulkan)
                └────► Assets ──► Core   (GPU-free : parsing testable sans GPU)
Platform ──► Core   (fenêtre GLFW, input, capture souris)
```

## Jalons — état

| # | Livrable | État | Session |
|---|---|---|---|
| M0 | Fenêtre, instance/device/swapchain, ResourceTracker | ✅ | S1 |
| M1 | Triangle (pipeline, shaderc runtime, frames-in-flight) | ✅ | S1 |
| M2 | Mesh 3D : depth, descriptors, UBO caméra, push constants, caméra libre | ✅ | S2 |
| M3 | GpuAllocator, staging uploads, textures + mipmaps + samplers | ✅ | S3 |
| M4 | Loader glTF, tangentes, Scene/Material/Renderer, fixtures Khronos | ✅ | S4 |
| M5 | PBR Cook-Torrance + 3 lumières HDR + ACES tone mapping | ✅ validé visuellement | S5 |
| M6 | Shadow mapping directionnel (D32 2048², PCF 3×3 manuel, slope-scaled bias) | ✅ | S6 |
| M7 | IBL compute (cubemap, irradiance, prefiltered, BRDF LUT) + skybox | ✅ validé visuellement | S7 |
| **M8** | **Hot reload shaders (+includes), labels RenderDoc, audit perf/leaks final** | ⏳ prochain | S8 |

Chaque jalon clôt sur : Sandbox propre (0 message validation, 0 leak), tests verts, double audit agent (csharp-lowlevel + architecte) PASS, board archivé.

## État courant (fin session 7)

**Ce qui tourne** : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox` → DamagedHelmet en **PBR complet + IBL + ombres** (Cook-Torrance GGX, normal mapping, AO, emissive, 3 lumières HDR, shadow mapping directionnel PCF, **IBL image-based** — irradiance diffuse + prefiltered specular + BRDF LUT — **skybox** environnement, tone mapping ACES), caméra libre 6DOF, capture souris OS-confinée. Contrôles : +/− exposition, L pivote la lumière clé, **N cycle 9 vues debug**. `dotnet run … -- MetalRoughSpheres.glb` pour la grille metallic×roughness ; `AGAPANTHE_HDRI=<path.hdr>` change l'environnement.

**Debug headless** : `AGAPANTHE_CAPTURE=sortie.ppm` dump le target HDR tonemappé, `AGAPANTHE_VIEW="x,y,z"` reproduit un angle caméra, `AGAPANTHE_MAX_FRAMES=N` auto-ferme, `AGAPANTHE_IBL_TEST=<préfixe>` génère l'IBL et dump les faces/maps (validation compute sans fenêtre). GpuReadback + Renderer.SaveHdrCapture.

**Métriques** : 54 commits · 109 fichiers C# · ~12 000 lignes (src+samples) · **180 tests xUnit** · 12 audits agents (2 par jalon M2-M7), tous PASS.

**Acquis techniques clés** :
- Allocateur GPU testé sans GPU (seam `IMemoryBackend`), stats mémoire au shutdown
- DeletionQueue zéro-allocation (payload 4×ulong + destructeurs statiques, offset+memType bit-packés 40/24)
- Upload staging synchrone explicite (jamais de submit caché) + chaîne de mips par blits
- Assets 100 % CPU : glTF/GLB source-gen STJ, matrices colonne-major→row-vector prouvées par test, génération de tangentes Lengyel prouvée sur DamagedHelmet
- Multi-passes : CommandList.BeginRendering/TransitionImage publics, FrameRenderer = pur frame-sync, chaîne scène→HDR Rgba16Sfloat→ACES→swapchain sRGB (fix WAR sur l'HDR partagée entre frames in flight)
- Shader PBR : GGX + Smith height-correlated + Schlick, TBN avec fallback anti-NaN, atténuation KHR_lights_punctual, std140 triple-vérifié (C# ↔ GLSL par réflexion)
- Renderer possède tout le câblage ; le Sandbox n'a plus un seul objet Vulkan manuel

**Leçon de guerre M5 (front face)** : le culling supprimait les faces avant — `FrontFace.Clockwise` venait d'un calcul de winding omettant le signe moins de la formule Vulkan (qui compense le Y-down du framebuffer). Avec le Y-flip baké dans la projection, glTF CCW = CCW visuel = CCW Vulkan → `CounterClockwise`. Invisible sur M2 (cube convexe fermé non éclairé ≈ identique en culling inversé) et M4 (Cull None). Diagnostic par captures headless comparées (bug → Cull None → fix).

**Dette consciente** : upload async (M8), feel souris (M8), WrapU/V séparés si fixture l'exige, instancing multi-mesh (phase 2), MikkTSpace si artefacts, auto-exposure (hors phase 1), ClearColor obsolète dès le skybox M7.

## Fin session 7 — M7 livré (IBL & skybox)

**Verdict visuel PASS** (revue humaine 2026-07-10) : helmet réfléchit l'environnement + ciel visible, MetalRoughSpheres rangée metallic net→flou correcte. L'IBL remplace l'ambiant constant — remède au métal sombre en place.

**Livré en 5 vagues** :
- Graphics (S6) : ImageUsage.Storage, DescriptorKind.StorageImage, GpuImage cubemap (ViewType.Cube + vues par mip/face via CreateMipView, possédées), ComputePipeline + CommandList.Dispatch, GraphicsDevice.SubmitImmediate.
- Assets : HdrImageLoader (Radiance .hdr float via StbImageSharp), HdrImageAsset.
- W3 : **IblGenerator** (4 kernels compute equirect→cube 512² / irradiance 32² / prefiltered 128²×8 mips GGX importance-sample / BRDF LUT 512² RG16F Karis) en un seul SubmitImmediate, **IblMaps** disposable. Gén. ~135 ms.
- W4 : **skybox** (triangle plein-écran far-plane fusionné dans le scope scène, DepthTest LessOrEqual sans Write), set 0 bindings 3/4/5 + **ambiant IBL** dans mesh.frag (kd·irradiance·albedo + prefilteredLod(R, roughness·maxMip)·(F0·brdf.x+brdf.y), Fresnel roughness-aware, ×AO).
- W5/W6 : override AGAPANTHE_HDRI, captures docs/visual-checks, audits (0 critique, 3 findings mémoire + 1 archi corrigés).

**Acquis techniques M7** :
- Half-float pour l'équirect stagé (MoltenVK ne filtre pas linéairement le 32-bit float) ; ToHalf clampe Half.MaxValue + scrub NaN (HDRI brillants → +Inf sinon).
- `ImageLayoutState.ShaderReadOnlyCompute` = ShaderReadOnlyOptimal mais stage compute (hand-off env→kernels lecteurs compute→compute).
- `TransitionImage(GpuImage)` full-subresource (mips×layers) ; no-op pour les cibles 1/1 pré-M7.
- IblGenerator réutilisable (pipelines/layouts/samplers) ; Generate() possède le transitoire (equirect, uploader, pool descripteurs) ; try interne libère sur échec (finding M1).

**Découverte plateforme** : rien de nouveau côté MoltenVK au-delà du half-float ; imageCubeArray évité (vues 2D-array par face, un seul cube).

## Plan M8 — hot reload shaders + confort + audit final (prochaine session, DERNIER jalon Phase 1)

Critère de sortie (spec §6) : édition shader à chaud < 1 s + audit csharp-lowlevel sans finding critique.

**Ordre recommandé (le refactor archi d'abord — c'est le nœud du hot reload)** :
1. **Dette archi M7 (préalable)** : extraire un `PipelineLayoutBuilder` partagé (dédup ComputePipeline/GraphicsPipeline, owner-lock levé) ; seam par-passe (`ShadowPass`/`SkyboxPass`/`IblResources` ou au minimum `CreatePipelines()`/`RecreatePipeline(shader)` par passe) pour donner au hot reload un point d'accroche unique — le Renderer God-object (845 lignes, ctor crée ~4 pipelines inline) bloque sinon.
2. **Hot reload** : résolveur `#include` maison (retourne la liste des fichiers touchés), cache disque keyé par hash du **source résolu après expansion**, watch shader + includes → recompile → recréation pipeline frame suivante (ancien → DeletionQueue), échec compile = log + ancien pipeline conservé (spec §3.6, §4).
3. **Debug labels RenderDoc** : `vkCmdBeginDebugUtilsLabel` sur les passes (shadow/scène/skybox/tonemap) + les 4 kernels du SubmitImmediate IBL.
4. **Confort souris** : lissage, sensibilité liée au FOV.
5. **Audit final perf/leaks** (csharp-lowlevel, 0 critique requis) + **validation multi-OS** Windows/Linux (prévu M1/M4/M8 ; M1 fait, M4 sauté — à faire ici).

**Dette M7 tracée pour M8/phase 2** (détail : `.absolute-human/board.md` → Deferred Work) : PipelineLayoutBuilder, seam par-passe, debug labels IBL, DrawScene hard-require env (vs placeholder 1×1 spec §3.4 — acté), prefilter env single-mip (fireflies possibles HDRI contrasté), overload TransitionImage sub-range si cas réel, cache disque IBL (déféré définitivement, 135 ms).

## Reprise — où repartir

**Point de reprise** : M7 clos (commits `712357f`→`454cf8c`), board S7 status DONE (à archiver → `.absolute-human/archive/board-session7-M7.md` à l'ouverture de S8). Rien en cours, arbre git propre.

**Pour lancer S8/M8** : ouvrir une session absolute-human (comme S7), commencer par un passage `engine-architect` sur le découpage des passes (préalable 1 ci-dessus), puis DAG des vagues. Run de sanity : `DYLD_LIBRARY_PATH=/opt/homebrew/lib AGAPANTHE_MAX_FRAMES=3 AGAPANTHE_CAPTURE=/tmp/check.ppm dotnet run --project samples/Sandbox` doit donner 0 validation / 0 leak.

**Après M8 → Phase 1 close** (chaîne graphique complète). Phase 2 : ECS/scene graph, audio, physique, gameplay.
