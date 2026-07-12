# Agapanthe — Spec de design : Phase 2, Fondations scalables

**Date** : 2026-07-12
**Statut** : **approuvé** (interview absolute-brainstorm) — **révision 3**, après deux relectures critiques indépendantes (v1 : 2,9/5 « trous majeurs » · v2 : 4,0/5 « approuvé sous réserve » · v3 : corrections appliquées)

> **Ce que les relectures ont corrigé** (à lire : ce sont les pièges du jalon)
> - **Ordre des jalons** : la couture ECS **avant** le camera-relative — sinon la soustraction `double→float` n'a aucune boucle où vivre, et le critère de M2 devient infaisable.
> - **Le culling des casters d'ombre était un no-op** : l'ortho était fittée sur les bounds de toute la scène, donc culler contre elle ne cullait rien. Le fit passe sur le **frustum caméra** (§3.5).
> - **La décomposition TRS n'est pas bit-exacte** : une entité importée doit porter sa **matrice bakée telle quelle**, sinon « byte-identique » est mathématiquement impossible (§3.4).
> - **Les lumières ponctuelles dérivaient** : elles sont stockées en `Vector3` monde et réécrites chaque frame — corriger leur *placement* ne suffisait pas, il faut corriger leur *stockage* (§3.3).
> - **Le gate AOT testait la mauvaise dépendance** : `Silk.NET.Windowing` (découverte de plateforme par réflexion) est un risque bien plus probable qu'Arch (§6.1).
**Portée** : transformer le viewer de la Phase 1 en moteur — ECS, coordonnées grande échelle, couture render-list, culling, conformité AOT, SPIR-V hors-ligne. **Hors portée** : physique, audio, gameplay, streaming, réplication, multi-serveurs.

## 1. Résumé

La Phase 1 a livré une chaîne graphique Vulkan complète (PBR, IBL, ombres, hot reload — 8 jalons, 205 tests, 0 leak). Mais c'est **un viewer, pas un moteur** : `Mesh.WorldTransform` est en lecture seule (baké à l'import glTF), donc **rien ne peut bouger**, et `Scene` confond deux rôles — la **possession des ressources GPU** et la **draw list**.

L'ambition à long terme est un univers persistant à grande échelle (planétaire, streamé, multi-serveurs avec transfert d'autorité). C'est un programme de plusieurs années. **La Phase 2 ne le construit pas.** Elle pose les fondations qui **ne se retrofitent pas**, et rien d'autre.

**Critère de sortie** : des milliers d'entités qui bougent, cullées, **à 10 000 km de l'origine sans perte de précision visible** — en NativeAOT, avec du SPIR-V précuit, 0 message de validation, 0 leak, zéro allocation par frame.

## 2. Décisions et justifications

| Décision | Choix | Justification |
|---|---|---|
| **ECS** | **Arch** (Apache-2.0, archétypes + chunks, `JobScheduler` alloc-free, `ParallelQuery`) — **conditionnel au gate §6 P2-M0** | Domaine résolu et éprouvé. Le temps va au vrai sujet (streaming, réplication, autorité). ⚠️ **Irréversible** : les types Arch irrigueront tout le code. |
| **Coordonnées monde** | `double` (`Double3` maison, §3.6) + **camera-relative rendering** (§3.3) | Le GPU ne sait faire que du `float32` — dans **tous** les moteurs. La solution universelle n'est pas « passer le GPU en double », c'est de mettre la caméra à l'origine et de n'envoyer que `objet − caméra`. |
| **Sérialisation** | `Arch.Persistence` **à valider en AOT** ; à défaut, source-gen maison | Prérequis du streaming et de la réplication. |
| **Physique** | **Hors Phase 2.** Décision *enregistrée* pour la Phase 3 : **BepuPhysics derrière un adaptateur `Agapanthe.Physics`** | Un solveur **stable** est un domaine à part entière. L'adaptateur — calque de « aucun `Vk*` hors `Graphics` » — laisse la porte ouverte à un solveur maison **contre la même interface**. On ordonnance, on ne renonce pas. |
| **Audio, gameplay, vertical slice** | **Hors Phase 2** | La phase doit rester bornée et sa cible nette. |
| **Scheduler parallèle** | Fourni par Arch (`ParallelQuery`). Pas de scheduler maison. | — |
| **Validation Linux** | **Déférée** (dette Phase 1) | Pas de machine disponible. Décision explicite. |
| **Import glTF** | **L'aplatissement est conservé en Phase 2** (une entité par mesh, transform bakée). `Parent` (§3.4) existe et est exercé par une hiérarchie **synthétique** de test. | Dé-aplatir `GltfLoader` est un chantier Assets à part entière, sans valeur pour le critère de sortie. Sans cette décision, `Parent` serait un composant mort au jalon où on le facture. |

