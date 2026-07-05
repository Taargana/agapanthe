# Agapanthe — Plan complet & état d'avancement

**Mis à jour** : 2026-07-05 (fin session 6) · **Machine de dev** : macOS (Apple M3, MoltenVK) · **Cibles** : Windows / Linux / macOS

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
| **M7** | **IBL compute (cubemap, irradiance, prefiltered, BRDF LUT) + skybox** | ⏳ prochain | S7 |
| M8 | Hot reload shaders (+includes), labels RenderDoc, audit perf/leaks final | ○ | S8 |

Chaque jalon clôt sur : Sandbox propre (0 message validation, 0 leak), tests verts, double audit agent (csharp-lowlevel + architecte) PASS, board archivé.

## État courant (fin session 5)

**Ce qui tourne** : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox` → DamagedHelmet en **PBR complet** (Cook-Torrance GGX, normal mapping, AO, emissive, 3 lumières HDR, tone mapping ACES, anisotropie 8×, back-face culling correct), caméra libre 6DOF, capture souris OS-confinée. Contrôles : +/− exposition, L pivote la lumière clé, **N cycle 9 vues debug** (normales, baseColor, metallic, roughness, AO, tangentes, NdotL).

**Debug headless** (né du débogage M5) : `AGAPANTHE_CAPTURE=sortie.ppm` dump le target HDR tonemappé, `AGAPANTHE_VIEW="x,y,z"` reproduit un angle caméra — les bugs de rendu se diagnostiquent désormais sans session fenêtrée (GpuReadback + Renderer.SaveHdrCapture).

**Métriques** : 44 commits · 77 fichiers C# · ~10 300 lignes (src+samples) · **159 tests xUnit** · 10 audits agents (2 par jalon M2-M5), tous PASS.

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

## Fin session 6 — M6 livré

Passe shadow depth-only sans descriptor set (push constants 128 o pile), fit ortho sur l'AABB de scène (stable caméra par construction), PCF 3×3, bias 1.25/1.75 sans acné (captures). Découverte plateforme : MoltenVK interdit les samplers comparateurs mutables (portability subset) → PCF manuel, comparateur hardware différé phase 2 (immutable samplers, avec CSM). 173 tests.

## Plan M7 — IBL & skybox (prochaine session, 9 points architecte actés au board S6)

Critère de sortie (spec §5/§6) : protocole visuel sur MetalRoughSpheres (rangée metallic correcte). L'IBL remplace l'ambiant constant — le vrai remède au métal sombre.

1. **Graphics d'abord** : ImageUsage.Storage + DescriptorKind.StorageImage ; GpuImage cubemap (layers, ViewType.Cube, vues par mip/face à cycle de vie propre — la plus grosse pièce) ; ComputePipeline + CommandList.Dispatch ; device.SubmitImmediate.
2. **Assets** : loader HDR float (equirect .hdr).
3. **Rendering** : générateur IBL compute (equirect→cubemap→irradiance→prefiltered→BRDF LUT), skybox pass (scene depth Store + DepthTest sans Write), set 0 bindings 3/4/5 + ambiant IBL dans mesh.frag.
4. **Déférable** : cache disque des maps par hash HDRI.

## Après M6

- **M7 IBL** : pipeline compute (equirect→cubemap→irradiance→prefiltered mips→BRDF LUT), cache disque par hash de HDRI, skybox (passe entre scène et tonemap dans la HDR). Gaps tracés : GpuImage 2D-only (cubemap → ArrayLayers + ViewType.Cube), CommandList sans dispatch compute (spec §3.3). L'IBL remplacera l'ambiant constant — le vrai remède au « métal noir » hors reflets.
- **M8 confort** : hot reload shaders avec watch des includes, debug labels RenderDoc, audit final perf/leaks, feel souris (lissage, sensibilité liée au FOV).
- **Validation multi-OS** : machines perso Windows/Linux au minimum à M8 (déjà fait M1 ; M4 à programmer).
