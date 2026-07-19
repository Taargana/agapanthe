# Absolute-Human Board — Agapanthe Session 18 (P3-M5 : CSM — Cascaded Shadow Maps)

**Status**: ✅ **CLOS (2026-07-19)** — W1→W3 livrés, **double audit PASS with concerns** (`csharp-lowlevel` +
`graphics-3d`), **findings majeurs appliqués**, **verdict visuel humain PASS**. Correctif du plafond cascade unique
constaté à la clôture P3-M4 (empreinte rectangulaire + acné sur sol plat). [backlog §2].
**334 tests** · 0 warning · 0 validation · 0 leak · 0 alloc/frame @10k AOT · NativeAOT PASS.

**But** : remplacer la carte d'ombre unique par **4 cascades** — découper le frustum caméra en tranches de
profondeur, une carte texel-snappée par tranche → résolution quasi-constante du pied à l'horizon (net près
ET loin, plus de texels grossiers / acné sur grand receveur plat).
**Spec** : [docs/plans/2026-07-19-p3m5-csm-design.md](../docs/plans/2026-07-19-p3m5-csm-design.md)
**Baseline** : PAS de bit-identique (le CSM change l'ombre exprès) → **nouveau protocole visuel**.
**Sessions passées** : S1–S17 → `archive/` (S17 = P3-M4 GPU-driven, clos).

## Décisions verrouillées (détail : spec § Journal des décisions)

- **Stockage = atlas 2×2** de la carte 4096² existante (2048²/cascade). 1 `BeginRendering`, clear une fois,
  4 draws viewport-scopés. Garde `sampler2D` + PCF manuel, zéro render-target par-layer, zéro risque array
  MoltenVK.
- **Immutable samplers = sans objet** : le moteur fait déjà du PCF manuel exprès pour MoltenVK. CSM le garde.
- **Cull casters = SIMPLE par cascade** (remplace le wedge two-pass P3-M2) : l'empreinte d'une cascade vient
  de sa tranche de frustum (caméra seule → **pas de circularité**), casters cullés par test de sphère contre
  le volume de chaque cascade. `_casterSpheres`/`CompactShadowCasters`/garde F7 **retirés**.
- **`NoShadowCast` inclus** : le sol reçoit mais ne projette pas → fits resserrés, plus d'auto-ombrage du sol.

## Critère de sortie

Tests verts (`ShadowFit.ComputeCascades`) · 0 warning · 0 validation · 0 leak · banc AOT 0 alloc/frame ·
mémoire inchangée (4×2048² = 1×4096²) · NativeAOT PASS · double audit PASS · **verdict visuel humain** (net
près+loin, pas de couture entre cascades, sol sans acné).

## Vagues

| # | Contenu | Gate |
|---|---|---|
| W1 | `CascadeSettings` + `ShadowFit.ComputeCascades` (split pratique λ≈0.5, `FitSliceSphere`/`QuantizeRadius`/`SnapToTexelGrid` réutilisés par tranche, setback amont fixe κ=4·r) + tests GPU-free. | ✅ **326 tests** (+5 : splits monotones+span, texels near<far, sphères couvrantes, caster amont dans la plage, snap stable) · 0 warning |
| W2 | Atlas : `RecordShadowPass` 4 viewports (`SetViewportScissorRect`, casters concaténés) · `LightsUniforms` 4 mat4 + `CascadeSplits` (448 o) · `mesh.frag` sélection par profondeur vue + fondu 10% + PCF clampé tuile + `DEBUG_CASCADE` · World `CollectShadowCasters` N listes (wedge two-pass **retiré**) · `NoShadowCast` (composant + registre + sol marqué) | ✅ **330 tests** · 0 warning · **0 validation** · **0 leak** · **zone rectangulaire + anneaux d'acné DISPARUS** (capture rasante) |
| W3 | Banc AOT `grid:100x100` 0 alloc/frame · protocole visuel (`docs/visual-checks/`) · double audit · docs · archive · verdict humain | ✅ **AOT PASS** · **0 alloc/frame @10k** (draws 2+4, 11,4 ms) · **verdict visuel humain PASS** ([protocole](../docs/visual-checks/2026-07-19-p3m5-csm.md)) · **double audit PASS with concerns, findings appliqués** · **334 tests** |

## Double audit (2026-07-19) — PASS with concerns, findings majeurs appliqués

`csharp-lowlevel` **PASS with concerns** (atlas vérifié correct de bout en bout : concaténation, offsets,
upload unique — « le bug que vous décrivez est réellement corrigé » ; 0 alloc confirmée ; aucun invariant orphelin
après le retrait du wedge). `graphics-3d` **PASS with concerns** (fit camera-only sain, atlas structurellement
étanche, chemin MoltenVK-safe ; **le bias slope-scaled est invariant par cascade** — surtout ne pas ajouter de bias
par cascade, ce serait casser une propriété gratuite).

**Findings majeurs — tous appliqués :**
- **M1 (low-level)** — `Cascades.Count ≠ 4` **silencieusement cassé** (l'orchestrateur codait 4 en dur → matrices
  nulles en queue, frustum dégénéré, coût ×2, fondu désactivé, sans le moindre message). → l'orchestrateur honore
  le count réel (spans slicés, splits paddés) ; garde `1..4` dans `DrawScene` (F7).
- **M2 (low-level)** — `LastEyeDistance` n'était **plus jamais assigné** : le log de capture affichait `0.000` et
  une `ShadowCasterDistance` sans effet. *Un outil de gate qui ment est pire que pas d'outil.* → remplacé par un
  log qui dit vrai (cascades, λ, portée effective, résolution de tuile).
- **M3 (low-level)** — la zone la plus risquée (calcul d'offset d'instances, qui **avait déjà produit un bug**)
  n'avait **aucun test**. → `ShadowBatchOffsetTests` (+4 tests) : runs par mesh, append + base de cascade,
  offsets pointant réellement dans le buffer concaténé, cascade vide.
- **MAJEUR-1 (graphics)** — **λ=0.5 gâchait la cascade 0** : splits réels 0-25/25-52/52-90/90-200 (et non ceux
  annoncés), soit 3,2 cm/texel au contact, étalés à ~16 cm par le PCF. → **λ=0.85** : cascade 0 à ~8 m et
  **1,0 cm/texel — ×3,2 de netteté au contact pour +2,5 % de texel en cascade 3**. Spec et assertion de test
  (`splits[0] < 50` → `< 12`) corrigées : l'ancienne bornait si mollement qu'elle passait à 25 m.
- **MAJEUR-2 (graphics)** — **bug introduit par le fondu** : il se déclenchait sur `view.Far`, donc en cadrage
  serré (casque seul, `far ≈ 17 m`) il effaçait les ombres dès 13,6 m alors qu'il n'y a **aucun horizon à masquer**
  (le far plane clippe déjà). → le seuil vient du CPU, qui sait si la portée est **choisie** (fondu) ou **subie**
  (pas de fondu) ; nouveau `ShadowParams.x` dans le UBO (448 → 464 o).
- Mineurs appliqués : `textureLod` (dérivées indéfinies en flot divergent), commentaires faux du shader remplacés
  par l'invariant réel.

**Findings laissés en dette (inscrits) :** cull par cascade quasi inopérant (volumes qui se recouvrent → ~4× les
casters rasterisés — c'est du GPU-driven shadow cull, backlog §1) · setback κ=4 proportionnel au rayon (un contenu
vertical très haut perdrait son ombre **dans la cascade proche seulement** — mode de défaillance vicieux, mais hors
d'atteinte pour du contenu de 2 m) · code mort laissé par le retrait du wedge (`ComputeLightViewProj`,
`ExtrudedShadowFrustum`, `ComputeFrustumSphere`, `ShadowCasterDistance` : ~200 lignes que plus rien n'exerce en
production) · docs XML obsolètes (F4/F5).

**Coût mesuré (assumé)** : banc AOT `grid:100x100` → **11,4 ms/frame** contre ~8 ms en P3-M4, soit **~3,4 ms** pour
le CSM (4 rendus d'ombre au lieu d'un, et 4× le cull des casters). Attendu et accepté : c'est le prix de la netteté
près ET loin. 0 alloc/frame, 0 leak, 0 validation, mémoire d'ombre inchangée (4×2048² = 1×4096²).

**Dépendances** : W2 après W1 (le rendu consomme les matrices de cascade). W2 touche `ShadowFit`/`Renderer`/
`mesh.frag`/`GameWorld` → séquencé en interne. W3 après W2.

## Risques

- **F1 — Bleed de tuile atlas** : le PCF 5×5 près d'un bord de tuile échantillonne la cascade voisine →
  **clamper l'UV du kernel dans la tuile** (ou gutter 1-2 texels). Correctness, pas optionnel.
- **F2 — Couture entre cascades** : saut visible à une frontière de split → **bande de fondu** (lerp cascade
  i/i+1 sur une marge).
- **F3 — Setback fixe** : un caster loin en amont d'une tranche pourrait être clippé (pas d'`UpstreamExtent`
  par-cascade en v1). Acceptable à 4 cascades ; sophistication → backlog. À surveiller au protocole visuel.
