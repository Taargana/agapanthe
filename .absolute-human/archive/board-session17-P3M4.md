# Absolute-Human Board — Agapanthe Session 17 (P3-M4 : GPU-driven render — cull compute + draw indirect)

**Status**: ✅ **CLOSED (2026-07-19)** — W0→W2 livrés, **double audit PASS**, findings appliqués, **verdict visuel
humain PASS avec dette d'ombre connue**. Le cull GPU est prouvé correct (GPU visible == CPU, 2557 @10k AOT ;
mono bit-identique `9790D95D`). Constat visuel humain sur grille (zone rectangulaire + anneaux au sol) **diagnostiqué
comme préexistant, PAS le cull** : (1) moiré texture d'herbe au rasant (backlog §5) ; (2) empreinte shadow map + acné
sur sol plat — plafond cascade unique, **le plan de sol de 340 m est caster → `eyeDistance` 248 m** (correctif = CSM
backlog §2 ; mitigation cheap = ground non-caster) ; (3) « ombres sans casque » = anti-popping P3-M2 (correct).
Preuve que ce n'est pas le cull : `AGAPANTHE_GROUND=0` supprime tout, mono bit-identique, passe d'ombre non touchée.
Scope livré : **(A)+(B)** ; **(C) slots persistants → jalon suivant** (backlog §1, rembourse aussi la régression A+B).

**But** : sortir l'**émission des draws** et le **frustum cull** de la scène opaque du CPU vers le GPU. Passe de
« cull CPU O(n) → RenderItem[] trié → Compact/frame → boucle CPU de batches → un DrawIndexed/run » à « buffer
d'args GPU + `DrawIndexedIndirect` » et « cull+compaction en compute shader ». *Payoff : mord à 100k entités.*
**Spec** : [docs/plans/2026-07-19-p3m4-gpu-driven-design.md](../docs/plans/2026-07-19-p3m4-gpu-driven-design.md)
**Baseline de rendu** : `9790D95D` · **Sessions passées** : S1–S16 → `archive/` (S16 = P3-M3 physique, clos).

## Décisions verrouillées (détail : spec § Journal des décisions)

- **Scope A+B, C reporté** (décision humaine) : plomberie + cull GPU = la moitié auto-contenue et sensible
  MoltenVK ; les slots persistants (vrai gain 100k, logique origine-mobile) = jalon distinct.
- **Offset de batch en push constant, pas `firstInstance`** : tue la dépendance à `drawIndirectFirstInstance` ET
  le risque `baseInstance` MoltenVK d'un coup. Shaders : `instances.model[gl_InstanceIndex + pc.batchOffset]`.
