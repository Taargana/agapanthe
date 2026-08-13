# Absolute-Work Board — Agapanthe Session 26 (MP-0a : headless split)

**Status**: ✅ **CLOS.** 5 vagues + **double audit** (`csharp-lowlevel` PASS-with-concerns · `engine-architect`
**4,2/5** PASS-with-concerns, **aucun 🔴**) avec tous les findings 🟠 appliqués et la plupart des 🟡 ·
feu vert humain · CONVERGE fait · commité.

**Findings d'audit appliqués** : 🟠 `BeginFrame()` séparé de `Tick()` (sous l'accumulator de MP-0c, le bracket
ouvert dans `Tick` aurait filé **N échantillons par frame** et le gate 0-alloc à l'écran aurait cessé de mesurer une
frame) · 🟠 **trouvé par les DEUX auditeurs indépendamment** : le gate statique ne lisait que `ProjectReference`
alors que le `.csproj` interdit aussi les packages → table couvrant `Engine`/`World`/`HeadlessSim` + assertion
« aucun `PackageReference` » · 🟠 `Add(IRenderSystem)` ne gèle plus après `Tick` — **contredisait la spec**, qui est
corrigée, et l'assertion perdue de `SchedulerTests` est remplacée · 🟠 `FrameOrchestrator` **reçoit** désormais le
`SimulationHost` (une simulation existe ; un client y attache une présentation) · 🟡 `DebugOverlaySystem` prend un
`FrameStats` → **la dette de test d'UI-2 est soldée** · 🟡 4 membres forwardés morts supprimés · 🟡 commentaires
périmés de `Program.cs` (« aggregate bounds ») et **commentaire menteur** de `HeadlessSim.csproj` qui promettait un
gate inexistant · 🟡 `HeadlessSim` : chemins vides, `--bodies` contradictoire avec `--load`.