- **F4 — std140** : `LightViewProj[4]` (mat4[4]) + `CascadeSplits` — recaler l'offset (176→432→448) et le
  déclarer identique CPU↔`mesh.frag`.
- **F5 — Circularité fit/casters** : évitée par construction (empreinte = tranche de frustum, indépendante
  des casters) — ne PAS réintroduire une dépendance caster→fit.

## Dette restante à la clôture (prévision)

PCSS (backlog §2.1bis) · GPU shadow cull (backlog §1) · `UpstreamExtent` par-cascade (setback fixe en v1) ·
slots persistants (C) reportés de P3-M4 · moiré d'herbe (backlog §5) · Linux/macOS jamais validés (P3-M0).

## Calibration (constat humain post-W2)

**Le plafond `ShadowDistance = 50 m` de la session 16 bridait le CSM.** Ce cap avait été posé *parce qu'*une
cascade unique ne peut être nette ET longue portée (on sacrifiait la portée) — la justification même que le CSM
supprime. `Renderer.ComputeCascades` fait `MaxDistance = min(Cascades.MaxDistance, ShadowDistance)`, donc les 4
cascades se serraient dans 50 m et **rien n'était ombré au-delà** (constat humain : « la shadow map n'apparaît que
très près »). **Corrigé** : `FrameCamera` pose désormais `ShadowDistance = max(diagonal*4, Cascades.MaxDistance)`.
La portée d'ombre est maintenant pilotée par **`Renderer.Cascades.MaxDistance`** (200 m par défaut) — le seul
bouton à tourner. *Leçon : un workaround doit mourir avec la contrainte qui l'a justifié.*

## Log

- 2026-07-19: **Session 18 ouverte — P3-M5 CSM.** Forks tranchés (humain) : cull simple par cascade, fix
  sol non-caster inclus. Découvertes : PCF manuel déjà en place (immutable samplers sans objet) ; atlas 2×2 ;
  fit par-cascade décochonne la circularité P3-M2. Plan approuvé, spec écrite. W1 à suivre.
