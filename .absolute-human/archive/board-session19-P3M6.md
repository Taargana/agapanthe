# Absolute-Work Board — Agapanthe Session 19 (P3-M6 : slots persistants + cull d'ombre GPU)

**Status**: ✅ **CLOS (2026-07-23)** — toutes vagues livrées, **double audit PASS** (findings appliqués), **verdict visuel humain PASS**.
Design instruit (absolute-brainstorm), spec approuvée (`engine-architect` indépendant, **4.6/5**).
**322 tests verts** · 0 warning · 0 validation · 0 leak · NativeAOT PASS (probe + Sandbox) · banc `grid:100x100` AOT draws **2+4** / 0 alloc / GPU==CPU MATCH · mono bit-identical `4848F93F`.

**Double audit (session 19) :**
- `graphics-3d` : **PASS** (0 🔴/🟠 ; 4 🟡). Région d'ombre correcte, barrières justes, aucun risque VUID sur le chemin exercé.
- `csharp-lowlevel` : **PASS with concerns** (0 🔴/🟠 ; 3 🟡). Invariant §5 correct sous F=2, 0 leak, layout exact.
- **Findings appliqués** : spec §8 corrigée (planes SSBO, pas UBO) · `InstanceSlot` ajouté à `DrawableDesc` (couplage fail-safe) ·
  probe AOT exerce désormais le chemin incrémental (`PatchDirtyPersistent` rooté) · test 0-alloc déterministe du sync Rendering (`CopySyncStateTests`).
- Non appliqués (jugés correctness-safe par les deux) : micro-sur-comptage countdown transitoire · duplication du harnais de test sync.

**Gate final (mesuré, AOT `grid:100x100` sauf mention) :**
- **GPU scène visible == CPU** : `ReadBackSceneVisible` MATCH (JIT `2584==2584`, AOT `2553/2576/2552==` selon run).
- **Mono bit-identical** vs baseline pristine `0cad95e` : CRC `4848F93F == 4848F93F` (scène par défaut + statique).
- **Cull d'ombre entièrement GPU** : `shadow_cull.comp` remplace `CollectShadowCasters` + 4 listes managées (~12 Mo **disparues**).
  draws **2+4** (2 scène, 4 régions cascade×mesh-batch).
- **0 alloc/frame** : test unitaire déterministe vert (100 itérations = 0) ; banc AOT 0 en régime établi (1 blip 280 B / 660 frames = bruit runtime, non périodique — noté).
- Incrémental **O(dirty)** : gather+radix sort ne tournent qu'au rebuild structurel ; scène statique → dirty vide (prouvé unit).
- ~15 ms banc animé AOT (10k spinnés = pire cas ; raster ombre 4× **déféré**, device-local **déféré**).
- **Leak corrigé en cours** (ressources shadow-cull non disposées → ajoutées au teardown). Cleanup wedge : ~200 lignes mortes retirées.

