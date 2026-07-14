# P3-M2 — Scheduler de systèmes + lifecycle d'entités (`Agapanthe.Engine`)

**Statut** : spec v2 — révisée après relecture indépendante (`engine-architect`, v1 notée 2,9/5 NEEDS WORK).
**Session** : 15 · **Date** : 2026-07-14 · **Branche** : `phase2-foundations`
**Jalon précédent** : [P3-M1 — instancing + culling](2026-07-14-p3m1-instancing-culling-design.md) (clos, double audit PASS).
**Baseline de rendu** : commit `d1671f3`.

---

## 1. Le problème

Trois dettes rouges, toutes causées par la même absence.

**(a) L'ordre de frame vit dans une closure du Sandbox.** `samples/Sandbox/Program.cs:283-340` enchaîne
`PropagateTransforms → AggregateBounds → ComputeLightViewProj → CollectRenderLists → DrawScene`. Cet enchaînement est
un **invariant du moteur** — inverser deux lignes donne un cadrage d'ombre périmé d'une frame, ou un culling contre un
volume de lumière qui n'existe pas encore — mais rien ne le protège. C'est ainsi que la dette #1 (bounds pliées une
fois) est née, et la deuxième application le recréera de travers.

**(b) Il n'existe aucun endroit où cet ordre puisse vivre.** `Agapanthe.World` est **GPU-free**,
`Agapanthe.Rendering` est **ECS-free** ; les deux ne se référencent pas, par décision d'architecture, et c'est **bien**
(le rendu doit pouvoir dessiner autre chose qu'un `GameWorld` ; la simulation doit tourner sans GPU). Seul le Sandbox
les tient ensemble — l'invariant tombe donc dans le sample, faute de foyer.

**(c) Aucun cycle de vie.** `SpawnImported` existe ; **`Despawn` n'existe pas**, ni add/remove de composant, ni
reparentage. Une scène ne peut aujourd'hui que croître. Toute la Phase 3 (physique, gameplay) en dépend.

**Effet de bord latent** : `ShadowFit.UpstreamExtent` (`ShadowFit.cs:95-113`) dérive la plage de profondeur de l'ortho
des bounds **globales**, recalculées par frame depuis P3-M1 — donc une entité qui bouge loin fait **vibrer la plage de
profondeur de la shadow map de tout le monde**, chaque frame. Invisible aujourd'hui, certain dès la physique.

---

## 2. Décisions verrouillées

### D0 — Nouveau projet `Agapanthe.Engine` (couche mince de mariage) — *décision humaine*

```
Sandbox ──► Engine ──► World     ──► Core
        │        └───► Rendering ──► Graphics
        └──► Platform ──► Core
```

`Engine` est **le seul** projet référençant à la fois `World` et `Rendering`. Il possède le scheduler, l'ordre de frame
et la couture simulation↔rendu. `World` reste GPU-free ; `Rendering` reste ECS-free (vérifié : `RenderList`,
`RenderItem`, `RenderView`, `Frustum`, `ExtrudedShadowFrustum`, `Double3Bounds` vivent **tous** dans `Core`).

**`Engine` ne référence PAS `Platform`** (sinon il traîne GLFW et l'argument « simulation headless » de D0 s'effondre)
et **`Engine` ne possède rien** : il ne dispose ni le `Renderer`, ni le `GameWorld`, ni la `ResourceRegistry`. L'ordre
de teardown de `Program.cs:535-543` est le gate 0-leak — il reste au propriétaire.

**Rejeté** : `Rendering` référence `World` — le rendu deviendrait incapable de dessiner autre chose qu'un `GameWorld`.

### D1 — Scheduler à étages, systèmes enregistrés — *décision humaine*

Étages fixes et ordonnés : **`Input` → `Simulation` → `PostSimulation` → `Render`**. L'ordre devient une **donnée**,
pas du code. Ordre d'enregistrement = ordre d'exécution **au sein d'un étage** (garantie testée).

**`Input` est un étage VIDE côté moteur** : l'input vit dans `Platform`, que `Engine` ne référence pas. C'est
l'applicatif qui y enregistre ses systèmes. L'étage existe pour que l'ordre soit nommé, pas pour que le moteur le
remplisse. *(Livré : le Sandbox garde son contrôle caméra dans le callback `window.Updated` de la fenêtre — l'ordre
`Updated` → `Rendered` garantit caméra-avant-`Tick` — plutôt que dans un `ISystem` de `Stage.Input`. L'étage reste
donc vide côté Sandbox aussi ; il attend un vrai système d'input applicatif. Pas de dette : l'input est légitimement
piloté par l'événement fenêtre.)*

**Deux contextes, deux interfaces** — c'est le seul découpage qui préserve « la simulation tourne sans GPU » :

```csharp
public interface ISystem       { void Execute(in TickContext ctx); }    // Input, Simulation, PostSimulation
public interface IRenderSystem { void Render(in RenderContext ctx); }   // Render uniquement

public readonly struct TickContext   { public float DeltaSeconds; public long FrameIndex; }
public readonly struct RenderContext { /* TickContext + */ CommandList Cmd; Graphics.FrameContext Frame; SwapchainTarget Target; }
```

> ⚠️ **Collision de nom** : `Agapanthe.Graphics.FrameContext` existe déjà (`Program.cs:77`). Les types d'Engine
> s'appellent `TickContext` / `RenderContext`, jamais `FrameContext`.

Implémentation : les deux interfaces étant **disjointes**, le stockage l'est aussi — `List<ISystem>[3]` indexée par
`(int)stage` pour `Input`/`Simulation`/`PostSimulation`, **plus** une `List<IRenderSystem>` pour `Render`. Itération
**par index**, pas de `Dictionary`. `Add()` après le premier `Tick` est **interdit** (throw) : muter une liste en cours
d'itération est le bug qu'on ne veut pas chercher.

**D1.a — `Tick` est appelé HORS du callback de rendu.** `FrameRenderer.DrawFrame` **saute silencieusement son
callback** quand un resize est demandé ou que l'acquire échoue (`FrameRenderer.cs:70`, early-return lignes 78-82 et
92-96). Mettre la simulation dedans ferait donc **sauter tous les étages et toutes les barrières structurelles à chaque
redimensionnement de fenêtre** — `dt` perdu, despawns différés bloqués en file. Avec la physique, c'est un bug de
gameplay silencieux. D'où :

```csharp
engine.Tick(dt);                                 // Input → Simulation → PostSimulation (+ barrières) — TOUJOURS
frameRenderer.DrawFrame(engine.RenderDelegate);  // étage Render seul, dans le callback ; délégué caché en champ
```

Ça règle du même coup « qui possède le délégué caché » (F1.i) et évite de simuler après l'acquire (latence).

### D2 — Lifecycle : `Spawn`/`Despawn` + structurel différé + reparentage — *décision humaine*

- **`Spawn` / `SpawnDeferred` / `Despawn(EntityRef)` / `SetParent` / `IsAlive`** publics sur `GameWorld` (surface
  livrée — voir la correction v3 ci-dessous : `AddComponent`/`RemoveComponent` génériques écartés, le reparentage
  `SetParent`/`ClearParent` est la seule opération structurelle-composant du jalon).
- **Tout changement structurel est différé** et appliqué à une **barrière en fin d'étage** (muter la structure pendant
  l'itération d'une query invalide les chunks — bug classique et silencieux de tout ECS).

