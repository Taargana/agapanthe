# Absolute-Human Board — Agapanthe Session 14 (P3-M1 : Instancing SSBO + solde des 2 dettes de culling)

**Status**: OPEN (2026-07-14) — Phase 3 ouverte. INTAKE + SPEC faits (spec approuvée, relue 4,1/5). Board décomposé, en attente feu vert avant EXECUTE.
**But** : **rembourser la dette perf du banc** (grid:100x100, FPS bas constaté à la vérif humaine P2-M4) en posant la **première marche du rendu GPU-driven** (façon Unreal 5 GPU Scene / Unity 6 BRG), sans rien gaspiller : transforms via un **buffer GPU (SSBO)** indexé par instance, **batching par (matériau, mesh)** → un draw instancié par batch, + solde des **2 dettes de culling**. Le culling **reste CPU** (décision humaine) ; GPU compute cull + slots persistants = jalon suivant P3-M2.
**Créé**: 2026-07-14
**Spec**: [docs/plans/2026-07-14-p3m1-instancing-culling-design.md](../docs/plans/2026-07-14-p3m1-instancing-culling-design.md) (approuvée ; relecture indépendante 4,1/5 APPROVED). Plan approuvé : `C:\Users\yannl\.claude\plans\misty-doodling-minsky.md`.
**Board persistence**: git-tracked
**Sessions passées**: S1-S13 → .absolute-human/archive/ (S13 = board-session13-P2M4.md, PHASE 2 CLOSE).

## Décisions d'architecture (verrouillées — cf. spec Decision Log)

- **D0** : périmètre = **cull CPU** ce jalon ; GPU-driven (compute cull + slots persistants + indirect) = P3-M2. (Décision humaine.)
- **D1** : instancing via **SSBO host-visible per-frame compacté** ; le Renderer copie les matrices de la `RenderList` (Core) dans un storage buffer mappé pendant le record (World reste GPU-free) ; VS lit `transforms[gl_InstanceIndex]`, offset par `firstInstance`. SSBO readonly en vertex + `firstInstance` en draw direct = **Vulkan core, aucune feature**.
- **D2** : `ComposeSortKey(int material, int mesh, uint tieBreak)` → `(material<<48)|(mesh<<32)|tieBreak` ; tie-break plein 32 bits (déterminisme). Batch = run de (matériau, mesh) identiques → 1 `DrawIndexed(indexCount, instanceCount=run, 0, 0, firstInstance=start)`.
- **D3** : cull d'ombre = **frustum caméra extrudé vers la lumière** (garder les plans dont `dot(n_in, dir) ≤ 0`, ε→garder) **ANDé** avec le test de volume existant. `ShadowFit` **inchangé** → casque byte-identique ; l'AND ne fait que restreindre (pas de clipping).
- **D4** : `AggregateBounds` **per-frame** (recompute, 0-alloc) ; dirty-track différé.

## Critère de sortie (jalon P3-M1)

- Casque **byte-identique (≤ 1 LSB)** scène + ombres (l'instancing et l'AND de cull ne changent pas les pixels) ;
- draw calls effondrés : **≈ 1 scène + 1 ombre** pour le banc mono-modèle (vs ~12 556) ;
- banc `grid:100x100` mesuré en **Release JIT ET NativeAOT** : cull+collect+record ms et draw calls avant/après ;
- **0 alloc/frame** maintenu · **0 message de validation** · **0 leak** (ResourceTracker) · **0 warning** ;
- tests unitaires verts (ExtrudedShadowFrustum, bounds sous translation, sort key + batching + déterminisme) ;
- **double audit** `csharp-lowlevel` + `engine-architect` PASS.

## Tâches (DAG)

Types : `code` | `test` | `verify` | `audit`. Tailles : S (<50 l) · M (50-200 l).
Convention tests : GPU-free → xUnit ; GPU → capture byte-identique + 0 val/0 leak.

