# Backlog — Agapanthe

> **Ce fichier n'est pas un plan.** Il garde ce qu'on sait devoir faire un jour, avec *pourquoi* et *quand ça mord* —
> pour que la décision soit déjà instruite le jour où on ouvre le jalon. L'état réel du projet, les jalons en cours et
> la dette du dernier jalon vivent dans [AVANCEMENT.md](AVANCEMENT.md) ; les specs approuvées dans [plans/](plans/).
>
> Règle de tri : chaque item dit **ce qui casse sans lui** et **à quelle échelle il devient obligatoire**. Un item sans
> déclencheur clair est une idée, pas du backlog.

Dernière mise à jour : 2026-08-11 (session 25 — **UI-1 livré** : texte à l'écran, §4quater à jour) · 2026-08-03 (session 25 — **réorientation cap « vrai engine »**, §4quater créée) · 2026-07-26 (session 24 — **VS-3 livrée** : glu gameplay = défi d'atterrissage planétaire ; §4ter à jour) · 2026-07-26 (session 23 — **VS-2 livrée** : spawn runtime différé + gravité newtonienne ; §4ter à jour) · 2026-07-24 (session 21 — échelle planétaire 1/2 uniforme, **§4ter Vertical Slice** formalisée).

---

## 0. Dette immédiate (ouverte par le jalon courant)

Détail et justification : `AVANCEMENT.md` § P3-M1 et board de session 14.

- 🔴 **Validation Linux / macOS** (P3-M0). AOT et SPIR-V hors-ligne sont **prouvés Windows uniquement** ; le titre
  « fondations cross-platform » est une hypothèse tant qu'un vrai Linux n'a pas tourné. *Bloqué : pas de machine.*
