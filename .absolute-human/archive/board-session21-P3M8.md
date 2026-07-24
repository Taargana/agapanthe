# Absolute-Work Board — Agapanthe Session 21 (P3-M8 : premier pas planétaire)

**Status**: ✅ **CLOS.** Wave 1+2+3 done, **double audit PASS** (`csharp-lowlevel` + `graphics-3d`, 0 🔴/🟠), findings appliqués,
verdict visuel humain PASS (casque/grille rebaselinés + scène planète). 333 tests, 0 warning/validation/leak, AOT PASS, GPU==CPU MATCH.
**Sessions passées** : S1–S20 → `archive/` (S20 = P3-M7 device-local + raster ombre, clos). Ce board → `archive/board-session21-P3M8.md`.

**But** : seconde scène de référence — planète + Soleil à l'échelle (§4bis pas 1). Sphère nue + **reversed-Z**
(fix depth range) + **jour/nuit analytique** (le Soleil est la seule lumière). LOD/atmosphère/orbites = hors scope.
**Spec** : [../docs/plans/2026-07-23-p3m8-planetary-first-step-design.md](../docs/plans/2026-07-23-p3m8-planetary-first-step-design.md)

## Décisions verrouillées (spec §2)

- Depth = **reversed-Z + D32 float**, frustum unique ; **global caméra** (rebaseline `4848F93F` assumé) ; comparateur
  **par pipeline** → passe d'ombre découplée (garde `LessOrEqual`).
- Scène = planète proche + **Soleil sphère à ~7,48e10 m** (near+far simultané = stress depth). **Échelle 1/2 UNIFORME** (tailles ET distances,
  décision humaine S21 — remplace l'ancien « tailles 1/2, distances 1/10 » ; 1/2 uniforme garde la taille angulaire réelle du Soleil ~0,53°).
- **Réutiliser le PBR, 0 nouveau shader.** Soleil = **sphère de plasma = seule lumière** : **point light co-localisée avec l'entité Soleil**
  (inverse-carré, la lumière part physiquement de la sphère ; directionnelle à intensité 0, gardée seulement pour ShadowFit) · **env noir**
  (ambient 0 + espace noir) · planète albedo (jour/nuit gratuit) · Soleil emissive · pas de shadow map (CSM no-op à 3e6 m). Fond noir pur.

## Critère de sortie

Casque + grille rebaselinés (verdict visuel + audit diff, pas de régression) · scène planète : jour/nuit net,
Soleil lointain sans z-fighting/clip, fond noir, précision double stable · 0 validation/leak · AOT PASS · GPU==CPU ·
double audit (`csharp-lowlevel` + `graphics-3d`) + **verdict visuel humain**.

## Tâches

### Wave 1 — Reversed-Z (fondation, global caméra, shadow pass découplée)
**AW-001** · code · **M** · deps: — · `done` ✅
`MathHelpers.PerspectiveVulkanReversed` (z→w−z : `M33=−1−M33 ; M43=−M43`) + 3 tests unit (near→1, far→0, monotone, x/y intact). Câblage `Camera` → AW-002.

**AW-002** · code · **M** · deps: 001 · `done` ✅
Comparateur depth **par pipeline** (`GraphicsPipelineDesc.DepthCompare`, défaut `LessOrEqual` back-compat) ; fin du hardcode. Scene+Skybox → `GreaterOrEqual`,
Shadow → `LessOrEqual`. Clear caméra `1f→0f` (shadow map reste `1f`). Skybox `gl_Position.z=0`. `Camera.ProjectionMatrix` → reversed.
**Gate PASS** (headless, moi) : casque+grille rendent nickel, GPU==CPU **MATCH** (401), CSM inchangé, 0 validation/leak, **AOT PASS**. `ShadowFit`/`Frustum` prouvés insensibles (reconstruits depuis scalaires / label-swap). Verdict visuel humain dû.

### Wave 2 — Sphère + scène planétaire
**AW-003** · code · **S** · deps: — · `done` ✅
`Primitives.UvSphere(segments, rings)` : sphère unité, normales = position, tangentes longitude, winding CCW, `ushort`. 5 tests unit (sommets unitaires, normales, indices, winding, garde-fous).

**AW-004** · code · **M** · deps: 002, 003 · `done` ✅
Scène `AGAPANTHE_SCENE=planet` : planète (3 186 km @ origine, albedo bleu-vert) + Soleil (348 170 km @ **7,48e10 m**, emissive) en `Double3`, **échelle 1/2 uniforme** (réel÷2), `SetupPlanetScene`/`BuildSphereModel`.
Env noir (`BuildBlackEnvironment` → IBL 0 + skybox noir), **point light co-localisée avec la sphère-Soleil** (inverse-carré, `I = irradiance·d²` ; directionnelle à 0 pour ShadowFit), ambient 0. `FramePlanetCamera` : croissant backlit (β off-axis + FOV large,
seule config gardant le Soleil en frame), `Near/Far` reversed-Z (1e2 → 2,1e10) dans **un** frustum. `MoveSpeed` mis à l'échelle. Env vars : `AGAPANTHE_PLANET_{RADIUS,PHASE,ALT,FOV}`, `AGAPANTHE_SUN_{DIR,RADIUS,DISTANCE}`.
**Gate PASS** (moi) : jour/nuit net + terminateur lisse + nuit noire + Soleil disque lointain **sans z-fighting/clip**, fond noir, GPU==CPU **MATCH** (2), 0 validation/leak, **AOT PASS**.

### Wave 3 — tail (verdict humain + audits) · `done` ✅
**AW-005/006/007/008** : full verif (333 tests, 0 warning/validation/leak, AOT PASS, GPU==CPU MATCH sur casque/grille/planète) · verdict visuel humain PASS · **double audit PASS**.

**Double audit (session 21)** — les deux **PASS**, 0 🔴/🟠 :
- `csharp-lowlevel` **PASS** : 0 alloc/frame préservé, chemins AOT-purs, ressources GPU possédées/libérées, précision point light saine (I≈2e22 sans overflow, 1/d²≈1.8e-22 sans underflow, `Range=0` désactive le cutoff, Soleil émissif non double-compté). 2 🟡 → **appliqués**.
- `graphics-3d` **PASS** : matrice reversed-Z exacte (`col3←col4−col3`), comparateur par pipeline correct, **culling invariant** (labels near/far échangés mais volume identique, AND symétrique → 0 faux-négatif), CSM confirmé insensible, skybox/point light/z-fighting sains. 1 🟡 (commentaires d'échelle périmés) → **appliqué** ; 1 🟡 (garde normalisation Frustum `1e-8` pour near sub-mètre) → **noté, aucune action** (conservateur + pré-existant, hors régime de la scène).

**Findings appliqués** : garde overflow `ushort` dans `UvSphere` (+ test) · fallback vecteur nul dans `EnvVector3` (log warn) · commentaires d'échelle `7,48e10`/`1/2 uniforme`/point light corrigés (`Program.cs`).

## Rollback Point
Avant que Wave 1 touche un fichier : commit `cafa3d5` (arbre propre).

## Clôture (CONVERGE)
Double audit findings appliqués · verdict visuel humain (rebaseline casque + scène planète) · maj AVANCEMENT/BACKLOG ·
board archivé `archive/board-session21-P3M8.md` · commit sur demande.
