# Absolute-Work Board — Agapanthe Session 28 (MP-0c : autorité du temps)

**Status**: ✅ **VERIFY CLOSE — toutes vagues + audits + docs + tail faits. En attente verdict humain → CONVERGE.**
Spec : `docs/plans/2026-09-05-mp0c-time-authority-design.md` (APPROVED 4,4/5, 2 tours de revue scorée).
Parcours : design présenté (S28) → approuvé → spec v1 (3,7/5) → v2 (F1-F9) → revue round 2 (4,4/5, R1-R3
corrigés) → revue humaine OK → **décomposition**.

## Rollback Point

`0307439` (`docs(backlog): mark MP-0b's two identity items delivered`). Arbre propre côté `src/` avant Wave 1.
Fichiers déjà modifiés hors code : `.absolute-human/board.md`, `CLAUDE.md`, `docs/AVANCEMENT.md` (docs de session),
`docs/plans/2026-09-05-mp0c-time-authority-design.md` (nouveau, la spec).

## Project Conventions

.NET 10 · `TreatWarningsAsErrors` (0 warning = gate) · xUnit (`tests/Agapanthe.Tests/`) · `dotnet build` /
`dotnet test` · NativeAOT probe `tools/AotComponentProbe` · aucun type `Vk*` hors `Agapanthe.Graphics` · aucun type
Arch hors `Agapanthe.World` · **`Agapanthe.Engine` ne référence que `{Core, World}`** (`EngineIsHeadlessTests`).
**Gates bloquants** : 0 warning · 0 validation layer · 0 leak ResourceTracker · 0 alloc/frame hot path · NativeAOT
PASS · double audit (`csharp-lowlevel` + `engine-architect`) · verdict humain. **Commits/push sur demande explicite
UNIQUEMENT.** Conversation FR, code/commits/docs EN. Vagues + feu vert humain entre chaque.

> **Captures** (protocole) : `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0
> AGAPANTHE_DROP_EVERY=12`, 1280×720, Debug, `AGAPANTHE_CAPTURE` + `AGAPANTHE_CAPTURE_UI`. **Attendu MP-0c : hashes
> INCHANGÉS** — HDR `12638edd`, UI `03421357` (F2 : mode capture = 1 tick/frame, aucun code prod ne lit
> `ctx.DeltaSeconds` → byte-identique). `AGAPANTHE_DROP_EVERY` défaut 30 — l'omettre change la scène.
> **HeadlessSim** : `7e8dc68f5a25914c84677a7a53ad3a58` (MD5, 1868 o) inchangé, JIT == AOT.
> **AOT publish** : PATH préfixé du dossier `vswhere.exe` (`/c/Program Files (x86)/Microsoft Visual Studio/Installer`).

---

## Dependency graph

```
Wave 1 (spec W1) — FixedTimestepAccumulator, isolé
  AW-101 (test) ──► AW-102 (code)

Wave 2 (spec W2) — câblage + rename + off-by-one
  AW-201 (rename FrameIndex→TickIndex, atomique)
     ├──► AW-202 (CurrentTick off-by-one + RenderStageNeutrality:108 code + docs)
     │       └──► AW-203 (FrameOrchestrator: accumulator + LastFrameTickCount)   [aussi ◄── AW-102]
     │               ├──► AW-205 (Sandbox Program.cs: dt capture + bench log)
     │               └──► AW-207 (test: équivalence tick-count + catch-up FrameOrchestrator-shape)
     ├──► AW-204 (PhysicsSystem.RatesMatch + Debug.Assert)
     │       └──► AW-208 (test: RatesMatch)
     └── AW-202 ──► AW-206 (test: CurrentTick last-executed, TickContext inside-tick-N)

Wave 3 (spec W3) — captures + tail
  AW-301 (verify: captures ×3 inchangées + HeadlessSim hash + AotComponentProbe)   [◄── toute Wave 2]
     └──► AW-302 (double audit csharp-lowlevel + engine-architect)
             ├──► AW-303 (docs: CLAUDE.md + AVANCEMENT.md + BACKLOG.md)
             └──► AW-304 (tail: self code review du diff)
                     └──► AW-305 (tail: requirements validation vs DoD)
                             └──► AW-306 (tail: full project verification)
```

### Execution waves (safety pass)

