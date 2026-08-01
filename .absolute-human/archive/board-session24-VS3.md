# Absolute-Work Board — Agapanthe Session 24 (VS-3 : défi d'atterrissage planétaire)

**Status**: ✅ **CLOS (session 24).** Wave 1, 2, 3 toutes closes. Double audit **PASS** (`csharp-lowlevel`) /
**PASS-with-concerns 4/5** (`engine-architect`), findings appliqués ; verdict humain PASS ; CONVERGE fait (docs à jour,
board archivé `archive/board-session24-VS3.md`) ; commit sur demande.
Design + spec **approuvés** (brainstorm → revue scorée **4.30/5 Approved**). Rollback point : `09637b5` (VS-2 poussé).
**Sessions passées** : S1–S23 → `archive/` (S23 = VS-2, clos + poussé `09637b5`).

### ✅ VS-3-05/06 (Wave 3) — fait
- **Double audit** : `csharp-lowlevel` PASS (0 🔴/🟠, 4 🟡) · `engine-architect` PASS-with-concerns 4/5 (1 🟠 A1 + 2 🟡).
- **Findings appliqués** : A2 (constantes de scène épinglées dans les 2 profils `planet-challenge`) · `F5` I/O gardée
  (`try/catch IOException`) · A1 (latch non-monotone au reload) + A3 (log non ré-émis) + churn frictionless =
  réconciliés en **dette documentée** (spec §Debts + `CLAUDE.md`).
- **Vérif finale** : 372 tests · 0 warning · `planet-challenge` headless 0 validation / 0 leak (226 resources) · NativeAOT PASS.

### ⏸️ REPRISE (prochaine session) — où repartir

**Fait (NON commité — commit sur demande uniquement) :**
- **Wave 1 CLOSE** (fondations pures, GPU-free, TDD) :
  - **VS-3-01** : `LandingCounts(Total, Airborne, InZone)` (`src/Agapanthe.World/LandingCounts.cs`) +
    `GameWorld.QuerySurfaceContacts(...)` (`GameWorld.Physics.cs`) — requête spatiale 0-alloc, Arch scellé, « posé=sur la surface ».
  - **VS-3-02** : `LandingStatus` + `LandingChallengeRule.Evaluate(counts, shotsIssued, prev)` **latchée** (`src/Agapanthe.Engine/LandingChallengeRule.cs`).
  - Tests : `SurfaceContactsTests.cs` (7) + `LandingChallengeRuleTests.cs` (10).
- **Wave 2 CLOSE** (Sandbox + AOT) :
  - **VS-3-03** : scène `AGAPANTHE_SCENE=planet-challenge` (`SetupPlanetChallenge` + `LandingChallengeSystem` PostSim +
    `B`→`TryShoot` radial + `F5` quicksave + généralisation load + `FramePlanetChallengeCamera` + 2 profils Rider).
  - **VS-3-04** : touch `QuerySurfaceContacts` dans `AotRootingSmoke` (`GameWorld.cs`).
- **Preuves** : **372 tests** · 0 warning · scène `planet-challenge` headless **0 validation / 0 leak** (229 resources) ·
  beacon-cible cadré au départ.

**Fichiers non commités** : `M` `GameWorld.Physics.cs`, `GameWorld.cs`, `Program.cs`, `launchSettings.json`, `board.md` ·
`??` `LandingCounts.cs`, `LandingChallengeRule.cs`, `SurfaceContactsTests.cs`, `LandingChallengeRuleTests.cs`,
`docs/plans/2026-07-26-vs3-landing-challenge-design.md`.

**Reste — Wave 3 (tail) :**
1. **VS-3-05** double audit (`csharp-lowlevel` + `engine-architect`) + application des findings (dernier `go` humain était pour lancer la Wave 3).
2. **VS-3-06** self review + requirements validation + full verification + **verdict humain interactif** + CONVERGE
   (maj AVANCEMENT/BACKLOG/CLAUDE, archive board `archive/board-session24-VS3.md`, commit sur demande).

