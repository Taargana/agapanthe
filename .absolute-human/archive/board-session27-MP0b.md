# Absolute-Work Board — Agapanthe Session 26 (MP-0b : identité d'entité)

**Status**: ✅ **CLOS (session 27, 2026-09-02).** Verdict humain PASS. Les 4 vagues (W1 clé de contact, W2 plage
d'allocation, W3 snapshot v2 + identité d'univers, W4 double audit + corrections) sont livrées, testées, auditées.
Commit sur demande explicite de l'humain (cette session).

## ⏸️ REPRISE — où repartir

1. ✅ **Spec écrite** → [`docs/plans/2026-08-13-mp0b-entity-identity-design.md`](../docs/plans/2026-08-13-mp0b-entity-identity-design.md).
2. ✅ **Revue scorée** (reviewer indépendant, seuil 4,0/5) : tour 1 **4,0/5** APPROVED WITH FINDINGS, 12 findings
   appliqués ; tour 2 **4,4/5** APPROVED, 3 findings restants appliqués. Ce que les deux tours ont attrapé, et qui
   n'était pas dans le brainstorm :
   - la clé de contact est une clé d'**ORDRE**, pas d'identité de paire (`_pairKey` n'est lu que par `Array.Sort`) —
     le défaut est la **dépendance au bloc d'ids** entre nœuds, pas une collision de paires ;
   - la couverture AOT venait de `GameWorld.cs:583-588` (3 corps → 3 paires), **pas** du pas à 2 corps `:570-575`
     (`Array.Sort` sort avant `ArraySortHelper<,>` quand `length <= 1`) ;
   - le gate « hashes inchangés » ne vaut que parce que la scène de capture forme un **tas** (`Program.cs:2118-2132`) ;
   - `7c889fec…` est un **MD5** — jamais consigné, retrouvé par mesure : valeur complète
     `7c889fec0df503fe8137ef6c28c7751a`, reproduite à `HEAD` par
     `dotnet run --project samples/HeadlessSim -c Debug -- --ticks 600 --bodies 8 --save <path>` (1852 octets).
3. ✅ **W1, W2, W3 et W4 (audits + corrections) CLOS (session 27)** — voir « Résultat W1 », « Résultat W2 »,
   « Résultat W3 » et « Résultat W4 » ci-dessous. **Reste : verdict humain, puis CONVERGE (commit sur demande).**

### Résultat W1 (session 27, 2026-09-02)

Livré exactement le plan ci-dessous, W1-1 → W1-5 :
- `src/Agapanthe.World/ContactPairKey.cs` — `internal readonly struct ContactPairKey` (`Min`/`Max` ulong,
  `IComparable<ContactPairKey>` lexicographique, normalise l'ordre des arguments au constructeur).
- `tests/Agapanthe.Tests/ContactPairKeyTests.cs` — 8 tests : collision 32 bits qui cesse (+ la collision exacte de
  `HEAD` pinnée comme documentation du défaut), permutation différente sur le triplet sparse du board
  (`2³²−1, 2³², 2³²+1` — l'ancien packing déborde un `ulong` sur `y << 32` et fait ressortir `(y,z)` en tête au lieu
  de dernier), équivalence d'ordre sur ids denses (3 cas), normalisation d'argument, égalité, et 0-alloc du tri
  générique.
- `GameWorld.Physics.cs:38,333` — `_pairKey` passe de `ulong[]` à `ContactPairKey[]` ; `Array.Sort(_pairKey,
  _pairPacked, 0, pairCount)` inchangé dans sa forme (chemin générique `IComparable<T>` toujours emprunté,
  0-alloc prouvé par test).
- `GameWorld.cs` (`AotRootingSmoke`, ~588) — commentaire ajouté : le 3ᵉ corps différé est ce qui fait passer
  `pairCount` de ≤1 à >1 et donc roote `ArraySortHelper<ContactPairKey, long>` sous l'ILC ; le retirer casserait
  le rooting en silence.
- `Components.cs` (doc `GlobalId`) — l'avertissement « packing suppose < 2³² » retiré (devenu faux), remplacé par
  un pointeur vers `ContactPairKey` et W2.

**Gates** : `dotnet build` 0 warning · `dotnet test` **499/499 verts** (491 + 8 nouveaux) · `AotComponentProbe`
PASS (`iterated 13`) · capture headless (`AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0
AGAPANTHE_DROP_EVERY=12`, 1280×720, Debug) : **HDR `12638edd` et UI `03421357` reproduits à l'identique** (MD5
complet vérifié : `12638eddd7f3f67ab161b298ffbcd15e`, `034213575932dabcff41c2e0c72addfa`) · 0 validation · 0 leak
(233 ressources) — preuve que la nouvelle clé induit exactement la même permutation tant que les ids restent denses.
Pas de double audit ni de verdict visuel humain lancés pour W1 seul (prévus en W4/tail, périmètre trop petit pour
un audit dédié) — **à discuter au feu vert avant W3** si l'humain veut un audit intermédiaire.
Rien commité (préférence projet : commits sur demande explicite).

### Résultat W2 (session 27, 2026-09-02)

Livré exactement le périmètre spec §2 (« The allocation range ») :
- `src/Agapanthe.World/GlobalIdRange.cs` — `public readonly record struct GlobalIdRange(Start, EndExclusive)`,
  validation au constructeur (`Start == 0` et `Start >= EndExclusive` → `ArgumentOutOfRangeException`),
  `GlobalIdRange.Default` = `[1, ulong.MaxValue)`, bit-pour-bit ce que le compteur nu faisait avant ce jalon.
- `GameWorld.cs` — nouveau champ `_idRange` (readonly), `GameWorld()` délègue à `GameWorld(GlobalIdRange.Default)`
  (les 75 sites d'appel existants ne bougent pas — c'est la preuve que le chemin par défaut est inchangé),
  nouveau `GameWorld(GlobalIdRange range)`. Les **7** sites `_nextGlobalId++` (2 dans `GameWorld.Physics.cs`, 5
  dans `GameWorld.cs`) collapsent dans **un seul** `private ulong NextId()` qui lève `InvalidOperationException`
  nommant la plage épuisée. Prédicat d'épuisement `_nextGlobalId >= EndExclusive` — cohérent avec la note W3 du
  board (« un monde qui a consommé tout son bloc tient `_nextGlobalId == EndExclusive` »).
- `Components.cs` (doc `GlobalId`) et `RenderItem.cs` (`ComposeSortKey`, doc du tie-break) — les deux commentaires
  périmés de la spec §4 corrigés : plus d'avertissement « < 2³² » sur `GlobalId`, et `GlobalId` explicitement écarté
  comme candidat de tie-break (id sparse alloué depuis une plage host, tronquer sur 32 bits aliaserait des entités
  de blocs différents). Le 3ᵉ commentaire périmé (`EntityRef.cs:19-21`, « per-run monotonic counter ») est **laissé
  pour W3** : le rendre exact demande de parler d'identité d'univers, qui n'existe pas encore.
- `tests/Agapanthe.Tests/GlobalIdRangeTests.cs` (8 tests) — rejet `Start=0`/`Start>=End`, `Default` = `[1,
  ulong.MaxValue)`, constructeur sans argument inchangé (ids depuis 1), constructeur à plage (ids depuis `Start`),
  deux mondes à plages disjointes → ids disjoints, épuisement bruyant, **round-trip snapshot octet-identique**
  (le format v1 n'est pas touché par W2 — preuve que ce jalon n'a rien perturbé silencieusement).
- `tests/Agapanthe.Tests/ContactResolutionOrderTests.cs` (2 tests, construction spec §Testing strategy) —
  trois corps délibérément asymétriques (masses, restitutions, disposition, vitesses initiales distinctes) :
  `SpawnOrder_ChangesFinalPositions` prouve que la scène EST sensible à l'ordre (spawn inversé → positions
  finales différentes — sans cette preuve le test suivant serait vacuueux) ; `IdOffset_DoesNotChangeFinalPositions`
  est le test-titre W2 — même scène, même ordre de spawn, une fois avec `GlobalIdRange.Default` (ids 1,2,3), une
  fois avec `[2³²−1, ulong.MaxValue)` (ids 2³²−1, 2³², 2³²+1, le triplet sparse de W1) — égalité **exacte** (pas de
  tolérance) sur les 3 positions finales. Les deux sont passés du premier coup.

