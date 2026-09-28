# Job system, sous-jalon 2b — `RequiresOwnerThread` (affinité de thread orthogonale à l'exclusivité)

## Summary

Job-2 (S45, clos) a fermé la garde de concurrence de `SimCommandQueue`, mais son propre audit
(`engine-architect`) a établi une reformulation majeure, versée au backlog §4quater comme
« candidat n°1 du prochain sous-jalon physique » : `ISystem.RequiresExclusiveExecution` confond
deux axes orthogonaux qu'un job system mûr sépare — **exclusivité de ressource** (personne d'autre
ne tourne en même temps) et **affinité de thread** (doit tourner sur le thread propriétaire, mais
peut chevaucher d'autres systèmes sur des workers). `PhysicsSystem` a besoin du second, pas du
premier : `GameWorld.StepPhysics` est protégé par `AssertOwnerThreadStrict()` (jamais relâché pour
un worker sanctionné, par design Job-1), donc flipper `RequiresExclusiveExecution` à `false` sans
rien d'autre ferait planter `StepPhysics` en Debug (ou corrompre en silence en Release) le jour
d'une vague partagée.

Ce sous-jalon ajoute l'axe manquant : `bool RequiresOwnerThread` sur `ISystem`, restructure
l'exécution des vagues de `SystemScheduler` pour honorer cette affinité (fork-join : les membres
pinnés tournent séquentiellement sur le thread appelant pendant que les membres éligibles tournent
sur des workers, dans la même vague), et bascule `PhysicsSystem` pour en être le premier — et
aujourd'hui seul — vrai consommateur. `AssertOwnerThreadStrict` n'est touché nulle part : il reste
l'invariant exact que ce mécanisme respecte, pas un obstacle à contourner.

**Un seul fork réel tranché en interview** : basculer `PhysicsSystem` dans ce même sous-jalon
(plutôt que livrer le mécanisme seul, testé uniquement par des doublures synthétiques) — choisi
parce que (a) c'est le seul vrai consommateur possible aujourd'hui, (b) c'est zéro-risque
comportemental (vérifié en D3, pas supposé), (c) ça prouve le mécanisme contre du code réel,
cohérent avec la culture du projet.

## Decision Log

### D1 — `ISystem.RequiresOwnerThread`, nouveau membre par défaut `false`

```csharp
/// <summary>
/// When <c>true</c>, this system's <see cref="Execute"/> always runs on the scheduler's OWNER thread — never a
/// Job-1 worker — even when it shares a <see cref="Stage.Simulation"/> wave with other systems (i.e. when
/// <see cref="RequiresExclusiveExecution"/> is <c>false</c>). Orthogonal to <see cref="RequiresExclusiveExecution"/>:
/// that axis is RESOURCE exclusivity (nobody else runs concurrently at all); this one is THREAD affinity (this
/// system specifically must run here, others may still run elsewhere in the same wave). Default <c>false</c> —
/// combined with <see cref="RequiresExclusiveExecution"/>'s own default of <c>true</c>, an existing implementation
/// that declares nothing is completely unaffected: it still always executes inline on the owner thread, in its own
/// solo wave, exactly as today.
/// <para>
/// Meaningless (harmlessly redundant) when <see cref="RequiresExclusiveExecution"/> is <c>true</c> — a solo wave
/// already always executes inline on the owner thread. This flag only changes anything once a system ALSO opts out
/// of exclusivity: it says "I may share a wave, but only ever run me on the thread that called
/// <see cref="SystemScheduler.Tick"/>, never a worker."
/// </para>
/// </summary>
bool RequiresOwnerThread => false;
```

Les 4 combinaisons des deux axes sont toutes bien définies — aucune n'est invalide, pas de garde
à ajouter :
- `(Exclusive=true, OwnerThread=true|false)` → vague solo, inline — comportement d'aujourd'hui,
  `RequiresOwnerThread` n'a aucun effet (déjà satisfait trivialement, puisque le `wave.Count<=1`
  inline exécute déjà sur le thread propriétaire).
- `(Exclusive=false, OwnerThread=true)` → peut partager une vague (selon `Reads`/`Writes`), mais
  s'exécute toujours sur le thread propriétaire — le cas neuf que ce jalon active.
- `(Exclusive=false, OwnerThread=false)` → comportement Job-1/2 par défaut, inchangé.