| Wave | Tasks | Parallélisme |
|---|---|---|
| **1** | AW-101 → AW-102 | séquentiel (TDD, même paire) |
| **2a** | AW-201 | seul (touche 9 fichiers : rename cross-cutting) |
| **2b** | AW-202 → AW-203 → AW-205 ; AW-204 en parallèle | AW-202/203/205 partagent `SimulationHost.cs`/`FrameOrchestrator.cs` → série ; AW-204 (`PhysicsSystem.cs`) disjoint → parallèle |
| **2c** | AW-206, AW-207, AW-208 | fichiers de test neufs disjoints → parallèle |
| **3** | AW-301 → AW-302 → {AW-303, AW-304 → AW-305 → AW-306} | séquentiel (verify/audit/docs/tail) |

---

## Tasks

### Wave 1 — `FixedTimestepAccumulator`, isolé (spec §Architecture 1, §Waves W1)

#### AW-101 — Tests du `FixedTimestepAccumulator` `[test, M]`
- **Deps** : —
- **Fichier** : `tests/Agapanthe.Tests/FixedTimestepAccumulatorTests.cs` (nouveau)
- **Cas** (spec §Testing strategy, lignes W1) :
  1. `Advance` compte juste : `dt = 5 * acc.FixedDeltaSeconds` → retourne 5 ; `dt = 0.5 * fixed` → 0, reste porté ;
     `Advance(0.5*fixed)` suivant → 1. **Construire depuis `acc.FixedDeltaSeconds`, jamais `n/60f`** (F1).
  2. Clamp d'entrée : `Advance(host, 10f)` sous plafond 250 ms → ≤ 15 ticks (14 exact).
  3. Validation ctor : `fixed <= 0` et `max < fixed` → `ArgumentOutOfRangeException`.
  4. NaN/négatif ne corrompt pas : après `Advance(host, NaN)`, `Advance(host, fixed)` retourne encore 1 (F6).
  5. 0-alloc après warmup : boucle d'`Advance`, `GC.GetAllocatedBytesForCurrentThread` delta == 0.
- **Host de test** : `SimulationHost.CreateDefault(new GameWorld())` (GPU-free), ou un compteur d'appels minimal si
  suffisant — mais `Advance(SimulationHost, float)` prend le vrai type, donc host réel.
- **Acceptation** : fichier compile une fois AW-102 fait ; les 5 cas rouges avant AW-102, verts après.

#### AW-102 — `FixedTimestepAccumulator` `[code, S]`
- **Deps** : AW-101
- **Fichier** : `src/Agapanthe.Engine/FixedTimestepAccumulator.cs` (nouveau)
- **API** (spec §Architecture 1) : `sealed class`, ctor `(float fixedDeltaSeconds, float maxWallClockDeltaSeconds =
  0.25f)`, props `FixedDeltaSeconds`, `MaxWallClockDeltaSeconds`, `int Advance(SimulationHost host, float
  wallClockDeltaSeconds)`.
- **Sanitisation `Advance` — ordre impératif (F6)** :
  ```csharp
  if (!float.IsFinite(dt))      dt = MaxWallClockDeltaSeconds;
  else if (dt < 0f)             dt = 0f;
  else                          dt = MathF.Min(dt, MaxWallClockDeltaSeconds);
  ```
- **Boucle** : `_acc += dt; int n = 0; while (_acc >= FixedDeltaSeconds) { _acc -= FixedDeltaSeconds; host.Tick(FixedDeltaSeconds); n++; } return n;`
- **Ctor validation** : `fixedDeltaSeconds > 0`, `maxWallClockDeltaSeconds >= fixedDeltaSeconds`, sinon
  `ArgumentOutOfRangeException` nommant le paramètre. `Debug.Assert` sur input non-fini/négatif dans `Advance`.
- **0-alloc** : pas de delegate, pas de closure, `_acc` champ `float`.
- **Acceptation** : AW-101 vert · `dotnet build` 0 warning · `EngineIsHeadlessTests` toujours vert (closure
  `{Core, World}` — le type ne référence que `SimulationHost`).

---

### Wave 2 — câblage + rename + off-by-one (spec §Architecture 2-5, §Waves W2)