**Gates** : `dotnet build` 0 warning · `dotnet test` **509/509 verts** (499 + 10 nouveaux) · `AotComponentProbe`
PASS (`iterated 13`) · capture headless (même protocole que W1) : **HDR `12638edd` et UI `03421357` reproduits à
l'identique** une seconde fois (MD5 complets identiques à W1) · 0 validation · 0 leak (233 ressources) ·
`HeadlessSim` JIT hash **`7c889fec0df503fe8137ef6c28c7751a` inchangé** (attendu : W2 ne touche pas le format
d'en-tête, c'est le travail de W3).
Rien commité.

### Résultat W3 (session 27, 2026-09-02)

Livré exactement le périmètre spec §3 (« Snapshot v2 ») :
- `src/Agapanthe.World/UniverseId.cs` — `public readonly struct UniverseId` (deux `ulong` little-endian `High`/`Low`,
  **pas** un `Guid` — `Guid.ToByteArray()` est mixed-endian, ça aurait cassé le déterminisme du format), `None` =
  tout-zéro, `ToString`/`Parse` en 32 hex minuscules pour qu'un host puisse en mettre un dans un fichier de config.
- `GameWorld.cs` — nouveau champ `_universeId` (mutable seulement via `Load` avec surcharge), `_idRange` n'est plus
  `readonly` (même raison). Le constructeur `GameWorld(GlobalIdRange range)` de W2 gagne un second paramètre
  optionnel `UniverseId universe = default` — signature source-compatible, les tests W2 n'ont pas bougé. Deux hooks
  de test ajoutés (`UniverseIdForTest`, `IdRangeForTest`), sur le patron de `NextGlobalIdForTest` (VS-1).