**Lancer la démo** (verdict) : profil Rider `planet-challenge (VS-3)` ou
`$env:AGAPANTHE_SCENE="planet-challenge"; dotnet run --project samples/Sandbox -c Debug`. Voler (ZQSD/WASD+Shift) au-dessus
du beacon vert, **`B`** largue une probe radiale, poser 3 dans la zone en ≤ 6 tirs, **`F5`** sauve → relaunch profil
`planet-challenge (resume save)` pour reprendre. Résidus revue à traiter en impl : `surfaceBand` serré (feel), `TryShoot`
lit `_status` (1 tir possible juste après win, inoffensif).

**But** : livrer la **glu de gameplay minimale** de la Vertical Slice — un défi d'atterrissage planétaire câblant
input → spawn (VS-2) → règle d'état → save/resume (VS-1). Le joueur vole au-dessus d'une planète, largue des probes
(radial sous la caméra) vers une zone-cible ; poser **N** en **≤ M** tirs, sinon échec. `F5` sauve, relaunch `AGAPANTHE_LOAD`
reprend. Thin : aucun nouveau chemin de rendu (HUD in-view = VS-4).
**Spec** : [../docs/plans/2026-07-26-vs3-landing-challenge-design.md](../docs/plans/2026-07-26-vs3-landing-challenge-design.md)

## Décisions verrouillées (spec §Locked)

- **Boucle** : poser N probes dans la zone en ≤ M tirs, sinon échec.
- **Visée** : largage radial sous la caméra (`n̂ = normalize(camPos − C)`), chute radiale (gravité VS-2).
- **Save/load** : relaunch-only (`F5` save ; `AGAPANTHE_LOAD` reload).
- **État reconstructible aux frontières de load** : `_shotsIssued` compteur autoritaire re-semé du body count monde ;
  `landed`/`airborne` dérivés d'une requête monde ; statut **latché** re-évalué au load. Zéro octet ajouté à VS-1.
  Précondition resume : mêmes constantes de scène (N, M, cible, μ, rayons) — figées dans le profil.
- **Posé = SUR LA SURFACE** (pas de seuil de vitesse → robuste au glissement sans frottement). `Lost` exige `Airborne==0`.
- **Gate** : nouvelle scène `AGAPANTHE_SCENE=planet-challenge` (laisse `planet-drop`/capture VS-2 intacts) + tests
  unitaires GPU-free (requête + règle) + verdict humain interactif. Pas de capture headless déterministe (input humain).

## Project Conventions (rappel)

.NET 10, `dotnet build` / `dotnet test`, `TreatWarningsAsErrors`. Aucun type Arch hors `Agapanthe.World`. Aucun `Vk*`
hors `Agapanthe.Graphics`. System.Numerics row-vector. NativeAOT-pur (dispatch switch concret, pas de réflexion hot path).
Gates bloquants : 0 warning · 0 message de validation · 0 leak ResourceTracker · 0 alloc/frame régime stable.
Env probe : `AGAPANTHE_MAX_FRAMES=N`, `AGAPANTHE_CAPTURE=out.ppm`. AOT publish : préfixer PATH avec le VS Installer
(`vswhere.exe`). Board archivé par session dans `.absolute-human/archive/`.

## Tâches (DAG + vagues)

> **Wave 1 ✅ · Wave 2 ✅ · Wave 3 ⏳** (détail statut dans « REPRISE » ci-dessus).

### Wave 1 — Fondations pures (GPU-free, TDD) — parallèle-safe (fichiers disjoints) — ✅ CLOSE

**VS-3-01** · `code`+`test` · M · deps: — · fichiers: `src/Agapanthe.World/GameWorld.Physics.cs`, `tests/Agapanthe.Tests/*`
`LandingCounts(int Total, int Airborne, int InZone)` (record struct) + `GameWorld.QuerySurfaceContacts(Double3 C,
double R, double band, Double3 zoneC, double zoneR)` : itère `BodyDesc`, 0-alloc, aucun type Arch ne fuit. `Airborne`
si `|p−C|−r > R+band` ; `InZone` si on-surface **et** `|p−zoneC| ≤ zoneR` (pas de seuil vitesse). Tests : on-surface
in/out zone, airborne au-dessus zone, corps rebondissant juste au-dessus de `band` = Airborne. Réutilise `SpawnBody`/`BodyAt`.

