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

## 1. Rendu GPU-driven (P3-M2 — la marche suivante)

P3-M1 a posé le SSBO d'instances, le batching et le cull CPU. La suite :

- **Slots persistants dirty-trackés** : les transforms ne sont plus recopiées par frame, seules les entités qui ont
  bougé écrivent leur slot. `RenderItem.WorldTransform` (64 des 88 octets) devient mort ; `RenderItem` se réduit à
  (handles, clé, instanceId), et les deux SSBO (scène + ombre) fusionnent en un buffer + deux plages d'index.
- **Cull en compute + draw indirect.** ⚠️ **Ce n'est PAS « une ligne de shader »** (erreur inscrite dans la spec P3-M1,
  corrigée depuis) : l'`instanceCount` d'un batch n'est plus connu côté CPU → il faut `vkCmdDrawIndexedIndirect(Count)`,
  donc `BufferUsage.Indirect`, `CommandList.DrawIndexedIndirect`, et la feature **`drawIndirectFirstInstance`** (le
  `firstInstance` d'un draw **direct** est gratuit ; celui d'un draw **indirect** ne l'est pas).
  → **Piste à trancher tôt** : porter l'offset de batch dans un **push constant** plutôt que dans `firstInstance`. Ça
  supprime la dépendance à la feature *et* neutralise le risque `baseInstance` sur MoltenVK d'un seul coup.
- **Clé mesh-major dédiée** pour la liste d'ombre : déjà fait en P3-M1, à conserver si les listes fusionnent.
- *Ce que ça rembourse* : le cull CPU O(n), ~2 ms AOT à 10 000 entités. *Mord : à 100 000 entités.*

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