- `EntityRef.cs` — le **3ᵉ commentaire périmé** identifié par la spec (§4, différé depuis W2 car il ne pouvait être
  rendu exact qu'une fois l'identité d'univers réelle) : « per-run monotonic counter » remplacé par un pointeur vers
  `GlobalIdRange`/`UniverseId` et l'explication de pourquoi deux univers peuvent légitimement réutiliser la même
  valeur numérique.
- `WorldSerialization.cs` — **format v2** : `SerializationVersion = 2`, en-tête 40 octets (`magic(4) | version(4)=2
  | componentCount(4) | universeId(16) | nextGlobalId(8) | entityCount(4)`, `universeId` inséré entre
  `componentCount` et `nextGlobalId` — la position exacte prescrite par la spec). `Save` écrit `_universeId.High`
  puis `.Low`. `Load(Stream)` devient un raccourci vers `Load(Stream, GlobalIdRange? allocatorOverride)` (nouvelle
  surcharge publique) :
  - **v1 refusé** avec un message dédié nommant la raison (« predates universe identity », pas de mise à niveau
    automatique) — pas juste le message générique « unsupported version ».
  - **Réconciliation d'univers**, les 5 cas de la spec : both `None` → reste non identifié · snapshot défini, monde
    `None` → **adoption** · snapshot `None`, monde défini → le monde **garde** son identité · both définis et
    identiques → confirmé (aucune erreur) · both définis et différents → `WorldSerializationException`.
  - **Allocateur, sans surcharge** : le compteur de l'en-tête est adopté seulement s'il tombe dans
    `[_idRange.Start, _idRange.EndExclusive]` — **inclusif au sommet** (un monde qui a tout consommé son bloc tient
    `_nextGlobalId == EndExclusive`, et `Save` peut encore écrire cette valeur ; c'est ÉMETTRE depuis là qui lève,
    pas s'y trouver). Hors plage → `WorldSerializationException` nommant les deux plages.
  - **Allocateur, avec surcharge** : le compteur de l'en-tête est **ignoré en totalité**, `_idRange` et
    `_nextGlobalId` deviennent ceux de la surcharge (`.Start`) — le cas nœud-récepteur (décision 3) : un monde qui
    reçoit des entités qu'il n'a pas créées ne doit pas faire avancer SON PROPRE allocateur en les acceptant.
  - **Décision 1 non re-discutée** : les ids sérialisés hors de la plage du monde sont acceptés sans validation —
    testé explicitement (`Load_WithOverride_KeepsOutOfRangeSerializedEntityIds_WithoutThrowing`).
- `tests/Agapanthe.Tests/WorldSerializationTests.cs` — offset épinglé mis à jour (`Load_RejectsOutOfRangeMaskBit` :
  `bytes[35]` → `bytes[51]`, exactement le décalage +16 prédit par la spec pour `UniverseId`), commentaire recalculé.
- `tests/Agapanthe.Tests/UniverseIdTests.cs` (5 tests) — `None`, égalité sur les deux moitiés, `ToString`/`Parse`
  round-trip, rejet de longueur.
- `tests/Agapanthe.Tests/WorldSerializationV2Tests.cs` (12 tests) — version 2 écrite · v1 refusé avec message nommé
  · round-trip v2 octet-identique · les **5 cas** de réconciliation d'univers · validation d'allocateur sans
  surcharge (rejet hors plage, `EndExclusive` accepté à la limite) · avec surcharge (compteur d'en-tête ignoré,
  alloue depuis `Start`) · id hors-plage conservé sans lever.
- `tests/Agapanthe.Tests/HeadlessSimSnapshotFormatTests.cs` (1 test) — **le gate manquant identifié par la spec** :
  le hash `HeadlessSim` ne vivait que dans la prose (`CLAUDE.md`, `AVANCEMENT.md`, le board S26), jamais dans un
  test. Reconstruit la scène exacte de `HeadlessSim/Program.cs` (`--ticks 600 --bodies 8`, mêmes corps, même
  hiérarchie) en process, sauve, et épingle le MD5. Passé du premier coup — preuve que la reconstruction est fidèle.
  `challenge.save` n'existe pas dans le dépôt (fichier généré au runtime, jamais committé, régénéré à la prochaine
  sauvegarde F5) — rien à régénérer côté dépôt.

**Nouveau hash `HeadlessSim` (re-épinglé selon le protocole de la spec)** : format v2, en-tête +16 octets →
**`7e8dc68f5a25914c84677a7a53ad3a58`**, **1868 octets** (ancien v1 : `7c889fec0df503fe8137ef6c28c7751a`, 1852 octets
— delta exactement +16, la taille d'`UniverseId`). Vérifié JIT (`dotnet run -c Debug`) == **NativeAOT** (publish
`win-x64` self-contained, PATH préfixé par le dossier de `vswhere.exe`) — **identique**. Valeur complète consignée
ici et dans le test épinglé ; à reporter dans `CLAUDE.md`/`AVANCEMENT.md` au CONVERGE (W4) pour éviter la perte
d'algorithme qui avait touché `7c889fec` (jamais consigné avant sa redécouverte en S26).

**Gates** : `dotnet build` 0 warning · `dotnet test` **527/527 verts** (509 + 18 nouveaux : 12 + 5 + 1)
· `AotComponentProbe` PASS (`AotSerializationSmoke` round-trip octet-identique sous le format v2) · capture
headless (même protocole) : **HDR `12638edd` et UI `03421357` reproduits à l'identique une 3ᵉ fois** (la scène de
capture ne passe jamais par Save/Load, donc rien ne pouvait bouger — vérifié quand même) · 0 validation · 0 leak
(233 ressources).
Rien commité.

