# Absolute-Work Board — Agapanthe Session 26 (MP-0b : identité d'entité)

**Status**: 🟠 **BRAINSTORM TERMINÉ, spec NON écrite.** 4 décisions verrouillées par interview, confiance 100 %.
Aucun code écrit, aucun fichier de production touché. Dernier jalon clos et poussé : **MP-0a** (`93415f8`).

## ⏸️ REPRISE — où repartir

**À faire au démarrage, dans l'ordre :**
1. **Écrire la spec complète en anglais** → `docs/plans/2026-08-13-mp0b-entity-identity-design.md`, à partir des
   décisions ci-dessous (patron : la spec MP-0a, `2026-08-13-mp0a-headless-split-design.md`).
2. **Revue scorée** par un agent reviewer indépendant (générateur ≠ évaluateur, seuil **4,0/5**), findings appliqués.
   *Rappel des deux tours de MP-0a : la v1 avait bâti son gate central sur un **commentaire** au lieu du code
   exécutable. Exiger que chaque affirmation cite une **ligne exécutable**.*
3. **`absolute-work` sur W1** (la clé de contact).

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