**Non appliqués, volontairement** : renommage `Engine.Render` → `Engine.Presentation` (décision humaine déjà prise à
la revue) · `FrameIndex` → `TickIndex` (appartient à MP-0c, qui redéfinira ce qu'est un index de tick).

**Spec** : [`docs/plans/2026-08-13-mp0a-headless-split-design.md`](../docs/plans/2026-08-13-mp0a-headless-split-design.md)
— **APPROVED 4,15/5** (2 tours de revue scorée ; v1 notée 2,85 « Major Gaps »). **Ne pas re-litiger ses décisions.**

## But

Premier des **4 sous-jalons** de MP-0. Rendre `Agapanthe.Engine` exécutable **sans GPU**, pour que le cap
serveur-autoritaire (« la topologie est un choix de déploiement, jamais d'architecture ») cesse d'être faux dans le
graphe de build. **L'ordre inverse celui du backlog** : les deux 🔴 identité ne sont pas cassés aujourd'hui (la clé
de contact est correcte tant que les ids sont denses, et ne devient fausse qu'au moment du partitionnement), alors
que le coût du split est **strictement croissant** avec la taille d'`Engine`.

## Vagues

**W1 — Gardes d'abord** ✅ · écrites contre `HEAD`, **aucun changement structurel**.
`RenderStageNeutralityTests` (parité état simulé avec/sans stage Render, via snapshot VS-1) ·
`RenderBarrierTests` (barrière post-Render — **le stage Render n'avait AUCUN test avant ce jalon**).
**Mutation-testées** : un système render qui enfile un spawn → FAIL ; barrière post-Render supprimée → 4 FAIL.

**W2 — Extraire `SimulationHost`** ✅ · **aucun `.csproj` touché**. `FrameOrchestrator` compose et délègue.
`AggregateBoundsSystem` + `_sceneBounds` **supprimés** (écrits chaque frame, lus par personne depuis P3-M5 ; un
commentaire périmé les a fait survivre 11 sessions). Captures inchangées.

**W3 — Split du projet** ✅ · `Agapanthe.Engine.Render` créé ; `Engine` perd `Rendering`+`Graphics`.
`RenderSystemScheduler` prend la barrière **directement** de `FrameOrchestrator` → aucun `InternalsVisibleTo` entre
les deux assemblies moteur. `Program.cs` gagne **un `using`**, rien d'autre.

**W4 — Gates + host** ✅ · 3 gates dans `EngineIsHeadlessTests` · `samples/HeadlessSim` NativeAOT.

**W5 — Tail** 🟠 EN COURS · self-review faite (`using` redondants retirés) · double audit lancé · reste findings +
verdict humain + CONVERGE.

## Gates mesurés

**483 tests** · 0 warning · **HDR `12638eddd7f3f67ab161b298ffbcd15e`** et **UI `034213575932dabcff41c2e0c72addfa`
inchangés** · 0 leak (233 resources) · 0 validation · **AOT PASS** · **`HeadlessSim.exe` 1,68 Mo, exit 0,
0 B/frame** · **snapshot JIT == AOT** (`7c889fec0df503fe8137ef6c28c7751a`) ·
**closure `Agapanthe.Engine` = {Core, World}**.

> ⚠️ **Invocation EXACTE des captures** (une omission a produit deux faux négatifs pendant W2) :
> `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12`, 1280×720,
> Debug. **`AGAPANTHE_DROP_EVERY` vaut 30 par défaut** — l'oublier change la scène et donc les deux hashes.
>
> **AOT publish** : préfixer le PATH avec le dossier de **`vswhere.exe`** lui-même
> (`/c/Program Files (x86)/Microsoft Visual Studio/Installer`), pas avec le dossier MSVC.

## Ce que le jalon a démontré, au-delà des tests verts

**Le double gate d'architecture est justifié empiriquement.** Mutation : ré-ajouter `ProjectReference Rendering` à
`Agapanthe.Engine` **sans utiliser aucun type** → le test **statique** (lit le `.csproj`) FAIL, le test de **closure
d'assemblies** reste VERT, parce que le compilateur élide la référence inutilisée. Sans le statique, l'échec serait
survenu au commit *suivant* et aurait été imputé au mauvais changement.

## Dette déclarée / décisions au dossier

- 🟠 **`CurrentTick.FrameIndex` est post-incrément** : un système render voit `N+1` là où les systèmes tick de la
  même frame ont vu `N` (`SystemScheduler` incrémente après les stages). **Comportement existant préservé
  bit-pour-bit**, épinglé par un test, à trancher au sous-jalon *autorité du temps*. Sans conséquence aujourd'hui :
  `RenderContext.Tick` n'a aucun consommateur.
- 🟠 **Changement de comportement déclaré** : `_frozen` n'est plus partagé — un `Render` sans `Tick` préalable ne gèle
  plus l'enregistrement des `ISystem`. Inatteignable dans un hôte réel (`Tick` précède toujours `DrawFrame`),
  couvert par `ARenderBeforeAnyTick_NoLongerFreezesSimulationRegistration`.
- 🟡 `Camera` reste dans `Rendering` (déplacement vers `Core` = scope creep, explicitement refusé).
- 🟡 `HeadlessSim` construit sa scène en dur — le jalon *contenu* la remplacera par des données.

## Prochains sous-jalons MP-0 (specs à écrire, ordre à confirmer)

**MP-0b identité** (🔴 `GlobalId` 64 bits partitionné + clé de contact) · **MP-0c autorité du temps** (accumulator ;
⚠️ invalide les baselines de capture) · **MP-0d input → commandes horodatées**.

## Conventions du projet (rappel)

.NET 10 · `TreatWarningsAsErrors` · aucun type `Vk*` hors `Agapanthe.Graphics` · aucun type Arch hors
`Agapanthe.World` · **`Agapanthe.Engine` ne doit référencer que Core + World** (3 tests l'imposent).
**Gates bloquants** : 0 warning · 0 message de validation · 0 leak · 0 alloc/frame · NativeAOT PASS · double audit ·
verdict humain. **Commits/push sur demande explicite UNIQUEMENT.**

## Clôture (CONVERGE)

Findings appliqués · verdict humain · maj `AVANCEMENT`/`BACKLOG`/`CLAUDE` (+ **nettoyer `BACKLOG.md` §0**, périmé de
11 sessions : le *scheduler de systèmes* et *`UpstreamExtent` bounds globales* y sont encore 🔴 alors qu'ils sont
soldés depuis P3-M2) · board archivé `archive/board-session26-MP0a.md` · **commit sur demande**.
