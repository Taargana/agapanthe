# Agapanthe — Plan complet & état d'avancement

**Mis à jour** : 2026-07-05 · **Machine de dev** : macOS (Apple M3, MoltenVK) · **Cibles** : Windows / Linux / macOS

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
| M5 | PBR Cook-Torrance + 3 lumières HDR + ACES tone mapping | ✅ (capture visuelle humaine en attente) | S5 |
| **M6** | **Shadow mapping directionnel (D32 2048², PCF 3×3, slope-scaled bias)** | ⏳ prochain | S6 |
| M7 | IBL compute (cubemap, irradiance, prefiltered, BRDF LUT) + skybox | ○ | S7 |
| M8 | Hot reload shaders (+includes), labels RenderDoc, audit perf/leaks final | ○ | S8 |

Chaque jalon clôt sur : Sandbox propre (0 message validation, 0 leak), tests verts, double audit agent (csharp-lowlevel + architecte) PASS, board archivé.

## État courant (fin session 4 + itérations caméra)

**Ce qui tourne** : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox` → DamagedHelmet texturé (15 452 triangles, 5 textures GLB, mips générés GPU, tangentes générées à l'import), caméra libre 6DOF (référentiel caméra complet), capture souris OS-confinée (clic capture / Échap libère / recentrage anti-butée), `-- BoxTextured.gltf` pour changer de modèle.

**Métriques** : 32 commits · 74 fichiers C# · ~9 300 lignes (src+samples) · **154 tests xUnit** (maths, allocateur via mock, DeletionQueue zéro-alloc prouvée, parsing glTF/GLB, accessors, tangentes, images, uniforms std140) · 8 audits agents (2 par jalon M2-M4), tous PASS.

**Acquis techniques clés** :
- Allocateur GPU testé sans GPU (seam `IMemoryBackend`), stats mémoire au shutdown
- DeletionQueue zéro-allocation (payload 4×ulong + destructeurs statiques, offset+memType bit-packés 40/24)
- Upload staging synchrone explicite (jamais de submit caché) + chaîne de mips par blits
- Assets 100 % CPU : glTF/GLB source-gen STJ, matrices colonne-major→row-vector prouvées par test, génération de tangentes Lengyel prouvée sur DamagedHelmet
- Set 1 per-material figé en forme PBR finale (5 textures + UBO facteurs) — M5 ne change aucun layout
- Renderer possède tout le câblage ; le Sandbox n'a plus un seul objet Vulkan manuel

**Dette consciente** (échéances au board) : anisotropie sampler dormante (M5), upload async (M8), feel souris à peaufiner (M8), WrapU/V séparés si fixture l'exige, instancing multi-mesh (phase 2), MikkTSpace si artefacts de tangentes.

## Plan M5 — PBR & lumières (prochaine session, plan architecte acté)

Critère de sortie (spec §5/§6) : DamagedHelmet éclairé, comparaison côte à côte avec le viewer glTF de référence Khronos (même angle/exposition), image annotée dans `docs/visual-checks/`.

Ordre d'exécution :

1. **Chantier multi-passes** (le gros morceau, à concevoir en premier) : `CommandList.BeginRendering/EndRendering/TransitionImage/SetViewport` publics ; FrameRenderer réduit à acquire/submit/present/sync ; depth déplacé au Renderer (ressource de technique). Callback `(cmd, frame, swapchainTarget)`.
2. **Target HDR** Rgba16Sfloat (ColorAttachment|Sampled) + depth possédés par le Renderer, recréés ensemble au resize.
3. **Passe tone mapping** fullscreen (triangle sans vertex buffer, `Draw(3)`) : ACES/Reinhard + exposition, sortie swapchain sRGB (OETF par le format), barrier HDR→ShaderReadOnly entre passes.
4. **Set 0 étendu** : binding 1 = UBO lumières (1 directionnelle + N ponctuelles), caméra visible Vertex|Fragment, `CameraUniforms` += position (+16 o).
5. **Shader PBR** : Cook-Torrance GGX metallic-roughness, normal mapping (TBN depuis les tangentes M4), AO, emissive — les 5 textures et l'UBO facteurs sont déjà bindés.
6. **Corrections actées** : normal matrix = inverse-transpose (1 ligne, requis avant de valider l'éclairage), normalScale/occlusionStrength lus du glTF (~4 lignes), Cull Back + validation du winding par modèle.
7. **Protocole visuel** : DamagedHelmet + FlightHelmet vs viewer Khronos, revue humaine documentée.

Estimation : ~12-14 tâches en 6-8 vagues, dont ~1 à 1,5 « session » pour le seul chantier multi-passes.

## Après M5

- **M6 ombres** : passe depth-only directionnelle, comparaison PCF 3×3, biais slope-scaled — dépend du multi-passes M5.
- **M7 IBL** : pipeline compute (equirect→cubemap→irradiance→prefiltered mips→BRDF LUT), cache disque par hash de HDRI, skybox. `CommandList` a déjà prévu le dispatch compute (spec §3.3).
- **M8 confort** : hot reload shaders avec watch des includes, debug labels RenderDoc, audit final perf/leaks, feel souris (lissage, sensibilité liée au FOV).
- **Validation multi-OS** : machines perso Windows/Linux au minimum à M8 (déjà fait M1 ; M4 à programmer).