### État de l'arbre à la sauvegarde (2026-08-16)

- **Rollback point** : `02c4909` (= `HEAD`, dernier commit poussé).
- **Non commité** (commits sur demande explicite uniquement — rien n'a été commité) :
  - `M .absolute-human/board.md` (ce fichier)
  - `?? docs/plans/2026-08-13-mp0b-entity-identity-design.md` (la spec approuvée)
- `dotnet build` / `dotnet test` **non relancés** depuis `02c4909` : l'arbre de code est intact, donc les 491 tests
  et les gates de MP-0a valent toujours.

### Faits établis pendant la session (ne pas les re-chercher)

- **`InternalsVisibleTo("Agapanthe.Tests")` et `("AotComponentProbe")` existent déjà** (`GameWorld.cs:10-11`) →
  `ContactPairKey` peut rester `internal` et être testé directement, **aucune surface publique à ajouter**.
- **Le hash de snapshot `HeadlessSim` est un MD5** : valeur complète `7c889fec0df503fe8137ef6c28c7751a`
  (1852 octets), reproduite à `HEAD` par
  `dotnet run --project samples/HeadlessSim -c Debug -- --ticks 600 --bodies 8 --save <path>` puis
  `Get-FileHash -Algorithm MD5`. **Jamais consigné avant cette session** — l'algorithme avait été perdu.
- **`AotRootingSmoke` atteint `Array.Sort` avec 3 paires grâce au 3ᵉ corps** (`GameWorld.cs:583-588`), **pas** avec
  le pas à 2 corps (`:570-575`) : `Array.Sort` sort avant `ArraySortHelper<,>` quand `length <= 1`.
- **La scène de capture trie plusieurs paires** (spirale à angle d'or → tas, `Sandbox/Program.cs:2118-2132`,
  35 largages) — c'est ce qui rend le gate « hashes inchangés » signifiant.

### Résultat W4 (session 27, 2026-09-02) — double audit + corrections + vérification finale

Self-review du diff complet (8 fichiers modifiés + 6 nouveaux à ce stade) puis **double audit en parallèle**,
`csharp-lowlevel` et `engine-architect`, sur l'état W1+W2+W3 (arbre propre, 527/527 tests avant audit).

**`csharp-lowlevel` — 1×🔴, 2×🟠, 2×🟡 (pas de note globale, verdict qualitatif « bloquant avant clôture »).**
**🔴 trouvé et confirmé** : `Load(stream, allocatorOverride)` pouvait faire adopter à un monde une plage qui
recouvre des `GlobalId` déjà chargés depuis le snapshot ; les trois sites de matérialisation
(`MaterialiseDrawable`/`MaterialiseNode`/`MaterialiseBody`) écrivaient `_live[globalId] = entity` par **indexeur**
— un spawn ultérieur réémettant le même id écrasait l'entrée en silence : l'entité Arch chargée restait vivante
et simulée mais devenait indespawnable, invisible à `EntityRef`, et **disparaissait du `Save` suivant** (`Save`
itère `_live`). Repro fournie par l'auditeur et **reproduite ici avant correctif** (voir le test
`Load_KeepMine_OwnRangeOverlappingLoadedIds_SpawningACollidingIdThrows` ci-dessous, qui échouait silencieusement
— pas d'exception, juste `LiveEntityCount` faux — avant la correction). 🟠 le rooting AOT du commentaire W1 était
mal expliqué (le rooting est **statique**, indépendant du runtime — le 3ᵉ corps apporte la **couverture
d'exécution**, pas le rooting). 🟠 le test anti-boxing triait un `int[]` alors que la production trie
`_pairPacked` en `long[]` — mauvaise instanciation générique verrouillée. 🟡 `default(GlobalIdRange)` contourne
le constructeur validant de la struct (`Start=0` accepté en silence). 🟡 ordre d'écriture `WriteU64(High)` puis
`WriteU64(Low)` — vérifié conforme à l'ordre d'évaluation gauche-à-droite garanti par le C#, aucun bug, noté PASS.

**`engine-architect` — PASS-with-concerns, 4,1/5, aucun 🔴.** Les 4 décisions verrouillées sont respectées à la
lettre (vérifié par grep : aucun type `GlobalIdRange`/`UniverseId`/`allocatorOverride` ne fuit hors
`Agapanthe.World` — closure `Engine = {Core, World}` de MP-0a intacte). 🟠 `allocatorOverride: GlobalIdRange?`
conflait deux choses : « ignore l'en-tête » et « voici une NOUVELLE plage » — ça rendait un bail (fixé à la
construction) réassignable par un paramètre de désérialisation, deux points de vérité pour un même fait ;
recommandation : une politique sans donnée (`enum`), le monde garde la plage de SA construction. 🟠 aucun code de
production ne nomme encore d'univers (Sandbox/HeadlessSim chargent tous en `UniverseId.None`) — le mécanisme du
🔴 (1) est fermé, son **usage** ne l'est pas encore ; à inscrire au backlog, rattaché au premier hôte réel
(`Agapanthe.App`/MP-0c-d). 🟠 la mitigation « l'adoption est loggée » promise par la spec n'est pas implémentée —
`Agapanthe.World` n'a et ne doit pas avoir de seam de logging ; recommandation : `Load` **retourne** ce qui s'est
passé, l'hôte décide de logger. 🟠 `UniverseIdForTest`/`IdRangeForTest` sont `internal` alors qu'un hôte qui
vient d'adopter une identité via `Load` ne peut ni la lire ni la logger. 🟡 `default(GlobalIdRange)` (même finding
que le low-level, confirmé indépendamment par les deux audits). 🟡 la forme `[Start, EndExclusive)` est validée
« bonne » pour un futur coordinateur de baux distribué (donnée), mais aucun contrôle de renouvellement avant le
mur (dette de forme, additive, non bloquante). 🟡 `UniverseId` : granularité par fichier confirmée correcte,
aucun cas légitime de mutation hors `Load` identifié.