**VS-3-02** · `code`+`test` · S · deps: — · fichiers: `src/Agapanthe.Engine/LandingChallengeRule.cs` (nouveau), `tests/*`
`enum LandingStatus { InProgress, Won, Lost }` + `readonly struct LandingChallengeRule(int N, int M)` avec
`Evaluate(LandingCounts c, int shotsIssued, LandingStatus prev)` : **latché** (prev Won/Lost renvoyé inchangé) ; Won si
`InZone ≥ N` ; Lost si `shotsIssued ≥ M && Airborne==0 && !Won` ; sinon InProgress. Tests table : InProgress, Won,
Lost conditionnel, borne dernier tir en vol (`Airborne>0` → InProgress), latch (régression counts ≠ dé-win), `Total==0`.

### Wave 2 — Intégration Sandbox + AOT — parallèle-safe (Program.cs vs GameWorld.cs) — ✅ CLOSE

**VS-3-03** · `code` · M · deps: VS-3-01, VS-3-02 · fichiers: `samples/Sandbox/Program.cs`, `Sandbox/Properties/launchSettings.json`
`SetupPlanetChallenge(...)` (jumeau `SetupPlanetDrop` : planète+Soleil, `WithAttractor`, probe spec, cible `T=C+t̂·R` +
beacon émissif drawable non-body). `LandingChallengeSystem : ISystem` (PostSimulation) : query → `Evaluate` → titre
**reconstruit seulement si `(InZone, _shotsIssued, status)` change** (0-alloc régime stable) → Log à la transition.
`_shotsIssued` semé du body count (construction + post-Load), `_status` re-évalué. Input : `B`→`TryShoot(camPos)`
gardé par `_shotsIssued < M` (pas la query) ; `F5`→`world.Save` (flush intégré VS-1) ; scène `planet-challenge` ;
généraliser le mode load (`AGAPANTHE_LOAD` + `planet-challenge`) ; caméra `FramePlanetDropCamera` départ ; profil Rider.

**VS-3-04** · `code` · S · deps: VS-3-01 · fichier: `src/Agapanthe.World/GameWorld.cs` (AotRootingSmoke)
Ajouter **inconditionnellement** un touch de `QuerySurfaceContacts` à `AotRootingSmoke` → rooter la nouvelle
chunk-query publique sous ILC. Vérifié par la probe NativeAOT existante.
*(fichier disjoint de VS-3-03 → parallèle-safe.)*

### Wave 3 — Audits + verdict (tail tasks) — ⏳ EN ATTENTE

**VS-3-05** · double audit `csharp-lowlevel` (alloc cachée titre/query, 0-alloc régime stable, cast, leak beacon) +
`engine-architect` (étanchéité Arch de la query, barrière/stage PostSim, layering règle, resume/re-seed, monotonie du
latch). Appliquer findings 🔴/🟠.

**VS-3-06** · tail — **Self review** (diff) · **Requirements validation** (spec §DoD : N/M/cible, radial drop, F5+relaunch,
état reconstruit) · **Full verification** : `dotnet build` (0 warning) + `dotnet test` (verts) + Sandbox `planet-challenge`
(0 validation, 0 leak) + save/resume round-trip + probe AOT PASS + **verdict humain interactif**.

## DAG

```
VS-3-01 ─┬─► VS-3-03 ─┐
         ├─► VS-3-04 ─┤
VS-3-02 ─┘            ├─► VS-3-05 ─► VS-3-06
                      ┘
Wave 1 (∥ safe)   Wave 2 (∥ safe)   Wave 3 (tail)
```

## Rollback Point
Avant que Wave 1 touche un fichier : commit `09637b5` (arbre propre, VS-2 poussé). Spec non-code déjà présente (untracked).

## Deferred Work
Hors-scope (spec §Debts) : quickload en-process · HUD in-view (VS-4) · audio (VS-5) · prefabs/pooling · multi-cible/niveaux ·
score continu · état non-dérivé persisté · lifetime/rest-cull des corps (dette VS-2) · glissement sans frottement (posé=surface).
Résidus revue à traiter en impl : `surfaceBand` serré (feel) · `TryShoot` lit `_status` (1 tir possible juste après win, inoffensif).

## Clôture (CONVERGE)
Double audit findings appliqués · verdict humain · maj AVANCEMENT/BACKLOG/CLAUDE · board archivé
`archive/board-session24-VS3.md` · commit sur demande.
