# Absolute-Work Board — Agapanthe Session 26 (MP-0b : identité d'entité)

**Status**: 🟢 **SPEC ÉCRITE ET APPROUVÉE (4,4/5, 2 tours).** 4 décisions verrouillées par interview, confiance 100 %.
Aucun code écrit, aucun fichier de production touché. Dernier jalon clos et poussé : **MP-0a** (`93415f8`).

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
3. ⏹️ **W1 NON DÉMARRÉ — arrêt demandé par l'humain avant toute écriture de code.**
   `absolute-work` avait été lancé et était en **Phase 3 (DECOMPOSE & PLAN)** ; aucun fichier de production touché,
   aucun test écrit. Reprendre au plan d'exécution ci-dessous.

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