Doc-comments à mettre à jour (déjà des commentaires vivants — Job-2 a lui-même mis à jour
`ISystem.RequiresExclusiveExecution` une fois lors de son propre audit) : ce commentaire, le
commentaire de classe de `SystemScheduler` (lignes 22-31 actuelles, qui affirme aujourd'hui « a
system with RequiresExclusiveExecution … always gets a solo wave » sans mentionner l'affinité de
thread au sein d'une vague partagée), et le commentaire de classe de `PhysicsSystem` (qui explique
aujourd'hui pourquoi il est exclusif — devra expliquer pourquoi ce n'est plus nécessaire).

### D2 — Restructuration de l'exécution des vagues (`SystemScheduler`)

**Ce qui NE change PAS** : l'algorithme de regroupement glouton de `BuildSimulationWaves`
(`Conflicts`/`ConflictsWithAny`/`Intersects`, lignes 446-543 actuelles) — `RequiresOwnerThread`
n'affecte jamais *quels* systèmes partagent une vague, seulement *quel thread exécute quoi* une
fois la vague décidée.

**Nouvelle étape, une fois par vague, à la fin de `BuildSimulationWaves`** : partition **stable**
en place de chaque vague — les membres *worker-eligible* (`RequiresOwnerThread == false`) d'abord,
les membres *owner-pinned* (`RequiresOwnerThread == true`) ensuite. Stable = l'ordre relatif à
l'intérieur de chaque sous-groupe est préservé. Ceci ne peut pas introduire de bug : deux systèmes
d'une même vague sont par construction sans conflit `Reads`/`Writes` — leur ordre d'exécution
relatif n'a jamais eu de sens garanti, c'est la prémisse même de les avoir regroupés dans la même
vague. Nouveau champ parallèle `_simulationWaveWorkerEligibleCount : List<int>`, calculé une fois,
miroir exact du patron existant `_simulationWaveExclusive`.

**Preuve de rétrocompatibilité totale** : aucun `ISystem` existant (production ou test) ne déclare
`RequiresOwnerThread` aujourd'hui (l'interface n'existait pas avant ce jalon) → pour CHAQUE vague
déjà construite par la suite de tests actuelle, `workerEligibleCount == wave.Count` toujours → la
partition est un no-op strict pour tout code existant. Zéro test existant ne peut être affecté par
ce changement.

**`RunSimulationWaves`, 3 cas au lieu de 2** :
1. `workerEligibleCount == wave.Count` (aucun membre pinné) → **inchangé** : `wave.Count<=1`
   inline, ou `RunWaveInParallel` tel quel pour `wave.Count>1`.
2. `workerEligibleCount == 0` (tout pinné, y compris `wave.Count > 1`) → boucle séquentielle sur
   le thread appelant, **le pool de workers n'est pas créé du tout** (`EnsureWorkerPool` jamais
   appelé pour cette vague) — pas de `try`/`catch` dédié ici, la propagation directe suffit
   (même comportement que le `wave.Count==1` d'aujourd'hui, juste étendu à N>1 membres tous
   pinnés).