### 2.1 Règles obligatoires (nouvelles — s'ajoutent aux règles verrouillées de la Phase 1)

1. **Le code doit être AOT-pur** (compatible NativeAOT). `<PublishAot>` sur le Sandbox **et `<IsAotCompatible>true</IsAotCompatible>` sur chaque bibliothèque** — sans quoi les warnings IL2xxx/IL3xxx ne remontent qu'au `publish` du Sandbox, trop tard. Warnings IL **traités en erreurs**. **Cette règle est rétroactive sur la Phase 1.**
2. **Chemin SPIR-V hors-ligne.** Le hot reload est un **luxe de développement** ; **la prod embarque du SPIR-V précuit** et ne charge pas shaderc au runtime.

Ces deux règles se servent mutuellement : SPIR-V précuit + AOT = **binaire natif autonome, sans JIT, sans shaderc**.

### 2.2 Arbitrage explicite à réexaminer (dette de conception assumée)

**`double` vs point-fixe `int64`.** `double` est retenu : simple, éprouvé, c'est ce que fait Star Citizen. Mais pour une simulation **déterministe répliquée** — l'horizon déclaré — le point fixe est plus sûr : les opérations IEEE 754 de base (`+`, `−`, `×`, `÷`) sont déterministes, **les transcendantes ne le sont pas** (`sin`, `cos`, `sqrt` peuvent différer selon CPU/runtime). À rouvrir si un jour on vise du lockstep déterministe.

## 3. Architecture

### 3.1 Modules et dépendances *(conditionnel au verdict §6 P2-M0)*

```
Sandbox ──► Rendering ──► Graphics ──► Core
   │            └──► Assets ─────────► Core
   └──────► World (Arch) ────────────► Core      ◄── NOUVEAU
Platform ──► Core
```

**`Agapanthe.World` ne référence aucun type GPU.** C'est ce qui le rend (a) testable sans GPU en xUnit, comme `Assets`, et (b) **sérialisable et streamable** plus tard — un monde qui connaîtrait des `GpuBuffer` ne pourrait jamais migrer entre processus.

*Plan B si P2-M0 rejette Arch* : le graphe de modules est **inchangé** ; seule l'implémentation interne de `World` bascule (autre lib, ou stockage maison). C'est précisément pourquoi `World` est GPU-free et pourquoi le reste du moteur ne parle que de `RenderList` et de handles (§3.2) : **aucun autre module ne voit l'ECS.**

### 3.2 La couture : des handles, pas des références GPU

Pièce maîtresse du design, et elle sert directement le streaming futur.

- **`MeshHandle` / `MaterialHandle`** — structs opaques, dans `Core`. **Le monde ne connaît que des handles.**
- **`ResourceRegistry`** (dans `Rendering`) — **possède** les `Mesh` / `Material` / textures GPU et résout `handle → ressource`. Reprend le rôle « propriétaire » de l'actuelle `Scene`.
- **`RenderList`** — produite **par frame**, **possédée par le Renderer**, buffer réutilisé (croissance amortie, **jamais réalloué en régime établi** → zéro alloc par frame, cf. règle Phase 1).
- **`Renderer.DrawScene(RenderList, ShadowCasterList, Camera, …)`** remplace `DrawScene(Scene, …)`. **Le Renderer ne connaît pas l'ECS.**

