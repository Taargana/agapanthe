# Absolute-Human Board — Agapanthe Session 13 (P2-M4 : Frustum culling + montée en charge)

**Status**: CLOSED (2026-07-13) — P2-M4 PASSÉ, **PHASE 2 CLOSE**. Double audit signe la clôture (archi PASS ; bas niveau FAIL conditionnel levé par le correctif σ_max). Voir « CLÔTURE » en bas.
**But** : **des milliers d'entités qui bougent, cullées, à 10 000 km, sans trembler, en NativeAOT, 0 leak.** On ne dessine plus que ce que la caméra voit (frustum culling), et on le prouve à l'échelle. On tranche d'abord l'**origine quantifiée** (décision de l'humain — engage la Phase 3), puis on pose les fondations GPU-free (Frustum, sphère locale), on inverse l'ordre de frame, on culle, on trie (radix), on charge.
**Créé**: 2026-07-13
**Spec**: docs/plans/2026-07-12-phase2-foundations-design.md §3.5 (systèmes render-list), §3.4 (composants), §6.2 (critère M4). Conception détaillée : passage engine-architect S13 (résumé ci-dessous).
**Board persistence**: git-tracked
**Sessions passées**: S1-S12 → .absolute-human/archive/ (S12 = board-session12-P2M3.md).

## Intake (conception engine-architect — actée)

### L'origine quantifiée (le sujet structurant, décision humaine)

- **Le snap vit dans `RenderView` (via `Camera.CreateView`)** — déjà le point unique de l'origine de frame. Trois champs changent de sémantique, **zéro nouvelle plomberie** (le hook `EyeRelative` a été posé exprès en M3) :

| Champ | M3 (origine = œil) | M4 (origine quantifiée) |
|---|---|---|
| `Origin` | `eye` exact | `Snap(eye, cell)` = `floor(eye/cell)·cell` par axe, en `double` |
| `View` | rotation seule | rotation **+ translation `−(eye − Origin)`** (l'œil n'est plus à 0) |
| `EyeRelative` | `Vector3.Zero` | `(eye − Origin)` narrow — borné à une cellule |

- **Maille = 1024 m FIXE**, jamais dérivée de la scène (sinon l'origine sauterait au streaming). Retunable, **non irréversible**. Borne les coords float à `≈ far + cell` → ULP sub-mm. **D1.**
- Lumières et `ShadowFit` **ne changent pas** : ils narrow déjà contre `view.Origin`, ils héritent de l'origine snappée gratuitement (dividende de l'agrégat M3).
- **⚠️ Coût assumé (D2)** : quantifier réintroduit un œil ≠ 0 → **défait la simplification de M3**. `mesh.frag` revient à `V = normalize(camera.Position − worldPos)` et `CameraUniforms.Position` redevient une valeur vivante (`= EyeRelative`), plus zéro. Erreur ici = spéculaire/Fresnel décalé, **ne crashe pas** → gate par capture.
- **Payoff Phase 3** : origine **discrète et stable inter-frame** (ne saute qu'au franchissement de cellule) → un buffer d'instances persistant devient viable (dirty-track les objets déplacés + rebase complet sur snap). C'est *pourquoi* l'humain a tranché quantifié. On **conçoit** pour, on ne **construit** pas le buffer en M4.

### ⚠️ L'invariant de précision CHANGE (D3 — à ne pas rater)

`objet − snap(eye) = (objet − eye) + (eye − snap(eye))`. Le 2e terme (offset sous-cellule de l'œil) **diffère selon la position absolue** → les float intermédiaires ne sont plus bit-identiques entre le run à l'origine et le run lointain. **Le résultat en espace vue reste exact** (la `View` porte `−(eye−Origin)` qui l'annule), mais l'arrondi intermédiaire non.

> **Reformulation (mesurée en W1)** : « loin == origine » est **bit-exact SSI le déplacement est un multiple entier de la maille** (offset sous-cellule identique). Pour un déplacement quelconque : **visuellement indiscernable**, gros de la distribution à 1 LSB ; le résidu est confiné au **spéculaire**, où le miroir amplifie un offset d'œil lui-même ≤ 1 ULP à la magnitude des coordonnées. Le « ≤ 1 LSB par canal » du plan initial est **faux pris à la lettre** sur une scène chrome — c'est une propriété de rendu, pas une faute de précision.

→ Le test de régression M3 **garde ses dents** en plaçant la caméra lointaine à un **multiple de la maille**. En W1 : correctif skybox (rayon reconstruit depuis la rotation de vue seule, jamais `point_monde − œil`) → le fond redevient origin-exact ; l'écart non-aligné tombe de 31 % à 0,9 % des canaux, résidu 100 % sur le casque chrome.

### Culling : linéaire, pas de structure spatiale (D4)

- **Cull linéaire sur les chunks Arch, 6 dots/entité.** À 10k mobiles : ~60k dots ≈ **< 0,1 ms**, cache-friendly, 0 alloc. Une grille/BVH devrait **se refit chaque frame** (tout bouge) → plus cher que l'O(N). Le point de bascule pour des entités mobiles est vers **~100k–1M**, pas 10k.
- Le cull linéaire est le **test-feuille** que toute large-phase future réutilise. La grille arrivera avec le streaming (keyée sur la même maille — jolie symétrie), pas maintenant.

### Ordre de frame inversé

`ShadowFit` doit tourner **avant** `CollectRenderLists` (les casters se cullent contre le **volume de lumière**, pas le frustum caméra). Orchestration **dans Program** (voit World + Rendering ; World reçoit un `Frustum` **Core**, ne référence pas Rendering) :
```
view = camera.CreateView()                     // origine snappée
(move entities)
bounds = world.AggregateBounds()               // O(n), zéro-alloc, PRÉCÈDE ShadowFit
lightVP = ShadowFit.ComputeLightViewProj(view, bounds, …)
camFrustum   = Frustum.FromViewProjection(view.View * view.Projection)
lightFrustum = Frustum.FromViewProjection(lightVP)
world.CollectRenderLists(render, shadowCasters, in view, in camFrustum, in lightFrustum)
renderer.DrawScene(render, shadowCasters, registry, in view, lightVP, …)
```

## Critère de sortie (spec §6.2 — critère de sortie de PHASE)

- **N ≥ 10 000** entités instanciées, **≥ 2000 visibles** après cull ;
- **0 alloc/frame** (test GC delta entre frame 2 et K) ;
- cull+collect **< 1 ms** à 10k sur RTX 5070 Ti (indicatif, pas gate dur) ;
- **0 message de validation, 0 leak** ;
- tourne en **NativeAOT** ;
- capture à 10 000 km : **bit-exacte si décalage = multiple de la maille**, **≤ 1 LSB sinon** (D3) ; « sans trembler » prouvé par l'égalité des transforms relatifs ;
- **test anti-popping** : un caster hors frustum caméra dont l'ombre entre dans le champ est conservé.

## Vagues

### P2-M4-W0 — Fondations GPU-free (parallélisables) [code+test, L]
- **(a) `Frustum` dans Core** : 6 plans, `FromViewProjection(Matrix4x4)` (Gribb-Hartmann) + `Intersects(center, radius)` (6 dots). **Un type, deux usages** (frustum caméra + volume lumière). GPU-free → réutilisable par un interest-management serveur.
- **(b) `Bounds` → sphère locale** : `Vector3 Center` + `float Radius` bakée à l'import (16 o vs 48). Par frame : `worldCenter = Center·WorldTransform + WorldPosition` (Double3), `worldRadius = Radius × maxScale`. `AggregateBounds` dérive l'AABB du fold des sphères. `ImportedEntitySpec` porte une AABB **locale**.
- **(c) Banc scène large** : `AGAPANTHE_SCENE=grid:NxN` instancie un mesh via handles globaux. **Tâche 1** (condition de clôture M3).
- **Gate** : build vert, tests `Frustum` (in/out/straddle) + sphère, scène large en passthrough (pas encore de cull), 0 validation/leak, capture sanity.

### P2-M4-W1 — Ordre de frame + origine quantifiée [code+test, M] [dep W0a]
- Hisser `ShadowFit` hors de `DrawScene` (ordre ci-dessus). Snap dans `RenderView`/`Camera`. `mesh.frag` + `CameraUniforms.Position` → `EyeRelative` (D2).
- **Gate** : capture à l'origine **≤ 1 LSB vs baseline M3** (PAS bit-identique — D3, assumé) ; test précision **reframé** (aligné maille = bit-exact ; arbitraire = ≤ 1 LSB) ; culling encore passthrough ; 0 leak/validation/alloc.

### P2-M4-W2 — Boucle de culling [code+test, M] [dep W0, W1]
- Cull frustum caméra → `RenderList` ; cull **volume de lumière** (frustum ortho de `ShadowFit` étendu upstream) → `ShadowCasterList`.
- **Gate** : tests in/out/straddle ; **≥ 2000 visibles/10k** ; **test anti-popping** ; capture ; 0 alloc.

### P2-M4-W3 — Tri radix + SortKey réelle [code+test, M] [dep W2]
- `SortKey` : matériau/pipeline/profondeur (bits hauts) + **tie-break stable `GlobalId`/`RenderOrder` (bits bas)**. ⚠️ *le* piège des audits : sans tie-break DANS la clé, les ex æquo suivent l'itération Arch non déterministe → déterminisme perdu **silencieusement** (un tri stable ne suffit pas). **Radix LSD 64-bit**, scratch réutilisé, remplace l'insertion sort O(n²).
- **Gate** : test déterminisme (ex æquo stables), zéro-alloc (scratch ne réalloue pas), capture inchangée.

### P2-M4-W4 — Montée en charge + preuve + audits + archive [test, L] [dep W3]
- Banc N=10k, mouvement déterministe (seedé par index), à 10 000 km. Mesures du critère de sortie. `Stopwatch` alloc-free autour de cull+collect sous `AGAPANTHE_CULL_STATS=1` (diagnostic). Double audit (`csharp-lowlevel` + `engine-architect`). Archive → board-session13-P2M4.md. **Clôture de la Phase 2.**

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, **AOT-pur**, **aucun Vk* hors Graphics**, **aucun type Arch hors World** (Frustum vit dans Core), IDisposable + DeletionQueue N+2, **zéro alloc/frame**, ResourceTracker (leak = échec), 0 message de validation.
- Baseline M3 (capture origine) : la capture de `fc1d876` fait foi ; le critère est **≤ 1 LSB** (le byte-identique strict n'est plus le critère depuis M3).
- Publish AOT : PATH doit inclure `C:\Program Files (x86)\Microsoft Visual Studio\Installer` (vswhere).
- Env vars debug : `AGAPANTHE_WORLD_ORIGIN` · `AGAPANTHE_UNLOAD_TEST` · (nouvelles M4) `AGAPANTHE_SCENE=grid:NxN` · `AGAPANTHE_CULL_STATS=1`.

## Décisions à confirmer avant W0 (pour feu vert)

- **D1** : maille = **1024 m** fixe. (Retunable, non irréversible.)
- **D2** : `RenderView.View` porte la translation sous-cellule ; `mesh.frag` + `CameraUniforms.Position` reviennent à `EyeRelative`. (Défait la simplif M3 — assumé.)
- **D3** : invariant reformulé — **bit-exact ssi déplacement aligné maille, ≤ 1 LSB sinon**. Le test M3 déplace la caméra lointaine à un multiple de la maille.
- **D4** : cull **linéaire**, pas de structure spatiale (bascule vers ~100k mobiles, pas 10k).
- **D5** : banc **à plat** (drawables importés, mouvement par écriture directe `WorldPosition`/`WorldTransform`) → court-circuite `PropagateTransforms` → la réécriture de la propagation O(n·d) **reste déférée** (pas sur le chemin critique M4).

## Hors périmètre M4 (garder le jalon fini)

Culling GPU-driven (→ P3, buffer persistant) · CSM cascades (une cascade suffit) · multi-thread/job system (contrat mono-thread gardé) · LOD · buffer d'instances persistant (on **conçoit** pour, on ne construit pas) · occlusion culling · réécriture propagation profondeur-ordonnée (déférée, D5). Chacun est **ordonnancé, pas oublié** — rien dans M4 ne le condamne.

## Portes Phase 3 ouvertes par M4 (à surveiller)

Origine discrète → buffer persistant + GPU-cull + îlots physiques · maille = sous-multiple de la future cellule de streaming (noter, pas coupler) · `GlobalId` dans le tie-break → ordre de draw **réplicable réseau** · sphère locale → réutilisable large-phase physique + LOD · `Frustum` Core GPU-free → interest-management serveur.

## Rollback Point

`fc1d876` (durcissements M3) / `fad6c06` (clôture docs M3) — dernier état vert : 257 tests, 0 warning, 0 validation, 0 leak, AOT PASS.

## Dette (rappel, à traiter en M4 ou léguée)

- 🔴 (M4) Scène large · `Bounds` sphère locale · ShadowFit hissé · SortKey+radix avec tie-break → **ce sont les vagues W0-W3**.
- 🟠 `AggregateBounds` plié une fois au chargement → dès que le monde bouge, dirty-flag (sinon reduce O(n)/frame gaspillé dans un grand monde).
- 🟡 (M5/P3) `AssertOwnerThread` `[Conditional("DEBUG")]` → non vérifié en Release (config du futur job system) · `_models` registry ne recycle pas ses ids · `FitSceneSphere` demi-diagonale (sur-dimensionne sur scène plate) · **crash shutdown reproductible** (`AGAPANTHE_UNLOAD_TEST=20`, ~2/10, `Silk.NET.Input`→GLFW) · **AOT/SPIR-V hors-ligne prouvés Windows only** → Linux/macOS.

## Vérifs humaines dues (non bloquantes)

- **P2-M3** : feel caméra en fenêtre (wrap du yaw) + ombre (jugée headless seulement). Démo : `AGAPANTHE_WORLD_ORIGIN="10000000,10000000,10000000"` → indiscernable de l'origine.
- **P2-M1** : hot reload Debug live (edit shader → recompile < 1 s), non re-testé depuis M8.

## Log

- 2026-07-13: **Session 13 ouverte — P2-M4 (frustum culling + charge = critère de sortie Phase 2).** Passage engine-architect fait. Humain a tranché **origine quantifiée**. Ordre : W0 fondations GPU-free (Frustum Core + sphère locale + banc) → W1 ordre de frame + snap → W2 culling → W3 radix → W4 charge/preuve/audits/clôture. 5 décisions (D1-D5) en attente de feu vert avant W0.

---

## CLÔTURE — P2-M4 PASSÉ · PHASE 2 CLOSE (2026-07-13)

**Status**: CLOSED. Double audit signe la clôture de la phase : `engine-architect` PASS sans réserve de correction ; `csharp-lowlevel` FAIL conditionnel **levé** (le seul bloquant, M1, corrigé — voir plus bas).

**Critère de sortie §6.2 — tenu, chaque gate vérifié** :

| Gate | Cible | Mesuré |
|---|---|---|
| Montée en charge | milliers, 1 upload | **10 000 / 1 upload** |
| Culling effectif | ≪ tout visible | **2556 / 10 000** (cull caméra serré) |
| Zéro-alloc hot path | 0 B/frame | **0 B** (animation incluse) |
| Précision grande échelle | stable à 10 000 km | **bit-identique** (caméra + entités en mouvement, maille alignée) |
| NativeAOT | tourne | **oui** (publish + run headless, shutdown propre) |
| Validation / leak | 0 / 0 | **0 / 0** |

**Commits** : `12a07e3` (W0 Frustum+sphère+banc) · `7d9428a` (W1 origine quantifiée + ordre de frame + skybox origin-exact) · `c5b7da7` (W2 culling) · `458e017` (W3 radix + SortKey) · `99076c1` (W4 banc + AnimateDrawables) · `2827777` (durcissements audits : σ_max exact).

**Ce que les audits ont trouvé (corrigé dans `2827777`)** :
- 🔴 **M1 — faux négatif de culling** : `MaxAxisScale` (norme de ligne) sous-couvrait le rayon monde sous shear (rotation × scale non-uniforme, ex. glTF hiérarchique) → objet visible droppable au bord. Remplacé par `MathHelpers.MaxStretch` = **σ_max exacte** (plus grande valeur propre de MᵀM). Frobenius écarté (sur-couvre √3 même pour une rotation → aurait changé toutes les captures) ; σ_max est tight → casque bit-identique à W3. 3 tests de régression.
- 🟠 Med1 : mesure d'alloc du banc bracketait mal `AnimateDrawables` (corrigé + couvert par le test zéro-alloc). Min1 : commentaire « œil à zéro » périmé. Min2 : contrat animator (divergence silencieuse en Release). 

**Écart au plan, assumé** : cull+collect **3,7 ms JIT-Release / ~6 ms AOT** à 10k, > cible **indicative** de 1 ms. Cause comprise et localisée (~80 % = liste d'ombres à 10 000 casters). Pas rédhibitoire (dette perf, pas correction). Le cull **caméra** est serré ; le cull du **volume de lumière** est conservateur sur une scène plate au sol (safe, jamais de faux négatif — direction imposée par l'audit M3 via UpstreamExtent).

**Métriques finales** : 275 tests · 0 warning · 0 message de validation · 0 leak · probe NativeAOT PASS.

## Dette léguée à la Phase 3 (issue des deux audits, par « quand ça mord »)

**🔴 Avant tout monde dynamique (donc avant la physique)** :
- **`AggregateBounds` plié une fois → périmé dès qu'une entité TRANSLATE.** Le banc survit « par chance de géométrie » (le spin ne déplace pas les centres). Une translation (mouvement, physique, streaming) rend `sceneBounds` faux → `FitSceneSphere`/`UpstreamExtent` faux → **clipping d'ombre** (celui même que M3 a corrigé) ou cadrage faux. Doit devenir per-frame ou dirty-tracké.
- **Cull du volume de lumière conservateur** → resserrer en cullant les casters contre le **frustum caméra extrudé le long de −lightDir** (l'archi : « le geste qui rembourse le plus de dette pour le moins de risque », aucun faux négatif). CSM = vrai correctif, plus tard.

**🔴 Dette de preuve** :
- **Linux/macOS jamais validés** — NativeAOT + SPIR-V hors-ligne **prouvés Windows uniquement**. « Fondations cross-platform » ne peut pas être affirmé tant qu'un vrai Linux n'a pas tourné. **Premier item P3** selon l'archi.

**🟠 À l'échelle / à l'arrivée d'une feature** :
- `SortKey` n'encode **pas la profondeur** : pas de front-to-back opaque (overdraw), et la transparence sera **fausse** dès qu'elle arrivera (arbitrage bits matériau/profondeur à trancher).
- Déterminisme du tri exige `(matériau, RenderOrder)` **globalement unique** (Min3) — tenu par le banc, à surveiller si un `GlobalId` non unique alimente la clé.
- Propagation O(n·d) déférée (D5) — mord avec des hiérarchies profondes à l'échelle (skinning).
- Pas d'API de **destruction d'entités** (`Despawn`) + `Parent` pendant — nécessaire au gameplay runtime, alloue côté Arch.

**🟡 À surveiller** :
- `AssertOwnerThread` Debug-only vs futur job system · crash shutdown Silk.NET reproductible (`AGAPANTHE_UNLOAD_TEST=20`, ~2/10, après le rapport propre) · pas d'assertion CI du critère de sortie (compteurs cull / 0-alloc / byte-identique vérifiés à la main).

## Phase 3 — séquencement recommandé (engine-architect)

**Linux d'abord** (P3-M0 : validation Linux/macOS + durcissement gate shutdown — « une fondation non validée sur ses cibles est une hypothèse, pas une fondation »), puis **buffer d'instances persistant + les 2 dettes de culling** (P3-M1 — paiement direct de l'origine quantifiée, rendu pur), puis **lifecycle d'entités + scheduler de systèmes** (P3-M2), puis **physique** (P3-M3 — dépend de l'origine quantifiée ✓ + lifecycle + bounds per-frame), **sérialisation source-gen** (P3-M4, parallélisable), **audio** en dernier/opportuniste.

## Bilan Phase 2

Les « fondations qui ne se retrofitent pas » sont posées et solides (vérif mécanique des frontières) : ECS Arch confiné + rooté AOT · **Double3 + camera-relative + origine quantifiée** (le joyau : débloque le buffer d'instances ET la stabilité physique — bon pari forward-looking, prouvé bit-identique à 10 000 km avec caméra ET entités en mouvement) · couture render-list sans types GPU (handles générationnels, registry slot-map) · culling linéaire honnête. Les manques (scheduler, lifecycle, bounds per-frame) sont des **couches à poser dessus**, pas des retrofits — le résultat attendu d'une phase « fondations ».

## Log (suite)

- 2026-07-13: **P2-M4 CLOS — PHASE 2 CLOSE.** W0→W4 + durcissements. Critère de sortie tenu (10k / 2556 visibles / 0 alloc / bit-identique 10 000 km / AOT). Double audit signe. Correctif M1 (σ_max exact) lève le seul bloquant. Vérif visuelle humaine du banc `grid:100x100` + skybox W1 **encore due**.