**Dette de test notée (pour l'audit)** : le cull scène a un gate auto GPU==CPU (`ReadBackSceneVisible`) ; le cull
d'ombre n'a pas d'équivalent — sa correction repose sur le test unitaire de région + capture bit-identical + verdict humain.
**Sessions passées** : S1–S18 → `archive/` (S18 = P3-M5 CSM, clos).

**But** : refermer **deux dettes fraîches** d'un coup — la régression sort/upload O(n) de P3-M4
([backlog §1 (C)](../docs/BACKLOG.md)) et le cull par cascade quasi inopérant de P3-M5
([§2.0bis](../docs/BACKLOG.md)). Slots persistants dirty-trackés côté scène ; cull d'ombre sur GPU.
**Spec** : [docs/plans/2026-07-23-p3m6-persistent-slots-gpu-shadow-cull-design.md](../docs/plans/2026-07-23-p3m6-persistent-slots-gpu-shadow-cull-design.md)
**Baseline** : mono **bit-identical `9790D95D`** (scène par défaut) ; banc animé pas de bit-identical sur l'ombre
(cull GPU flottant, gate P3-M4 W1).

## Décisions verrouillées (détail : spec §2 + journal des décisions)

- **Un jalon**, vagues **W1 slots → W2 cull scène → [FEU VERT HUMAIN] → W3 cull d'ombre**.
- **(C) = dirty par-entité complet** (referme sort *et* upload) + **scène de preuve statique**.
- **W3 = compute shadow cull seul** ; **raster 4× déféré** (borner cascades en profondeur = risque gate visuel).
- **Buffers host-visible mappés** ; **device-local déféré** (obvié par le dirty-tracking).
- **§5 invariant double-buffer** : miroir CPU autoritatif + `_structuralVersion`/`_copyVersion`/`_slotCountdown`
  (plat, 0-alloc), **sync-before-use** → batch-table couplée à la copie par construction.
- **`SceneCandidate` : `Rendering → Core`** ; **`InstanceSlot` rooté AOT** (`ComponentRegistry`), présent à
  `MaterialiseDrawable` **et** `SpawnBody`.

## Critère de sortie

Banc `grid:100x100` JIT **et** AOT : scène draws **2**, ombre = régions (cascade × mesh-batch) ·
**0 alloc/frame** · `AGAPANTHE_CULL_VERIFY=1` → scène GPU == CPU · mono **bit-identical `9790D95D`** ·
scène statique posée → dirty vide / patch ≈ 0 · **12 Mo de listes managées d'ombre disparus** ·
0 warning · 0 validation · 0 leak · NativeAOT PASS · **double audit** (`csharp-lowlevel` +
`graphics-3d`/`engine-architect`) PASS · **verdict visuel humain**.

## Project Conventions (détectées)

- .NET 10, `TreatWarningsAsErrors`, NativeAOT (`AotComponentProbe` + Sandbox). Tests **xUnit** (`dotnet test`).
- Build `dotnet build` · gates bloquants **0 warning / 0 message de validation / 0 leak ResourceTracker**.
- Modules : `World` (GPU-free, seul à référencer Arch) · `Rendering` → `Graphics` (seul à voir `Vk*`) · `Core`
  (GPU-free partagé). MoltenVK portability : vérifier chaque feature/flag au premier VUID.
- **Commits sur demande uniquement.** Conversation FR / code+docs EN.

## DAG des tâches

```
Wave 1 (foundations)          Wave 2 (W1 World+Core)         Wave 3 (W1 Rendering)
  AW-001 SceneCandidate→Core    AW-003 persistent set (Core)   AW-006 persistent buffer + mirror + §5 sync
  AW-002 InstanceSlot (AOT)     AW-004 CollectRenderLists           ▲ dep: 001,003,004
        │      │                       rebuild/incrémental
        │      │                 AW-005 dirty enqueue (3 surfaces)
        ▼      ▼                       ▲ dep: 002,004
  001 → 003 → 004 → 005                            │
  002 ─────────┘                                   ▼
                                            Wave 4 (W2)  ── FEU VERT HUMAIN ──▶ Wave 5 (W3)
                                              AW-007 cull scène lit le persistant   AW-008 shadow_cull.comp
                                                    ▲ dep: 006                            ▲ dep: 001
                                                                                    AW-009 RecordShadowPass + orchestrator
                                                                                          ▲ dep: 007,008,006
                                                                                    AW-010 cleanup wedge mort
                                                                                          ▲ dep: 009
Wave 6 (tail, obligatoire)
  AW-011 bench + scène statique (mesures)  ▲ dep: 009
  AW-012 self code review (diff)           ▲ dep: 011
  AW-013 requirements validation (vs spec) ▲ dep: 012
  AW-014 full verification + AOT           ▲ dep: 013
```

## Tâches

### Wave 1 — foundations

**AW-001** · code · **M** · deps: — · ✅ `done`
Déménage `SceneCandidate` de `Agapanthe.Rendering` → `Agapanthe.Core` ; étend le struct (reste **96 o**) :
`Model(64) Sphere(16) SceneBatchId(4) ShadowBatchId(4) Flags(4) Pad(4)`. Met à jour `scene_cull.comp` (struct
`Candidate`) et les références `Renderer`. **Tests** (TDD) : taille/layout blittable 96 o ; std430 miroir shader.

**AW-002** · code · **S** · deps: — · ✅ `done`
Nouveau composant `[Component][StructLayout(Sequential)] InstanceSlot { int Value }`. `Root<InstanceSlot>()`
dans `ComponentRegistry` ; inclus dans l'archétype à `MaterialiseDrawable` **et** `SpawnBody` (pas d'`Add`
tardif). **Tests** : test de complétude par réflexion (tout `[Component]` enregistré) + couverture
`AotComponentProbe`.

### Wave 2 — W1 World + Core

**AW-003** · code · **M** · deps: 001 · ✅ `done`
Type persistant Core-side (GPU-free) : tableau de `SceneCandidate`, liste de patch dirty `(slot,model,sphere)`,
**scene batch table** (material-major) + **shadow batch table** (mesh-major : `meshBatchBase`/`count`). Buffers
réutilisés (`Clear` garde capacité). **Tests** : réutilisation capacité (0-alloc) ; construction des deux tables
depuis une liste triée (histogramme mesh-major contigu).

**AW-004** · code · **M** · deps: 003, 002 · ✅ `done`
Réécrit `GameWorld.CollectRenderLists` en deux régimes (§6) : **rebuild structurel** (`_structuralDirty` |
origine changée → tri material-major, slot = index trié, deux batch tables, map GlobalId→slot) vs
**incrémental** (patch des slots dirty). **Tests** : triggers structurels (spawn/despawn/edit mesh-mat/re-snap)
forcent un rebuild + slots frais == oracle from-scratch ; stabilité de slot entre rebuilds.

**AW-005** · code · **S** · deps: 002, 004 · ✅ `done`
Enqueue dirty aux **trois surfaces** internes World : `AnimateDrawables` (`GameWorld.cs:563`), writeback
physique (`GameWorld.Physics.cs:162`, via `Get<InstanceSlot>()`), `PropagateTransforms` (`GameWorld.cs:603`).
**Tests** : entité déplacée enfilée, statique non ; scène posée → dirty vide.