#### AW-201 — Rename `FrameIndex` → `TickIndex` (atomique) `[code, M]`
- **Deps** : —
- **Fichiers** (spec §Measured blast radius — liste exhaustive vérifiée, ne pas re-chercher) :
  - `src/Agapanthe.Engine/SystemScheduler.cs:37,50,103,116` — `_frameIndex`→`_tickIndex`, `FrameIndex`→`TickIndex`
  - `src/Agapanthe.Engine/Systems.cs:13-26` — `TickContext` : param ctor `frameIndex`→`tickIndex`, prop `FrameIndex`→`TickIndex`
  - `src/Agapanthe.Engine/SimulationHost.cs:58-59` — forwarding prop `FrameIndex`→`TickIndex` (garder `CurrentTick`
    tel quel ici — l'off-by-one c'est AW-202)
  - `samples/HeadlessSim/Program.cs:114` — `host.FrameIndex`→`host.TickIndex`
  - `tests/Agapanthe.Tests/SchedulerTests.cs:64-92` — `FrameIndex`→`TickIndex` (`:91` commentaire reste vrai)
  - `tests/Agapanthe.Tests/HeadlessSimulationTests.cs:118` — `host.FrameIndex`→`host.TickIndex`
  - `tests/Agapanthe.Tests/RenderBarrierTests.cs:28-29,61,108,114-118` — param `frameIndex`, `tick.FrameIndex`→`TickIndex`
  - `tests/Agapanthe.Tests/RenderStageNeutralityTests.cs:106,108,187` — `scheduler.FrameIndex`→`TickIndex`,
    `ctx.FrameIndex`→`TickIndex` (le **code** de la valeur passée c'est AW-202 ; ici rename seul)
- **Hors périmètre** : `DeletionQueueTests.cs` (`completedFrameIndex:` = param `GraphicsDevice`, sans rapport).
- **Acceptation** : rename mécanique pur, 0 changement de comportement · `dotnet build` 0 warning · `dotnet test`
  intégralement vert (aucune assertion ne change) · captures inchangées (rien ne touche un pixel).

#### AW-202 — `CurrentTick` : dernier tick exécuté + `RenderStageNeutrality:108` `[code, S]`
- **Deps** : AW-201
- **Fichiers** : `src/Agapanthe.Engine/SimulationHost.cs` · `tests/Agapanthe.Tests/RenderStageNeutralityTests.cs`
- **Changement** :
  - `SimulationHost.cs:71` : `CurrentTick => new(_dt, Math.Max(0L, _scheduler.TickIndex - 1))` (`0L` pour la clarté).
  - Doc `:61-70` réécrite : « post-increment délibérément préservé » → « le dernier tick réellement exécuté »,
    remplacer la ligne périmée *"Pinned by `RenderBarrierTests`"* par le nom du test AW-206.
  - `RenderStageNeutralityTests.cs:108` : le **code** passe à
    `new TickContext(Dt, Math.Max(0L, scheduler.TickIndex - 1))` (F5 — pas juste le commentaire `:105-106`) pour que
    le test reflète toujours `FrameOrchestrator.cs:84`.
- **Acceptation** : `dotnet test` vert (dont `RenderStageNeutralityTests`, `RenderBarrierTests`) · captures
  inchangées (F2 : aucun système de rendu ne lit `ctx.Tick.TickIndex` — vérifié 2 fois en revue).

#### AW-203 — `FrameOrchestrator` : accumulator + `LastFrameTickCount` `[code, M]`
- **Deps** : AW-102, AW-202
- **Fichiers** : `src/Agapanthe.Engine.Render/FrameOrchestrator.cs` · `src/Agapanthe.Engine/SimulationHost.cs`
- **`FrameOrchestrator`** (spec §Architecture 2) :
  - Champ `_accumulator` construit dans le ctor privé (`:70`) depuis `fixedTickRate`, `maxWallClockDt`.
  - Les **deux** `CreateDefault` (`:95-97`, `:108-122`) gagnent `float fixedTickRate = 1f/60f, float maxWallClockDt
    = 0.25f` en fin de signature.
  - `Tick(float wallClockDeltaSeconds)` : `_simulation.BeginFrame(); _accumulator.Advance(_simulation, wallClockDeltaSeconds);`
    (valeur de retour non utilisée ici).
  - Prop `public float FixedTickDeltaSeconds => _accumulator.FixedDeltaSeconds;`
- **`SimulationHost`** (F8) : `int LastFrameTickCount { get; private set; }` — champ remis à 0 dans `BeginFrame()`,
  `++` dans `Tick()`. `FrameOrchestrator` forwarde : `public int LastFrameTickCount => _simulation.LastFrameTickCount;`
- **Acceptation** : `dotnet build` 0 warning · tests existants verts · `EngineIsHeadlessTests` vert · captures
  inchangées (mode capture = 1 tick/frame).

#### AW-204 — `PhysicsSystem.RatesMatch` + `Debug.Assert` `[code, S]`
- **Deps** : AW-201
- **Fichier** : `src/Agapanthe.Engine/PhysicsSystem.cs`
- **Changement** (spec §Architecture 4, R3) :
  ```csharp
  internal static bool RatesMatch(float tickDeltaSeconds, float fixedDt) => tickDeltaSeconds == fixedDt;

  public void Execute(in TickContext ctx)
  {
      Debug.Assert(RatesMatch(ctx.DeltaSeconds, _settings.FixedDt),
          "PhysicsSystem's fixed step and the accumulator's tick rate have drifted apart — configure them to match.");
      _world.StepPhysics(in _settings);
  }
  ```
  Remark `:12-16` réécrite : `DeltaSeconds` n'est plus « deliberately ignored » — il est *vérifié*.
- **Acceptation** : `PhysicsSettings` inchangé · les 3 sites qui construisent `PhysicsSystem` (`HeadlessSimulationTests
  :44`, `RenderStageNeutralityTests:91`, `HeadlessSimSnapshotFormatTests:45`) + les 3 du Sandbox utilisent tous
  `1f/60f` → assert ne casse rien (vérifié en revue) · `dotnet test` vert.

#### AW-205 — Sandbox : sélection dt capture + ligne bench `[code, S]`
- **Deps** : AW-203
- **Fichier** : `samples/Sandbox/Program.cs`
- **Changement** (spec §Architecture 5, R1) :
  - `window.Rendered` (`:766`) : `var captureMode = maxFrames > 0; var wallClockDt = captureMode ?
    orchestrator.FixedTickDeltaSeconds : (float)dt; orchestrator.Tick(wallClockDt);` (`> 0`, pas `>= 0` — cohérent
    avec `:797`/`:803`).
  - Ligne de log bench (`:787-790`) : appendre `LastFrameTickCount` (ex. `ticks {orchestrator.LastFrameTickCount}`).
- **Acceptation** : `dotnet build` 0 warning · Sandbox headless tourne 0 validation / 0 leak · captures inchangées.

#### AW-206 — Tests : `CurrentTick` last-executed + `TickContext` inside-tick-N `[test, S]`
- **Deps** : AW-202
- **Fichier** : `tests/Agapanthe.Tests/SchedulerTests.cs` (ajout) ou `HeadlessSimulationTests.cs`
- **Cas** :
  1. `CurrentTick` reporte le dernier tick exécuté : host frais → `CurrentTick.TickIndex == 0` ; après 1 `Tick` →
     `0` ; après 3 `Tick` → `2`.
  2. `TickContext` vu *dans* le tick N a `TickIndex == N` (vue pré-increment inchangée — un système en tick 0 voit 0,
     en tick 1 voit 1).
- **Acceptation** : vert · c'est le test nommé dans la doc de `CurrentTick` (AW-202).

#### AW-207 — Tests : équivalence tick-count + catch-up FrameOrchestrator-shape `[test, M]`
- **Deps** : AW-203
- **Fichier** : `tests/Agapanthe.Tests/AccumulatorEquivalenceTests.cs` (nouveau), modelé sur
  `ContactResolutionOrderTests.cs:38-61` (`[Collection("World")]`, scène asymétrique multi-corps, `SpawnBody` public,
  `GetWorldPosition`).
- **Cas** (spec §Testing strategy + « The equivalence test, spelled out ») :
  1. **Équivalence tick-count (primaire, entiers)** : profil A = 20× `Advance(host, 3 * acc.FixedDeltaSeconds)`,
     profil B = 60× `Advance(host, 1 * acc.FixedDeltaSeconds)`. Somme des retours d'`Advance` → **60 == 60**.
  2. **Équivalence positions (secondaire, garde de câblage)** : mêmes profils, positions finales **bit-à-bit
     identiques** (`Assert.Equal`, pas de tolérance).
  3. **Sensibilité au nombre de pas (compagne)** : 3ᵉ profil totalisant 30 ticks → position finale **différente**
     des runs 60 ticks.
  4. **Catch-up FrameOrchestrator-shape (F3)** : rejouer `host.BeginFrame(); acc.Advance(host, dt); … host.EndFrame()`
     sur un profil avec une frame `dt = 4 * fixed` parmi des frames `dt = fixed`. Assert : (a) `host.Stats.FrameCount`
     == nombre de frames (pas de ticks) ; (b) `host.LastFrameTickCount` == 4 sur la frame lente, 1 ailleurs ;
     (c) total de ticks correct.
- **Host** : `SimulationHost.CreateDefault(world)` + `host.Add(Stage.Simulation, new PhysicsSystem(world, settings))`,
  `Advance` pilote `host.Tick`. Vrai chaînage accumulateur → host → système.
- **Acceptation** : vert · les 4 groupes d'assertions passent.

#### AW-208 — Test : `PhysicsSystem.RatesMatch` `[test, S]`
- **Deps** : AW-204
- **Fichier** : `tests/Agapanthe.Tests/PhysicsTests.cs` (ajout) — pas de nouveau fichier
- **Cas** : `RatesMatch(1f/60f, 1f/60f)` == true ; `RatesMatch(1f/30f, 1f/60f)` == false.
- **Acceptation** : vert. (Le vrai filet runtime reste le `Debug.Assert` + l'équivalence AW-207.)

---

### Wave 3 — captures + tail (spec §Verification (DoD), §Waves W3)

#### AW-301 — Vérification captures + HeadlessSim + AOT `[verify, M]`
- **Deps** : AW-202..208
- **Actions** :
  - Capture protocol ×3 runs : `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0
    AGAPANTHE_DROP_EVERY=12`, 1280×720, Debug, `AGAPANTHE_CAPTURE` + `AGAPANTHE_CAPTURE_UI`. **Attendu : HDR
    `12638edd` + UI `03421357` INCHANGÉS** (F2). Un hash qui bouge = régression → retour au générateur.
  - `HeadlessSim` : `dotnet run --project samples/HeadlessSim -c Debug -- --ticks 600 --bodies 8 --save <path>` →
    MD5 == `7e8dc68f5a25914c84677a7a53ad3a58`. `HeadlessSimSnapshotFormatTests` doit passer **sans édition** (preuve
    que le chemin headless n'a pas bougé).
  - `AotComponentProbe` : publish win-x64 (PATH avec `vswhere.exe`) + run → PASS.
  - JIT == AOT pour `HeadlessSim`.
- **Acceptation** : tout inchangé/PASS. Sinon → diagnostic + fix ciblé.

#### AW-302 — Double audit `[audit, M]`
- **Deps** : AW-301
- **Agents** : `csharp-lowlevel` (alloc cachées, `Advance` hot path, `float` drift, `Debug.Assert`) +
  `engine-architect` (placement de l'accumulateur, `CurrentTick` sémantique, fidélité aux 8 décisions, dette F7 vs
  MP-0d). Pas de `graphics-3d` — aucune passe GPU touchée.
- **Acceptation** : PASS ou PASS-with-concerns sans 🔴. Tout 🔴 → nouvelle tâche visible + fix avant clôture.

#### AW-303 — Docs `[docs, S]`
- **Deps** : AW-302
- **Fichiers** :
  - `CLAUDE.md` : §État courant — bloc MP-0c clos (métriques, décisions, dette F7) ; §Commandes / env vars — ajouter
    « un run déterministe DOIT poser `AGAPANTHE_MAX_FRAMES` » (F9) ; retirer MP-0c de la liste « à faire ».
  - `docs/AVANCEMENT.md` : §Reprise — MP-0c CLOS, MP-0d devient le prochain ; métriques.
  - `docs/BACKLOG.md` : accumulateur livré ; interpolation reste 🟡 ; dette F7 (`TickIndex==0` ambigu) notée pour
    MP-0d ; décision 6 (overlay N× en rattrapage) notée pour UI-3.
- **Acceptation** : cohérent avec l'état réel post-jalon.

#### AW-304 — Tail : self code review du diff `[test, S]`
- **Deps** : AW-302
- **Action** : relire le diff complet `git diff 0307439..HEAD` — scope respecté, aucun TODO, conventions,
  nommage, pas de dette introduite silencieusement.
- **Acceptation** : rapport écrit ; tout écart → tâche.

#### AW-305 — Tail : requirements validation vs DoD `[test, S]`
- **Deps** : AW-304
- **Action** : cocher chaque ligne de la spec §Verification (DoD) + les 8 décisions verrouillées contre le livré.
- **Acceptation** : 100 % couvert ou écart documenté.

#### AW-306 — Tail : full project verification `[test, M]`
- **Deps** : AW-305
- **Action** : `dotnet build` (0 warning) · `dotnet test` complet (3 runs pour la stabilité) · `AotComponentProbe`
  PASS · Sandbox headless 0 validation / 0 leak · captures finales confirmées.
- **Acceptation** : tout vert, sortie collée sur le board. → CONVERGE (verdict humain, board archivé
  `archive/board-session28-MP0c.md`, suggestion de commit).

---

## Deferred Work

- **Interpolation visuelle** (lissage rendu entre ticks) — jalon dédié, aucune infra d'état « précédent » n'existe.
  Backlog 🟡. `FixedTimestepAccumulator` détient déjà `_accumulated` (le facteur de blend `_accumulated / FixedDeltaSeconds`) ;
  ce jalon-là ajoutera l'accesseur (audit arch F4).
- **`DebugOverlaySystem` on-screen tick-count** — `LastFrameTickCount` exposé + loggé au bench, mais pas câblé à
  l'overlay (ctor `FrameStats` seul, MP-0a audit). Reporté à UI-3 (F8).
- **`bool HasTicked`** sur `SimulationHost` — désambiguïse `TickIndex == 0` (« aucun tick » vs « tick 0 fini »).
  Additif, pour MP-0d s'il en a besoin (spec F7 / audit arch F3).
- **`FixedTimestepAccumulator.Reset()`** — jeter le reliquat accumulé sur un `Load` de snapshot en cours de process,
  une pause, un reseed de monde. Aujourd'hui théorique (`AGAPANTHE_LOAD` relaunch-only) ; mord au premier
  `Agapanthe.App` / hôte qui charge en process (audit arch F4).
- **Pas fixe = source de vérité unique** — aujourd'hui 4 littéraux `1f/60f` indépendants (`PhysicsSettings.Default`,
  `FrameOrchestrator.CreateDefault` ×2 défauts, `HeadlessSim/Program.cs:21`, scènes Sandbox), réconciliés par le seul
  `Debug.Assert` de `PhysicsSystem`. **Prérequis netcode** : le tick rate est une constante de protocole. Le porter
  sur `SimulationHost` (ou un `SimulationSettings` dont `PhysicsSettings` + l'accumulateur dérivent) — à instruire en
  MP-0d ou au premier `Agapanthe.App`, hors scope MP-0c (audit arch F7).
- **Restructuration cadence `DebugOverlaySystem`/`UiRenderSystem`** sous rattrapage (décision 6) — UI-3 ou dédié.
- **Ancrage thread mono-thread dans `SimulationHost.Tick`** — le `Debug.Assert` mono-thread vit dans `EndFrame`, pas
  `Tick` ; `_frameTickCount++` n'est pas ancré. Marginal aujourd'hui (mono-thread), à traiter avec le job system
  (audit LL F-6).

---

## Progress log

- **2026-09-05** — Board bâti. 16 tâches, 3 vagues. Rollback `0307439`.
- **2026-09-05 — Wave 1 CLOSE ✅** — AW-101 (13 cas) + AW-102 (`FixedTimestepAccumulator.cs`).
  - **Écart assumé au spec** : `Advance` **ne fait pas** de `Debug.Assert` sur input non-fini/négatif (le spec
    §Architecture 1 disait « Debug.Assert-flagged »). Raison : un `Debug.Assert` en échec termine le test host →
    le cas DoD « does not corrupt the accumulator » (spec §Waves W1) devient intestable. La sanitisation silencieuse
    (`float.IsFinite` d'abord, puis négatif, puis `MathF.Min`) EST le traitement ; commentaire dans le code. Même
    raisonnement que R3 pour `PhysicsSystem.RatesMatch`. À signaler au double audit AW-302.
  - **Gates** : `dotnet build` 0 warning · `dotnet test` **543/543** (530 baseline + 13) · `EngineIsHeadlessTests`
    vert (le type ne référence que `SimulationHost`). Aucun fichier existant touché.
- **2026-09-05 — Wave 2 CLOSE ✅** — AW-201 → AW-208.
  - **AW-201** rename `FrameIndex→TickIndex` : `SystemScheduler`, `Systems`(TickContext), `SimulationHost`,
    `HeadlessSim/Program.cs` + `SchedulerTests` (2 méthodes renommées), `HeadlessSimulationTests`, `RenderBarrierTests`
    (param `tickIndex`), `RenderStageNeutralityTests`. Mécanique pure, `dotnet test` resté **543/543**.
  - **AW-202** `SimulationHost.CurrentTick` → `Math.Max(0L, TickIndex - 1)` (dernier tick exécuté) + doc réécrite +
    `RenderStageNeutralityTests.cs:108` **code** aligné (F5). Aucun test existant ne cassait — l'off-by-one n'était
    épinglé par rien, comme la revue l'avait dit.
  - **AW-203** `FrameOrchestrator` : champ `_accumulator`, ctor privé + 2× `CreateDefault` gagnent
    `fixedTickRate = 1/60, maxWallClockDt = 0.25`, `Tick` → `_accumulator.Advance`, props `FixedTickDeltaSeconds` +
    `LastFrameTickCount` (forward). `SimulationHost.LastFrameTickCount` (reset `BeginFrame`, `++` `Tick`).
  - **AW-204** `PhysicsSystem.RatesMatch(float,float)` internal static + `Debug.Assert(RatesMatch(...))` + remark
    réécrite. Les 6 sites construisant un `PhysicsSystem` utilisent tous `1/60` → assert ne fire jamais.
  - **AW-205** Sandbox : `wallClockDt = maxFrames > 0 ? FixedTickDeltaSeconds : (float)dt` (`> 0` — R1), ligne bench
    `+ "sim ticks {LastFrameTickCount}"`.
  - **AW-206** `HeadlessSimulationTests` : `CurrentTick_ReportsTheLastExecutedTick` (0 avant tick, 0 après tick 0,
    2 après 3 ticks) + `ASystemInsideTickN_SeesTickIndexN` (0,1,2).
  - **AW-207** `AccumulatorEquivalenceTests.cs` (nouveau, `[Collection("World")]`, cluster asymétrique) :
    **20×`Advance(3·Fixed)` == 60×`Advance(1·Fixed)` == 60 ticks** (entiers) · positions bit-identiques · 30≠60
    (non-vacuité) · **catch-up FrameOrchestrator-shape** : frame `4·Fixed` → `LastFrameTickCount`=[1,1,4,1,1],
    `Stats.FrameCount`=4 (frames, pas ticks), total 8 ticks.
  - **AW-208** `PhysicsSystemTests.cs` (nouveau) : `RatesMatch` true/false.
  - **Gates** : `dotnet build` **0 warning** · `dotnet test` **551/551** (543 + 8) · **captures byte-identiques** —
    HDR `md5 12638eddd7f3f67ab161b298ffbcd15e` (`12638edd`), UI `md5 034213575932dabcff41c2e0c72addfa` (`03421357`),
    tous deux 2 764 816 o, **inchangés** (F2 prouvé empiriquement — 0 pixel bougé) · Sandbox headless : **0 leak**
    (233 resources), **0 validation**.
- **2026-09-05 — Wave 3 : AW-301 (verify) + AW-302 (double audit) + corrections ✅**
  - **AW-301** — captures ×3 HDR `12638edd…`/UI `03421357…` **byte-identiques** · `HeadlessSim`
    `7e8dc68f5a25914c84677a7a53ad3a58` **JIT == AOT** inchangé (`HeadlessSimSnapshotFormatTests` passe sans édition) ·
    `AotComponentProbe` PASS · Sandbox headless 0 leak / 0 validation.
  - **AW-302** — `csharp-lowlevel` **PASS-with-concerns** (0-alloc réel, résidu **exactement 0f** en capture,
    dérive 5,5e-7 s/h jitteré) · `engine-architect` **PASS-with-concerns 4,2/5** · **aucun 🔴** aux deux.
  - **Corrections appliquées** (8) :
    1. **arch F1** — params `fixedTickRate`/`maxWallClockDt` → `fixedTickDeltaSeconds`/`maxWallClockDeltaSeconds`
       (périodes, pas des fréquences ; `FrameOrchestrator` ×3 sites).
    2. **LL F-1** — garde de ratio ctor : `max/fixed > 1024` → `ArgumentOutOfRangeException` (sinon `fixed=1e-9` →
       boucle infinie, soustraction float = no-op).
    3. **arch F6 / LL F-2** — `FixedTimestepAccumulator.Sanitise(float,float)` extrait (unit-testé) + `SanitisedInputCount`
       (compteur Release-observable). **Le `Debug.Assert` du spec/de l'audit n'est PAS remis** : il tue le test host
       ET rend `SanitisedInputCount` intestable ; un compteur lisible partout est un meilleur signal — décision
       explicite, commentée dans le code.
    4. **LL F-3** — non-fini/négatif → `0f` (perd la frame, échec inerte) au lieu de `→ Max` (15 ticks physique en
       silence). Retour d'`Advance` = 0 sur NaN, asserté.
    5. **arch F2** — `FixedTimestepAccumulator.AdvanceFrame(host, dt)` = `BeginFrame` + `Advance` ; `FrameOrchestrator.Tick`
       l'appelle ; `AccumulatorEquivalenceTests` catch-up cible `AdvanceFrame` (câblage réel exercé, plus recopié).
    6. **arch F3** — `LastFrameTickCount` latché dans `EndFrame` (champ privé `_frameTickCount`), même cycle de vie
       que `LastFrameMs`.
    7. **LL F-4** — `AotComponentProbe` exerce maintenant l'accumulateur (`AotAccumulatorSmoke: 1 + 3 ticks`) — la
       ligne de gate n'est plus vacuous.
    8. **LL F-5** — test 0-alloc sur pattern mixte (whole step / sub-step / catch-up / NaN / négatif).
    Docs/notes : arch F5 (doc `CurrentTick` : frame à 0 tick répète l'index — fait), LL F-8 (dérive chiffrée),
    arch F4/F7 + LL F-6 → §Deferred Work (fait).
  - **Gates post-fix** : `dotnet build` **0 warning** · `dotnet test` **558/558** (543 + 15) · captures **inchangées**
    (HDR `12638edd…` / UI `03421357…`) · `HeadlessSim` **JIT == AOT** `7e8dc68f…` inchangé · `AotComponentProbe`
    PASS (+ AotAccumulatorSmoke) · 0 leak / 0 validation.
- **2026-09-05 — Wave 3 : AW-303 (docs) + AW-304/305/306 (tail) ✅**
  - **AW-303** — `CLAUDE.md` (§État courant bloc MP-0c, header, env vars `AGAPANTHE_MAX_FRAMES` obligatoire pour
    run déterministe, dette `CurrentTick` cochée), `docs/AVANCEMENT.md` (§Reprise MP-0c CLOS + « Mis à jour » + §MP-0a
    dette cochée + item 5 réorientation), `docs/BACKLOG.md` (§Physique accumulateur ✅ / interpolation 🟡 / `Reset()` /
    source de vérité unique, §4quater item 5 coché + bloc MP-0c CLOS + « Dernière mise à jour »).
  - **AW-304** — self-review du diff (`git diff 0307439 -- src/ samples/ tests/`) : dans le scope, 0 TODO,
    conventions OK, rename mécanique propre, aucune dette introduite hors §Deferred Work.
  - **AW-305** — requirements validation vs spec §Verification (DoD) : **tout coché sauf verdict humain** (build 0
    warning ✅ · test 558 ✅ · HDR `12638edd`/UI `03421357` inchangés ✅ · `HeadlessSim` `7e8dc68f…` JIT==AOT,
    `HeadlessSimSnapshotFormatTests` sans édition ✅ · AOT probe PASS ✅ · Sandbox headless 0 val/0 leak ✅ · 0
    alloc/frame ✅ · 8 décisions verrouillées honorées, confirmé par l'audit archi 8/8).
  - **AW-306** — full verification : `dotnet build` **0 warning**, `dotnet test` **558/558 × 3 runs**.
  - **Prochaine étape : verdict humain → CONVERGE** (archiver le board `archive/board-session28-MP0c.md`, suggérer
    un commit — commit sur demande explicite uniquement).