- 🔴 **Scheduler de systèmes.** L'ordre de frame (`PropagateTransforms → AggregateBounds → ComputeLightViewProj →
  CollectRenderLists`) vit dans une closure du Sandbox. Toute autre application le recréera de travers. *Mord : dès
  la deuxième app, et dès la physique.*
- 🔴 **`ShadowFit.UpstreamExtent` dérivé des bounds globales.** Une entité qui bouge à 10 000 km fait vibrer la plage
  de profondeur de la shadow map de tout le monde. *Mord : dès la physique.* Correctif : le dériver de la liste de
  casters (désormais serrée par le wedge).
- 🟠 **`SortKey` sans profondeur** → toute transparence future sera fausse (pas de tri arrière-vers-avant).
- 🟠 **Plafond 16 bits** mesh/matériau dans la clé de tri : limite dure documentée, à faire échouer bruyamment au spawn
  plutôt qu'à dégrader le batching en silence.
- 🟡 **Crash au shutdown GLFW/Silk.NET** reproductible (`AGAPANTHE_UNLOAD_TEST=20`, ~2 runs/10, *après* le rapport
  propre) — upstream, gate CI keyé sur la ligne de rapport et non sur l'exit code.

## 1. Rendu GPU-driven

- ~~**Cull en compute + draw indirect**~~ ✅ **livré en P3-M4** (session 17) : `vkCmdDrawIndexedIndirect`, `BufferUsage.Indirect`,
  offset de batch en **push constant** (pas `firstInstance` → pas de dépendance `drawIndirectFirstInstance`, risque
  `baseInstance` MoltenVK neutralisé), `scene_cull.comp` (frustum-cull + compaction atomics), barrières compute→indirect/vertex.
  Gate : GPU visible == CPU (2557 @10k AOT), mono bit-identique. **Double audit PASS.** Cull d'ombre resté CPU two-pass (P3-M2).

- ~~🔴 **Slots persistants dirty-trackés (partie (C) reportée de P3-M4)**~~ ✅ **livré en P3-M6** (session 19,
  *double audit + verdict visuel en attente*). Buffer de candidats **persistant host-visible** (`PersistentInstanceBuffer`,
  F copies + miroir CPU autoritatif + sync-before-use §5) ; le gather + radix sort ne tournent qu'au **rebuild structurel**
  (spawn/despawn/edit mesh-matériau/re-snap d'origine), et une frame ordinaire ne patche que les slots **dirty** (O(dirty),
  marqués aux 3 surfaces de mutation du World : animation, physique, propagation). Le re-upload O(n) de ~960 Ko/frame est
  supprimé (scène statique → dirty vide → upload ≈ 0). Slot stable = index trié material-major ; **casse le jour où la
  profondeur entre dans `SortKey`** (§0) — condition de validité inscrite dans `InstanceSlot`. `RenderItem.WorldTransform`
  reste vivant (véhicule du gather au rebuild). *Reste : à 100 000 entités le rebuild structurel O(n log n) redevient le mur
  — mais il n'est payé qu'aux changements structurels, pas par frame.*

- ~~🟠 **Buffers GPU-produits en device-local.**~~ ✅ **livré en P3-M7** (session 20). Les **buffers d'instances**
  (scène + ombre) passent device-local sans staging (le compute écrit, le vertex lit — aucun accès host). Le **buffer
  de candidats persistant** garde un **staging host-visible = miroir** (P3-M6 §5) et copie les ranges dirty vers un
  **device-local** via un nouveau `CommandList.CopyBuffer` **async** intra-frame (core `vkCmdCopyBuffer`, pas
  `CmdCopyBuffer2` = KHR/1.3, risque MoltenVK). Les **args restent host-visible** (host-lus par `ReadBackSceneVisible` —
  choix assumé, taille négligeable). Gain mesuré (avec la réduction raster §2.0bis) : **~15,3 → ~8,0 ms @10k AOT**.
  *Reste : chemin device-local/transfer **non exécuté sur MoltenVK** (dette P3-M0) ; coalescing avancé des régions dirty
  déféré (fallback copie pleine si dirty > count/2 en place).*

- 🟠 **La compaction atomique est un SECOND verrou sur la transparence** (en plus du `SortKey` sans profondeur, §0) :
  l'`atomicAdd` **scramble l'ordre intra-batch**, donc trier les candidats arrière→avant ne suffira pas — le futur
  transparent devra re-trier les survivants (readback CPU, ou sort GPU par batch). Sans effet aujourd'hui (opaque z-testé).

- 🟡 **MultiDrawIndirect** (un seul `vkCmdDrawIndexedIndirect` pour tous les batches, `drawCount = N`) : aujourd'hui un
  draw + binds **par batch**, y compris les batches entièrement cullés (`instanceCount=0`, no-op qui gonfle `LastSceneDrawCalls`).
  Repousse `VK_KHR_draw_indirect_count` (hors core 1.2, incertain MoltenVK) tant que le nombre de batches reste connu CPU.
- ~~🟡 **Cull d'ombre en compute**~~ ✅ **livré en P3-M6** : le seam de culling est réunifié (scène ET ombre en compute,
  lisant le même buffer de candidats persistant). ~~Reste asymétrique côté **gate** (l'ombre n'a pas de readback)~~
  ✅ **soldé en P3-M7** : `ReadBackShadowVisible` somme l'`instanceCount` par région/cascade, symétrique à
  `ReadBackSceneVisible`.

## 2. Ombres à l'échelle

L'ordre ci-dessous suit ce que la scène impose, pas l'élégance.

### 2.1 CSM — Cascaded Shadow Maps *(le prochain vrai jalon d'ombre)*
Aujourd'hui : **une seule cascade**, 4096², cadrée sur `min(sphère de la scène, sphère du frustum tronqué par
`ShadowDistance`)`. Ça tient tant que la scène est petite ; dès qu'il y a un sol de plusieurs dizaines de mètres, les
texels grossissent et le bord d'ombre marche en escalier (constaté session 14).
- Découper le frustum en 3–4 tranches (typiquement 0–10 m / 10–40 m / 40–150 m / 150–500 m), une carte 2048² par
  tranche, chacune texel-snappée comme aujourd'hui. Coût constant (~64 Mo) **quelle que soit la taille du monde**.
- Sélection de cascade par profondeur en vue, avec fondu entre cascades (sinon couture visible).
- **Prérequis connu** : les *immutable samplers* (comparateur hardware) — déjà inscrit dans la dette héritée de la
  Phase 1. Attention MoltenVK : pas de comparateur mutable.
- *Mord : dès la première scène de gameplay avec un vrai terrain.*

### 2.1bis PCSS — pénombre à largeur variable
Le PCF actuel (5×5 pondéré) est une pénombre de **largeur fixe** : le seul réglage possible est « net mais cranté »
(noyau étroit) ou « lisse mais mou » (noyau large). On a choisi le second. **PCSS** (*percentage-closer soft shadows*)
sort du compromis : une première passe estime la distance entre le receveur et l'occulteur (*blocker search*), et la
largeur du filtre en découle — bord **net au contact** (sous l'objet), flou quand l'ombre est loin de ce qui la
projette. C'est ce que fait l'œil. À faire **avec ou après le CSM** (les deux partagent la sélection de cascade).
*Mord : dès qu'un objet posé au sol doit avoir un contact crédible.*

### 2.2 Ombres analytiques planétaires
Une planète éclairée par un soleil quasi ponctuel : la nuit, c'est `dot(N, L) < 0`. Le terminateur sort de la formule,
à toute échelle, sans une seule texture. Les **éclipses** (lune → planète) : intersection rayon/sphère et cône d'ombre,
formule fermée. **Ne jamais rasteriser une shadow map à l'échelle d'un système solaire** — mauvais outil.

### 2.0bis Dette léguée par P3-M5 (CSM) — double audit session 18

- 🟠 **Le cull par cascade est quasi inopérant** (audit graphics MINEUR-1) : les volumes ortho viennent de sphères
  englobantes de tranches, donc ils **se recouvrent massivement** — celui de la cascade 3 contient presque tout le
  champ proche (et déborde ~97 m *derrière* la caméra). Presque chaque caster entre dans les 4 listes →
  **~4× la géométrie rasterisée** dans la passe d'ombre, et 4× le buffer d'instances.
  - ✅ **Volet CPU soldé en P3-M6** (session 19) : le cull d'ombre est passé **en compute** (`shadow_cull.comp`,
    une passe par candidat × 4 cascades, compaction atomique par région (cascade, mesh-batch)). Le scan CPU O(n×4)
    et les **4 `RenderList` managées (~12 Mo)** disparaissent ; `CollectShadowCasters` est retiré.
  - ~~🟠 **Reste : le raster ~4×**~~ ✅ **soldé en P3-M7** (session 20) : un **7ᵉ plan de coupe near-side en
    profondeur-vue** par cascade fait **tuiler** les cascades au lieu de s'emboîter → chaque caster tombe dans
    ~1 cascade (**cascade 0 exemptée**, anti-popping P3-M6 préservé ; marge 25% de tranche > bande de fondu 10%).
    Mesuré : shadow-verify **total ≈ 1×/caster** (vs ~4× avant), part du gain **~11 → ~8 ms**. Double audit + verdict
    visuel humain PASS (incl. cas soleil bas). *Reste déféré : `UpstreamExtent` par cascade complet (ci-dessous) — la
    marge est calée sur l'épaisseur de tranche, pas la longueur d'ombre ; un soleil **très** rasant reste le cas limite.*
- 🟠 **Setback amont fixe κ=4·r** (spec, decision log) : la marge est **proportionnelle au rayon de la cascade**,
  donc la **cascade 0 est la plus exposée**. Un contenu vertical dépassant `4r₀` au-dessus de la tranche proche
  perd son ombre **dans la cascade proche seulement** et la garde au loin → l'ombre d'une tour **disparaît quand on
  s'en approche**. Mode de défaillance vicieux (incohérence sous déplacement caméra) mais hors d'atteinte pour du
  contenu de 2 m. *Mord : premier bâtiment/falaise/grue.* Correctif : `UpstreamExtent` par cascade.
- ~~🟡 **Code mort du wedge**~~ ✅ **retiré en P3-M6** : `ShadowFit.ComputeLightViewProj`, `ExtrudedShadowFrustum`
  (+ tests), `Renderer.ComputeFrustumSphere`/`ShadowCasterDistance`, `GameWorld.CollectShadowCasters` (+ tests),
  `Renderer.Batch`/`BuildShadowBatches` et les crefs/commentaires obsolètes — supprimés (~200 lignes).
- ~~🟡 **Empreinte mémoire des listes de casters** (~12 Mo)~~ ✅ **disparue en P3-M6** (le cull d'ombre GPU ne
  construit plus de `RenderList` de casters).
- 🟡 **`textureLod` / flot divergent** ✅ **soldé** (session 18) · **bias par cascade** : **NE PAS FAIRE** — l'audit
  graphique a montré que le bias slope-scaled est **invariant par cascade** (taille de texel et plage de profondeur
  varient toutes deux linéairement en `r` et se compensent). Ajouter un bias par cascade casserait cette propriété.

### 2.2bis Ombres LOINTAINES — au-delà de la portée du CSM *(question ouverte, instruite session 18)*

**Le constat** (humain, session 18, après le CSM) : un CSM a une **portée finie** (`Renderer.Cascades.MaxDistance`,
200 m par défaut). Au-delà, plus d'ombre. Monter la portée dilue la dernière cascade ; le problème se déplace, il ne
disparaît pas. Un **fondu sur les derniers 20 %** est en place (session 18) : il supprime l'« horizon d'ombre »
(la ligne franche au sol, qui *lisait* comme un bug) mais ne crée évidemment pas d'ombres lointaines.

**⚠️ Contrainte dure — pas de ray tracing matériel via MoltenVK.** macOS/Apple silicon a bien du RT (Metal 3,
matériel sur M3+), mais **MoltenVK n'expose pas** `VK_KHR_ray_query`/`ray_tracing_pipeline` : `VK_KHR_acceleration_structure`
n'est pas implémenté ([MoltenVK #1956](https://github.com/KhronosGroup/MoltenVK/issues/1956), [#1953](https://github.com/KhronosGroup/MoltenVK/issues/1953),
[#427](https://github.com/KhronosGroup/MoltenVK/issues/427)). Et ce n'est pas qu'un retard : Khronos note que
certaines exigences du RT (**device addresses**) rendent l'implémentation *en couche* au-dessus d'une autre API
structurellement très difficile ([Ray Tracing in Vulkan](https://www.khronos.org/blog/ray-tracing-in-vulkan)).
Atteindre le RT de Metal imposerait un **backend Metal natif** → contredit la décision verrouillée « couche GPU
mince **mono-backend** » (CLAUDE.md). *À revérifier avant toute décision : cet état peut bouger.*

**Les trois options, toutes légitimes :**
1. 🟠 **Plus de cascades** (atlas 3×3, ou texture-array) — étend la portée **à netteté constante**. La suite naturelle
   du CSM, sans nouvelle extension. *Mord : dès qu'on veut > 200 m net.*
2. 🟡 **RT optionnel hors macOS** (`ray_query` détecté au runtime, fallback CSM). Reste dans Vulkan, ne casse pas le
   mono-backend — mais **deux chemins d'ombre** à maintenir et valider, et le Mac garde le problème.
3. 🟢 **Ray marching / analytique** (§2.2, §2.3) — **la réponse préférée**, cohérente avec le fil conducteur du
   projet (*« on ne photographie que ce qui a une surface et qui est proche »*). Marche partout, sans extension.
   *Prérequis : un vrai terrain* (le sol est un quad plat — §5), car c'est le relief qui porte l'occlusion lointaine.

**Note de cadrage** : à 300 m, un casque de 2 m fait quelques pixels — son ombre n'apporte presque rien. Ce que l'œil
réclame au loin, c'est l'occlusion **grande échelle** (relief, gros bâtiments). D'où la préférence pour (3), et
l'intérêt limité du RT pour *repousser un horizon* (le RT brille sur les **contacts nets en champ proche**).

### 2.3 Relief au soleil rasant
Une shadow map dégénère quand les rayons sont quasi horizontaux (texels étirés à l'infini). Solutions :
**ray marching sur la heightmap** accéléré par un **max-mipmap** (chaque mip stocke l'altitude max d'un bloc → gros
sauts), ou **horizon map** précalculée (pour chaque point, l'angle au-dessus duquel le soleil est visible → un test
d'angle). *Mord : premier terrain avec du relief et un cycle jour/nuit.*

## 3. Nuages volumétriques & atmosphère

- **Nuages** : champ de densité 3D procédural (bruit Perlin-Worley), **ray marching** en shader plein écran, pas de
  géométrie donc **pas de shadow map**. L'auto-ombrage se fait en relançant un mini-rayon vers le soleil (5–6 pas, en
  cône) et en convertissant la densité cumulée en transmittance par **Beer-Lambert** (`T = exp(-σ·d)`). Fonction de
  phase **Henyey-Greenstein** pour le *forward scattering* — c'est elle qui embrase les bords au soleil rasant.
  Optimisations obligatoires : marche à résolution réduite (¼), **bruit bleu** + **reprojection temporelle**.
- **Ombre des nuages sur le sol** : **cloud shadow map** — accumulation de densité vue du dessus le long du soleil,
  dans une texture **basse résolution** (≈1024² sur des centaines de km) qui stocke une **transmittance**, pas une
  profondeur. Peu coûteux : l'ombre d'un nuage est basse fréquence, personne n'attend un bord net.
- **Atmosphère** : ray marching atmosphérique avec **LUT précalculées** (modèle Bruneton, ou Hillaire pour la version
  temps réel moderne) — Rayleigh (bleu du ciel), Mie (halo autour du soleil). Donne le halo bleu vu de l'orbite, la
  bande orange au terminateur et les rayons crépusculaires.
- *Mord : dès qu'on veut une planète vue du ciel ou de l'orbite. Phase à part entière — pas un correctif.*

**Ce qui rend tout ça possible et qui est déjà payé** : positions en `double` + rendu camera-relative à origine
quantifiée (P2-M3/M4). Tenir en orbite à 400 km sans que les pixels tremblent, c'est exactement ce que ça achète.

### Table de décision (à garder sous la main)

| Échelle / matière | Bon outil | Pourquoi la shadow map échoue |
|---|---|---|
| Objets proches (< 500 m) | Shadow map en cascades (CSM) | — (c'est son domaine) |
| Nuages volumétriques | Ray marching + Beer-Lambert | Pas de surface à photographier |
| Nuages → sol | Carte de transmittance basse résolution | On veut de l'absorption, pas une profondeur |
| Relief, soleil rasant | Ray marching heightmap / horizon map | Texels étirés à l'infini |
| Planète, éclipses | Formules analytiques (sphère, cône) | Des millions de km à couvrir |

> Fil conducteur : **on ne photographie que ce qui a une surface et qui est proche.** Tout le reste s'intègre le long
> d'un rayon ou se résout par une équation.

## 4. Gameplay (Phase 3, après P3-M2)

- **Pooling d'entités + prefabs** *(écarté du périmètre de P3-M2, décision humaine session 15)*. P3-M2 livre
  `Spawn`/`Despawn` + changements structurels différés ; la **réutilisation** d'entités (pooling) et l'instanciation
  d'**archetypes prédéfinis** (prefabs) attendent d'avoir un client réel. *Mord : haute fréquence de spawn/despawn —
  projectiles, particules, débris — où le coût de création/destruction d'archetype devient visible. Concevoir avant
  d'avoir ce client, c'est concevoir à l'aveugle.*
- ~~**Physique**~~ : **v1 livrée en P3-M3** (corps rigides linéaires déterministes : gravité, intégration à dt fixe,
  collision sphère↔sol + sphère↔sphère, broadphase grille uniforme 0-alloc, résolution triée `(GlobalId)`). Constat :
  `UpstreamExtent` est désormais **exercé sous mouvement réel** et le wedge borné (P3-M2 D3) tient (eyeDistance stable).
  **Dette léguée par P3-M3** (double audit `csharp-lowlevel` + `engine-architect`, tous deux PASS with concerns) :
  - 🟠 **Spawn de corps runtime absent** : `SpawnBody` est **immédiat** (seam load-time, comme `SpawnImported`). Un
    `SpawnBodyDeferred` + `CommandKind.SpawnBody` (le fat `StructuralCommand` doit porter vitesse/masse/restitution/rayon)
    est requis avant tout spawn de corps en cours de simulation. *Mord : projectiles/débris (spawn haute fréquence).*
    Nommage à clarifier : `SpawnImported`/`SpawnBody` = immédiat, `Spawn`/`SpawnDeferred` = différé (le nom ne porte pas
    le timing).
  - 🟠 **Plafond `GlobalId < 2³²`** dans la clé de tri des paires de contact (`(minGid<<32)|(uint)maxGid`). Sûr tant que
    `GlobalId` est un compteur dense par run ; **casse silencieusement** quand le streaming rendra les ids process-uniques
    (bits hauts tagués). Même famille que le plafond 16 bits mesh/matériau — à faire échouer bruyamment, ou repacker,
    quand la sérialisation arrive.
  - 🟡 **Accumulateur wall-clock + interpolation** : la physique step à dt fixe (1 substep/tick, déterminisme by frame
    count). En **interactif** à framerate variable la vitesse de sim est couplée au framerate (attendu). *Mord : jeu réel.*
  - 🟡 **Pré-grow du `_cellHead`** ~~absent~~ ✅ **soldé** (`EnsureCapacity(count)`), gate 0-alloc rendu général (scènes
    dispersantes incluses).
  - 🟡 **Qualité solver** : rotation/inertie/friction, warm-starting, sleeping/islands, CCD (tunneling à grande vitesse),
    colliders non-sphériques, gravité non-verticale (le clamp de repos suppose Y). Pile profonde = micro-jitter résiduel
    (pas de clamp de repos corps-corps). Heap multi-couches impossible sur sol plat infini → **conteneur (parois)**.
  - 🟡 **Scatter par `Entity.Set` × 2N** (accès aléatoire) : réécrire les spans in place en second passage de chunk
    (comme `AnimateDrawables`) supprime 2N lookups/frame. Optim d'altitude, pas un défaut.
- **Sérialisation** source-gen (partage le générateur du rooting AOT ; parallélisable).
- **Audio** : en dernier, opportuniste.

## 4bis. Scène de test « planète / système solaire à l'échelle 1/2 » *(demande humaine, session 18)*

**L'idée** : une **seconde scène de référence** à côté de la grille de casques — une planète dans un système
solaire à l'échelle 1/2. Ce n'est pas un caprice de démo : c'est **le banc qui met enfin à l'épreuve ce pour quoi
les fondations `double` + camera-relative + origine quantifiée ont été construites**, et que rien n'a testé.

**Ce que l'échelle donne (chiffré)** — Terre 6 371 km → **3 186 km** de rayon ; Terre-Soleil 149,6 M km →
**74,8 M km** = `7,5e10 m`.
- ✅ **La précision `double` tient largement** : ULP à `7,5e10` ≈ **17 µm**. (Rappel des mesures P2-M3 : `1e7` m
  parfait, `1e15` visiblement cassé à 0,125 m d'ULP.) Le snap d'origine à 1024 m est sans effet à cette échelle.
  **La fondation est bonne — c'est le reste qui va craquer.**
- 🔴 **Le depth buffer, lui, ne tient pas.** Rendre une surface à 1 m *et* une planète à `1e11` m dans un seul
  frustum est impossible : near/far ≈ `1e11`. Il faudra du **reversed-Z** (gratuit, on est déjà en Z[0,1]), du
  **depth logarithmique**, ou des **passes multi-frustum** (proche / orbital / stellaire). **C'est le premier vrai
  blocage, et il est structurel** — à trancher avant d'écrire la scène.
- 🔴 **Le CSM devient le mauvais outil** — exactement ce que la table de décision §2 énonce déjà : à l'échelle
  planétaire, la nuit c'est `dot(N, L) < 0`, et les éclipses sont une intersection rayon/sphère. → **§2.2 ombres
  analytiques**, pas de shadow map.
- 🟠 **La planète a besoin d'une surface** : une sphère de 3 186 km avec du détail au sol = **LOD sphérique**
  (quadtree chunké, morphing). Sous-système à part entière → dépend du **terrain (§5)**.
- 🟠 **Les orbites doivent être analytiques (Kepler), pas intégrées.** La physique P3-M3 est un Euler semi-implicite
  à dt fixe : intégrer une orbite d'un an dériverait catastrophiquement (et coûterait des millions de pas). Les
  corps célestes se propagent par **éléments orbitaux évalués au temps t** — l'intégrateur ne touche qu'aux objets
  *locaux*. Deux régimes distincts à assumer explicitement.
- 🟠 **Atmosphère + terminateur** (§3) : c'est ce qui fait qu'une planète *ressemble* à une planète. Sans ça, une
  sphère texturée reste une balle.

**✅ Décision (humain, session 21) : UN facteur unique — 1/2 de la réalité, tailles ET distances.**
Remplace la décision « deux facteurs » de la session 18 (ci-dessous, gardée pour le raisonnement). Motif : un facteur
uniforme **conserve la taille angulaire réelle** des corps — le Soleil vu de la planète fait **~0,53°** (comme le vrai
depuis la Terre), au lieu d'être grossi ×5 par un 1/10 en distance. Une étoile est une **sphère de plasma physique** et
doit *paraître* à sa vraie taille.

| | Facteur | Résultat (réel ÷ 2) |
|---|---|---|
| **Rayon planète** | **1/2** | Terre 6 371 km → **3 185,5 km** |
| **Rayon Soleil** | **1/2** | Soleil 696 340 km → **348 170 km** |
| **Distance (1 UA)** | **1/2** | 1,496e11 m → **7,48e10 m** |

Coordonnées à `7,5e10` m (ULP `double` ≈ **17 µm** — très confortable ; le snap d'origine 1024 m sans effet). Les trois
valeurs sont dérivées du réel÷2 dans le code (constantes/env vars `AGAPANTHE_PLANET_*`/`AGAPANTHE_SUN_*`).

> **Décision superseded (session 18) — DEUX facteurs (tailles 1/2, distances 1/10), gardée pour mémoire.** L'idée était
> de servir deux objectifs contraires : *test* (grandes coordonnées absolues) et *usage* (planète atteignable/visible),
> qu'un facteur unique semblait sacrifier. En pratique 1/2 uniforme garde des coordonnées à `1e10` m (la valeur de test)
> ET la fidélité physique ; l'« atteignabilité » est réglée par la **vitesse de déplacement** mise à l'échelle, pas par
> une distorsion de la distance.

**Découpage réaliste** (ce n'est pas un jalon, c'est une petite phase) :
1. ~~**Sphère planétaire nue à l'échelle** + fix du **depth range** (reversed-Z) + jour/nuit analytique~~ ✅ **P3-M8**
   (session 21) : `Primitives.UvSphere`, `AGAPANTHE_SCENE=planet` (planète + Soleil sphère émissive à 7,48e10 m en
   `Double3`), **reversed-Z** global + comparateur depth par pipeline (shadow pass découplée), **point light
   co-localisée avec la sphère-Soleil** (la lumière part physiquement de l'étoile). *Depth + précision prouvés à
   `7,5e10` m dans un frustum, sans z-fighting. Verdict visuel + double audit en cours.*
2. Orbites képlériennes + échelle temporelle (le système bouge).
3. LOD sphérique (dépend du terrain §5) + atmosphère (§3).

*Mord : c'est la scène qui valide — ou infirme — la thèse « fondations pour un univers persistant » de la Phase 2.*

## 4ter. Vertical Slice — preuve d'intégration (ancre planétaire) *(cible instruite, session 21)*

> **Ce que c'est.** Le premier chemin **de bout en bout, mince mais complet**, qui prouve qu'on peut *faire un jeu* avec
> ce moteur — pas une démo jolie, une **preuve d'intégration**. La roadmap Phase 3 avance par jalons de *capacité*
> (P3-M0…M8) ; la vertical slice est le **capstone transversal** qui les fait tenir ensemble sur un cas réel.

**Décisions d'ancrage (humain, session 21) :**
- **Ancre = planétaire / spatial.** Prolonge P3-M8 : une caméra/sonde qu'on pilote autour de la planète et du Soleil à
  l'échelle 1/2, qu'on approche, avec des éléments **dynamiques spawns au runtime**. C'est le *payoff d'usage* de la
  scène §4bis, et la mise à l'épreuve grandeur nature de `double` + camera-relative + reversed-Z.
- **Ambition = preuve d'intégration** (pas de mini-jeu jouable). On prouve que `input → simulation → règle → rendu →
  save/load` tient de bout en bout ; le *fun* n'est pas l'objectif. Dette de scope minimale, gameplay délibérément mince.
- **Plateforme = Windows d'abord** (JIT + NativeAOT). **P3-M0 (Linux/macOS) n'est PAS un gate dur** de la slice — il
  est débloqué dès qu'une machine est dispo, mais n'empêche pas le « done » Windows.

**Definition of Done** (le chemin qui DOIT tourner, gates habituels : 0 validation, 0 leak, 0 alloc/frame sur le hot
path, tests verts, NativeAOT PASS, GPU==CPU) :
1. Boot dans la scène planétaire (P3-M8) ; **free-fly** autour de planète + Soleil à l'échelle, précision stable à
   `7,5e10` m (déjà acquis).
2. Au moins **un élément dynamique spawné au runtime** pendant la simulation (pas au load) qui se comporte selon une
   **règle minimale** (p. ex. une sonde larguée qui tombe/orbite localement) — prouve le spawn différé + la physique
   sous mouvement réel.
3. **Sauvegarder l'état du monde sur disque puis le recharger** de façon fidèle (round-trip vérifié) — la preuve de
   persistance, cœur de la thèse Phase 2.
4. **HUD minimal** à l'écran : coordonnées `double` courantes, nombre d'entités, état save/load (au-delà de la barre
   de titre debug actuelle).
5. *(stretch, opportuniste)* **un cue audio** sur un événement (spawn / save).

**Découpage** (ordre de dépendance ; chaque item = un jalon P3-Mx, spec + board + double audit + verdict comme d'hab) :
- ~~**VS-1 — Sérialisation**~~ ✅ **livrée session 22** (double audit PASS, verdict humain PASS). `GameWorld.Save/Load(Stream)`,
  format **binaire blittable déterministe** (byte-identique cross-process), remap `Parent` par GlobalId, compteur restauré.
  **Correction de cadrage** : *pas de générateur source-gen* (les composants sont blittables → bulk-copy sans réflexion ;
  le « partage le générateur du rooting AOT » supposait un rooting source-generated qui n'a jamais existé — le rooting est
  écrit à la main). **Seam GPU = handles reproductibles** (Option 1) : le caller recharge les mêmes assets d'abord.
  *Dette léguée* : la `Generation` des handles n'est pas validée au load → un **ordre de chargement d'assets différent
  casse en silence**. Correctif futur non bloquant (streaming/prefabs) : un **fingerprint d'assets** (hash count/ordre)
  fourni par le caller dans le header transformerait le mauvais-asset-silencieux en erreur dure, sans casser le
  GPU-free du World. *Mord : le jour où l'ordre/le set d'assets chargés varie entre save et load.*
- ~~**VS-2 — Spawn runtime**~~ ✅ **livrée session 23** (double audit PASS-with-concerns [4,5/5], verdict visuel PASS).
  `SpawnBodyDeferred` + `CommandKind.SpawnBody` (le `StructuralCommand` fat portant vitesse/masse/restitution/rayon,
  `MaterialiseBody` = point de matérialisation unique) — dette P3-M3 soldée. **Élargi (décision humaine)** : gravité
  **newtonienne** minimale — attracteur unique dans `PhysicsSettings` (`WithAttractor(C, μ, R)`, pas un composant),
  gravité radiale inverse-carré + sol radial (`|p−C|−r<R`, rest-clamp `2·(μ/R²)·dt`). `μ=0` byte-identique. Démo
  `AGAPANTHE_SCENE=planet-drop`. *Dette léguée* : **pas de lifetime/rest-cull des corps runtime** (croissance non
  bornée) · garde `r2>ε` = accel 0 au barycentre (envisager un échec bruyant pour l'univers persistant). Hors scope
  assumé : orbites (Euler+velocity float), n-body/attraction mutuelle, friction, terrain non-sphérique.
- ~~**VS-3 — Couche gameplay minimale**~~ ✅ **livrée session 24** (double audit PASS / PASS-with-concerns [4/5],
  verdict humain PASS). Défi d'atterrissage planétaire câblant **input → spawn (VS-2) → règle d'état → save/resume
  (VS-1)** : `GameWorld.QuerySurfaceContacts` (requête spatiale générique 0-alloc, World gameplay-free) + règle pure
  latchée `LandingChallengeRule` (Engine) + `LandingChallengeSystem` PostSim (Sandbox). `AGAPANTHE_SCENE=planet-challenge`
  (largage radial visé `B`, poser N=3 en ≤ M=6, `F5` quicksave + relaunch `AGAPANTHE_LOAD`). **Zéro octet ajouté au
  snapshot VS-1** (état re-dérivé du monde au load). *Dette léguée* : **latch Won/Lost non-monotone à travers un reload**
  (état re-dérivé ; un-win/un-lose possible si un probe glisse hors/dans la zone entre save et reload — faible proba,
  `.state` 1 octet déférée) · churn titre au bord de zone (glissement frictionless). Hors scope assumé : quickload
  en-process, HUD in-view (VS-4), prefabs/pooling, multi-cible.
- ⏸️ **VS-4 — HUD minimal** : **EN PAUSE (session 25)**. La slice a fait son travail (VS-1→VS-3 ont prouvé
  l'intégration) ; le texte à l'écran revient en **§4quater** comme *infrastructure* (debug overlay → profiler → UI de
  jeu), pas comme HUD de démo.
- ⏸️ **VS-5 — Audio** *(stretch)* : **EN PAUSE (session 25)**, sans regret. Repris quand un jeu-échantillon le tire.
- **Prérequis externe non bloquant** : **P3-M0** (validation Linux/macOS) — à faire dès machine dispo, hors gate slice.
  *Requalifié en §4quater* : le cross-platform est **revendiqué** sans avoir jamais été validé → item de crédibilité
  pour un moteur-artefact.

**Ce qui reste explicitement HORS slice** (pour ne pas élargir le scope) : orbites képlériennes (§4bis pas 2), LOD
sphérique + atmosphère (§4bis pas 3), prefabs/pooling (§4), mini-jeu jouable (ambition supérieure), toute UI au-delà du
HUD de lecture.

*Mord : c'est le jalon qui transforme « un moteur avec des fondations » en « un moteur avec lequel on a fait tourner un
monde de bout en bout ». Tant qu'il n'a pas tourné, l'intégration des sous-systèmes reste théorique.*

## 4quater. Cap moteur — « faire d'Agapanthe un vrai engine » *(réorientation instruite, session 25)*

> **Ce que c'est.** La Vertical Slice a prouvé l'*intégration*. Ce cap-ci change la **nature** du projet : passer d'un
> excellent *runtime* de rendu/simulation à un **moteur** — c'est-à-dire à quelque chose avec lequel on fait un
> **deuxième jeu, différent, sans éditer le code du moteur**.

**Décisions d'ancrage (humain, session 25) :**
- **L'artefact, c'est le moteur** (pas un jeu). Les jeux deviennent des **instruments de mesure**.
- **Généraliste, mais spécialement les sims spatiales à grande échelle** — et capable d'un Stardew-like.
  *Constat* : ces deux cibles ne divergent PAS côté rendu ; leur goulot commun est **l'authoring de contenu et l'UI**.
  La généralité vient des **frontières**, pas des features.
- **Multijoueur pensé dès maintenant**, **serveur autoritaire** (pas lockstep : la byte-identité d'Agapanthe est
  *intra-binaire* seulement — cf. VS-2 — et le float bit-exact cross-machine à l'échelle `double` planétaire est un piège).
- **Massif / persistant visé, petite coop possible** → **la topologie doit être un choix de DÉPLOIEMENT, jamais
  d'architecture** : même code de simulation partout, seule l'**autorité** change (listen-server = coop ; cluster = massif).
  Corollaire : ne jamais supposer qu'un seul process possède tout — l'**ownership** d'entité est un concept dès le départ.

### Constat de départ (mesuré, session 25)

`Agapanthe.Graphics` 47 types publics · `Rendering` 22 · `Core` 17 · `Assets` 15 · **`Engine` 10 (6 fichiers)** ·
`Platform` 1 — contre **`samples/Sandbox/Program.cs` = 2 164 lignes** qui contiennent le bootstrap, la génération de
contenu, 5 scènes, 4 cadrages caméra, les rigs de lumière, l'input, le save/load et le gameplay. **La couche « engine »
n'existe pas encore : elle est dans le Sandbox.**

### MP-0 — Fondations d'autorité *(le prochain jalon ; décisions quasi irréversibles)*

Tout ici coûte **peu maintenant** et est **catastrophique à rétrofitter**.

1. 🔴 **Identité d'entité globale.** `GlobalId` est un **compteur local** (`_nextGlobalId = 1` par `GameWorld`) → deux
   serveurs allouent tous deux 1, 2, 3… (**collision**), et VS-1 restaure ce compteur → deux shards sauvegardés sont
   **inmergeables**. *Décision proposée* : **64 bits partitionné** (poids fort = id d'autorité/shard, solo = shard 0,
   zéro coordination, marche hors-ligne ; poids faible = compteur local).
2. 🔴 **Clé de contact physique 64-bit-safe.** `_pairKey = (_pGid[j] << 32) | (uint)_pGid[k]` **écrase l'ID sur 32 bits**
   (commentaire du code : *« Assumes GlobalId < 2^32 »*). Couplé au point 1 : si on partitionne l'ID, deux entités de
   shards différents deviennent **silencieusement la même paire de collision**. *Fix* : garder l'ordre déterministe
   `(minGid, maxGid)` mais sur **deux clés parallèles** au lieu d'un packing 32+32 (reste 0-alloc et déterministe).
3. 🟠 **Split headless.** `Agapanthe.Engine` référence aujourd'hui **`Rendering` ET `Graphics`** → un serveur dédié
   embarquerait Vulkan. Il faut une simulation qui tourne **sans GPU**. (`Agapanthe.World` est déjà GPU-free : la moitié
   du chemin est faite.)
4. 🟠 **Input → commandes horodatées.** Aujourd'hui l'input **mute directement** (`Key.B` → spawn immédiat). Le netcode
   exige des commandes **envoyables / bufferisables / rejouables**. Règle *aussi* l'absence d'abstraction d'input
   (aujourd'hui : `Silk.NET.Input.Key` brut dans un `switch` du Sandbox).
5. 🟠 **Autorité du temps.** Découpler le **tick de simulation** de la frame de rendu (accumulator + interpolation) —
   dette déjà notée en P3-M3, que le multijoueur rend **obligatoire**.

### Ensuite, dans l'ordre

- **`Agapanthe.App`** — le host + le contrat `Game` (`OnLoad`/`OnUpdate`/`OnRender`), extraction de `Program.cs`.
  *Placé APRÈS MP-0 volontairement* : le split headless fait naître la couture que `App` doit formaliser.
- **Contenu** : identité d'assets stable (GUID/path — solde la dette VS-1 « ordre de chargement différent casse en
  silence ») → import/cook + graphe de dépendances → **prefabs & scènes déclaratifs** (⚠️ le snapshot VS-1 est une
  **sauvegarde**, PAS un format d'authoring) → définitions **data-driven** (items/recettes = données, pas du code —
  prérequis du Stardew-like).
- **La 2ᵉ slice, dissemblable** : une mini-slice top-down/orthographique. **Le moteur, c'est ce qui est commun aux deux.**
  Test de généralité le moins cher qui existe ; exposera violemment tout ce qui est hardcodé pour l'échelle planétaire.
- **Texte & UI** (§ ci-dessous), audio, **queries physiques** (raycast/formes/layers — aujourd'hui on ne peut même pas
  demander « qu'y a-t-il sous le curseur ? »), **job system** (tout est mono-thread, `AssertOwnerThread` partout =
  plafond dur), transparence triée.
- **Netcode réel** : transport, réplication delta, prediction/reconciliation.

### Hooks « massif/persistant » — à prévoir, PAS à implémenter

- **Relevance / interest management** : prévoir le *point d'insertion* d'un filtre par client dans le chemin de
  réplication (les structures spatiales existent déjà : grille broadphase, culling, origine quantifiée).
- **Persistance partielle** : VS-1 sauve le monde **entier en un bloc** ; un univers persistant sauve **par région, en
  incrémental**. Bonne nouvelle : le format (par entité, masque de composants, trié par GlobalId) **se shard
  naturellement** — ne pas le laisser se figer en « monde entier seulement ».
- **Réplication** : le snapshot VS-1 est déjà à ~80 % un snapshot réseau ; il manque le **dirty-tracking par composant**
  et le **delta contre baseline**.

**Explicitement HORS scope maintenant** : server meshing · transport · prediction/reconciliation · lag compensation ·
anti-triche · éditeur GUI · skinning/animation (sauf si un jeu-échantillon le tire) · terrain LOD/atmosphère.

### Texte & UI ✅ *(brainstorm fait, session 25 — spec écrite)*

**Spec : [plans/2026-08-03-text-ui-design.md](plans/2026-08-03-text-ui-design.md)** (revue scorée à passer).

Décisions verrouillées : périmètre = **texte & overlay SANS interactivité** — le scan a montré que toute UI
interactive est bloquée derrière l'input (**pas de position souris, pas d'événements boutons, pas de `KeyChar`, et
tout clic capture le curseur**), chantier qui appartient à **MP-0** ; la spec porte en annexe les exigences d'input à
concevoir **une seule fois** là-bas. Cook **hors-ligne** (imposé par le code : Release interdit la compilation runtime
des shaders et strippe `shaderc`). Atlas **SDF via `StbTrueTypeSharp`** (MIT, pur managé) → **zéro dépendance native,
y compris dans l'outil** ; FreeType écarté (n'apporte que du hinting, inutile en SDF, contre 3 OS × 2 arch de
binaires) ; `SixLabors.Fonts` écarté (licence Split payante). **Latin + kerning avec seam de shaping** (`Rune`, pas
`char`) → HarfBuzz insérable sans refonte d'API ; CJK/écritures complexes hors scope. Nouveau projet **`Agapanthe.Ui`
GPU-free**, **`tools/FontCooker`** (patron `ShaderPrecompiler`), format **`.agfont`** binaire mono-fichier (patron
VS-1, sortie déterministe).

**3 jalons séquencés** : ~~**UI-1** texte à l'écran~~ ✅ **livré session 25** · ~~**UI-2** DebugOverlay + profiler
CPU~~ ✅ **livré session 25** · **UI-3** timestamps GPU (`QueryPool`, dégradation gracieuse, abandonnable).

*UI-2 livré* (double audit PASS-with-concerns ×2, aucun 🔴) : `FrameStats`/`FrameSeries` + `DebugOverlaySystem`
(Engine) · `Sparkline` + `TextBuilder` public 0-alloc (Ui) · overlay in-view remplaçant le HUD `window.Title` **et**
son hack de cession VS-3, bascule `F3`, `AGAPANTHE_OVERLAY=0`. **Le gate 0-alloc est visible en continu à l'écran.**
*Leçon* : la fenêtre de mesure est le sujet, pas le compteur — l'overlay a d'abord mesuré le pump d'événements GLFW
(272 B/frame fantômes), puis fermé son bracket avant submit/present et **jamais** sur les frames à sortie précoce
(resize) → « 0 B » en vert pendant une recréation de swapchain par frame. Le bracket vit maintenant dans
`FrameOrchestrator` (`Tick` → `EndFrame()` après `DrawFrame`), exactement celui du banc.
*Dette léguée* : **seam `FrameProfiler` reporté à UI-3** — les timestamps GPU arrivent à N+2 et casseront
`Record(float, long)` (séries désalignées) ; le refactor appartient au jalon qui en connaîtra la forme, et
`DebugOverlaySystem` reste sans tests pour la même raison (il dépend de l'orchestrator concret).

*UI-1 livré* (double audit PASS-with-concerns ×2, verdict humain PASS) : `tools/FontCooker` (SDF hors-ligne, pur
managé) · `.agfont` déterministe · `Agapanthe.Ui` GPU-free · `BlendMode` + `R8Unorm` · `UiPass` + `Renderer.LoadFont`/
`DrawUi` · `UiRenderSystem` · **capture swapchain** `AGAPANTHE_CAPTURE_UI` (la capture HDR ne pouvait pas voir un
overlay dessiné après le tonemap). *Dette léguée* : ~~**synchronization validation non activée**~~ ✅ **soldée en
tête d'UI-2** (`141e374`) — activée par défaut en Debug, elle a immédiatement révélé **3 hazards préexistants** de la
boucle de frame, invisibles depuis le début du projet ; le gate « 0 message de validation » couvre désormais la
synchro. Restent : `MaxStorageBuffers` 12/16 ; pas d'échec bruyant sans format sRGB de swapchain ; troncature
silencieuse > 256 glyphes.

*Prérequis bas niveau découverts* : `Agapanthe.Graphics` n'a **aucun format mono-canal** (`R8Unorm` à ajouter,
précédent `Rg16Sfloat`) et son **blending est câblé en dur à `false`** (`GraphicsPipeline.cs:208`) — ajouter
`BlendMode` débloque aussi la dette « 2ᵉ verrou transparence ». Aucun `QueryPool` n'existe (UI-3).

**Le XAML retenu reste à instruire** (spec séparée, bien plus tard) : source generator XAML→C# (la réflexion est
hostile à NativeAOT ; précédents BAML/Avalonia/NoesisGUI), avec un v1 **brutalement restreint** — le volume de surface
(layout, styling, templates, binding, animations, routage d'input, focus) est un chantier de la taille du renderer.

## 5. Confort / qualité d'image (opportuniste)

- **Anti-aliasing** (aucun aujourd'hui : les bords de géométrie crénellent). TAA si la reprojection temporelle arrive
  pour les nuages — les deux partagent la même plomberie (vecteurs de mouvement, historique).
- **Auto-exposure** (l'exposition est fixée à la main dans le Sandbox), **bloom**, **prefilter env multi-mip**
  (fireflies possibles sur HDRI contrasté).
- **Upload asynchrone** des assets (aujourd'hui synchrone au chargement).
- **MikkTSpace** si des artefacts de normal mapping apparaissent.
- **Aliasing de texture au rasant** : traité une fois (herbe du Sandbox — texture 512² accumulée en flottant, brins
  splattés avec un footprint bilinéaire, flou final, aniso 16×). La règle générale à retenir : **une texture dont le
  détail est plus fin qu'un texel de sa propre mip chain aliasera quoi qu'on filtre** — c'est à la génération/à
  l'auteur de l'asset qu'on la corrige, pas au sampler.
- **Sol du Sandbox** : plan unique aujourd'hui. Un vrai terrain (heightmap + LOD) est un prérequis du §2.3.