- **Gate W0 = bit-identique `9790D95D`** (plomberie, comportement inchangé). **Gate W1 = visuellement identique +
  compte visible == cull CPU** (test frustum float GPU ≠ CPU aux marges de plan ; ordre intra-batch via atomics
  sans effet sur l'image opaque z-testée).
- **Table de batches construite CPU** (évite `VK_KHR_draw_indirect_count`, hors core 1.2 / incertain MoltenVK) :
  le CPU connaît le nombre de draws, le GPU ne remplit que l'`InstanceCount` de chaque batch.
- **Cull d'ombre reste CPU** (wedge deux passes P3-M2, subtil) — profite quand même du draw indirect de W0.

## Critère de sortie

Tests verts · 0 warning · 0 validation · 0 leak · **W0 capture bit-identique `9790D95D`** · **W1 compte visible ==
cull CPU + visuellement identique** · banc `grid:100x100` draws==2 + 0 alloc/frame · NativeAOT PASS · double audit
PASS · **verdict visuel humain**.

## Vagues

| # | Contenu | Gate |
|---|---|---|
| W0 | Graphics : `BufferUsage.Indirect` · `DrawIndexedIndirectCommand` · `CommandList.DrawIndexedIndirect` · `BufferBarrier`/`BufferSync`. Rendu : `IndirectArgsRing` (ring/frame) + `batchOffset` push-constant (scène offset 0, ombre offset 64), shaders indexent `gl_InstanceIndex+batchOffset`. **Cull+batch CPU inchangés.** | ✅ Capture **bit-identique `9790D95D`** (mono + grid + **AOT**) · 321 tests · 0 warning · 0 validation · 0 leak |
| W1 | `SceneCandidate` (transform cam-relative + sphère + batchId) + `StorageBufferRing<T>` + table de batches CPU · `scene_cull.comp` (frustum-cull reproduisant `Frustum.Intersects` + compaction atomics + écrit `InstanceCount`) · `CommandList.BufferBarrier` compute→indirect/vertex · readback `AGAPANTHE_CULL_VERIFY`. Scène non cullée CPU (candidats) ; ombre reste CPU two-pass. | ✅ **GPU visible == CPU** (110 grid, **2557 @10k AOT** — MATCH) · mono-modèle **bit-identique `9790D95D`** · 0 validation · 0 leak · 321 tests |
| W2 | Banc `grid:100x100` Release+AOT · draws==2 · 0 alloc/frame · double audit · findings · docs · archive · verdict humain | 🚧 **AOT PASS** · **0 alloc/frame @10k** · draws 2+2 · **double audit PASS** (`csharp-lowlevel` PASS · `engine-architect` PASS with concerns) · findings appliqués · **verdict visuel humain dû** |

**Double audit P3-M4 (2026-07-19) — PASS, aucun changement de code requis.** `csharp-lowlevel` **PASS** (sync/hazards
couverts, 0 alloc, 0 leak, compaction sûre, layouts std430 concordants) ; `engine-architect` **PASS with concerns**.
Findings appliqués : (code) `BufferSync.TransferWrite` mort retiré, commentaires `RenderItem` 88→104 o ; (docs/backlog)
**§1 réécrit** — honnêteté de la régression A+B @10k (le mur migre cull→sort+upload, (C) le rembourse), buffers
GPU-produits en device-local (dette), compaction atomique = 2ᵉ verrou transparence, MultiDrawIndirect + cull-ombre-GPU.
Bonus livré à la demande humaine : **HUD debug barre de titre** (fps/ms/draws/candidates/GC MB, `EngineWindow.Title`).

**Dépendances** : W1 après W0 (le cull GPU écrit dans le buffer d'args que W0 introduit) ; W2 après W1. W0 et W1
touchent tous deux `Renderer.cs` + les shaders → **séquentiels**.

## Risques

- **F1 — MoltenVK** : `BufferUsageFlags.IndirectBufferBit`, la stage `DrawIndirect`, l'atomic SSBO compute →
  vérifier chaque feature au **premier VUID** sur MoltenVK (non testable ici : pas de machine macOS — dette P3-M0).
  L'offset en push constant neutralise déjà `drawIndirectFirstInstance`/`baseInstance`.
- **F2 — Bit-exact W0** : déplacer l'offset de `firstInstance` vers un push constant ne doit changer aucun vertex
  → capture `9790D95D`. Si diff, c'est un bug d'indexation, pas une tolérance.
- **F3 — Aléa de synchro** : compute-write → indirect-read ET vertex-read = deux barrières (stages différents). Un
  oubli = message de validation (gate bloquant) ou corruption silencieuse. Zéro-init des `InstanceCount` avant le
  dispatch, sinon accumulation entre frames.
- **F4 — Alloc/frame** : buffer d'args + buffer de candidats = rings réutilisés (doublement, 0 alloc régime établi),
  comme `InstanceBufferRing`.
- **F5 — Compte W1** : le readback debug du compte visible ne doit pas être sur le hot path (Debug-only / banc).

## Dette restante à la clôture (prévision)

(C) slots persistants dirty-trackés (backlog §1, prochain jalon) · MultiDrawIndirect (un seul draw) · cull compute
de l'ombre · verdict visuel P3-M1 toujours dû · Linux/macOS jamais validés (P3-M0, MoltenVK non prouvé) · CSM/PCSS
(backlog §2) · dette physique P3-M3 (backlog §4).

## Log

- 2026-07-19: **Session 17 ouverte — P3-M4 GPU-driven.** Fork tranché (humain) : scope A+B (C reporté) ; gate W1 =
  compte+visuel (W0 bit-identique). Plan approuvé, spec écrite.
- 2026-07-19: **W0→W2 livrés.** W0 draw-indirect bit-identique `9790D95D` (AOT) ; W1 cull compute (GPU==CPU 2557 @10k) ;
  W2 banc 0 alloc/frame @10k AOT, draws 2+2. **Double audit PASS** (findings docs/backlog appliqués). HUD barre de titre
  livré (voie A). **Verdict visuel PASS** — artefacts sol diagnostiqués préexistants (shadow/texture), pas le cull.
  **P3-M4 CLOS.** Reste : commit (sur demande) + choix du prochain jalon.
