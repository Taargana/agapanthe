# P3-M8 — Premier pas planétaire : sphère à l'échelle + reversed-Z + jour/nuit analytique

> Status: **draft** (2026-07-23, session 21). Backlog §4bis pas 1. Design instruit via absolute-brainstorm
> (5 décisions verrouillées). Fondations : `Double3` + camera-relative à origine quantifiée (`RenderView`/
> `Camera.CreateView`), prouvées jusqu'à 1e10–1e11 m ; le depth buffer est le seul vrai blocage.

## 1. But & scope

La **seconde scène de référence** (à côté de la grille de casques) : une planète et un Soleil à l'échelle, qui
met enfin à l'épreuve ce pour quoi les fondations `double`/camera-relative ont été bâties. Pas 1 uniquement.

**Échelle** : **1/2 de la réalité, UNIFORME** (tailles ET distances — décision humaine S21, remplace l'ancien « tailles 1/2,
distances 1/10 » de §4bis). Terre → rayon **3 185,5 km**, Soleil → **348 170 km**, 1 UA → **7,48e10 m**, tous dérivés du réel÷2.
Le facteur uniforme conserve la **taille angulaire réelle** du Soleil (~0,53° depuis la planète) — un facteur non-uniforme le
grossissait (×5 à 1/10). Configurable (constantes/env vars en tête de la scène Sandbox).

**In scope** :
- (W1) **Reversed-Z** — le fix du depth range, **global** au renderer (convention caméra), comparateur depth rendu
  **configurable par pipeline** pour découpler la passe d'ombre.
- (W2) **Générateur de sphère** (`Primitives.UvSphere`) + scène `AGAPANTHE_SCENE=planet` (planète proche + Soleil
  à 1,5e10 m, en `Double3`), **sun-only** (env noir, Soleil emissive, planète albedo).
- (W3) mesures + rebaseline documenté + audits + verdict visuel.

**Out of scope → backlog** : LOD sphérique / terrain (§5), atmosphère & terminateur avancé (§3), orbites
képlériennes (§4bis pas 2), éclipses analytiques, starfield. Multi-frustum (repli depth différé).

## 2. Décisions verrouillées (interview — ne pas re-litiger)

1. **Depth = reversed-Z + D32 float, frustum unique.** Multi-frustum différé ; depth-log rejeté.
2. **Reversed-Z GLOBAL** (convention caméra du renderer). **Rebaseline assumé** de `4848F93F` → équivalence par
   audit du diff + verdict visuel, pas par le hash.
3. **Scène = planète (proche) + Soleil (sphère à ~7,48e10 m) dans un frustum** (near+far simultané = stress depth).
   Échelle **1/2 uniforme** (voir §1) — le Soleil est un **vrai objet physique** (sphère-entité emissive), à sa taille angulaire réelle.
4. **Réutiliser le PBR, zéro nouveau shader. Le Soleil est une SPHÈRE de plasma et la SEULE lumière** — donc la
   lumière **part physiquement de la sphère** : **point light co-localisée avec l'entité Soleil** (inverse-carré,
   `I = irradiance·d²` ; à 7,48e10 m les rayons arrivent quasi-parallèles → même terminateur qu'une directionnelle,
   mais liée à la position du Soleil). La directionnelle reste à **intensité 0** (gardée uniquement pour ShadowFit
   qui lit sa direction). La surface du Soleil reste purement **emissive** (ses normales pointent dehors → la point
   light au centre ne l'éclaire pas). **Env noir** → ambient IBL 0 + espace noir · planète = albedo (jour/nuit
   gratuit) · pas de shadow map (CSM se no-op à 3e6 m).
5. **Fond noir pur** au pas 1.

## 3. W1 — Reversed-Z (fondation, global caméra, shadow pass découplé)

- **Matrice** : `MathHelpers.PerspectiveVulkanReversed(fov, aspect, near, far)` = le standard `PerspectiveVulkan`
  puis la transformation clip-space **`z → w − z`** : `M33 = −1 − M33 ; M43 = −M43` (M13/M23 déjà 0). Exacte quels
  que soient near/far (near→1, far→0). `Camera.ProjectionMatrix` l'utilise.
- **Comparateur par pipeline** : `GraphicsPipelineDesc.DepthCompare` (nouveau, enum `DepthCompare`), au lieu du
  `LessOrEqual` **codé en dur** (`GraphicsPipeline.cs:201`). Passes **caméra** (Scene, Skybox) → `GreaterOrEqual` ;
  passe **d'ombre** (Shadow) → `LessOrEqual` (inchangée : ortho standard, shadow map = espace depth séparé, sampler
  PCF `LessOrEqual` intact). **C'est ce qui découple reversed-Z du CSM** (point de vigilance de l'instruction).