3. **Mixte** (`0 < workerEligibleCount < wave.Count`) → fork-join réel : `RunWaveInParallel`
   dispatché seulement sur le sous-intervalle `[0, workerEligibleCount)` de la liste de vague
   (réutilise `WorkerJob(wave, start, size)` tel quel, juste avec un compte de référence réduit —
   la partition stable garantit que ces indices sont contigus et correspondent exactement aux
   membres worker-eligible). **Immédiatement après avoir signalé les workers** (`_workerGoSignals[i].Release()`
   — jamais avant, sinon aucun recouvrement réel n'a lieu), le thread appelant exécute
   séquentiellement `[workerEligibleCount, wave.Count)` (les membres pinnés) dans un `try`/`catch`
   qui alimente la **même** collection `errors` que les exceptions workers (mirroring le
   comportement déjà établi de `WorkerLoop` : sur exception, on arrête la boucle locale et on
   continue vers l'attente des workers plutôt que de laisser une exception non catchée corrompre
   l'état pour le tick suivant), puis attend (`WaitForWaveOrDiagnoseHang`, complètement inchangé)
   et rethrow via la logique `AggregateException`/`ExceptionDispatchInfo` déjà existante, désormais
   capable de fusionner une exception owner-thread avec des exceptions worker.

**Sizing du pool de workers** (`EnsureWorkerPool`/`_maxSimulationWaveWidth`) : **inchangé**, reste
calé sur `wave.Count` total plutôt que sur `workerEligibleCount` — une borne supérieure toujours
sûre (jamais besoin de plus de workers que de membres dans la vague), parfois légèrement
sur-provisionnée si des membres sont pinnés. Accepté comme dette mineure documentée (§Deferred) —
sans consommateur réel aujourd'hui qui rendrait cette sur-provision mesurable, affiner le sizing
maintenant serait prématuré et complexifierait `EnsureWorkerPool` pour un gain nul en pratique.

Nouvel accessor test/diagnostic : `internal IReadOnlyList<int>? GetSimulationWaveWorkerEligibleCountForTest()`,
miroir de `GetSimulationWaveExclusivityForTest`.

**Interaction mineure, notée pas corrigée** (relevée par la revue scorée) : en Debug, le
chronomètre de 30s de `WaitForWaveOrDiagnoseHang` ne démarre qu'après que le thread propriétaire a
fini d'exécuter son propre segment pinné (séquence : `Release()` des workers → boucle pinnée locale
→ `Wait(30s)`). Un système pinné anormalement lent retarderait donc le moment où le détecteur de
blocage commence à surveiller les workers. Sans conséquence pratique ici (`PhysicsSystem` est
rapide, et c'est un diagnostic Debug-only), mais un vrai effet de bord à connaître plutôt qu'à
découvrir plus tard.

### D3 — `PhysicsSystem` bascule réellement

```csharp
public IReadOnlyList<Type> Reads => [typeof(GlobalId), typeof(RigidBody), typeof(InstanceSlot)];
public IReadOnlyList<Type> Writes => [typeof(WorldPosition), typeof(Velocity)];
public bool RequiresExclusiveExecution => false;
public bool RequiresOwnerThread => true;
```

Vérifié ligne par ligne contre `GameWorld.Physics.cs` (549 lignes lues en entier, pas résumées) :
- **Reads** : `GlobalId` (lu à la passe de gather + dans `MarkNetworkDirty`), `RigidBody` (lu —
  `InverseMass`/`Restitution`/`Radius` — jamais écrit nulle part dans ce fichier), `InstanceSlot`
  (lu via `.Get<InstanceSlot>().Value` pour alimenter `MarkDirty` — un vrai accès ECS, donc un vrai
  risque de conflit si un futur système l'écrivait concurremment).
- **Writes** : `WorldPosition`, `Velocity` (les deux seuls `.Set<T>()` du fichier, dans la passe 3
  « scatter »).
- Le scratch de broadphase (`_cellHead`/`_cellNext`/`_pEntity`/`_pPos`/`_pVel`/etc., lignes 26-39)
  reste **invisible** à `Reads`/`Writes` par construction — protégé exclusivement par
  `AssertOwnerThreadStrict()` (complètement intouché par ce jalon), exactement pourquoi
  `RequiresOwnerThread=true` est nécessaire **en plus de** `Reads`/`Writes`, jamais à la place.

**Zéro changement de comportement observable aujourd'hui, vérifié et pas seulement supposé** :
`PhysicsSystem` reste l'unique système `Stage.Simulation` de tout le dépôt (confirmé par
exploration du code) → sa vague a toujours `Count==1` → elle tombe dans la branche `wave.Count<=1`
inchangée de D2, qui ne consulte même pas `RequiresOwnerThread`. Aucun hash `HeadlessSim`/
Sandbox/TopDown ne peut donc bouger — la partie de ce jalon qui « active » quelque chose de neuf
n'est exercée que par les tests synthétiques et le test d'intégration de D4 ci-dessous.

### D4 — Tests

Dans `SystemSchedulerWaveGroupingTests.cs` (patron `DeclaringSystem`, threadless — teste
l'algorithme de groupement pur, jamais l'exécution concurrente) :

0. **Partition structurelle, sans thread** : une vague à plusieurs membres owner-pinnés et
   worker-eligible non-conflictuels (`DeclaringSystem` étendu d'un paramètre
   `requiresOwnerThread`) — assert via `GetSimulationWavesForTest()` que l'ordre au sein de la
   vague place bien tous les worker-eligible avant tous les owner-pinnés (partition stable), et via
   `GetSimulationWaveWorkerEligibleCountForTest()` que le compte correspond exactement. Miroir
   direct de la couverture déjà existante pour `_simulationWaveExclusive`
   (`GetSimulationWaveExclusivityForTest`) — relevé par la revue scorée du spec comme le trou de
   couverture le plus proche de la classe de bug que ce projet a déjà trouvée post-hoc plusieurs
   fois (S42 `OverlapBox`) : une propriété d'algorithme de groupement pur doit avoir un test
   threadless dédié, pas seulement une preuve comportementale multi-thread.

Dans `SystemSchedulerParallelismTests.cs` (patron `TrackedSystem`/`ConcurrencyTracker` déjà établi
par Job-1/Job-2, étendu avec un paramètre `requiresOwnerThread` sur les doublures existantes) :

1. **Recouvrement réel prouvé** : vague à 1 système owner-pinné + 1 worker-eligible, `Reads`/
   `Writes` disjoints — capture le thread-id du pinné et assert qu'il égale le thread créateur du
   `SystemScheduler` ; capture le thread-id du worker-eligible et assert qu'il diffère ; et,
   surtout, assert un **recouvrement temporel réel** via `ConcurrencyTracker` (pas une simple
   séquentialité owner-puis-worker ou worker-puis-owner déguisée en parallélisme).
2. **Tout-pinné, aucun pool créé** : vague à 2 systèmes owner-pinnés non-conflictuels, 0
   worker-eligible → les deux tournent sur le thread appelant, et
   `GetWorkerAllocatedBytesForTest()` reste `null` (assertion **structurelle** qu'aucun pool n'a
   jamais été créé, pas seulement une inférence comportementale).
3. **Exception owner-thread propagée sans perdre les workers** : le membre pinné lève → l'exception
   ressort bien (seule ou agrégée), et les workers de la même vague ont fini proprement avant le
   rethrow (pas de corruption d'état pour le tick suivant).
4. **Agrégation double** : un pinné ET un worker-eligible lèvent dans la même vague →
   `AggregateException` à exactement 2 entrées (extension du test d'agrégation déjà existant côté
   Job-1).
5. Test direct sur `PhysicsSystem` : `RequiresExclusiveExecution == false`,
   `RequiresOwnerThread == true`, `Reads`/`Writes` contiennent exactement les types attendus (D3).
6. **Test d'intégration avec le VRAI `PhysicsSystem`** (pas seulement des doublures) : construit un
   `GameWorld` + `SystemScheduler` réels, enregistre `PhysicsSystem` (owner-pinné) aux côtés d'un
   système de test synthétique disjoint dans la même vague, tick, assert qu'une position a bougé
   (la physique a réellement avancé) ET que le système synthétique a tourné sur un thread worker —
   la preuve la plus forte du jalon : le mécanisme fonctionne contre du code de production réel,
   pas seulement deux doublures qui se ressemblent l'une l'autre.

**Aucune vérification visuelle/capture requise** — ce mécanisme est purement
`Agapanthe.Engine`/`Agapanthe.World`, invisible à tout rendu. `HeadlessSim` (JIT==AOT) reste le
seul binaire concerné pour la non-régression, et son comportement de tick est prouvé inchangé
(D3), pas seulement supposé.

## Fichiers touchés

- `src/Agapanthe.Engine/Systems.cs` — `ISystem.RequiresOwnerThread` (D1).
- `src/Agapanthe.Engine/SystemScheduler.cs` — partition stable par vague, 3ᵉ branche
  d'exécution dans `RunSimulationWaves`, `RunWaveInParallel` étendu pour un sous-intervalle +
  exécution owner-pinnée entrelacée, nouvel accessor test (D2).
- `src/Agapanthe.Engine/PhysicsSystem.cs` — `Reads`/`Writes` réels, `RequiresExclusiveExecution`/
  `RequiresOwnerThread` basculés, commentaire de classe mis à jour (D3).
- `tests/Agapanthe.Tests/SystemSchedulerWaveGroupingTests.cs` — 1 nouveau test threadless
  (D4, item 0).
- `tests/Agapanthe.Tests/SystemSchedulerParallelismTests.cs` — 4 nouveaux tests + extension des
  doublures existantes (D4, items 1-4).
- Nouveau test (fichier dédié ou section dans un fichier existant de tests `PhysicsSystem`) pour
  D4 items 5-6.

## Verification

1. `dotnet test` — 100% vert, aucune régression (la partition par vague est un no-op strict sur
   toute vague déjà construite par la suite actuelle, prouvé par construction en D2 — vérifié en
   exécutant la suite complète après implémentation, pas seulement affirmé).
2. `samples/HeadlessSim` (JIT + NativeAOT) — snapshot par défaut et `--drive` byte-identiques
   (`PhysicsSystem` reste seul dans sa vague partout où il tourne aujourd'hui, D3).
3. Les 6 nouveaux tests de D4 verts, y compris le test d'intégration avec le vrai `PhysicsSystem`
   (item 6) — la preuve la plus significative de ce jalon.
4. Pas de capture/verdict visuel humain requis (mécanisme headless pur, aucune surface de rendu
   touchée).
5. 0 warning, `TreatWarningsAsErrors` inchangé.

## Post-implementation: double audit outcome

Double audit `csharp-lowlevel` + `engine-architect`, tous deux PASS-with-concerns. Convergence forte
sur un vrai 🔴 : la prémisse implicite du split Job-1 `AssertOwnerThread` (lenient)/
`AssertOwnerThreadStrict` supposait que le thread propriétaire ne mute jamais PENDANT qu'un worker
tourne (il ne faisait que bloquer dans `WaitForWaveOrDiagnoseHang`) — ce jalon casse cette prémisse
en faisant tourner du code utilisateur sur le propriétaire pendant une vague mixte. Un futur système
pinné non-exclusif qui appellerait `Spawn`/`Despawn` courrait une vraie race contre un worker
appelant la surface lenient — **reproduit empiriquement** par `csharp-lowlevel` (une
`NullReferenceException` intermittente). `PhysicsSystem` est sûr aujourd'hui uniquement parce que
`StepPhysics` ne touche aucun champ que la surface lenient lit, pas parce que le pinning rend la file
structurelle sûre en général.

**Corrigé** : documentation explicite de cette contrainte sur `ISystem.RequiresOwnerThread`/
`RequiresExclusiveExecution`, `GameWorld.AssertOwnerThread`/`AssertOwnerThreadStrict` et
`PhysicsSystem.RequiresOwnerThread` ; nouveau test `GameWorldOwnerThreadGuardTests` (scan de source)
épinglant que la surface lenient n'a que 2 call sites connus-sûrs (`IsAlive`, `GetGlobalId`). Pas de
garde runtime ici — versé à Job-3 (voir `docs/BACKLOG.md`, entrée Job-2b).

Findings 🟠/🟡 supplémentaires corrigés : test de propagation d'exception pinnée qui ne discriminait
rien par mutation (worker instantané → corrigé avec un worker à délai réel + flag) ; test
d'intégration avec le vrai `PhysicsSystem` ne prouvait le pinning qu'en Debug → décorateur `ISystem`
capturant le thread-id indépendamment de la configuration ; sizing du pool de workers calé sur
`wave.Count` plutôt que `workerEligibleCount` (dette initialement différée, finalement corrigée
immédiatement — une ligne) ; 3 commentaires hors diff rendus faux par ce jalon ; combinaison
`(Exclusive=true, OwnerThread=true)` non testée. `HeadlessSim` JIT+NativeAOT re-vérifié
byte-identique après la passe complète de correctifs.

## Deferred / Out of scope

~~Sizing du pool de workers optimisé sur `workerEligibleCount` plutôt que sur `wave.Count` total~~ —
**corrigé pendant le double audit** (finding 🟠 F3/🟡5, une ligne), voir la section « Post-
implementation » ci-dessus. Un vrai garde runtime pour l'invariant « propriétaire mutant pendant une
vague mixte » (🔴 F1, ci-dessus) — versé à Job-3, qui n'est donc plus un principe abstrait mais porte
désormais deux items concrets. Job-3 (filet de vérification runtime prouvant que l'accès réel d'un
système correspond à ses `Reads`/`Writes` déclarés) reste un sous-jalon séparé, non planifié — sans
lui, un système qui ment sur `RequiresOwnerThread` ou `Reads`/`Writes` est un hasard silencieux non
détecté, exactement la posture qu'`AssertOwnerThread` lui-même a eue avant Job-1. Un 2ᵉ système
`Stage.Simulation` réel qui exercerait enfin le fork-join en conditions de production — aucun
candidat concret aujourd'hui
dans le dépôt.