> **Correction v3 (relecture `engine-architect` en V2, faits décompilés d'Arch 2.1.0).** La v2 disait « le
> `CommandBuffer` d'Arch est réutilisé ». **C'est une erreur d'outil, pas une décision verrouillée** : `Arch.Buffer.
> CommandBuffer` est **inadéquat** pour D2, prouvé par le code d'Arch — (1) `Create` retourne une entité **bufferisée à
> id négatif** dont la ref **est invalidée par `Playback(dispose:true)`** (le caller perd le handle) ; (2) il n'existe
> **aucune API publique pour reset le buffer sans re-Playback** → `dispose:false` grossit sans borne (viole zéro-alloc) ;
> (3) `Playback` **ne résout pas** les refs d'entités **stockées dans les données de composant** (`Parent.Value` vers un
> parent bufferisé du même lot reste à l'id négatif) → hiérarchie même-lot cassée. Or refs stables + hiérarchies sont le
> titre de V2. **Décision retenue** : `GameWorld` possède sa **propre file de commandes** (`List` de structs, `Clear()`
> garde la capacité → zéro-alloc), appliquée à la barrière via `_world.Create/Destroy/Add/Remove` **immédiats** — sûrs
> car la barrière tourne **hors de toute query**. L'identité durable qui **précède la création** est le **`GlobalId`**
> (un `ulong` monotone) : `EntityRef` porte désormais ce `GlobalId`, résolu vers l'`Entity` d'Arch via une map
> **`_live` (`Dictionary<ulong,Entity>`) réutilisée**. Les commandes portent des `GlobalId`, jamais des `Entity`, donc
> le cross-ref même-lot se résout trivialement à la barrière (create de tous les spawns → `_live` peuplé, PUIS câblage
> des liens). Le **hot path ne touche jamais la map** : les systèmes itèrent les chunks et suivent `Parent.Value`
> (`Entity` cru) directement. `GlobalId` devient **universel** (toute entité en reçoit un au spawn) et **unique**
> (compteur, découplé de `RenderOrder`). Corollaire : `EntityRef` bascule de `Entity` vers `ulong`.
>
> **Scope V2 (YAGNI, comme le pooling/prefabs).** Les composants sont `internal` → une API générique publique
> `AddComponent<T>`/`RemoveComponent<T>` n'a **aucun consommateur externe** aujourd'hui. V2 livre la seule opération
> structurelle-composant réellement exercée : le **reparentage** (`SetParent`/`ClearParent`, = add/remove du composant
> `Parent`). Les cas limites deviennent : double `Despawn` · `Spawn`+`Despawn` même étage · `SetParent` **vers** une
> despawnée · `SetParent(x, P)` avec `P` en file de despawn · reparentage d'un enfant despawné.

**D2.a — `Despawn` d'un parent : CASCADE.** Le lien est enfant→parent seulement (`Parent { Entity }`), il n'existe
aucune liste d'enfants. Sans traitement, `ComputeWorld` (`GameWorld.cs:419-441`) déréférence un slot **recyclé** →
transform silencieusement faux. Décision : **`Despawn` détruit récursivement les descendants**, résolus à la barrière
par un scan de la query `Parent` (itéré jusqu'au point fixe). C'est ce qu'attend le gameplay (détruire un vaisseau
détruit ses tourelles) ; l'orphelinage ferait silencieusement téléporter les enfants à la racine du monde. Coût : un
scan **uniquement quand des despawns sont en attente**.

**D2.b — Sémantique intra-étage.** Un `Despawn` demandé par le système A, lu par le système B **du même étage** :
- **`IsAlive(EntityRef)` renvoie `false` DÈS que le despawn est mis en file** (l'entité est *logiquement* morte). Les
  systèmes testent `IsAlive` — c'est le contrat.
- Les composants restent **lisibles sans crash** jusqu'à la barrière (aucune corruption, la structure n'a pas bougé).
- **Après** la barrière, tout déréférencement lève : un unique helper interne `Deref(EntityRef)` garde **tous** les
  accesseurs (`GameWorld.cs:480-499` déréférencent aujourd'hui sans garde).

**Asymétrie assumée** : `IsAlive` est **immédiat** pour l'entité despawnée, mais **différé pour ses descendants** — la
cascade n'est résolue qu'à la barrière (résoudre la descendance à chaque appel de `Despawn` coûterait un scan par
appel). Un système qui lit un enfant dans le même étage le verra donc encore vivant. C'est écrit, donc ce n'est pas un
piège.

**Cas limites, chacun avec un test** : double `Despawn` · `Spawn` puis `Despawn` dans le même étage · `AddComponent`
sur une entité despawnée · `SetParent` vers une entité despawnée · **`SetParent(x, P)` alors que `P` est déjà en file
de despawn** (le scan de la barrière emportera `x` — c'est le comportement voulu).

**Zéro-alloc du chemin structurel** : la file de despawn **et** le set d'entités mortes (nécessaire à `IsAlive` en O(1)
et au scan à point fixe) sont des **champs réutilisés** (`Clear()` garde la capacité) ; le scan de cascade n'alloue
rien.

**Hors périmètre** (→ [backlog §4](../BACKLOG.md)) : **pooling** et **prefabs** — aucun client réel, concevoir à
l'aveugle serait pire que ne rien faire.

### D3 — Plage de profondeur de l'ombre : casters bornés, pas bounds globales

**Le fond du problème (que la v1 de cette spec ratait) : le wedge est NON BORNÉ vers l'amont.**
`ExtrudedShadowFrustum` est la somme de Minkowski du frustum caméra avec un **rayon** — il s'étend à l'infini vers la
lumière. Une entité à 10 000 km en amont du soleil est donc *dans* le wedge, entrerait dans la liste de casters, et
piloterait `UpstreamExtent` exactement comme les bounds globales le font aujourd'hui : `eyeDistance ≈ 1e7`, ortho
`far = eyeDistance + radius`, et **la précision de profondeur de la shadow map s'effondre**. Passer aux casters sans
borner l'amont, c'est échanger un bug contre le même bug avec plus de code.

**D3.a — Borner l'amont : `Renderer.ShadowCasterDistance` (défaut : `= ShadowDistance`).** Un caster au-delà de N
mètres **en amont le long de la lumière** est rejeté. C'est une **limite assumée** — tous les moteurs ont leur *shadow
caster distance* : une tour à 1000 km ne projettera pas son ombre dans la vue. **La coupe est franche (aucun fondu)** :
un caster qui traverse la frontière fait **popper** son ombre. C'est le prix normal de cette borne, et il doit être
connu d'avance, pas découvert en vérification visuelle.

**Constructibilité — vérifiée contre le code, ne pas improviser :**
- `ExtrudedShadowFrustum` stocke **exactement 6 plans**, et **aucun slot n'est garanti libre** : avec une lumière quasi
  perpendiculaire à l'axe de vue, `near` **et** `far` sont tous deux conservés par `ParallelEpsilon`
  (`ExtrudedShadowFrustum.cs:84`). → il faut un **7ᵉ champ `_cut`** ; `Intersects` passe à 7 produits scalaires.
- `Frustum` n'expose **ni corner ni centre** (`Frustum.cs:50,73`) : impossible d'y ancrer un plan « à distance N **du
  frustum** ». → l'ancrage est **passé en paramètre** — la sphère du frustum, que `ShadowFit.FitFrustumSphere` calcule
  déjà :
  ```csharp
  FromCameraFrustum(in Frustum f, Vector3 dir, Vector3 anchorCenter, float anchorRadius, float maxUpstream)
  // plan de coupe : normale +dir, offset = dot(dir, anchorCenter) - anchorRadius - maxUpstream
  ```
  Ancrer sur l'œil au lieu de la sphère **couperait des casters légitimes** dès que `ShadowDistance` est grand.

**D3.b — Qui reçoit quoi.** `ComputeLightViewProj` consomme les bounds **deux fois** (`ShadowFit.cs:54` et `:76`) :
- **`FitSceneSphere` garde `sceneBounds`** (la règle « jamais plus large que la scène » ; lui passer les casters
  gonflerait la sphère et ferait perdre le fit serré des petites scènes → moins de résolution d'ombre) ;
- **seul `UpstreamExtent` prend `casterBounds`.**

**D3.c — Comment casser la circularité (le fit veut les casters, le cull des casters veut le fit).**
Le wedge ne dépend **que** de la caméra et de la direction de lumière — pas du fit. Donc, **en deux passes** :

1. **Passe 1 (World)** — scan des chunks : cull caméra → `render` ; cull **wedge borné seul** → `shadowCasters`, en
   accumulant au passage `casterBounds` (AABB) **et** les sphères `(centre, rayon)` des casters dans un **tableau
   parallèle réutilisé, propriété du `GameWorld`** — car **`RenderItem` ne porte ni centre ni rayon**
   (`RenderItem.cs:13-17`), le rayon local vit dans le composant `Bounds` et est perdu à la sortie.
2. **Fit (Engine)** — `ComputeLightViewProj(view, sceneBounds, casterBounds, …)` → `lightFrustum`. La liste « wedge
   seul » est un **sur-ensemble** de la liste finale : fitter dessus ne peut que **grossir** `eyeDistance` → **aucun
   caster de la liste finale ne sort de la plage de profondeur** (pas de faux négatif, pas de clipping, pas
   d'itération à point fixe).
3. **Passe 2 (World)** — `CompactShadowCasters(shadowCasters, in lightFrustum)` : compaction **en place** contre le
   volume de lumière (scan linéaire sur la seule liste de casters, pas sur toutes les entités), **puis** `SortByKey()`
   — l'ordre des runs contigus du draw instancié (P3-M1) est ainsi préservé.

**Conséquences assumées, à ne pas maquiller** : `GameWorld.CollectRenderLists` (`GameWorld.cs:313`) **change de
signature** (plus de `lightFrustum` en entrée, un `out Double3Bounds casterBounds` en sortie), et
`Renderer.ComputeLightViewProj` (`Renderer.cs:662`) aussi — **c'est une API publique qui bouge**. Ce jalon n'est pas
« chirurgical » côté Rendering, et il faut le dire.

---

## 3. Architecture livrée

### 3.1 `Agapanthe.Engine` (nouveau projet)

| Type | Rôle |
|---|---|
| `Stage` (enum) | `Input`, `Simulation`, `PostSimulation`, `Render`. Ordre fixe. |
| `ISystem` / `IRenderSystem` | `Execute(in TickContext)` / `Render(in RenderContext)`. |
| `SystemScheduler` | Enregistrement par étage ; `Tick` exécute les étages dans l'ordre, avec **barrière structurelle** (flush) en fin de chaque étage. Zéro-alloc, `Add` gelé après le premier tick. |
| `FrameOrchestrator` | Le montage par défaut : enregistre les systèmes moteur dans le bon ordre. **L'invariant, enfin exécutable et testable.** Ne possède rien. Cache son délégué de rendu. |
| `SceneViewSystem` | La couture (`IRenderSystem`) : `RenderView` → passe 1 → fit → passe 2 → `Renderer.DrawScene`. Le seul type qui voit `GameWorld` **et** `Renderer`. |

### 3.2 `Agapanthe.World`

- Lifecycle (D2) : `Spawn`, `SpawnDeferred`, `Despawn`, `SetParent` (reparentage = add/remove du composant `Parent`),
  `IsAlive`, `FlushStructuralChanges()` (appelé par le scheduler à la barrière — **jamais** par l'utilisateur en
  pleine query). Pas de `AddComponent`/`RemoveComponent` génériques (v3, YAGNI).
- `CollectRenderLists` : nouvelle signature (D3.c passe 1) + `CompactShadowCasters` (passe 2).
- Reste **GPU-free**. Les systèmes existants sont exposés à Engine via des adaptateurs `ISystem` minces — mais
  `CollectRenderLists` change **bel et bien** de code (D3.c) : la v1 prétendait le contraire, à tort.

### 3.3 `Agapanthe.Rendering`

- `ShadowFit.ComputeLightViewProj` : prend `casterBounds` (D3.b) ; `UpstreamExtent` en dérive.
- `Renderer.ShadowCasterDistance` (D3.a) + le plan de coupe amont du wedge (`ExtrudedShadowFrustum`, dans `Core`).
- Ne connaît toujours pas l'ECS.

### 3.4 `samples/Sandbox`

La closure `drawScene` disparaît :

```csharp
var engine = FrameOrchestrator.CreateDefault(world, renderer, registry, camera);
// … par frame :
engine.Tick(dt, cmd, frame, target);
```

**Le spin du banc devient un `ISystem` d'exemple** enregistré en `Stage.Simulation` — sinon l'ordre de frame refuit
dans le sample (`Program.cs:288-298`), ce que ce jalon prétend justement supprimer. C'est accessoirement **la preuve
que le scheduler est extensible**. Le sample garde ce qui lui appartient : banc, input, captures.

---

## 4. Risques & points de veille

- **F1 — Zéro-alloc, les vrais pièges.** (i) `FrameRenderer.DrawFrame` prend un `Action<CommandList, FrameContext,
  SwapchainTarget>` : si `FrameOrchestrator` construit la lambda **par frame**, c'est **une alloc/frame**, invisible
  aux tests unitaires — le délégué doit être **caché dans un champ** (`Program.cs:77` le fait déjà aujourd'hui).
  (ii) `CommandBuffer` Arch **réutilisé**, pas recréé par flush. (iii) `foreach` sur `List<T>` utilise l'énumérateur
  struct (pas d'alloc) — mais l'itération par index lève le doute.
- **F2 — Le gate 0-alloc ne couvre pas le lifecycle.** Le banc `grid:100x100` ne spawne ni ne despawne rien : « 0 B/frame »
  ne dirait **rien** du chemin que ce jalon ajoute. → **mode churn** `AGAPANTHE_CHURN=N` (N spawn + N despawn par frame),
  intégré au gate. Le churn doit spawner des **hiérarchies** (parent + enfants) et despawner des **parents**, sinon il
  ne traverse jamais le chemin de **cascade** — c'est-à-dire le code neuf le plus coûteux.
- **F7 — Une seule `RenderView` par frame.** Le tableau parallèle de sphères (D3.c) est un état de passe 1 porté par le
  `GameWorld`, alors que les `RenderList` appartiennent à l'app (`Program.cs:56-57`). Deux vues (split-screen, cascades
  CSM) l'écraseraient entre passe 1 et passe 2. Contrainte assumée aujourd'hui ; **le CSM devra sortir cet état du
  World**. La passe 1 **ne trie pas** ; la compaction déplace les deux tableaux **en lock-step**, le tri vient après.
- **F3 — AOT.** Classes + appel virtuel = rien de fragile ; mais `AotRootingSmoke` **doit** exercer `Despawn`, le flush
  différé et la cascade hiérarchique, sinon le chemin structurel reste non prouvé sous ILC. La probe est un **projet
  séparé** (`tools/AotComponentProbe`), pas seulement un test.
- **F4 — Fidélité du rendu, critère binaire.**
  - **V3 (déplacement de l'ordre)** : aucun changement de math → capture **bit-identique à `d1671f3`** (SHA). Non
    négociable.
  - **V4 (D3)** : la plage de profondeur change légitimement → la capture **est autorisée à différer**, la différence
    doit être **justifiée par un diff d'`eyeDistance` loggé**, et une **nouvelle baseline** est figée en fin de V4.
- **F5 — Threading.** `AssertOwnerThread` garde `GameWorld` mono-thread. Le scheduler **ne parallélise rien** et doit
  le dire explicitement (le job system est une autre histoire).
- **F6 — `Engine` god-object.** Il ne possède rien, ne dispose rien, et ne contient que scheduler + orchestrateur +
  couture. Toute autre responsabilité qui y atterrit est une erreur.

---

## 5. Critère de sortie

**Tests** :
- Ordre des étages garanti ; **ordre d'enregistrement = ordre d'exécution** au sein d'un étage ; `Add` après `Tick` lève.
- Barrière structurelle : un `Despawn` demandé pendant l'itération n'invalide rien et prend effet à la barrière.
- **D2.a** : `Despawn` d'un parent détruit ses descendants (et `ComputeWorld` ne marche jamais dans un slot recyclé).
- **D2.b** : `IsAlive` faux dès la mise en file ; déréférencement après barrière → exception ; les 4 cas limites.
- **D3.a — le test qui compte** (assert **borné**, pas rassurant) : avec un caster à 10 000 km **en amont du soleil**,
  (i) `eyeDistance ≤ ShadowCasterDistance + radius + ε`, et (ii) ce caster est **absent** de la liste finale — l'ombre
  perdue est le comportement **voulu**.
- **D3.c** : la liste de casters finale est identique à celle produite par l'ancien AND (aucun caster perdu, aucun
  ajouté) ; les runs contigus survivent au tri.
- `AggregateBounds` correct après un `Despawn`.

**Gates projet** : 0 warning · 0 message de validation · 0 leak `ResourceTracker` · **0 B/frame** au banc **et en mode
churn** · **NativeAOT PASS** (probe étendue) · captures selon F4.

**Double audit** (`csharp-lowlevel` + `engine-architect`) avant clôture.

---

## 6. Vagues

| Vague | Contenu | Gate |
|---|---|---|
| **V0** | **Décision écrite** de D3 (borne amont, qui reçoit quoi, deux passes). *Papier : le test de F1 a besoin de l'API de V4.* | Spec relue |
| **V1** | Projet `Agapanthe.Engine` (+ `.sln`), `Stage`/`ISystem`/`IRenderSystem`/`TickContext`/`RenderContext`/`SystemScheduler` + tests d'ordre. Aucun changement de rendu. | Build 0 warn, tests verts |
| **V2** | Lifecycle World (D2 + cascade + sémantique intra-étage), `CommandBuffer` réutilisé, mode churn au banc, `AotRootingSmoke` étendu. | Tests + 0 B/frame en churn |
| **V3** | `FrameOrchestrator` + `SceneViewSystem` ; l'ordre quitte le Sandbox ; le spin devient un `ISystem`. | **Capture bit-identique à `d1671f3`** |
| **V4** | D3 : borne amont, deux passes, `UpstreamExtent` depuis les casters, signatures. | Tests D3 + capture justifiée + **nouvelle baseline** |
| **V5** | Banc, AOT, double audit, clôture, archive board. | Tous gates |

**Dépendances** : V4 dépend de V3 (la couture doit exister avant qu'on change ce qu'elle transporte). V2 et V4 touchent
toutes deux `GameWorld.cs` → **séquentielles, non parallélisables**.

## 7. Fichiers impactés (liste exhaustive)

**Nouveaux** : `src/Agapanthe.Engine/` (projet + `Stage`, `ISystem`, `IRenderSystem`, `TickContext`, `RenderContext`,
`SystemScheduler`, `FrameOrchestrator`, `SceneViewSystem`) · `tests/Agapanthe.Tests/SchedulerTests.cs` ·
`tests/Agapanthe.Tests/LifecycleTests.cs`.
**Modifiés** : `Agapanthe.sln` · `src/Agapanthe.World/GameWorld.cs` (lifecycle + D3.c) ·
`src/Agapanthe.Core/ExtrudedShadowFrustum.cs` (plan de coupe amont) · `src/Agapanthe.Rendering/ShadowFit.cs` +
`Renderer.cs` (signature, `ShadowCasterDistance`) · `samples/Sandbox/Program.cs` (orchestrateur, spin en système,
churn) · `tools/AotComponentProbe/` (chemin structurel) · `tests/…/ShadowFitTests.cs`, `WorldSystemsTests.cs`,
`SceneBoundsTests.cs`, `ExtrudedShadowFrustumTests.cs` (signatures).

---

## Journal des décisions

| # | Décision | Alternative rejetée | Pourquoi |
|---|---|---|---|
| D0 | Nouveau projet `Agapanthe.Engine`, ne référence pas `Platform`, ne possède rien | `Rendering` référence `World` | Le rendu doit rester capable de dessiner autre chose qu'un `GameWorld` ; la simulation doit tourner sans GPU. |
| D1 | Étages + systèmes enregistrés ; **deux** contextes (`TickContext`/`RenderContext`) ; `Input` vide côté moteur | Pipeline codé en dur ; contexte unique | L'ordre devient une donnée testable. Un contexte unique ferait voir des types Graphics aux systèmes de simulation. |
| D2 | `Despawn` **cascade** ; `IsAlive` faux dès la mise en file ; structurel différé à la barrière | Orphelinage des enfants | L'orphelinage téléporte silencieusement les enfants à la racine ; la cascade est ce qu'attend le gameplay. |
| D3.a | Borne amont explicite (`ShadowCasterDistance`, plan de coupe du wedge) | Casters non bornés | **Le wedge est infini vers la lumière** : sans borne, D3 déplace le bug au lieu de le corriger. |
| D3.b | Seul `UpstreamExtent` prend `casterBounds` ; `FitSceneSphere` garde `sceneBounds` | Tout passer aux casters | Sinon on perd le fit serré des petites scènes (moins de résolution d'ombre). |
| D3.c | Deux passes (wedge → fit → compaction) + sphères en tableau parallèle côté World | Rejet final « au record » (v1) | **`RenderItem` ne porte pas de sphère** ; filtrer au record casserait les runs du draw instancié. |
