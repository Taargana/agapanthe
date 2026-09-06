# Absolute-Work Board — Agapanthe Session 29 (MP-0d : input → commandes horodatées)

**Status**: ⏸️ **SPEC APPROUVÉE — en attente de lancement `absolute-work`. Sauvegardé pour prochaine session.**
Spec : `docs/plans/2026-09-06-mp0d-input-commands-design.md` (APPROVED **4,30/5**, 2 tours de revue scorée
`engine-architect` : v1 3,33 NEEDS WORK — 2 🔴 + 6 🟠, toutes les citations exactes ; v2 4,30 APPROVED, R1-R4
repliés). Aucun code écrit.

## ⏸️ REPRISE — où repartir

1. **Spec approuvée** : `docs/plans/2026-09-06-mp0d-input-commands-design.md` (bloc Status = historique des 2
   tours + les résidus R1-R4). **Ne pas re-brainstormer, ne pas re-reviewer** — la spec est bonne.
2. **Prochaine étape exacte** : lancer `absolute-work` sur l'exécution (skip interview + spec, aller direct au
   task board depuis les 4 vagues de la spec). Feu vert humain entre chaque vague, commit sur demande explicite
   uniquement.
3. **MP-0c est CLOS** (commit `098c4b7`, poussé). MP-0d est le **dernier** des 4 sous-jalons de MP-0. Après :
   `Agapanthe.App`.

## Contexte

MP-0 (fondations d'autorité) : MP-0a (split headless) ✅ · MP-0b (identité) ✅ · MP-0c (autorité du temps,
`FixedTimestepAccumulator`) ✅ (S28, commit `098c4b7`) · **MP-0d (input → commandes horodatées) = ce jalon**.

**Le problème** : aujourd'hui l'input mute la sim directement (`Sandbox/Program.cs:600` `window.KeyPressed +=
key => switch`, `Key.B` → `probeDropper.DropOne()` → `world.SpawnBodyDeferred()`). Le netcode serveur-autoritaire
exige des commandes envoyables/bufferisables/rejouables + un point d'application déterministe dans le tick.
MP-0a a créé la couture (type commande + file dans `Agapanthe.Engine`), MP-0c a donné le stamp
(`TickContext.TickIndex`) et l'accumulateur (0..N ticks/frame → traduction par tick).

## Décisions verrouillées (interview brainstorm S28, 10/10)

Toutes dans la spec §Locked decisions. En bref :
1. Portée : mécanisme de commande + `InputSnapshot` **générique** dans Engine + traduction déclarative
   **par tick, pilotée par l'engine** + démo d'entité pilotable.
2. `InputSnapshot` = `{ ulong Held, Pressed, Released; InputAxes Axes }` (`[InlineArray(4)] float`), blittable,
   **40 o**, `[StructLayout(Sequential)]`, index par l'app.