| ID | Titre | Type | Sz | Dép. |
|---|---|---|---|---|
| **V0 — Plomberie SSBO (Graphics)** ||||
| AW-001 | `BufferUsage.Storage` + `GpuBuffer.ToVkUsage` map + `GpuBuffer.MappedSpan<T>` (zéro-copie) | code | M | — |
| AW-002 | `DescriptorKind.StorageBuffer` + `ToVkType` + `DescriptorWrites.StorageBuffer` + `FrameContext.WriteStorageBuffer` + pool size storage | code | M | — |
| **V1 — SortKey (Core/World)** ||||
| AW-010 | Tests `ComposeSortKey(material,mesh,tieBreak)` : layout, batching (material,mesh), déterminisme forward/reverse (MAJ `RenderListTests.cs`) | test | S | — |
| AW-011 | `RenderItem.ComposeSortKey` 3-arg + layout `<<48/<<32` ; `CollectRenderLists` passe `Mesh.Index` | code | S | AW-010 |
| **V2 — Scene pass instancié (Rendering/shaders)** ||||
| AW-020 | `mesh.vert` lit SSBO (set 0 b6) ; `ScenePass` (drop model push, garde `debugView` frag off.64) ; `_frameSetLayout` +b6 ; `_sceneTransforms[]` + compaction (MappedSpan) + boucle batching dans `RecordScenePass` ; `LastSceneDrawCalls` | code | M | AW-001, AW-002, AW-011 |
| AW-021 | Gate capture casque **byte-identique** (scène) + 0 validation + log draw calls scène | verify | S | AW-020 |
| **V3 — Shadow pass instancié** ||||
| AW-030 | `shadow.vert` lit SSBO (set 0 b0, push 64) ; `ShadowPass` set layout ; `_shadowTransforms[]` ; `RecordShadowPass(frame,…)` + compaction + batching | code | M | AW-020 |
| AW-031 | Gate capture casque **byte-identique** (ombres) + 0 validation + log draw calls ombre | verify | S | AW-030 |
| **V4 — Bounds per-frame (dette #1)** ||||
| AW-040 | Test `AggregateBounds` correct sous **translation** (World, GPU-free) | test | S | — |
| AW-041 | `Program.cs` : `AggregateBounds()` per-frame dans `drawScene` avant `ComputeLightViewProj` ; 0-alloc préservé | code | S | AW-040 |
| **V5 — Cull d'ombre extrudé (dette #2)** ||||
| AW-050 | Tests `ExtrudedShadowFrustum` : (i) garde caster latéral qui projette dans la vue, (ii) jette hors-champ non-projetant, (iii) garde on-screen, (iv) ε parallèle → garde | test | M | — |
| AW-051 | `ExtrudedShadowFrustum` (Core, GPU-free) : plans caméra filtrés `dot(n_in,dir)≤0`, ε→garder | code | M | AW-050 |
| AW-052 | `CollectRenderLists` +`in ExtrudedShadowFrustum` (AND) ; MAJ appel interne `AotRootingSmoke:159` (`Count==8` reste valide) ; `Program.cs` construit l'extrudé ; MAJ `WorldSystemsTests` (8 appels) + **révision anti-popping (F3)** | code | M | AW-051, AW-041 |
| **V6 — Banc, mesures, clôture** ||||
| AW-060 | `Renderer.LastSceneDrawCalls/LastShadowDrawCalls` loggés dans le bloc bench (`Program.cs`) | code | S | AW-030, AW-052 |
| AW-061 | Banc `grid:100x100` **Release JIT + AOT** : cull+collect ms + draw calls avant/après ; 0-alloc ; 0 leak ; 0 validation | verify | M | AW-060 |
| **Queue obligatoire** ||||
| AW-070 | Self code review du diff complet (portée, conventions, 0-alloc, frontières modules) | verify | S | AW-061 |
| AW-071 | **Double audit** `csharp-lowlevel` + `engine-architect` (générateur ≠ évaluateur) | audit | M | AW-070 |
| AW-072 | Vérif projet complète : `dotnet build` (0 warn) + `dotnet test` + capture headless + publish/run AOT | verify | M | AW-071 |

## Vagues (exécution, gate humain entre chaque)

- **Wave 1** : AW-001, AW-002 (Graphics disjoints → parallèle-safe), AW-010, AW-040, AW-050 (tests GPU-free, fichiers disjoints → parallèle-safe). *Gate : build + tests rouges attendus posés.*
- **Wave 2** : AW-011 (Core+World), AW-041 (Program), AW-051 (Core). *Gate : tests unitaires verts.*
- **Wave 3** : AW-020 + AW-021 (scène instanciée + capture). *Gate : byte-identique scène.*
- **Wave 4** : AW-030 + AW-031 (ombre instanciée + capture). *Gate : byte-identique ombres.*
- **Wave 5** : AW-052 (cull extrudé branché). *Gate : tests extrudé + anti-popping + casque byte-identique + chute casters.*
- **Wave 6** : AW-060, AW-061 (banc + mesures). *Gate : perf Release/AOT + 0-alloc/leak/validation.*
- **Wave 7 (queue)** : AW-070 → AW-071 → AW-072. *Gate : double audit PASS + vérif complète → clôture.*

**Fichiers partagés (sérialisés entre vagues)** : `Renderer.cs` (W3→W4), `GameWorld.cs` (W2 AW-011 → W5 AW-052), `Program.cs` (W2 AW-041 → W5 AW-052 → W6 AW-060). Aucun conflit intra-vague (fichiers disjoints).

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, **AOT-pur**, **aucun Vk* hors Graphics**, **aucun type Arch hors World**, IDisposable + DeletionQueue N+2, **zéro alloc/frame**, ResourceTracker (leak = échec), 0 message de validation.
- Perf mesurée en **Release/AOT uniquement** (Debug+validation non représentatif : 74-92 ms vs ~3,7 ms attendu).
- Publish AOT : PATH inclut `C:\Program Files (x86)\Microsoft Visual Studio\Installer` (vswhere).
- Env vars : `AGAPANTHE_SCENE=grid:NxN` · `AGAPANTHE_CULL_STATS=1` · `AGAPANTHE_MAX_FRAMES` · `AGAPANTHE_CAPTURE` · `AGAPANTHE_WORLD_ORIGIN`.

## Risques & veille (cf. spec §Risks)

- **F4** : `baseInstance` (firstInstance ≠ 0) sur **MoltenVK/Apple** = seul risque de portabilité → gate macOS dédié (rejoint la dette Linux/macOS). Non bloquant desktop.
- **F3** : test anti-popping `CollectRenderLists_KeepsAnOffScreenCasterInTheShadowList` à réviser sous la sémantique plus serrée (V5, avant merge).
- **F8** : appel interne `CollectRenderLists` dans `AotRootingSmoke:159` (+ assert `Count==8`) à ré-signer en V5 — **réel** (l'évaluateur de spec l'avait nié à tort, re-vérifié contre `GameWorld.cs:110-164`).
- Signe des plans de l'extrudé figé par les tests (i)/(ii)/(iii) avant merge (erreur de signe = faux négatif = popping).

## Hors périmètre (→ P3-M2 « rendu GPU-driven »)

Slots persistants dirty-trackés · culling GPU compute · draw indirect · visibility index buffer. Le VS passera de `transforms[gl_InstanceIndex]` à `transforms[visible[gl_InstanceIndex]]`.

> **Correction (audit de clôture)** : « 1 ligne, chemin de draw inchangé » était **faux**. Avec un cull GPU, l'`instanceCount` d'un batch n'est plus connu côté CPU → il faut `vkCmdDrawIndexedIndirect(Count)`, donc `BufferUsage.Indirect`, `CommandList.DrawIndexedIndirect`, et la feature **`drawIndirectFirstInstance`** (le `firstInstance` d'un draw **direct** est gratuit ; celui d'un draw **indirect** ne l'est pas). Piste : porter l'offset de batch en **push constant** → s'affranchit de `firstInstance` et neutralise F4/MoltenVK du même coup.