**Deux listes, pas une** (§3.5) — c'est un point de conception, pas un détail.

Conséquence : `World → Core` seulement. Les **handles survivent au streaming** : les ressources GPU vont et viennent, les handles restent.

**Ownership** (règle Phase 1 §3.2 étendue) : le `ResourceRegistry` possède les ressources GPU et les libère via la `DeletionQueue`. Le `Renderer` possède ses targets **et** les listes par frame. `World` ne possède **rien** de GPU. Un handle dont la ressource a été déchargée est une **erreur** (§4), pas un silence.

### 3.3 Camera-relative rendering

**Principe.** La caméra est **toujours à l'origine**. On calcule `objet_monde − caméra_monde` **en `double` sur le CPU**, et on n'envoie au GPU que la différence — petite (portée visible), donc parfaitement représentable en `float32`.

**Pourquoi c'est bon marché.** Cette soustraction se fait **dans la boucle qui construit les listes de rendu** (§3.2). Cette boucle est livrée par **P2-M2** ; le camera-relative arrive en **P2-M3**, donc *après* — c'est l'ordre qui rend l'argument valide. (La v1 de cette spec plaçait le camera-relative avant la couture : contradiction corrigée.)

**Impact sur les shaders** (établi en lisant le code) :

| Élément | Changement |
|---|---|
| `mesh.vert` | **Inchangé** (vérifié : il ne lit jamais `camera.position`, cf. `mesh.vert:34-49`). `push.model` change de *sémantique* (modèle → monde **relatif caméra**), pas de forme. |
| `mesh.frag` | `camera.position` devient `vec3(0)` → `V = normalize(-worldPos)` (**plus simple qu'avant**). Les lumières arrivent relatives caméra. |
| `skybox.vert` | Inchangé (avec eye = 0 et view rotation-seule, la direction reconstruite reste correcte). |
| Shaders IBL / tone map | **Intacts** — ils ne travaillent qu'en directions. |
| `CameraUniforms.Position` (`CameraUniforms.cs:34`) | Devient **toujours 0**. **Conservé** : l'identité du bloc UBO doit rester byte-identique entre `mesh.vert` et `mesh.frag` (cf. `mesh.vert:22-23`). |
| `CameraUniforms.View` | Devient **rotation seule** (translation nulle par construction). |

**⚠️ Impact CPU — ce que la v1 de cette spec avait FAUX.** Les shaders sont intacts, **mais pas la construction CPU**. Trois sites lisent des positions monde **absolues en `float`** et **cassent à 10 000 km** (ULP d'un `float` ≈ **1 m** à 1e7 m) :

| Site | Fichier | Correction |
|---|---|---|
| Ajustement du frustum ortho des ombres | `Renderer.ComputeLightViewProj` — `Renderer.cs:649-663` (lit `scene.BoundsCenter` / `BoundsDiagonal`) | **Fitte sur le frustum CAMÉRA**, plus sur les bounds monde (§3.5, note critique) |
| **Stockage** des positions de lumières ponctuelles | `LightsUniforms` (`Lights.cs`), réécrites chaque frame en `Renderer.cs:783-785` — aujourd'hui `Vector3` **monde absolu** | Stockées en **`Double3`**, converties en **relatif-caméra à chaque frame**. Sans ça elles **dérivent dès que la caméra bouge** — corriger `SetupLights` ne suffit pas. |
| Placement initial des lumières ponctuelles | `Program.SetupLights` — `Program.cs:445-446` | Consomme les bounds `Double3` (§3.5) |
| Cadrage caméra initial | `Program.FrameCamera` — `Program.cs:476-477` | Idem |
| Intégration du déplacement caméra | `FreeCameraController` | Arithmétique en **`Double3`** — sinon on reperd la précision juste après l'avoir gagnée |

`Camera.Position` (`Camera.cs:31`) passe de `Vector3` à **`Double3`** ; `Camera.ViewMatrix` (`Camera.cs:71`) produit une view **rotation seule**.

### 3.4 Modèle de monde (ECS) *(conditionnel au verdict §6 P2-M0)*

| Composant | Contenu | Raison |
|---|---|---|
| `GlobalId` | Identité **stable, unique, non locale** | Doit survivre à une **migration inter-processus**. Coûte ~rien maintenant, **impossible à greffer après**. Distinct du handle `Entity` local d'Arch. |
| `LocalTransform` | `Double3` position + quaternion + scale (`float`) | Seule la **position** exige la précision absolue ; rotation et échelle non. |
| `Parent` | Référence au parent | Hiérarchie de transforms. Exercée en Phase 2 par une hiérarchie **synthétique** (l'import glTF reste aplati, cf. §2). |
| `WorldTransform` | `Matrix4x4` dérivé, propagé | Sortie de la propagation hiérarchique — **et l'entrée du rendu** (§3.2). |
| `MeshRef` | `MeshHandle` + `MaterialHandle` | Aucun type GPU (§3.2). |
| `Bounds` | AABB locale | Entrée du culling et de l'agrégation (§3.5). |

**⚠️ La décomposition TRS n'est pas bit-exacte — et cela dicte le contenu de P2-M2.** La source d'un mesh importé est une **matrice** (`MeshAsset.WorldTransform`, bakée par `GltfLoader.cs:198`). Faire `matrice → décomposition TRS → recomposition` **ne redonne pas les mêmes bits**. Donc :

- **En P2-M2**, une entité importée porte **directement sa `WorldTransform` matricielle bakée** (recopie bit-exacte). `LocalTransform` (TRS) **n'est pas dérivé de l'import** : il est porté par les entités **créées par code** (la hiérarchie synthétique, les entités en mouvement de M4). C'est ce qui rend le critère « byte-identique » de M2 **atteignable**.
- **En P2-M3**, le passage en `Double3` + camera-relative introduit forcément de nouveaux arrondis → le critère devient « ≤ 1 LSB/canal » (§5). C'est **assumé**, pas subi.

**Contrainte non négociable, imposée dès maintenant** : **aucun changement structurel pendant l'itération** (ajouter/retirer un composant déplace l'entité d'archétype et invalide le chunk parcouru). Les changements passent par un **command buffer** rejoué à un point de synchro. C'est ce qui rendra le parallélisme possible sans rien réécrire.

### 3.5 Chaîne de systèmes (l'ordre est un contrat)

```
1. propagation de hiérarchie   → WorldTransform                   [P2-M2]
2. agrégation des bounds monde → bounds Double3 de la scène       [P2-M2]  ◄── remplace Scene.Bounds*
3. culling caméra              → RenderList        (frustum caméra)   [P2-M4]
4. culling casters             → ShadowCasterList  (volume de LUMIÈRE) [P2-M4]
```

**Répartition entre jalons (contrat explicite)** : **P2-M2 livre la couture** — les systèmes 1 et 2, le `ResourceRegistry` et **les deux listes en mode passthrough (aucun culling : toutes les entités sont dessinées, dans un ordre stable)**. C'est ce qui rend son critère « byte-identique » atteignable (§6). **P2-M4 livre le culling réel** (systèmes 3 et 4) et la montée en charge.

**Le système 2 remplace `Scene.BoundsMin/Max/Center/Diagonal`** (aujourd'hui calculées une fois à l'import par `SceneBuilder.ComputeWorldBounds`, `SceneBuilder.cs:98-111`). Il produit des bounds en `Double3`, réduites en relatif-caméra par frame. Consommateurs : `SetupLights`, `FrameCamera` (§3.3).

**⚠️ Le système 4 est un point de conception, pas un détail.** Le shadow pass a **sa propre boucle de draw** (`RecordShadowPass`, `Renderer.cs:673-724`). **Il ne doit PAS consommer la `RenderList`** : celle-ci est cullée contre le **frustum caméra**, donc un objet hors champ mais projetant une ombre **dans** le champ en serait absent → **des ombres qui apparaissent et disparaissent quand la caméra tourne**. Les casters sont cullés contre le **volume de la lumière**.

**⚠️ Corollaire obligatoire — le fit ortho des ombres doit changer.** Aujourd'hui `ComputeLightViewProj` (`Renderer.cs:649-663`) ajuste l'ortho sur les bounds de **toute la scène**. Deux conséquences fatales à l'échelle de la Phase 2 :
1. Culler les casters contre un volume qui **englobe par construction toute la scène** est un **no-op** — le système 4 ne cullerait rien.
2. À 10 000 entités étalées (§6.2), une ortho unique couvrant tout le monde donne une **shadow map inutilisable** (la taille du texel explose).

→ **Décision** : en Phase 2, `ComputeLightViewProj` fitte le volume de lumière **sur le frustum caméra** (sphère englobante du frustum, bornée par une distance d'ombre maximale), **pas** sur les bounds monde. C'est aussi le prérequis naturel du CSM (hors portée, §7).

### 3.6 `Double3`

Struct dans `Core` (auprès de `MathHelpers`) : **3 × `double`**, pas de SIMD (`System.Numerics` n'offre pas de `Vector3` en double — c'est la raison d'être de ce type). Opérations : `+`, `−`, `*` scalaire, `Length`, `Distance`, et surtout **`ToVector3(Double3 origin)`** — la conversion relative-origine qui matérialise le camera-relative. `[StructLayout(Sequential)]`, blittable (prérequis de la sérialisation future).

### 3.7 Chemin SPIR-V hors-ligne (règle §2.1-2)

- Cible **MSBuild** : précompile les shaders au build → `.spv` à l'output.
- `ShaderCompiler` en mode **« cache seul »** : shaderc **non chargé** quand le précuit est présent.
- Le **hot reload reste actif en Debug uniquement**.
- Le cache disque keyé par le **hash du source résolu après expansion des includes** (livré en M8-03) est déjà la moitié du travail : il reste à pouvoir le **pré-remplir au build**.

## 4. Gestion des erreurs

- **Handle inconnu** (absent du `ResourceRegistry`) **ou ressource déchargée** : exception à la construction de la liste, avec le handle et l'entité fautive. **Pas de fallback silencieux.**
- **Cycle dans la hiérarchie** (`Parent`) : détecté et rejeté à la propagation, avec le chemin du cycle.
- **Warning AOT (IL2xxx/IL3xxx)** : **erreur de build** (§2.1-1).
- **SPIR-V précuit manquant en Release** : échec **explicite** au démarrage. Pas de repli silencieux sur shaderc — sinon la règle §2.1-2 ne serait pas tenue.
- Le reste (VkResult, validation layers, assets) : inchangé, cf. spec Phase 1 §4.

## 5. Stratégie de test

- **Unitaires (xUnit, sans GPU)** : `Double3` (précision, `ToVector3` à grande distance) ; propagation de hiérarchie **et détection de cycle** ; agrégation des bounds ; frustum culling (dedans/dehors/à cheval) ; construction des deux listes ; snapshot/restore d'entité.
- **Preuve de non-régression (§6 P2-M2)** : la couture ECS est un **refactor pur** → capture **byte-identique à la baseline M8** (`24001B24…`). Deux conditions le rendent atteignable, et elles sont **exigibles** : (a) l'entité importée porte la **matrice bakée telle quelle**, sans aller-retour TRS (§3.4) ; (b) **l'ordre de draw est stable et identique à celui de `Scene.Instances`** (l'itération Arch par archétype/chunk ne le garantit pas par elle-même → la `RenderList` est ordonnée par une clé stable). Sans (a) et (b), le critère serait faux.
- **Preuve de précision (§6 P2-M3)** : rendu **à 10 000 km** vs **à l'origine**. Critère **chiffré** : les transforms relatifs caméra sont **exactement égaux** (test unitaire `Double3`, comparaison bit-à-bit) ; la capture GPU tolère **≤ 1 LSB par canal** (la reconstruction `double → float` ne retombe pas nécessairement sur les mêmes bits, exiger l'identité binaire du framebuffer serait un critère faux).
- **Preuve AOT (§6 P2-M0)** : `dotnet publish /p:PublishAot=true` → binaire natif qui tourne, **0 warning IL**.
- **Runtime** : à chaque jalon — **0 message de validation, 0 leak, zéro allocation par frame**.

## 6. Jalons

Chaque jalon clôt sur : Sandbox propre (0 validation, 0 leak), tests verts, **double audit agent** (`csharp-lowlevel` + `engine-architect`) PASS, board archivé.

| # | Livrable | Critère de sortie |
|---|---|---|
| **P2-M0** | **Gate AOT** ⚠️ **BLOQUANT** (§6.1) | Sandbox publié en **NativeAOT**, il tourne, **0 warning IL**. Verdict sur Arch. |
| P2-M1 | Chemin SPIR-V hors-ligne (§3.7) | Build Release/AOT qui démarre **sans shaderc**. **Prouvé, pas déclaré** : (a) la lib native shaderc est **absente de la sortie de `publish`** ; (b) une assertion vérifie qu'aucun `ShaderCompiler` n'est instancié en Release. |
| **P2-M2** | **Couture : ECS + `ResourceRegistry` + 2 listes (passthrough, sans culling)** — systèmes 1-2 (§3.2, §3.4, §3.5) | **Refactor pur** → capture **byte-identique à la baseline M8**, sous les conditions (a) matrice bakée sans aller-retour TRS et (b) ordre de draw stable (§5). Le helmet est dessiné **via l'ECS**. |
| **P2-M3** | **`Double3` + camera-relative** (§3.3, §3.6) | Rendu **à 10 000 km == à l'origine** : transforms relatifs **exactement** égaux (unitaire), capture **≤ 1 LSB/canal**. |
| P2-M4 | **Culling réel** — systèmes 3-4 (§3.5) : frustum caméra + volume de lumière, fit ortho sur le frustum — **et montée en charge** | **Critère de sortie de la phase** (§6.2). |
| P2-M5 | Audits + clôture | `csharp-lowlevel` (zéro-alloc, **AOT**, leaks) **sans finding critique** ; `engine-architect` PASS ; protocole visuel humain. |

**L'ordre M2 → M3 est un contrat** : la couture *d'abord* (le refactor pur, prouvé par une capture byte-identique), le camera-relative *ensuite* (il hérite alors de la boucle qui existe déjà). L'inverse rendrait la soustraction orpheline et le critère de M2 infaisable.

### 6.1 P2-M0 est un gate, pas une formalité — et il teste dans l'ordre du RISQUE

**Rien ne commence avant ce verdict.** L'ordre ci-dessous est celui du risque réel, pas de l'intuition (la v1 de cette spec testait Arch en premier : erreur).

1. **`Silk.NET.Windowing` / `Window.Create`** (`src/Agapanthe.Platform/EngineWindow.cs:42`) et `Silk.NET.Input` — Silk.NET 2.x découvre ses plateformes **par réflexion**. **C'est le point de rupture AOT le plus probable de tout le projet**, et il est dans la Phase 1, pas dans Arch. Silk.NET supporte le trimming depuis 2.18 et a des correctifs NativeAOT explicites, mais **rien ne le prouve ici**.
   **Plan B si ça casse** (c'est le seul risque sans contingence, et il touche une décision verrouillée — bindings Silk.NET) , par ordre de préférence : (a) enregistrement **explicite** de la plateforme de fenêtrage/input (Silk.NET expose des API de registration manuelle qui contournent la découverte réflexive) ; (b) **P/Invoke GLFW maison** dans `Agapanthe.Platform` — le module est petit et déjà isolé, et GLFW est une API C stable ; (c) en dernier recours, `PublishAot` sur le serveur/headless uniquement, le Sandbox fenêtré restant en JIT. **(c) affaiblirait la règle §2.1-1 → à n'accepter qu'explicitement.**
2. **`Silk.NET.Shaderc.Native`** sous `PublishAot` (packaging de la lib native). La règle §2.1-2 le retire de la prod — les deux règles interagissent, et c'est P2-M1 qui solde ce point.
3. **Arch + `Arch.Persistence` (snapshot/restore) + `ParallelQuery`.** La sérialisation est le composant le plus susceptible de casser en AOT (réflexion), et c'est celui dont dépendront le streaming et la réplication.

**Faux risques, écartés explicitement pour cadrer l'audit** : STJ **source-gen** (déjà conforme), StbImageSharp (managé pur), finalizers du `ResourceTracker`, vérification std140 par réflexion (**dans les tests** → hors périmètre AOT).

**Verdict sur Arch :**
- ✅ compile sans warning IL et tourne → **Arch validé**, on continue.
- ⚠️ warnings mais ça tourne → évaluation au cas par cas (sérialisation source-gen maison possible).
- ❌ ça casse → **on rouvre la décision ECS**, avant d'avoir écrit une ligne de moteur dessus. Le graphe de modules (§3.1) ne bouge pas.

**Inclus dans M0 — dette Phase 1 qui menace les gates de la Phase 2** : le crash rare de `GlfwEvents.Dispose` au shutdown (`AVANCEMENT.md`) **masque le rapport `ResourceTracker`** quand il frappe. Or **chaque** jalon de la Phase 2 se ferme sur un gate « 0 leak » qui lit ce rapport. À traiter (hypothèse : delegates GLFW collectés par le GC avant le dispose natif) ou à re-tracer explicitement.

### 6.2 Critère de sortie de la phase (P2-M4), chiffré

- **N ≥ 10 000 entités** instanciées, **≥ 2 000 visibles** après culling.
- Positionnées **à 10 000 km de l'origine**, en **mouvement** (translation + rotation par frame).
- **Zéro allocation managée par frame** (prouvé par l'audit `csharp-lowlevel`, comme en Phase 1).
- **0 message de validation, 0 leak.**
- « Sans trembler » = **aucun jitter de position observable** ; garanti par le test unitaire de M3 (transforms relatifs exactement égaux à ceux obtenus à l'origine).

## 7. Hors portée (explicite)

Physique · audio · gameplay · vertical slice · streaming · réplication · transfert d'autorité · interest management · scheduler parallèle maison · rendu multi-thread (command buffers secondaires) · CSM · GPU-driven rendering · uploads async · validation Linux · **dé-aplatissement de l'import glTF** (§2).

Ces sujets ne sont pas oubliés — ils sont **ordonnancés**. Le rôle de la Phase 2 est de garantir qu'**aucun ne sera condamné**.

## 8. Exécution

Session **absolute-human** (comme S1–S8) : décomposition en tâches, vagues parallèles, feu vert humain entre les étapes, TDD.

Agents : `engine-architect` (couture World/Rendering, ownership), `graphics-3d` (camera-relative, culling, shadow casters), `csharp-lowlevel` (conformité AOT, zéro-alloc, leaks — audit de sortie **sans finding critique requis**).
