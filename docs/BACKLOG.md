# Backlog — Agapanthe

> **Ce fichier n'est pas un plan.** Il garde ce qu'on sait devoir faire un jour, avec *pourquoi* et *quand ça mord* —
> pour que la décision soit déjà instruite le jour où on ouvre le jalon. L'état réel du projet, les jalons en cours et
> la dette du dernier jalon vivent dans [AVANCEMENT.md](AVANCEMENT.md) ; les specs approuvées dans [plans/](plans/).
>
> Règle de tri : chaque item dit **ce qui casse sans lui** et **à quelle échelle il devient obligatoire**. Un item sans
> déclencheur clair est une idée, pas du backlog.

Dernière mise à jour : 2026-07-14 (session 14, après P3-M1).

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

- 🔴 **Slots persistants dirty-trackés — LE prochain jalon GPU-driven (la partie (C) reportée de P3-M4).** Les transforms
  ne sont plus recopiées/re-narrow/re-triées par frame ; seules les entités qui ont bougé écrivent leur slot (+ re-narrow
  global au changement d'origine quantifiée). `RenderItem.WorldTransform` devient mort ; `RenderItem` se réduit à
  (handles, clé, instanceId, sphère). *Quand ça mord — DEUX raisons (audit P3-M4)* : (a) à ~100 000 entités, le cull CPU
  historique ; (b) **immédiatement, pour défaire la régression qu'A+B a introduite** : P3-M4 sans (C) fait *strictement
  plus* de travail CPU qu'avant — il narrow/bake/**trie TOUS** les candidats (10001) et **upload ~960 Ko/frame** là où
  P3-M1 ne traitait que les visibles. Le mur CPU n'a pas disparu, il a **migré** du cull vers le sort+upload (et à 40k il
  est probablement plus haut). C'est le prix assumé d'une fondation ; (C) est ce qui le rembourse.

- 🟠 **Buffers GPU-produits en device-local.** L'instance buffer compacté et l'args buffer sont **host-visible** (le
  compute y écrit / y fait des atomics). Sur GPU discret sans ReBAR (le banc Windows) = trafic PCIe par dispatch, sûrement
  une part des ~8 ms @10k. Sur MoltenVK (mémoire unifiée, cible portability) = neutre. Les passer en **device-local** une
  fois la gate `ReadBackSceneVisible` (qui exige host-visible pour `MappedSpan`) **isolée en Debug**. À faire avec (C).

- 🟠 **La compaction atomique est un SECOND verrou sur la transparence** (en plus du `SortKey` sans profondeur, §0) :
  l'`atomicAdd` **scramble l'ordre intra-batch**, donc trier les candidats arrière→avant ne suffira pas — le futur
  transparent devra re-trier les survivants (readback CPU, ou sort GPU par batch). Sans effet aujourd'hui (opaque z-testé).

- 🟡 **MultiDrawIndirect** (un seul `vkCmdDrawIndexedIndirect` pour tous les batches, `drawCount = N`) : aujourd'hui un
  draw + binds **par batch**, y compris les batches entièrement cullés (`instanceCount=0`, no-op qui gonfle `LastSceneDrawCalls`).
  Repousse `VK_KHR_draw_indirect_count` (hors core 1.2, incertain MoltenVK) tant que le nombre de batches reste connu CPU.