3. Traduction dans `SimulationHost.Tick`, avant `_scheduler.Tick`.
4. `SimCommand` = `readonly record struct (long TargetTick, byte Kind, EntityRef Target, Double3 Vector, float
   Scalar, uint Flags)`, `[StructLayout(Sequential)]`, **56 o**. `Kind` = **byte opaque** (l'app définit ses
   constantes, Engine ne l'interprète jamais). `Double3` — pas `Vector3` — car toute position monde est `double`.
5. `OriginatorId` **différé** (struct transiente, additif plus tard).
6. `InputMap` déclaratif : `BindButton(bit, kind, trigger)` (`OnPress`/`OnRelease`/`WhileHeld`) +
   `BindAxisVector(kind, x, y, z)`. `InputTranslation.Emit` applique la map. L'app fournit `ApplyCommand` qui
   **valide** (budget VS-3, `IsAlive`) et exécute — split serveur-autoritaire.
7. Entité pilotable (démo) = corps physique vélocité-contrôlé, gravité zéro. Nouvelle API
   `GameWorld.SetBodyVelocity(EntityRef, Vector3)`.
8. `HeadlessSim --drive` (séquence d'`InputSnapshot` scriptée) = **le gate déterministe**, nouveau MD5 pinné. La
   scène Sandbox `drive` = démo interactive **non épinglée**.
9. `Key.B` → `host.Commands.Enqueue` direct (porte `camera.Position` `Double3`), stampé `host.TickIndex`.
10. `ProbeDropSystem` (cadence scriptée) reste `SpawnBodyDeferred` direct → captures `planet-drop` inchangées.

## Résidus de revue (R1-R4, repliés dans la spec, à respecter à l'exécution)

- **R1 🟠 (W3)** — `Double3` n'a **pas** de cast vers `Vector3` (délibéré). Utiliser `cmd.Vector.ToVector3(Double3.Zero)`
  (c'est une direction, pas de retrait d'origine).
- **R2 🟡 (W1)** — le guard de thread de `SimCommandQueue` doit **jeter** (`[Conditional("DEBUG")]` +
  `InvalidOperationException`, façon `GameWorld.AssertOwnerThread`), **pas** un `Debug.Assert` (qui tue le host
  xUnit — leçon `FixedTimestepAccumulator.cs:94-97`). Attention : `DiscardHandler` juste au-dessus utilise un vrai
  `Debug.Assert` — ne pas copier cette forme pour le guard.
- **R3 🟡 (W2)** — `IsAlive` couvre le despawn, **pas** un spawn différé non-flushé (`Deref` jette aussi pour cet
  état). Concern rejeu netcode, pas la démo — une phrase dans le contrat de `SetBodyVelocity`.
- **R4 🟡 (W2)** — `[StructLayout(Sequential)]` explicite sur `SimCommand`/`InputSnapshot` (fait dans la spec).

## Rayon d'action mesuré (vérifié en revue, ne pas re-chercher)

- **`SimulationHost.Tick` est le SEUL point d'entrée par tick** : accumulateur (`FixedTimestepAccumulator.cs:109`),
  `HeadlessSim/Program.cs:109`, tests, Sandbox via `orchestrator.Tick` → `AdvanceFrame`. Poser la phase en tête de
  `Tick` couvre tout, l'accumulateur reste inchangé.
- **`_scheduler.TickIndex` = le tick sur le point de tourner** (incrément en fin de `SystemScheduler.Tick`,
  `:103`/`:116`).
- **`planet-drop` n'exerce aucun input sous capture** → `12638edd`/`03421357` ne peuvent pas bouger (`SampleInput`
  null → phase sautée). `Key.B` EST câblé (`Program.cs:657`) mais hors gate déterministe.
- **`EntityRef`** = `public readonly struct` + `internal ulong Id` : Engine peut le tenir dans `SimCommand`, pas
  le fabriquer (OK — vient d'un spawn).
- **`Velocity`** = `internal struct` (`Components.cs:126`) → `SetBodyVelocity` doit être une méthode `GameWorld`.
- **`Deref`** (`GameWorld.cs:1037-1046`) jette pour un handle non-live. Toute méthode publique `GameWorld` ouvre
  sur `ObjectDisposedException.ThrowIf(_disposed, this); AssertOwnerThread();`.
- **`GameWorld.cs:138-160`** = `StructuralCommand`/`CommandKind` : l'idiome fat-blittable-struct + `List` réutilisé,
  commentaire *"no per-command allocation and no boxing"* — le modèle pour `SimCommand`.
- **`Double3`** (`Double3.cs`) : `[StructLayout(Sequential)]`, 3 `double` = 24 o, déjà dans la closure `{Core,
  World}` d'Engine. Narrowing : `ToVector3(Double3 origin)` seulement.
- **`[InlineArray]`** : jamais utilisé dans les sources du projet — 1ʳᵉ fois, AOT-safe par construction,
  `AotComponentProbe` le prouve.
- **`AccumulatorEquivalenceTests.cs`** EXISTE déjà avec les profils `Repeat(3f*Fixed, 20)` vs `Repeat(1f*Fixed,
  60)` + la règle ULP dans son commentaire de classe → **étendre** ce fichier, jamais écrire `n/60f`.
- **`AotComponentProbe/Program.cs`** exerce déjà GameWorld + sérialisation + accumulateur → ajouter un smoke
  commande/input suit le même patron 3 lignes.

## Vagues (depuis la spec §Waves)

- **W1** — mécanisme Engine isolé : `SimCommand`, `InputSnapshot`+`InputAxes`, `InputMap`, `ButtonTrigger`,
  `SimCommandQueue`, `InputTranslation`. Tests blittabilité + file (ordre, 0-alloc, thread-throw R2) + traduction.
  0 fichier existant touché.
- **W2** — câblage `SimulationHost` (phase `Tick`, nouveaux membres, contrat edge-bit) + `GameWorld.SetBodyVelocity`
  (gardes standard + contrat R3). Tests : phase complète, **extension** `AccumulatorEquivalenceTests` (+ compagnon
  non-vacuité 30≠60), edge-bit multi-tick, cible despawnée, `Key.B` niveau host. Tests existants verts (phase =
  no-op si `SampleInput` null).
- **W3** — démo : `AGAPANTHE_SCENE=drive` (interactif), `Key.B` → `Commands.Enqueue` (2 scènes planète, R1),
  `HeadlessSim --drive` (scripté), smoke `AotComponentProbe`.
- **W4** — captures + tail : `12638edd`/`03421357` inchangés (3 runs), `HeadlessSim --drive` JIT==AOT nouveau
  pin + hash défaut `7e8dc68f…` inchangé, `AotComponentProbe` PASS, self-review, **double audit** (`csharp-lowlevel`
  + `engine-architect`), verdict humain (piloter l'entité ; `Key.B` largue toujours une sonde), CONVERGE.

## Conventions du projet (rappel)

.NET 10 · `TreatWarningsAsErrors` (0 warning) · xUnit · `dotnet build` / `dotnet test` · NativeAOT probe
`tools/AotComponentProbe` · aucun type `Vk*` hors `Agapanthe.Graphics` · aucun type Arch hors `Agapanthe.World` ·
**`Agapanthe.Engine` closure `{Core, World}`** (`EngineIsHeadlessTests` — deny-list, pas allowlist de noms).
**Gates** : 0 warning · 0 validation · 0 leak · 0 alloc/frame · NativeAOT PASS · double audit · verdict humain.
**Commits/push sur demande explicite UNIQUEMENT.** Conversation FR, code/commits/docs EN. Vagues + feu vert humain.

> **Captures** : `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12`,
> 1280×720, Debug, `AGAPANTHE_CAPTURE` + `AGAPANTHE_CAPTURE_UI`. Attendu MP-0d : **HDR `12638eddd7f3f67ab161b298ffbcd15e`
> / UI `034213575932dabcff41c2e0c72addfa` INCHANGÉS**. `HeadlessSim` défaut = `7e8dc68f5a25914c84677a7a53ad3a58`
> (1868 o) inchangé ; `--drive` = nouveau pin. **AOT publish** : PATH préfixé du dossier `vswhere.exe`
> (`/c/Program Files (x86)/Microsoft Visual Studio/Installer`).

## Note

Le plan brainstorm local `C:\Users\yannl\.claude\plans\refactored-scribbling-piglet.md` **ne voyage pas avec le
dépôt** — la vérité est `docs/plans/2026-09-06-mp0d-input-commands-design.md` + ce board.