- **Clear depth caméra** : `1f → 0f` (attachement depth de la scène, `Renderer.cs`). Shadow map garde `1f`.
- **Skybox** : dessiné au plan lointain = désormais **z = 0** (reversed-Z) avec `GreaterOrEqual`, no write → ne peint
  que les pixels vides (depth encore 0). Le shader skybox force `gl_Position.z = 0` (au lieu de `= w`). Vérifier
  `SkyboxPass` + son shader.
- **Rebaseline** : le hash mono/grille va (peut-être) changer. Nouveau hash de référence enregistré ; l'équivalence
  est prouvée par audit du diff (aucun z-test ne doit s'inverser à l'échelle métrique) + verdict visuel — **jamais**
  masquer une régression derrière le rebaseline.
- **Gate W1** : casque + grille rendent correctement (verdict visuel), 0 validation, 0 leak, GPU==CPU, AOT ; les
  ombres CSM **inchangées** (shadow pass non touchée).

## 4. W2 — Sphère + scène planétaire

- **`Primitives.UvSphere(segments, rings)`** : UV-sphere unité (rayon 1), normales analytiques (= position
  normalisée), tangentes le long de la longitude, winding CCW extérieur (comme `Cube`). Indices `ushort` (une
  tessellation raisonnable ~128×64 < 65 535 sommets ; la planète est petite à l'écran depuis l'orbite). Le modèle
  la met à l'échelle (rayon planète/Soleil) via son transform.
- **Scène `AGAPANTHE_SCENE=planet`** (Sandbox) : deux entités `Double3` — planète à l'origine monde (rayon 3 185,5 km),
  Soleil à `~7,48e10 m` (rayon 348 170 km). Caméra à quelques rayons planétaires au-dessus, `Far ≈ dist(planète,Soleil)·1,4`
  (pour voir le Soleil), `Near` petit (reversed-Z). `MoveSpeed` mis à l'échelle (sinon inatteignable). Facteurs tailles/distances
  = constantes configurables en tête.
- **Sun-only** : une **point light co-localisée avec l'entité Soleil** (`Position = sunOrigin`, `Intensity =
  irradiance·dist²`, `Range = 0`), 0 autre lumière, directionnelle à intensité 0. **Env noir**
  (`BuildBlackEnvironment` = cubemap 0) → ambient IBL 0 + skybox noir. Planète = matériau albedo (bleu-vert).
  Soleil = matériau **emissive** fort (auto-lumineux).
- **Gate W2** : la planète montre un **jour/nuit** net (terminateur lisse), côté nuit **noir**, le Soleil est un
  disque brillant lointain **sans z-fighting ni clipping** (la preuve reversed-Z), fond noir. Précision `double` :
  la planète reste stable quand la caméra bouge à grande coordonnée.

## 5. Testing / gates

- Unit (GPU-free) : `PerspectiveVulkanReversed` (near→1, far→0 ; un point à near projette z=1, à far z=0) ;
  `UvSphere` (sommets sur la sphère unité, normales unitaires = position, indices valides, winding).
- Rendu : verdict visuel (casque rebaseliné + scène planète) ; headless capture de la scène planète (nouveau hash
  de référence). 0 validation / 0 leak / AOT PASS / GPU==CPU / 0 alloc-frame (la scène est quasi-statique).
- **Rebaseline** : re-signer `4848F93F` (casque) → nouveau hash, documenté ; diff visuel audité.

## 6. Journal des décisions
- **Reversed-Z global mais comparateur par pipeline** — un seul chemin de depth caméra, tout en laissant la passe
  d'ombre sur sa convention (découple le CSM, évite de reversed-Z la shadow map qui n'en a pas besoin).
- **`z → w − z` pour la matrice** — dérivation exacte, indépendante de near/far, moins fragile qu'une reconstruction
  des termes M33/M43 à la main.
- **Finite reversed-Z (garde Camera.Near/Far)**, pas infinite-far — plus petit changement conceptuel ; la scène
  planète pose `Far ≈ 2e10`. Infinite reversed-Z = optimisation ultérieure possible.
- **Réutilisation totale du PBR + env noir** — « seul le Soleil éclaire » imposé mécaniquement (aucune autre source
  dans le cubemap), zéro nouveau shader ; le shader dédié planète n'apporterait rien au pas 1 (atmosphère = passe
  fullscreen séparée, plus tard).
- **UV-sphere ushort** — suffisant (planète petite à l'écran depuis l'orbite) ; icosphère/LOD = §5.