## Rollback Point

`8311751` (docs: clôture vérifs humaines Phase 2) — dernier état vert : 275 tests, 0 warning, 0 validation, 0 leak, AOT PASS. Tree propre hors nouveaux fichiers de doc/board de S14.

## Log

- 2026-07-14: **Session 14 ouverte — P3-M1.** INTAKE via absolute-brainstorm (plan approuvé) → décision humaine : instancing prêt-pour-GPU, cull CPU ce jalon (D0). SPEC écrite + relue (4,1/5 APPROVED, une erreur d'évaluateur sur F8 rejetée après vérif code). Board décomposé (18 tâches, 7 vagues).
- 2026-07-14: **Wave 1 verte** — AW-001/002 (plomberie SSBO : `BufferUsage.Storage`, `MappedSpan<T>`, `DescriptorKind.StorageBuffer`, `WriteStorageBuffer`, pool). Build 0 warn, 275 tests.
- 2026-07-14: **Wave 2 (GPU-free) verte** — AW-010/011 (sort key `[material:16][mesh:16][tie:32]`), AW-050/051 (`ExtrudedShadowFrustum` + 4 tests), AW-040 (bounds sous translation). 280 tests.
- 2026-07-14: **V2 verte** — AW-020 (scene pass instancié : `mesh.vert` lit SSBO b6, batching (material,mesh), `firstInstance`). AW-021 gate : casque **byte-identique** (SHA match, 0 canal), grille 3×3 = 9 casques distincts (firstInstance validé), 0 validation, 0 leak. Checkpoint V0-V2 committé (`d7de0fd`).
- 2026-07-14: **V3 verte** — AW-030 (shadow pass instancié : `shadow.vert` lit SSBO, push 64B, batching par mesh). AW-031 gate : casque+ombres byte-identique, 0 validation, 0 leak.
- 2026-07-14: **V4+V5 vertes** — AW-041 (`AggregateBounds` per-frame, debt #1), AW-052 (`CollectRenderLists` + `in ExtrudedShadowFrustum` AND, appels tests + `AotRootingSmoke` MAJ, anti-popping F3 révisé + test drop d'intégration). Gate : 281 tests, casque byte-identique, **casters 10 000 → 5040**, cull+collect+record ~78 → 6,9 ms (Debug), 0 alloc/leak/validation.
- 2026-07-14: **V6 verte** — banc + compteurs de draw calls. Mesures : draws **12 556 → 2**, cull+collect 3,7 → ~2,0 ms (Release JIT), ~6 → ~2,2 ms (AOT), 0 alloc/frame.
- 2026-07-14: **V7 — double audit de clôture** (`csharp-lowlevel` + `engine-architect`) : **PASS / PASS, 0 bloquant**, 2 MAJEURS **corrigés dans le jalon** :
  - 🔴 **ε du wedge inversé** (`ExtrudedShadowFrustum:74`) : les plans *exactement parallèles* au rayon étaient jetés — or ce sont eux qui ferment le wedge. Soleil au zénith + caméra à plat ⇒ 4 plans latéraux tombés ⇒ **le wedge ne cullait plus rien** ; le banc y échappait par accident (soleil non aligné). Corrigé (drop ssi `dot > +ε`), test zénith ajouté, test near-parallel réécrit (il épinglait le mauvais comportement).
  - 🔴 **Fit d'ombre instable** (`ShadowFit`) : la branche « scène » ne snappait pas (hypothèse « scène statique » tuée par les bounds désormais per-frame) ⇒ crawl des bords d'ombre dès qu'une entité bouge. Rayon **quantifié** (16 crans/octave) + snap texel dans **les deux** branches + test en escalier. **Conséquence assumée : capture casque plus bit-identique** (0,25 % de canaux, décalage sub-texel) — rendu vérifié intact.
  - Mineurs appliqués : clé **mesh-major** pour la liste d'ombre (fin de la sur-découpe) · `InstanceBufferRing` extrait de `Renderer` (compaction dupliquée + **shrink** après 60 frames sous ¼ de capacité) · rebind du set 1 seulement au changement de matériau · pool persistant déclarant `StorageBuffer` · `GpuBuffer.Write` en multiplication 64 bits · plafond 16 bits documenté comme limite dure.
  - Gate final : **284 tests**, 0 warning, 0 validation, 0 leak, 0 alloc/frame, **NativeAOT PASS** (banc 10k : draws 1+1, ~2,2 ms).