**Corrections appliquées** (tout avant tout commit — l'audit a fait exactement son travail) :
- **Le 🔴** : les trois `_live[globalId] = entity` remplacés par un nouveau `RegisterLive(ulong, Entity)`
  (`GameWorld.cs`) qui fait `_live.TryAdd` et lève `InvalidOperationException` nommée sur collision — silencieux
  → bruyant. Testé par `Load_KeepMine_OwnRangeOverlappingLoadedIds_SpawningACollidingIdThrows` (le scénario exact
  de l'auditeur : plage du monde recevant qui recouvre un id déjà chargé, le spawn suivant lève au lieu de
  corrompre `_live`).
- **`allocatorOverride: GlobalIdRange?` → `SnapshotAllocatorPolicy` (`enum AdoptFromHeader | KeepMine`)**, nouveau
  fichier `SnapshotAllocatorPolicy.cs`. `KeepMine` ne touche plus `_idRange`/`_nextGlobalId` — le monde garde
  exactement ce que **son propre constructeur** a posé (`Load` exige déjà un monde vide/neuf, donc rien n'a pu
  bouger entretemps). Le bail redevient un fait à un seul point de vérité : la construction. Findings low-level
  🟡 (`default(GlobalIdRange)`) et architecte 🟡 corrigés au même endroit : garde `Start == 0` dans le constructeur
  `GameWorld(GlobalIdRange, UniverseId)` **et** listée comme non-applicable côté `Load` (`KeepMine` ne construit
  plus de `GlobalIdRange` lui-même, donc rien à garder là).
- **`Load` retourne `SnapshotLoadResult(UniverseOutcome, int EntityCount)`** au lieu de `void` (nouveau fichier
  `SnapshotLoadResult.cs`) — répond au 🟠 « mitigation loggée non tenue » sans introduire de dépendance de
  logging dans `Agapanthe.World` : l'hôte décide. `UniverseOutcome` = `StayedUnidentified | Adopted | Kept |
  Confirmed` (le cas `throw` ne renvoie jamais rien).
- **Ordre de mutation dans `Load` corrigé** : toute la lecture/validation d'en-tête (magic, version, count,
  univers, compteur, politique) précède désormais **toute** mutation de `_universeId`/`_idRange`/`_nextGlobalId`
  — un `Load` refusé (univers différent, compteur hors plage) ne laisse plus le monde à moitié renommé. Testé par
  `Universe_BothSetAndDifferent_WorldsUniverseUnchangedAfterThrow`.
- **`UniverseIdForTest`/`IdRangeForTest` → `public UniverseId Universe`/`public GlobalIdRange IdRange`**
  (propriétés en lecture seule, `GameWorld.cs`) — un hôte peut désormais lire ce qu'il a construit ou ce qu'un
  `Load` a adopté. `NextGlobalIdForTest` reste `internal` (détail d'allocateur, pas fait d'identité — accord des
  deux audits).
- Commentaire `AotRootingSmoke` (rooting statique vs couverture d'exécution) et test anti-boxing
  (`ContactPairKeyTests.cs`, `long[]` au lieu d'`int[]`) corrigés — findings 🟡/🟠 cosmétiques des deux audits.
- **Non appliqué, noté dette explicite** : 🟠 architecte « aucun hôte ne nomme d'univers » — inhérent au périmètre
  (aucun hôte réel n'existe encore), à rattacher au premier qui en construira un. 🟡 renouvellement de bail avant
  épuisement — additif, aucune refonte requise, pas nécessaire pour ce jalon.

**Tests** : 3 nouveaux pendant W4 (`Load_ReturnsEntityCount`,
`Universe_BothSetAndDifferent_WorldsUniverseUnchangedAfterThrow`,
`Load_KeepMine_OwnRangeOverlappingLoadedIds_SpawningACollidingIdThrows` — ce dernier est la preuve du 🔴 fermé),
plus les tests W3 existants renommés `Load_NoOverride_*` → `Load_AdoptFromHeader_*` et
`Load_WithOverride_*` → `Load_KeepMine_*` pour suivre le renommage d'API.

**Gates finaux** : `dotnet build` 0 warning · `dotnet test` **530/530 verts** · `AotComponentProbe` PASS ·
capture headless (protocole inchangé) : **HDR `12638edd` et UI `03421357` reproduits à l'identique une 4ᵉ fois**
· `HeadlessSim` **`7e8dc68f5a25914c84677a7a53ad3a58` inchangé** (la refonte de `Load` est une API pure, le format
sur fil n'a pas bougé) · **re-vérifié JIT == NativeAOT** après les corrections (publish `win-x64` self-contained,
même hash) · 0 validation · 0 leak (233 ressources).

**CONVERGE (session 27, 2026-09-02)** : verdict humain **PASS** reçu sur l'ensemble MP-0b (W1→W4). Commit demandé
explicitement par l'humain. Board clos.

## Plan d'exécution W1 (à reprendre tel quel)

**Périmètre : la clé de contact, SEULE.** Ni plage d'ids (W2), ni snapshot v2 (W3).

| # | Tâche | Taille | Dépend de |
|---|---|---|---|
| W1-1 | **Tests d'abord**, écrits contre l'expression de packing du `HEAD` : (a) collision — `key(1, 2³²+2) == key(2³²+1, 2³²+2)` aujourd'hui, doit cesser ; (b) les deux packings induisent des **permutations différentes** sur les 3 paires sparse `(2³²−1, 2³²)`, `(2³²−1, 2³²+1)`, `(2³², 2³²+1)` ; (c) ordre identique au packing actuel pour toutes les paires d'ids denses | S | — |
| W1-2 | `ContactPairKey` : `internal readonly struct`, deux `ulong` (`Min`, `Max`), `IComparable<ContactPairKey>` lexicographique, fichier `src/Agapanthe.World/ContactPairKey.cs` | S | W1-1 |
| W1-3 | Bascule `GameWorld.Physics.cs` : `_pairKey` `ulong[]` → `ContactPairKey[]` (`:38`), écriture (`:333`), `Array.Sort` inchangé dans sa forme (`:347`), `EnsurePairCapacity` (`:432-442`) | S | W1-2 |
| W1-4 | Commentaire dans `AotRootingSmoke` : le 3ᵉ corps (`:583-588`) est ce qui donne `length > 1` et roote donc `ArraySortHelper<ContactPairKey, long>` sous l'ILC — le retirer supprime le rooting **en silence** | S | W1-3 |
| W1-5 | Vérification : `dotnet test`, gate 0-alloc existant (`PhysicsTests.cs:204-227`), **les deux hashes de capture inchangés** | S | W1-4 |

**Contrainte dure** : `Array.Sort<TKey,TValue>` doit passer par le **chemin générique contraint** (`IComparable<T>` sur
le struct) — jamais une `Comparison<T>` ni une instance d'`IComparer<T>` (précédent P2-M2 : `Span.Sort(structComparer)`
boxait le comparateur, ~88 B/appel).

**Gate central de W1** : `12638edd` (HDR) et `03421357` (UI) **inchangés** — c'est la preuve que la nouvelle clé
induit la même permutation tant que les ids sont denses. Protocole (ne pas omettre la dernière variable) :
`AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12`, 1280×720, Debug.

**Après W1** : feu vert humain requis avant W2 (plage d'allocation) puis W3 (snapshot v2).

## But

Deuxième sous-jalon de **MP-0**. Referme les **deux 🔴** du backlog §4quater, tous deux des problèmes d'identité :

1. **`GlobalId` est un compteur par monde** (`GameWorld.cs:79`) → deux process allouent tous deux 1, 2, 3… ; VS-1
   restaure ce compteur verbatim → **deux mondes sauvegardés sont inmergeables**.
2. **La clé de contact écrase l'id sur 32 bits** (`GameWorld.Physics.cs:333`,
   `(_pGid[j] << 32) | (uint)_pGid[k]`, commentaire *« Assumes GlobalId < 2^32 »*) → dès que les ids cessent d'être
   denses, deux entités deviennent **silencieusement la même paire de collision**.

Aucun des deux n'est cassé *aujourd'hui* : ils cassent à l'instant où l'on rend les ids process-uniques — d'où bug et
correctif dans le même jalon.

**Rayon d'action mesuré** : `GlobalId` est **`internal` à `Agapanthe.World`**, 80 références sur 11 fichiers,
**aucune hors du projet** (`Rendering`/`Graphics`/`Engine`/`Ui`/`Assets` en contiennent zéro).

## Décisions verrouillées (interview)

1. **Identité seule.** Pas de composant d'autorité/ownership : l'autorité courante exige un protocole de transfert
   pour signifier quoi que ce soit, et c'est du netcode. **L'id nomme la NAISSANCE, jamais l'autorité courante** —
   une entité née sur A puis passée à B garde un id qui dit A. Les confondre serait irréparable (ça vit dans le
   format de sauvegarde).
2. **L'id reste un `u64` opaque ; aucun découpage de bits n'est figé dans le format.** *Motivé par la cible
   **meshing dynamique** (question humaine explicite)* : les nœuds y sont éphémères et nombreux dans le temps, donc
   dépenser des bits d'id en identité de nœud est un piège — un préfixe 16 bits s'épuise en **~4,5 jours** à
   100 nœuds recyclés toutes les 10 min. Les bits hauts deviennent un **bloc de bail**, et qui les alloue est une
   politique d'exécution derrière une couture. **Solo = bloc 0, compteur depuis 1 = bit-pour-bit ce que fait le
   moteur aujourd'hui.**
3. **L'état de l'allocateur reste dans l'en-tête du snapshot mais devient surchargeable au `Load`.** Aujourd'hui
   `_nextGlobalId` confond deux questions : *« que puis-je émettre ? »* et *« que détiens-je ? »*. Sous un modèle
   entity graph, un nœud reçoit en permanence des entités qu'il n'a pas créées et ne doit **pas** faire avancer son
   allocateur en les acceptant. Garder le champ préserve le round-trip byte-identique ; la surcharge ouvre la porte.
4. **L'en-tête gagne une identité d'univers ; format v1 → v2 ; v1 refusé.** C'est la pièce qui referme réellement le
   🔴 (1) : deux univers solo comptant depuis 1 collisionnent et **rien dans les fichiers ne le révèle**. L'identité
   d'univers est un fait **par fichier**, pas par entité. Le refus de version existe déjà
   (`WorldSerialization.cs:160`). `challenge.save` des profils Rider est à regénérer — rien n'est shippé.

### Décidé pendant la planification (À FAIRE VALIDER à la revue)

- **Identité d'univers par défaut VIDE, pas aléatoire.** Un `Guid` tiré à la création ferait produire des octets
  différents à deux runs de la même scène → casse le déterminisme cross-process de VS-1 **et** le hash JIT-vs-AOT de
  `HeadlessSim` (`7c889fec`). « Univers non identifié » est un état honnête pour un monde solo ; fusionner deux
  univers anonymes est de toute façon ambigu, donc exiger qu'on les nomme est la bonne contrainte.
- **L'allocation est une PLAGE, pas une interface.** `[start, end)` fourni par l'hôte, défaut `[1, ulong.Max)`. Un
  futur coordinateur distribuant des blocs *est* exactement une plage — c'est un bail sans inventer de protocole de
  bail. **Épuisement = échec bruyant** (préférence du projet sur la dégradation silencieuse).
- **Clé de contact = struct 128 bits comparable**, triée par `Array.Sort<TKey,TValue>`. L'ordre requis —
  lexicographique `(min, max)` — est exactement une comparaison à deux champs. **0-alloc à PROUVER, pas à supposer**
  (`AllocationProbe`). Repli documenté si ça alloue ou régresse : radix LSD 16 passes sur deux `ulong[]` parallèles,
  sur le patron de `RenderList.SortByKey` (`RenderList.cs:112-145`) — **non réutilisable tel quel**, couplé aux
  champs de `RenderList`.

## Vagues

**W1 — La clé de contact.** Changée **en premier et seule**, tant que les ids sont encore denses : **tous les hashes
de capture doivent rester identiques** (preuve que le nouvel ordre de résolution est le même). Ajouter une probe
0-alloc **et le test qui échoue sur le code actuel** : deux entités dont les 32 bits de poids faible coïncident
(`1` et `2³²+1`) ne doivent **pas** former la même paire.

**W2 — La plage d'allocation.** `GameWorld` prend `[start, end)` ; le défaut reproduit exactement aujourd'hui. Échec
bruyant à l'épuisement. Test : deux mondes à plages disjointes produisent des ids disjoints.

**W3 — Snapshot v2.** Identité d'univers dans l'en-tête, surcharge d'allocateur au `Load`, v1 refusé avec message
clair. Regénérer `challenge.save`.

**W4 — Tail.** Self-review, double audit (`csharp-lowlevel` + `engine-architect` — aucune passe GPU touchée),
vérification complète, verdict humain, CONVERGE.

## Fichiers impactés (prévision)

`GameWorld.Physics.cs` (clé) · `GameWorld.cs` (plage) · `WorldSerialization.cs` (header v2 + surcharge) ·
`Components.cs` (le doc comment de `GlobalId` avertit d'un « < 2³² / compteur dense par run » qui **devient faux**) ·
`WorldSerializationTests.cs` (épingle des offsets d'octets : `bytes[4]`, `bytes[8]`, offset 32).
**`EntityRef` ne bouge pas** (enveloppe le `ulong` opaquement, `GetHashCode` hache déjà les 64 bits).
**`GlobalId` reste `internal`.**

## Vérification (DoD prévu)

0 warning · tests verts + les nouveaux (collision 32 bits, plages disjointes, round-trip v2, refus v1, probe 0-alloc)
· **captures HDR `12638edd` et UI `03421357` inchangées** (W1/W2 ne changent rien d'observable ; W3 ne touche que le
snapshot) · `HeadlessSim` JIT==AOT toujours byte-identique (**son hash CHANGERA avec l'en-tête v2** — le ré-épingler
une fois, puis il doit rester stable) · 0 validation · 0 leak · `AotComponentProbe` PASS · double audit + verdict
humain.

## Hors scope

Fusion de deux univers (le jalon la rend **détectable et possible**, pas implémentée) · protocole de bail /
coordinateur · toute notion d'autorité ou d'ownership · entity graph · réplication · persistance partielle ·
`FrameIndex` → `TickIndex` (MP-0c) · input → commandes (MP-0d).

## Conventions du projet (rappel)

.NET 10 · `TreatWarningsAsErrors` · aucun type `Vk*` hors `Agapanthe.Graphics` · aucun type Arch hors
`Agapanthe.World` · **`Agapanthe.Engine` ne référence que `{Core, World}`** (`EngineIsHeadlessTests`).
**Gates bloquants** : 0 warning · 0 validation · 0 leak · 0 alloc/frame · NativeAOT PASS · double audit · verdict
humain. **Commits/push sur demande explicite UNIQUEMENT.**

> **Captures** : `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 AGAPANTHE_DROP_EVERY=12`,
> 1280×720, Debug. **`AGAPANTHE_DROP_EVERY` vaut 30 par défaut — l'omettre change la scène et les deux hashes.**
> **AOT publish** : préfixer le PATH avec le dossier de **`vswhere.exe`**
> (`/c/Program Files (x86)/Microsoft Visual Studio/Installer`), pas celui de MSVC.
