# VS-1 — Sérialisation du World (save/load snapshot)

> Status: **reviewed — Approved 4.3/5** (2026-07-24, session 22), raffinements R1–R5 repliés (invariant d'ordre
> append-only + test, byte-identique round-trip, non-bump du compteur au load, cas d'erreur tronqué/hors-borne,
> compte 12 composants). Premier jalon de la **Vertical Slice** (backlog §4ter). Design instruit via
> absolute-brainstorm. Décisions d'ancrage humain (S21) : preuve d'intégration · ancre planétaire · Windows d'abord.
> Les décisions **techniques** ci-dessous ont été déléguées à Claude (l'humain n'a pas l'expertise pour les arbitrer)
> et prises avec argumentaire.

## 1. But & scope

La thèse « fondations pour un univers persistant » de la Phase 2 n'est pas prouvée tant qu'on ne peut pas
**sauvegarder l'état du monde sur disque et le recharger fidèlement**. VS-1 livre ce round-trip — la pièce manquante
n°1 de la vertical slice (DoD item 3).

**Résultat visé** : `world.Save(Stream)` / `world.Load(Stream)`, un round-trip **byte-identique et déterministe**,
NativeAOT-pur, 0 leak — et la scène planète qui se recharge à l'identique dans un **process neuf**.

**In scope** : sérialisation de toutes les entités du World (les 12 composants `[Component]`), le remapping du lien
`Parent` par identité stable, la restauration du compteur d'id, le format binaire, l'API sur `GameWorld`, les tests
(round-trip unit + exhaustivité + probe AOT), le câblage Sandbox minimal (save/load de la scène planète).

**Out of scope → backlog / jalons ultérieurs** : clés d'asset stables & rebinding robuste (→ streaming/prefabs,
backlog §4) · merge d'un snapshot dans un monde déjà peuplé · migration de schéma multi-version · compression ·
sérialisation des settings physique/caméra (concern Sandbox, hors World) · spawn runtime de corps
(`SpawnBodyDeferred` = **VS-2**).

## 2. État des lieux (ce qui existe)

- **12 composants** (`src/Agapanthe.World/Components.cs`), tous `[Component]`, **blittables** `StructLayout(Sequential)`,
  **`internal`** à `Agapanthe.World`. Le commentaire de tête anticipe déjà VS-1 (« prereq of the future
  source-generated serialization »).
- **Trois archétypes réels** : *drawable importé* (`GlobalId, WorldTransform, WorldPosition, MeshRef, Bounds,
  RenderOrder, InstanceSlot` [+`NoShadowCast`]) · *nœud hiérarchique* (`GlobalId, LocalTransform, WorldTransform,
  WorldPosition` [+`Parent`]) · *corps physique* (drawable + `Velocity, RigidBody`).
- **`Parent` porte un `Entity` Arch** (handle mémoire) → doit être remappé par `GlobalId` au load. L'infra existe :
  `_live` (GlobalId→Entity), `LinkParent`, les passes 1/2 de `FlushStructuralChanges`.
- **`MeshRef` porte `MeshHandle`/`MaterialHandle`** = `(int Index, uint Generation)` (`src/Agapanthe.Core/Handles.cs`),
  **process-locaux** (slots de `ResourceRegistry`).
- **`InstanceSlot`** = état runtime (`-1` puis réassigné au rebuild `CollectRenderLists`) → ne pas sérialiser.
- Le **rooting AOT est écrit à la main** (`ComponentRegistry.RootAll`), PAS source-generated. Précédent AOT-safe
  dans le projet : le glTF via System.Text.Json source-gen (`GltfJsonContext`) — inadapté à un save-game d'ECS
  blittable. `ComponentRegistryTests` garde déjà l'exhaustivité du registre par réflexion sur `[Component]`.

## 3. Décisions verrouillées (techniques)