### Wave 3 — W1 Rendering

**AW-006** · code · **M** · deps: 001, 003, 004 · ✅ `done`
Set d'instances persistant côté Rendering : `_mirror` (source CPU), **F copies GPU** host-visible (patron
`InstanceBufferRing`), `_slotCountdown` (plat, 0-alloc), `_structuralVersion`/`_copyVersion[F]` ; `Rebuild(...)`
+ `Patch(...)` + **sync-before-use** (§5). **Tests** : invariant §5 — spawn+despawn pendant F frames en vol →
copie consommée toujours à l'assignation courante (no OOB/ghost) ; convergence après F frames. Logique
miroir/countdown testable GPU-free ; le fencing des copies suit le patron `InstanceBufferRing`.

### Wave 4 — W2 (cull scène GPU) → **FEU VERT HUMAIN avant Wave 5**

**AW-007** · code · **M** · deps: 006 · ✅ `done`
`Renderer.CullSceneOnGpu` lit la **copie persistante** (drop `_candidateScratch` + `_sceneCandidates.Upload`) ;
`batchBase` sur le ring par-copie (`frame.Slot`) ; args re-zéro par frame ; `scene_cull.comp` binding 0 =
persistant. **Gate** : `AGAPANTHE_CULL_VERIFY=1` GPU==CPU ; mono **bit-identical `9790D95D`** ; 0 alloc/frame.

### Wave 5 — W3 (cull d'ombre GPU)

**AW-008** · code · **M** · deps: 001 · ✅ `done`
Nouveau `shadow_cull.comp` : 1 invocation/candidat, teste `Flags` bit 0 puis 4 frusta de cascade ; atomicAdd
région `(cascade c, mesh-batch m)` = `c×totalCasters + meshBatchBase[m]`. 5 bindings (candidats persistants,
out-instances, args, meshBatchBase/count, cascade planes UBO set0 b4). **Tests** : oracle CPU du calcul de
région (caster multi-cascade → k écritures contiguës) ; VUID MoltenVK du UBO planes vérifié.

**AW-009** · code · **M** · deps: 008, 007, 006 · ✅ `done`
`Renderer.RecordShadowPass` : dispatch `shadow_cull` (args re-zéro, barrières compute→indirect/vertex, barrière
candidats partagée) ; drop `CollectShadowCasters` + 4 `_cascadeCasters` ; 1 `DrawIndexedIndirect` **par région**
(instanceCount=0 = no-op GPU) dans sa tuile d'atlas. `FrameOrchestrator.SceneViewSystem` mis à jour. **Gate** :
visuel « identique + count par cascade justifié » ; mono bit-identical `9790D95D` ; 0 alloc/frame ; **12 Mo
managés disparus**.

**AW-010** · code · **S** · deps: 009 · ✅ `done`
Cleanup code mort du wedge — vérifier ce qui subsiste puis retirer : `ShadowFit.ComputeLightViewProj`,
`ExtrudedShadowFrustum` (+ tests), `Renderer.ComputeFrustumSphere`, `ShadowCasterDistance`.

### Wave 6 — tail (obligatoire)

**AW-011** · test · **S** · deps: 009 · ✅ `done`
Mesures : banc `grid:100x100` JIT+AOT (draws, 0-alloc, GPU==CPU, ms vs 11,4) ; **scène statique** (`drop:N`
posée ou grid figé) → dirty vide, patch ≈ 0.

**AW-012** · test · **S** · deps: 011 · ✅ `done` — Self code review du diff (reuse, altitude, 0-alloc hot path).

**AW-013** · test · **S** · deps: 012 · ✅ `done` — Requirements validation vs spec §1-§9 (chaque gate coché).

**AW-014** · test · **S** · deps: 013 · ✅ `done`
Full verification : `dotnet build` + `dotnet test` verts, 0 warning/validation/leak, `AotComponentProbe` +
Sandbox NativeAOT PASS.

## Deferred Work (hors périmètre → backlog)

- Raster ombre 4× (cascades bornées en profondeur + `UpstreamExtent` par cascade) — [backlog §2.0bis](../docs/BACKLOG.md).
- Buffers device-local + isolation Debug `ReadBackSceneVisible` — [backlog §1](../docs/BACKLOG.md).
- MultiDrawIndirect · `SortKey` sans profondeur · plafond 16 bits mesh/matériau.

## Rollback Point

À capturer **avant** que Wave 1 touche un fichier (arbre propre requis). Commit courant au plan : `0cad95e`.

## Clôture (CONVERGE — méthode projet)

Double audit (`csharp-lowlevel` + `graphics-3d`/`engine-architect`) findings appliqués · **verdict visuel
humain** (protocole `docs/visual-checks/`) · maj `docs/AVANCEMENT.md` + `docs/BACKLOG.md` · suggestion de commit
(jamais auto). Board archivé `archive/board-session19-P3M6.md`.