- 🟡 **Cull d'ombre en compute** : le wedge two-pass P3-M2 reste CPU (subtil) — le déplacer réunifierait le seam de culling
  (aujourd'hui scène→GPU, ombre→CPU dans le World).
- *Ce que ça rembourse (avec (C))* : le cull CPU O(n) + le sort/upload O(n) de la fondation. *Mord : dès 40k, et à 100k.*

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
  **~4× la géométrie rasterisée** dans la passe d'ombre, et 4× le buffer d'instances. Image juste, coût GPU réel.
  *Ça mord : au banc `grid:100x100` (part des 11,4 ms).* Correctif : borner aussi la tranche **en profondeur**
  (plan aval), ou passer le cull d'ombre en compute → **backlog §1 (GPU-driven shadow cull)**.
- 🟠 **Setback amont fixe κ=4·r** (spec, decision log) : la marge est **proportionnelle au rayon de la cascade**,
  donc la **cascade 0 est la plus exposée**. Un contenu vertical dépassant `4r₀` au-dessus de la tranche proche
  perd son ombre **dans la cascade proche seulement** et la garde au loin → l'ombre d'une tour **disparaît quand on
  s'en approche**. Mode de défaillance vicieux (incohérence sous déplacement caméra) mais hors d'atteinte pour du
  contenu de 2 m. *Mord : premier bâtiment/falaise/grue.* Correctif : `UpstreamExtent` par cascade.
- 🟡 **Code mort du wedge retiré** : `ShadowFit.ComputeLightViewProj`, `ExtrudedShadowFrustum` (+ ses tests),
  `Renderer.ComputeFrustumSphere`, `ShadowCasterDistance` — ~200 lignes de production que **seuls les tests
  exercent** encore. Décider : supprimer, ou documenter comme conservé. (Le retrait du wedge lui-même est propre :
  aucun invariant orphelin, la dette P3-M2 « sortir `_casterSpheres` du World » est **soldée**.)
- 🟡 **Empreinte mémoire des listes de casters** : 4 `RenderList` à ~10k casters ≈ 12 Mo de tableaux managés au banc
  (conséquence directe du recouvrement ci-dessus). Pas un leak ; disparaît avec le vrai cull.
- 🟡 **Doc XML obsolète** : bloc `<summary>` résiduel de `ComputeLightViewProj` (`Renderer.cs`) et doc de
  `CollectRenderLists` (`GameWorld.cs`) décrivant encore le wedge + un `<see cref="CompactShadowCasters"/>` mort.
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

**✅ Décision (humain, session 18) : DEUX facteurs distincts — un pour les tailles, un pour les distances.**
C'est le bon arbitrage, parce que la scène sert deux objectifs qui tirent en sens inverse :
- **objectif de test** — de *grandes coordonnées absolues*, c'est tout l'intérêt (stresser `double` + depth) ;
- **objectif d'usage** — une planète *atteignable et visible*, sinon on regarde un point pendant tout le trajet.

Un facteur unique force à sacrifier l'un des deux. Deux facteurs laissent garder des distances énormes (la valeur
de test) tout en rapprochant les corps de l'observateur (la valeur d'usage).

**Point de départ proposé** (à caler à la construction, ce sont des molettes, pas des constantes gravées) :
| | Facteur | Résultat |
|---|---|---|
| **Tailles** (rayons) | **1/2** | Terre **3 186 km**, Soleil **348 000 km** |
| **Distances** (orbites) | **1/10** | 1 UA → **1,5e10 m** |

Ça donne ~4 700 rayons terrestres entre la Terre et le Soleil (contre **23 480** à l'échelle réelle) : **5× plus
compact**, tout en gardant des coordonnées à `1e10` m — soit exactement le régime qu'on veut éprouver
(ULP `double` ≈ **2 µm** à cette distance : encore très confortable). *Le rapport tailles/distances est le vrai
bouton de « game feel » ; le rendre configurable dès le départ évitera de le regretter.*

**Découpage réaliste** (ce n'est pas un jalon, c'est une petite phase) :
1. **Sphère planétaire nue à l'échelle** + fix du **depth range** (reversed-Z / multi-frustum) + jour/nuit
   analytique. *Prouve la précision et le depth à `1e11` m — l'essentiel du risque, sans terrain.* ⇦ **commencer ici**
2. Orbites képlériennes + échelle temporelle (le système bouge).
3. LOD sphérique (dépend du terrain §5) + atmosphère (§3).

*Mord : c'est la scène qui valide — ou infirme — la thèse « fondations pour un univers persistant » de la Phase 2.*

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