1. **Seam GPU = handles reproductibles (Option 1).** Le snapshot stocke les `MeshHandle`/`MaterialHandle` **bruts**.
   Contrat de load : le caller recharge les **mêmes assets dans le même ordre** AVANT d'appliquer le snapshot → les
   handles (compteurs déterministes de `ResourceRegistry`) reviennent identiques. **Zéro nouvelle infra.** Marche
   cross-process pour la scène planète (chargement déterministe).
   - *Limite documentée* : casse silencieusement si l'ordre de chargement des assets change. Le registre de **clés
     d'asset stables** (Option 2 : `MeshRef` sérialisé comme `(clé, index local)` + résolveur au load) est **déféré**
     au jour du streaming/prefabs — il touche les modèles procéduraux (planète/Soleil n'ont pas de path) et c'est une
     couche qu'on conçoit avec un vrai client, pas à l'aveugle. Le **header de version** (§4) permet de faire évoluer
     la sérialisation de `MeshRef` derrière un bump sans toucher au reste (entités/composants/`Parent`/compteur sont
     identiques dans les deux options).
2. **Format = binaire blittable** (`MemoryMarshal.AsBytes` / `Cast`). Zéro réflexion → **AOT-safe par construction** ·
   **déterministe** (save byte-identique = gate projet) · compact · **exact** (pas de round-trip float texte). Un
   save-game d'univers persistant n'est pas un blob JSON de matrices. Little-endian **assumé** (toutes les cibles
   Win/Linux/macOS x64/arm64 sont LE) : le header utilise des primitives LE explicites et les composants sont en
   ordre hôte. **Le header ne *détecte pas* un mismatch d'endianness** (il force LE) — la sûreté vient de l'invariant
   « toutes cibles LE », pas d'un check ; un backend big-endian, s'il existait un jour, relirait mal les composants
   silencieusement (académique, aucune cible BE). *(correctif d'audit : la justification initiale « échouerait au
   check de version » était fausse.)*
3. **Pas de générateur source-gen.** Les composants sont blittables → un bulk-copy n'a besoin **ni de réflexion ni de
   générateur** (le « un seul générateur » du backlog supposait un rooting source-generated qui n'existe pas). On garde
   le style **écrit-à-la-main** de `ComponentRegistry`, avec le **même test-garde par réflexion** qui échoue si un
   `[Component]` n'est pas couvert par la sérialisation (invariant « source unique »).
   - **Invariant d'ordre (R1, critique)** : le `presenceMask` encode la présence **par index dans
     `ComponentRegistry.All`**. `componentCount` (égalité stricte) attrape un ajout/retrait, mais **permuter deux
     composants à count constant ré-interprète silencieusement les vieux saves**. Donc : **`ComponentRegistry.All` est
     append-only ; toute réorganisation impose un bump de `version`**. Énoncé ici et en §6, et **scellé par un test**
     qui vérifie l'**ordre** (une `SequenceEqual` contre une liste attendue figée), pas seulement l'ensemble — le
     `ComponentRegistryTests` actuel compare des `HashSet` (insensible à l'ordre) et ne suffit pas.
4. **État inclus** : toutes les entités + leurs composants présents + le compteur **`_nextGlobalId`** (sinon les
   futurs spawns collisionnent avec des ids rechargés). **Exclus** : `InstanceSlot` (runtime) · la file de commandes
   différées (flush AVANT save) · les settings physique/caméra (hors World).
5. **API GPU-free sur `GameWorld`** : `void Save(Stream)` / `void Load(Stream)`. Arch ne fuit pas (Stream in/out).
   `Load` s'applique sur un World **frais** (fraîchement construit, aucune entité) — pas de merge dans un monde
   peuplé (hors scope). Un `Load` sur un World non vide lève.

## 4. Format binaire

```
Header (fixe) :
  magic        4o   "AGWD"
  version      u32  = 1
  componentCount u32  = ComponentRegistry.All.Count (garde : le reader refuse un count différent)
  nextGlobalId u64  le compteur d'identité à restaurer
  entityCount  u32

Corps : entityCount entrées, TRIÉES par GlobalId croissant (→ save byte-identique pour un même monde)
  Par entité :
    globalId     u64
    presenceMask u32   bit i = « le composant d'index i (ordre ComponentRegistry.All) est présent »
    puis, pour chaque composant présent DANS L'ORDRE DES INDICES, sauf InstanceSlot :
      ses octets blittables (sizeof(T)), écrits verbatim
      exception Parent : écrit comme le GlobalId u64 du parent (résolu via parentEntity.Get<GlobalId>()),
                         PAS l'Entity Arch (un handle mémoire non persistable)
```

Le tri par `GlobalId` et l'ordre d'index fixe des composants rendent le save **déterministe** : deux `Save` du même
monde produisent des octets identiques (gate projet). `presenceMask` sur `u32` couvre jusqu'à 32 composants (12
aujourd'hui) — une garde d'assertion au build épingle le jour où on dépasse.

## 5. Flux

### 5.1 SAVE (`GameWorld.Save(Stream)`)
1. `FlushStructuralChanges()` d'abord → le monde est **settled** (aucune entité à moitié spawnée, aucune commande en
   attente). Sauver un monde avec des commandes pendantes serait un état ambigu.
2. Gather chaque entité (chunk-iteration) dans un scratch réutilisé, **trié par `GlobalId`**.
3. Écrire le header, puis chaque entité : `globalId`, `presenceMask`, puis les octets de chaque composant présent
   (sauf `InstanceSlot`). `Parent.Value` (Entity) → `parent.Get<GlobalId>().Value`.

### 5.2 LOAD (`GameWorld.Load(Stream)`, World frais)
1. Lever si le World n'est pas vide (`_live.Count != 0` ou commandes en attente).
2. Lire+vérifier le header (magic, `version`, `componentCount`) → **throw** `WorldSerializationException` si mismatch.
   Restaurer `_nextGlobalId`.
3. **Passe 1** — pour chaque entité : la créer avec le **GlobalId sérialisé** (PAS un nouvel id) et ses composants
   présents **sauf `Parent` et `InstanceSlot`**, l'enregistrer dans `_live[globalId]`. Les drawables (présence de
   `MeshRef`) reçoivent `InstanceSlot = -1`. Le dispatch write/read par composant est un **switch sur l'index**
   (instanciations concrètes `Add<T>` → AOT-safe, **même table** que le writer et que le rooting).
   - **R3** : le Load **bypasse le compteur** — `_nextGlobalId` est restauré **exactement une fois** depuis le header
     (étape 2), jamais incrémenté par la création d'entités. Les helpers `MaterialiseDrawable`/`MaterialiseNode`
     prennent un `globalId` explicite (ce sont leurs *appelants* — `Spawn*` — qui font `_nextGlobalId++`), donc le
     load les réutilise en passant l'id sérialisé et **ne route pas** par un helper qui bumperait.
4. **Passe 2** — câbler `Parent` : pour chaque entité qui l'avait, `LinkParent(childGlobalId, parentGlobalId)`
   (réutilise l'existant ; un parent absent est silencieusement ignoré, comme au flush).
5. `_structuralDirty = true` → le premier `CollectRenderLists` reconstruit les slots. `WorldTransform`/
   `WorldPosition` sont restaurés **tels quels** (source de vérité pour les drawables ; les nœuds hiérarchiques seront
   re-dérivés par `PropagateTransforms` à la frame suivante — le restore reste néanmoins fidèle par construction).

### 5.3 Reconstruction d'archétype (note d'implémentation)
Au load, une entité est créée puis ses composants présents ajoutés. Deux options d'implémentation, tranchées à
l'exécution (non bloquant pour le design) : (a) `world.Create()` puis `Add<T>` par composant (simple, un archetype
move par composant — acceptable car le load n'est **pas** hot-path) ; (b) reconstruction par signature d'archétype
groupée. On part sur (a) pour VS-1, avec une note perf si un save massif le justifie plus tard.

## 6. Invariant « source unique » (AOT)

Le writer ET le reader itèrent les composants dans l'ordre de `ComponentRegistry.All`. Le dispatch par-type
(write bytes / read+Add) vit **au même endroit** que la liste des composants — soit une table portée par
`ComponentRegistry`, soit un switch dans `WorldSerialization` gardé par un test. **Ajouter un composant sans le
couvrir échoue au test-garde** (réflexion sur `[Component]`, comme `ComponentRegistryTests` pour le rooting) — jamais
au runtime sous AOT. C'est le même principe « rooting et registration sont une seule opération » étendu à la
sérialisation.

**Ordre append-only (R1)** : parce que le format encode la présence par **index** dans `ComponentRegistry.All`,
l'ordre de cette liste fait partie du contrat du format. Un test dédié (`SequenceEqual` contre une liste attendue
figée) échoue si l'ordre change **sans** bump de `version` — c'est le complément indispensable du test d'ensemble
existant (`HashSet`, ordre-insensible).

## 7. API & fichiers

- **Nouveau** `src/Agapanthe.World/WorldSerialization.cs` — `public sealed partial class GameWorld` (comme
  `GameWorld.Physics.cs`) : `Save(Stream)`, `Load(Stream)`, le dispatch blittable par composant, le header, la
  `WorldSerializationException`. Atteint les composants internes sans exposer Arch.
- **Étendre** `src/Agapanthe.World/ComponentRegistry.cs` — exposer l'index stable des composants (déjà `All`) et,
  au besoin, la table de dispatch write/read (source unique partagée avec le rooting).
- **Réutilise** : `_live`, `LinkParent`, `FlushStructuralChanges`, les patterns `MaterialiseDrawable`/`MaterialiseNode`.
- **Sandbox** (`samples/Sandbox/Program.cs`) : env vars `AGAPANTHE_SAVE=<path>` (sauver après build de la scène puis
  continuer/quitter) et `AGAPANTHE_LOAD=<path>` (recharger APRÈS le chargement des assets — contrat Option 1).

## 8. Testing / gates

- **Unit round-trip** (`tests/Agapanthe.Tests/WorldSerializationTests.cs`) : un World avec **tous les archétypes**
  (drawable importé, hiérarchie parent→enfant→petit-enfant avec `Parent`, corps physique, tag `NoShadowCast`) →
  `Save` vers `MemoryStream` → `Load` dans un World frais → asserts : nombre d'entités, **valeurs de chaque
  composant** (positions `Double3`, quaternions, matrices, handles, velocity, rigidbody), **liens `Parent`** (par
  GlobalId), `_nextGlobalId`. **Save byte-identique** (déterminisme : deux `Save` du même monde = mêmes octets).
- **Byte-identique round-trip (R2)** : `Save(Load(bytes)) == bytes` (octets identiques après un aller-retour). C'est
  la propriété forte promise par §1 — plus stricte que « deux `Save` du même monde = mêmes octets » — et le meilleur
  test de non-régression du format. Elle tient par construction (re-tri par GlobalId canonique, `InstanceSlot` exclu
  des deux côtés, compteur restauré), donc son échec signale une vraie asymétrie writer/reader.
- **Round-trip fidèle après re-dérivation** : après `Load` + `PropagateTransforms` + `CollectRenderLists`, la liste
  de rendu (slots, transforms camera-relative) est équivalente à celle du monde d'origine.
- **Garde d'exhaustivité + ordre (R1)** : test réflexion prouvant que **chaque `[Component]` est couvert** par le
  dispatch (échec au build si un composant est ajouté sans sérialisation) — sauf les exclusions explicites
  (`InstanceSlot`), listées ; **plus** un test `SequenceEqual` scellant l'**ordre** de `ComponentRegistry.All`
  (append-only ou bump `version`).
- **Erreurs** : header corrompu / mauvaise magic / mauvaise version / `componentCount` différent → `Load` lève
  `WorldSerializationException` (pas de crash, pas de lecture hors-borne). `Load` sur World non vide → lève.
  **R4 (défensif)** : body **tronqué** (moins d'octets que `entityCount` annonce → EOF) et bit de `presenceMask`
  **≥ `componentCount`** (hors-plage) → `WorldSerializationException`, jamais de lecture hors-borne ni d'exception
  non typée. Bon marché même si l'entrée est en principe de confiance (un save-game local).
- **AOT** : étendre `tools/AotComponentProbe` (ou `AotRootingSmoke`) avec un round-trip save/load sous **NativeAOT**
  — les casts `MemoryMarshal` sont AOT-safe par nature, mais le dispatch `Add<T>` par type doit être prouvé rooté.
- **Intégration Sandbox** : `AGAPANTHE_SAVE=planet.sav` puis relance `AGAPANTHE_LOAD=planet.sav` en **process neuf** →
  capture headless **identique** à la scène planète directe.
- **Gates projet** : 0 warning, 0 message de validation, 0 leak, **0 alloc/frame** (la sérialisation n'est pas sur le
  hot path — save/load ponctuels), NativeAOT PASS.

## 9. Journal des décisions

- **Option 1 (handles reproductibles) plutôt qu'Option 2 (clés d'asset)** — la vertical slice est une preuve
  d'intégration, pas un mini-jeu ; Option 1 livre un vrai save/load cross-process avec zéro infra spéculative. Option 2
  se conçoit avec un vrai client streaming/prefabs (backlog §4), et le header de version la laisse arriver sans casser
  le reste.
- **Binaire blittable plutôt que JSON source-gen** — même si le glTF utilise STJ source-gen (AOT-safe, précédent
  projet), un save-game d'ECS blittable veut le bulk-copy exact/déterministe/compact, pas un blob texte de matrices.
- **Pas de générateur** — les structs blittables se copient sans réflexion ; le « un seul générateur » du backlog
  présupposait un rooting source-generated qui n'a jamais été écrit. YAGNI assumé, tracé.
- **Restaurer `_nextGlobalId`** — sinon un spawn post-load réémet un id déjà chargé (collision d'identité, corruption
  de `_live`). C'est l'état minimal hors-entités qu'il FAUT persister.
- **`Parent` par GlobalId, pas par Entity** — l'`Entity` Arch est un handle mémoire invalidé entre process ; le
  GlobalId est l'identité stable (le même raisonnement que `EntityRef` = GlobalId en P3-M2 D2).
