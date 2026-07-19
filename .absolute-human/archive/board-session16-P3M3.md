# Absolute-Human Board — Agapanthe Session 16 (P3-M3 : Physics v1)

**Status**: ✅ **CLOSED (2026-07-19)** — W1→W4 livrés, double audit **PASS with concerns** (0 bloquant/majeur
code, findings appliqués), **verdict visuel humain PASS** (chute/collision/repos/ombres corrects ; l'humain note
que les casques **glissent au lieu de rouler** — attendu : v1 **linéaire seulement**, pas de dynamique angulaire ;
`StepPhysics` n'écrit que `WorldPosition`, l'orientation reste la pose bakée → rotation/inertie/friction au
[backlog §4](../docs/BACKLOG.md)). Physics-first (décision humaine ; GPU-driven = prochain jalon). Ouvert avec le
**fix Sandbox** appliqué + verdict humain PASS (rig coupé en multi-instances, `ShadowDistance` plafonné 50 m ;
capture mono-modèle bit-identique `9790D95D`). **Commits : `fix(sandbox)` + `feat(p3m3)` séparés (sur demande humaine).**

**But** : un **bac à sable de corps rigides déterministe** — gravité, chute, rebond sur le sol,
empilement (W2), à l'échelle, camera-relative correct, 0 alloc/frame, **reproductible run-à-run**,
NativeAOT. Fait **mordre `ShadowFit.UpstreamExtent`** (casters mobiles).
**Spec** : [docs/plans/2026-07-19-p3m3-physics-design.md](../docs/plans/2026-07-19-p3m3-physics-design.md)
**Baseline de rendu** (non-physique, inchangée) : `9790D95D` · **Sessions passées** : S1–S15 → `archive/`.

## Décisions verrouillées (détail : spec § Journal des décisions)

