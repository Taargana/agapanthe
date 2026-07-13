# Absolute-Human Board — Agapanthe Session 11 (P2-M2 : Couture ECS)

**Status**: OPEN — session ouverte 2026-07-13. Troisième jalon Phase 2. P2-M1 clos (SPIR-V hors-ligne, double audit PASS). **P2-M2 = la couture : débrancher `Scene` en deux (possession GPU → `ResourceRegistry`, monde → ECS Arch), faire passer le rendu par des handles + 2 listes en passthrough, PROUVER que rien n'a bougé (capture byte-identique M8).** C'est un **refactor pur**, pas une feature — le culling (M4) et le camera-relative (M3) sont HORS périmètre.
**Créé**: 2026-07-13
**Spec**: docs/plans/2026-07-12-phase2-foundations-design.md §3.1 (modules), §3.2 (couture : handles, pas de types GPU dans le monde), §3.4 (modèle ECS), §3.5 (chaîne de systèmes — l'ordre est un contrat ; M2 livre systèmes 1-2 + 2 listes passthrough), §3.6 (Double3), §6 P2-M2 (critère byte-identique), §6.1 (rooting AOT).
**Board persistence**: git-tracked
**Sessions passées**: S1-S10 → .absolute-human/archive/ (S10 = board-session10-P2M1.md).
**Conception d'ouverture**: passe `engine-architect` (2026-07-13) — synthèse ci-dessous.

## Intake (spec §6 P2-M2 — acté)

- **But** : refactor pur. `Scene` mélange possession GPU (`_meshes/_materials/_textures/_placeholders/_samplerCache`) ET draw list (`Instances`) + bounds figées (`BoundsMin/Max/Center/Diagonal`). On sépare : `ResourceRegistry` (possession) + `GameWorld` Arch (le monde, en handles). Le Renderer consomme 2 listes (`RenderList` + `ShadowCasterList`) en **passthrough** (aucun culling, toutes les entités, ordre stable). Le helmet est dessiné **via l'ECS**.
- **Critère de sortie** : **capture headless byte-identique à la baseline M8** (`24001B24…`), 0 message validation, 0 leak, publish NativeAOT OK — sous 2 conditions : (a) matrice monde **bakée sans aller-retour TRS**, (b) **ordre de draw stable**.
- **Le vrai risque** : le **rooting AOT** des composants Arch (§6.1) — `T[]` instanciés par voie générique non pré-générée par l'ILC → échec runtime `'T[]' is missing native code` SANS warning au publish, corruption partielle possible.

## État actuel (constaté en lisant le code — confirme le diagnostic spec)

- `Scene` (Scene.cs) = double rôle possession + draw list + bounds figées. `Renderer.DrawScene(Scene,…)` itère `scene.Instances` 2× (RecordShadowPass ~l.715-725, RecordScenePass ~l.818-831) en poussant `mesh.WorldTransform` (Matrix4x4 baké). `ComputeLightViewProj` (~l.657-671) lit `scene.BoundsCenter/BoundsDiagonal`.
- **Les handles n'existent pas encore** (seule la spec les mentionne). **Arch n'est référencé nulle part** (le probe P2-M0 était dans le scratchpad, jetable). **`Double3` n'existe pas.** `IsAotCompatible=true` déjà sur Core + Rendering.

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, **AOT-pur** (IsAotCompatible libs, warnings IL = erreurs), **aucun Vk* hors Graphics**, **aucun type Arch (`Entity`/`World`) hors du projet World**, IDisposable + DeletionQueue N+2, **zéro alloc/frame sur le hot path**, ResourceTracker (leak = échec), 0 message de validation.
- Baseline capture byte-identique : SHA256 `24001B240C6C6B956F3F1AC6ABC1FE1D2CAA8914D9013169DFB049027E3309B7`.
- Publish AOT : PATH doit inclure `C:\Program Files (x86)\Microsoft Visual Studio\Installer` (vswhere).

## Rollback Point

`5f9ab22` (P2-M1 clos, branche phase2-foundations).

## Décisions de conception (tranchées par l'architecte — à valider par les audits de sortie)

**D1 — Frontière modules (irréversible, à faire en premier)** :
```
Sandbox ─► Rendering ─► Graphics ─► Core
   │          └─► Assets ─────────► Core
   └────► World ───────────────────► Core        ◄── NOUVEAU (seul projet référençant Arch)
Platform ─► Core
World.SourceGen (generator+analyzer Roslyn, référencé par World en Analyzer)
```
| Élément | Projet | Raison |
|---|---|---|
| `MeshHandle`/`MaterialHandle`, `Double3`/`Double3Bounds`, `RenderItem`/`RenderList`, `ImportedEntitySpec` | **Core** | GPU-free. `RenderList` en Core = **le point non-évident** : permet à World de la remplir et à Rendering de la consommer **sans que Rendering référence World** (le Renderer ne connaît pas l'ECS). *Compromis de couche assumé → à valider par `engine-architect`.* |
| composants, systèmes, `GameWorld`, `ComponentRegistry` | **World** | Arch confiné ici ; types Arch ne sortent jamais de l'API publique. |
| `ResourceRegistry` | **Rendering** | reprend le rôle « propriétaire » de `Scene`. |
| générateur + analyzer de rooting | **World.SourceGen** (netstandard2.0) | Analyzer, `ReferenceOutputAssembly=false`. |

**D2 — Rooting AOT (le risque n°1)** : attribut `[Component]` = source unique. `World.SourceGen` (`IIncrementalGenerator`) émet `ComponentRegistry.RootAll()` qui, par type T, **touche les mêmes entrées génériques d'Arch que le runtime** (`Add/Remove/Get/Set/Has<T>` + **`CommandBuffer.Add<T>`**, pas seulement `new T[1]`) sur un world jetable — appelé dans le ctor de `GameWorld`. Double garde : (a) **analyzer au build** → erreur si un argument de type des méthodes du wrapper `GameWorld` n'est pas `[Component]` (fiable car TOUT accès composant passe par `GameWorld`, jamais Arch en direct) ; (b) **filet AOT** `tools/AotComponentProbe` (console PublishAot, modèle ShaderPrecompiler) qui exerce chaque composant et sort non-zéro si un `T[]` manque. **Converge avec la sérialisation source-gen future (même générateur — dette).**

**D3 — Double3 en M2 = stockage seul** : le *type* `Double3` et le stockage monde `double` arrivent en M2 (système 2 bounds `Double3` + `LocalTransform.Position` en Double3, exigés §3.4-3.5) ; le *rendu camera-relative* (soustraction à origine ≠ 0) reste **M3**. `Camera.Position` et `Lights` **restent `Vector3`** en M2 ; `WorldTransform` reste `Matrix4x4` monde absolu.

**D4 — Byte-identique** : (a) le helmet importé **ne porte pas de `LocalTransform`** → il porte sa `WorldTransform` **bakée bit-exacte** depuis `MeshAsset.WorldTransform` (le système 1 exclut les entités sans `LocalTransform`, ne recalcule jamais sa matrice) ; bounds = **fold-sommets float actuel CONSERVÉ** puis élargi en `Double3` (float→double exact, `ToVector3(Zero)` re-narrow bit-à-bit). (b) ordre de draw stable : **l'itération Arch n'est PAS stable** → composant `RenderOrder { uint }` = index mesh source, la liste est **triée** avant draw via `Span<RenderItem>.Sort(struct comparer)` (zéro-alloc, pas de boxing).

**D5 — Déviation `Bounds` assumée** : §3.4 dit « Bounds = AABB **locale** », mais en M2 transformer l'AABB locale ≠ fold-sommets → casserait le byte-identique. Donc **en M2 `Bounds` = AABB *monde* statique** (seedée par le fold-sommets), système 2 = union. La forme « AABB locale × WorldTransform » arrive en **M4** (culling). *→ à valider par `engine-architect`.*

**D6 — Ownership/zéro-alloc** : `ResourceRegistry` reprend exactement la possession + l'ordre de teardown de `Scene` (gate 0-leak préservé, mêmes ressources). `RenderList` possédée par le Renderer, buffer réutilisé (`Clear()` = `_count=0`), croissance amortie → zéro-alloc/frame. Résolution handle→ressource = indexation + garde (`GraphicsException` avec handle+contexte, §4). Vérifier que le source-gen `[Query]` d'Arch n'alloue pas/frame, sinon repli itération chunk-manuel.

## Task Graph

```
W0 ──► W1 ──┬──► W2 ──┐
            └──► W3 ──┴──► W4 ──► W5
```

## Waves

| Vague | Contenu | Critère de vérification |
|---|---|---|
| **W0** | Fondations **Core** : `Double3`, `Double3Bounds`, handles, `RenderItem`, `RenderList`, `ImportedEntitySpec`. Aucun changement de comportement. | Build 0 warning ; unitaires : précision `Double3`, **`ToVector3(Zero)` = re-narrow bit-exact**, `RenderList` croissance amortie / `Clear` sans réalloc. |
| **W1** ⚠️ *risque n°1* | Projet **World** + **World.SourceGen** ; `[Component]` + 7 composants ; `ComponentRegistry.RootAll` généré ; `GameWorld` (wrapper médiateur, seule API) ; `tools/AotComponentProbe`. | Unitaires (create/spawn/query) ; **`AotComponentProbe` publie NativeAOT et sort 0** (gate rooting) ; test négatif : analyzer met en **erreur** un composant non-`[Component]`. **→ second avis `csharp-lowlevel` obligatoire avant W2/W3.** |
| **W2** (∥ W3) | Systèmes 1 & 2 : propagation hiérarchie local→world + **détection de cycle** (chemin dans l'exception) ; agrégation bounds `Double3` (remplace `Scene.Bounds*`). GPU-free. | Tests : propagation parent×enfant, **cycle rejeté avec chemin**, union bounds, **zéro-alloc** (compteur d'allocations). |
| **W3** (∥ W2) | `ResourceRegistry` ; `SceneBuilder`→(registry, specs) avec `ComputeWorldBounds` **float conservé** ; `Renderer.DrawScene(RenderList,…)` ; boucles shadow/scene sur listes+registry ; `RenderListBuilder` (tri `RenderOrder`). | Build vert (ancien chemin `Scene` bridgé jusqu'à W4 pour comparer) ; run Debug sanity 0 validation / 0 leak. |
| **W4** | Câblage Sandbox : model → registry+specs → `SpawnImported` → par frame `PropagateTransforms`/`AggregateBounds`/`CollectRenderLists` → `DrawScene`. Suppression `Scene`/`MeshInstance`. | **Capture byte-identique M8 `24001B24…`** ; **0 validation ; 0 leak** ; **publish NativeAOT** tourne + capture identique ; re-run `AotComponentProbe` sur le jeu complet. |
| **W5** | Durcissements, docs, archive. | **`csharp-lowlevel`** (zéro-alloc query/tri/listes, rooting AOT, parité leak) **+ `engine-architect`** (couture, ownership, `RenderList` en Core, déviation `Bounds` monde) **PASS 0 critique** ; AVANCEMENT à jour. |

**Points de passage rooting AOT** : W1 (gate de vague), W4 (jeu complet), W5 (final). **Byte-identique** : W4 (seul endroit où il a du sens ; W0-W2 prouvés par unitaires, W3 garde l'ancien chemin vert).

## Tâches

### ✅ P2-M2-00 — Fondations Core [code, S] — done — OWNER Core/
`Double3` (`[StructLayout(Sequential)]`, blittable, `+/-/*`, Length, Distance, `ToVector3(Double3 origin)`, ctor widen depuis Vector3), `Double3Bounds` (Union/Center/Diagonal/**Empty** seed), `MeshHandle`/`MaterialHandle` (`readonly record struct` + `Invalid`/`IsValid`), `RenderItem` (WorldTransform + handles + `ulong SortKey`), `RenderList` (possédée Renderer : `Clear`/`Add` amorti/`Items`/`Capacity`/`Sort<TComparer struct>` zéro-boxing), `ImportedEntitySpec`. **Ajout pur, aucun câblage.**
**Vérif** : build 0 warning ; **13 unitaires verts** (dont `ToVector3(Zero)` re-narrow **bit-exact** d'un float élargi, précision camera-relative à 1e7 m, `RenderList` croissance amortie + `Clear` sans réalloc + `Sort` struct comparer). Suite complète **220 tests**.

### ✅ P2-M2-01 — World + rooting AOT [code+infra, L] — done (audit csharp-lowlevel PASS + durcissements) — OWNER src/Agapanthe.World, tools/AotComponentProbe ⚠️
Projet **`Agapanthe.World`** (Arch **2.1.0** + Core, `IsAotCompatible`, seul réf. Arch), `[Component]`, 7 composants (`GlobalId`, `LocalTransform`, `Parent`(Entity interne), `WorldTransform`, `MeshRef`, `Bounds`, `RenderOrder`), `GameWorld` (wrapper — Arch ne fuit pas : `ArchWorld` aliasé, ctor→`RootAll()`, `SpawnImported`, `Dispose`, interne `AotRootingSmoke`). `tools/AotComponentProbe` (PublishAot). Câblés dans .slnx + réf test.
**⚠️ DÉVIATION vs board (D2), assumée** : P2-M0 a **prouvé que `GC.KeepAlive(new T[1])` par composant suffit** au rooting (create/add/remove/query/CommandBuffer, cf. probe P2-M0). Donc **pas** de source-generator + analyzer Roslyn : **`ComponentRegistry` écrit à la main** (RootAll + All) **+ test de complétude par réflexion** (`ComponentRegistryTests` : tout `[Component]` ∈ `All`, sinon échec du gate de test — remplace la détection build-time de l'analyzer). Même garantie de sûreté, surface/risque bien moindres. Source-gen repoussé en Phase 3 (convergence avec le générateur de sérialisation). **À valider par l'audit `engine-architect` en W5.**
**Gate PASSÉ** : `AotComponentProbe` **publié NativeAOT (win-x64)** → `IsDynamicCodeSupported=False`, 7 composants, `AotRootingSmoke` itère 9 entités **sans `missing native code`**, **exit 0**. Build solution 0 warning, **223 tests** (+3 : complétude réflexion, pas de doublon, smoke JIT).
**✅ Audit `csharp-lowlevel` PASS** (0 critique ; gate AOT reproduit indépendamment). Durcissements appliqués + re-vérifiés (probe AOT exit 0, 223 tests, 0 warning) :
- **M1** (le résidu du risque n°1) : `All` et `RootAll` fusionnés en **une source unique** — `Root<T>()` roote `new T[1]` **et** enregistre `typeof(T)` ; impossible d'enregistrer un composant sans le rooter (fermait le trou « ajouté à All, oublié dans RootAll → crash AOT différé »).
- **M3** : composants `internal` (retire `Entity` de toute surface publique via `Parent`) + `PrivateAssets="compile"` sur Arch (le compile-time ne fuit pas aux consommateurs ; runtime conservé).
- **m1** `using` sur `CommandBuffer` ; **m3** commentaire rooting corrigé (atteignabilité de l'opcode, pas survie de l'instance) ; **m4** `[StructLayout(Sequential)]` explicite sur les 7 composants.
**CONDITIONS REPORTÉES (audit)** :
- 🔴 **Gate d'entrée W2** : dès que W2 emploie le source-gen `[Query]` d'Arch.System ou `ParallelQuery` (chemins AOT les plus fragiles, exigés §6.1/spec l.172), **étendre `AotRootingSmoke` + re-publier le probe** avant de déclarer W2 verte ; sinon repli itération chunk-manuel (D6). Guidance : queries/frame **sans lambda capturante** (struct `IForEach`, D6/m2).
- 🟡 **Avant M3 (jalon)** : le smoke exerce les types en dur — tout nouveau composant devra y être ajouté (ou rendre l'exercice piloté par `All`).

### ✅ P2-M2-02 — Systèmes 1 & 2 (+ builder de listes) [code, M] — done — OWNER GameWorld.cs, RenderList.cs [dep 01]
**Système 1** `PropagateTransforms` : world = local·parent·…·root (row-vector), walk de la chaîne `Parent`, **détection de cycle** (`WorldHierarchyException` avec le chemin des ids) sur un `_walkStack` réutilisé. Exclut les entités sans `LocalTransform` → **ne touche jamais la matrice bakée du helmet** (condition (a) byte-identique, prouvé par test). **Système 2** `AggregateBounds` : union des `Bounds` monde → `Double3Bounds` (remplace `Scene.Bounds*`) ; `Empty` si aucune entité. **Builder** `CollectRenderLists(render, shadow)` : passthrough (aucun culling, tout est drawable + caster), tri stable par `SortKey`.
**Zéro-alloc : itération par chunk** (`world.Query(in desc)` + `chunk.GetSpan<T>()`), `QueryDescription` en `static readonly`, **aucune lambda capturante** (m2 de l'audit respecté), **pas d'Arch.System `[Query]`/`ParallelQuery`** (chemins AOT les plus fragiles évités — repli chunk-manuel de D6 retenu).
**🔴 Découverte (le test de zéro-alloc a payé)** : `Span.Sort<T,TComparer>(structComparer)` **alloue ~88 B/appel** (il boxe le comparateur en `IComparer<T>` en interne) → 176 B/frame. L'hypothèse « struct comparer = zéro-boxing » du board (D4) était **fausse**. → `RenderList.SortByKey()` **écrit à la main** (insertion sort sur `SortKey`, stable, zéro-alloc, O(n) sur quasi-trié) + test de non-régression. **Dette M4** : avec 10 000 entités, remplacer par un **radix LSD** sur la clé 64-bit (O(n), scratch réutilisé) — l'insertion sort est O(n²) au pire.
**Vérif** : build 0 warning ; **233 tests** (+10 : propagation root/enfant/petit-enfant/scale, cycle avec chemin, helmet baké intact, union bounds, Empty, **zéro-alloc 0 octet sur 100 frames** pour les 3 systèmes, tri zéro-alloc). **Gate AOT (exigence audit W1) re-franchi** : `AotRootingSmoke` exerce désormais les chemins W2 (chunk iteration, walk, les 3 systèmes) → probe **NativeAOT exit 0**.

### P2-M2-03 — ResourceRegistry + split Renderer [code, L] — todo — OWNER Rendering [dep 00]
`ResourceRegistry` (possession ex-Scene, teardown ordre inverse), `SceneBuilder`→(registry, specs) fold float conservé, `Renderer.DrawScene(RenderList, RenderList, …, ResourceRegistry, …, in Double3Bounds)`, boucles sur listes+`Resolve`, `RenderListBuilder` (tri `RenderOrder` zéro-alloc).

### P2-M2-04 — Câblage Sandbox + gate byte-identique [code, M] — todo — OWNER Program.cs [dep 02,03]
Spawn ECS + collect + DrawScene ; retrait `Scene`/`MeshInstance`. **AC : capture byte-identique + 0 validation + 0 leak + publish AOT + probe complet.**

### P2-M2-05 — Double audit + archive [test, S] — todo [dep 04]
csharp-lowlevel + engine-architect PASS 0 critique ; archive → board-session11-P2M2.md ; AVANCEMENT.

## Dette (rappel P2-M0/M1 — pour mémoire)

- Sérialisation maison source-gen (Arch.Persistence NO-GO) = même générateur que le rooting, Phase 3.
- AOT prouvé Windows-only → re-prouver Linux/macOS.
- P2-M1 : pas d'assertion auto du critère §6 SPIR-V ; includes non exercés.
- CI : keyer le gate 0-leak sur la ligne de rapport, pas l'exit code.

## Log

- 2026-07-13: **W2 DONE** — systèmes 1 & 2 + builder de listes passthrough dans GameWorld (itération chunk zéro-alloc, sans lambda ni Arch.System). Cycle détecté avec chemin ; helmet baké jamais recalculé (byte-identique préservé par construction). **Le gate zéro-alloc a trouvé un vrai bug de conception** : `Span.Sort(structComparer)` alloue 88 B/appel (boxing interne) → tri maison `RenderList.SortByKey()` (dette M4 : radix pour 10k entités). `EntityRef` (handle opaque) introduit pour que les tests manipulent des entités sans compiler contre Arch. 233 tests, 0 warning, **probe AOT exit 0 avec les chemins W2**. **Non committé.** Prochain : W3 (ResourceRegistry + split Renderer).
- 2026-07-13: **W1 audit + durcissements** — `csharp-lowlevel` **PASS** (0 critique, gate AOT reproduit). Appliqués : M1 (All+RootAll source unique via `Root<T>`), M3 (composants internal + Arch `PrivateAssets="compile"`), m1 (`using` CommandBuffer), m3/m4 (doc + StructLayout). Probe AOT re-publié **exit 0**, 223 tests, 0 warning. Conditions reportées : gate W2 (couvrir `[Query]`/`ParallelQuery` dans le probe), smoke à piloter par `All` avant M3. **Non committé.** Prochain : W2 ∥ W3.
- 2026-07-13: **W1 DONE (gate AOT PASSÉ)** — projet Agapanthe.World (Arch 2.1.0), 7 composants, GameWorld (Arch confiné), ComponentRegistry + RootAll, tools/AotComponentProbe. **Déviation assumée** : registre à la main + test réflexion au lieu de source-gen+analyzer (P2-M0 a prouvé `new T[1]`/composant suffisant → surface bien moindre ; source-gen → Phase 3). Probe **NativeAOT exit 0**. 223 tests, 0 warning.
- 2026-07-13: **W0 DONE** — 6 types Core (Double3, Double3Bounds, handles, RenderItem, RenderList, ImportedEntitySpec), ajout pur sans câblage. 13 unitaires (re-narrow bit-exact, précision camera-relative, RenderList amorti/Clear/Sort). Build 0 warning, 220 tests. **Non committé.** Prêt pour W1 (World + rooting AOT — le risque n°1, second avis csharp-lowlevel requis).
- 2026-07-13: **Session 11 ouverte — P2-M2 (couture ECS).** Passe `engine-architect` faite : conception tranchée (frontière modules avec nouveau projet World + World.SourceGen ; rooting AOT source-gen + analyzer + probe ; Double3 = stockage seul en M2 ; byte-identique via bake sans TRS + tri RenderOrder ; ResourceRegistry reprend la possession de Scene). DAG 6 tâches, 6 vagues (W2∥W3). 2 déviations à valider aux audits (RenderList en Core, Bounds monde en M2). **En attente feu vert pour W0.**