- **Fixed dt, 1 substep/tick** (pas d'accumulateur wall-clock) — seul moyen de garder les captures
  reproductibles (déterminisme « by frame count », cf. `BenchSpinSystem`). Interpolation → backlog.
- **Linéaire seulement** (pas de rotation/inertie/friction) → milestone suivant.
- **Logique physique dans `GameWorld`** (composants `internal`, mirroir Propagate/Aggregate/Collect) ;
  wrapper `PhysicsSystem : ISystem` en `Stage.Simulation`, enregistré par l'app (opt-in). À challenger
  à l'audit `engine-architect`.
- **Corps = drawable créé AVEC `Velocity`+`RigidBody`** (jamais ajoutés après) → pas d'archetype move,
  captures non-physiques byte-identiques.
- **Ordre de résolution stable par `(GlobalId,GlobalId)`** (jamais l'ordre de chunk Arch).

## Critère de sortie

Tests verts (intégration, ground bounce, sphère-sphère, déterminisme d'ordre, 0-alloc, AOT smoke) ·
0 warning · headless 0 validation / 0 leak · **0 alloc/frame au banc** · **2 runs = même SHA** ·
NativeAOT PASS · double audit PASS · **verdict visuel humain** (une pluie qui tombe, rebondit,
s'empile, repose).

## Vagues

| # | Contenu | Gate |
|---|---|---|
| W1 | Composants `Velocity`/`RigidBody` + intégration (gravité, dt fixe) + `CollideGround` + `PhysicsSettings` + `SpawnBody` + `PhysicsSystem` (Simulation) + Sandbox `drop:N` + `AGAPANTHE_PHYSICS`. TDD. | ✅ 5 tests · 0 warning · headless `drop:200` 0 validation/0 leak · **2 runs = même SHA `82EE3B9F`** |
| W2 | Broadphase grille uniforme (0-alloc) + sphère-sphère + impulsions + correction positionnelle, ordre `(GlobalId)`. TDD. | ✅ 4 tests (push-apart, momentum, déterminisme pile, 0-alloc) · reproductible `19D1A629` · dispersion sans interpénétration |
| W3 | Banc `drop:1000` Release+AOT · 0 alloc/frame · eyeDistance loggé · reproductibilité · capture verdict | ✅ **AOT PASS** (probe 10 comp/12 iter + Sandbox) · **0 B/frame @1000 bodies** · Debug≡AOT `19D1A629` · 0 leak · eyeDistance 248 m stable (wedge borné tient sous mouvement) |
| W4 | Double audit (`csharp-lowlevel` + `engine-architect`) · findings · doc (`AVANCEMENT`/`BACKLOG`) · archive · verdict visuel humain | ✅ **double audit PASS with concerns** (0 bloquant/majeur code) · findings appliqués · **verdict visuel humain dû** |

**Résultats W1–W3** : 320 tests (+9 physique) · 0 warning · 0 validation · 0 leak · 0 alloc/frame (unit + banc AOT 1000 corps) ·
AOT PASS · captures reproductibles run-à-run ET Debug≡AOT. Note honnête : eyeDistance ne bouge pas (248 m) — le **wedge borné**
(P3-M2 D3, `ShadowCasterDistance` 50 m) garde `UpstreamExtent` sage même avec des casters qui bougent enfin vraiment ; le cull
d'ombre deux passes est **exercé sous mouvement réel** pour la première fois et le design tient (ombres correctes). Pas de heap
multi-couches : des sphères sur un sol plat infini s'étalent toujours en une couche (physique correcte ; conteneur → backlog).

**Dépendances** : W2 après W1 (collision-bodies après l'intégration) ; W3 après W2 (ou W1 si W2 coupée) ;
W4 en dernier. W1 et W2 touchent tous deux `GameWorld.cs` → **séquentiels**.

## Risques

- **F1** — Alloc cachée dans la broadphase (buckets par cellule) → arrays réutilisés sur le World, jamais
  ré-alloués par frame ; gate churn physique = 0 B.
- **F2** — Jitter au repos (rebonds infinis) → clamp des petits rebonds à l'arrêt (seuil de vitesse).
- **F3** — Déterminisme : ordre de résolution des paires DOIT être trié `(GlobalId)` — sinon 2 runs
  divergent dès la 1re frame multi-contact.
- **F4** — Tunneling à grande vitesse (pas de CCD en v1) → borner la scène (vitesses modestes) ; CCD =
  backlog. Documenté, pas corrigé.
- **F5** — Captures physiques ≠ baseline statique (attendu) → nouveau hash de référence + assert 2 runs.

## Dette restante à la clôture (prévision)

Verdict visuel P3-M1 toujours dû · Linux/macOS jamais validés (P3-M0, différé) · **GPU-driven render**
(backlog §1, prochain jalon après physique) · rotation/friction/CCD/accumulateur (backlog §4) · CSM/PCSS
(backlog §2).

## Log

- 2026-07-19: **Session 16 ouverte.** Fix Sandbox appliqué + verdict humain PASS. Fork tranché (humain) :
  **physique d'abord**, GPU-driven ensuite. Plan approuvé, spec écrite.
- 2026-07-19: **W1→W3 livrés** (intégration+sol, sphère-sphère+broadphase, banc AOT). 320 tests, 0 warning, 0 validation,
  0 leak, 0 alloc/frame @1000 corps AOT, Debug≡AOT reproductible `19D1A629`.
- 2026-07-19: **Double audit PASS with concerns** (aucun bloquant/majeur code). Findings appliqués : (code) pré-grow
  `_cellHead` → gate 0-alloc général ; (docs) spec §4 `SpawnBody` immédiat corrigé, plafond `GlobalId<2³²` cross-réf sur
  le composant + backlog, `SpawnBodyDeferred`/accumulateur/solver-quality/scatter-optim inscrits au backlog §4.
  **Reste : verdict visuel humain, puis clôture + commit (sur demande).**
