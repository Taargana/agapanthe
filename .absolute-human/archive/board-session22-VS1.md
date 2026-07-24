# Absolute-Work Board — Agapanthe Session 22 (VS-1 : sérialisation du World)

**Status**: ✅ **CLOS.** Wave 1+2+3 done, **double audit PASS** (`csharp-lowlevel` 0 🔴/🟠 · `engine-architect` 1 🟠 + 6 🟡),
findings appliqués, **verdict humain fonctionnel PASS**. 346 tests, 0 warning/validation/leak, AOT probe PASS,
round-trip cross-process byte-identique JIT+AOT. Premier jalon de la Vertical Slice livré.
**Sessions passées** : S1–S21 → `archive/` (S21 = P3-M8, clos + poussé `ba498f6`). Ce board → `archive/board-session22-VS1.md`.

**But** : `world.Save(Stream)` / `world.Load(Stream)` — round-trip **byte-identique, déterministe, AOT-pur, 0 leak**.
Premier jalon de la **Vertical Slice** (backlog §4ter), preuve de persistance (DoD item 3).
**Spec** : [../docs/plans/2026-07-24-vs1-world-serialization-design.md](../docs/plans/2026-07-24-vs1-world-serialization-design.md)

## Décisions verrouillées (spec §3)

- **Seam GPU = handles reproductibles** (Option 1) : snapshot stocke les handles bruts, le load recharge les mêmes
  assets d'abord. Clés d'asset stables déférées (streaming/prefabs).
- **Format binaire blittable** (`MemoryMarshal`), **pas de générateur** (structs blittables), style écrit-à-la-main
  `ComponentRegistry` + test-garde exhaustivité **et ordre** (append-only sinon bump version, R1).
- **Inclus** : entités + composants + `_nextGlobalId`. **Exclus** : `InstanceSlot` (runtime), file de commandes
  (flush avant save), settings physique/caméra.
- **`Parent` remappé par GlobalId** (réutilise `LinkParent`). Load sur World frais uniquement.

## Critère de sortie

Round-trip unit (tous archétypes) + **byte-identique `Save(Load(bytes))==bytes`** · garde exhaustivité+ordre · cas
d'erreur (magic/version/count/tronqué/mask hors-plage/World non vide) · **probe AOT** save/load · intégration Sandbox
(save planète → reload process neuf → capture identique) · 0 warning/validation/leak · 0 alloc hot-path · NativeAOT
PASS · double audit (`csharp-lowlevel` + `engine-architect`) + **verdict humain**.

## Tâches

### Wave 1 — Cœur sérialisation (GPU-free, TDD) · `done` ✅
**VS-01** · `done` ✅ — `WorldSerialization.cs` (header `AGWD`/version/count/nextId/entityCount, 3 switches concrets
Has/Write/ReadAdd sur l'ordre `ComponentRegistry.All`, blittable `MemoryMarshal` LE) + `WorldSerializationException`.
**Garde d'ordre** `ComponentRegistry_All_MatchesTheFrozenOrder` (`SequenceEqual`, append-only) — complète l'exhaustivité `HashSet`. R1.
**VS-02** · `done` ✅ — `Save` (flush → gather trié GlobalId → `Parent`→GlobalId, `InstanceSlot` sauté) + `Load` (World frais ;
compteur restauré **sans bump** R3 ; passe 1 create + `InstanceSlot=-1` drawables ; passe 2 `LinkParent`). Tests : round-trip
tous archétypes, **byte-identique `Save(Load(bytes))==bytes`** R2, remap `Parent` par GlobalId, déterminisme, re-collect.
**VS-03** · `done` ✅ — 6 cas d'erreur (magic/version/count/**tronqué EOF**/**mask hors-plage**/World non vide) → `WorldSerializationException`. R4.
**Gate Wave 1** : **344 tests** (+11), 0 warning, build vert, 0 régression.

### Wave 2 — AOT + intégration Sandbox · `done` ✅
**VS-04** · `done` ✅ — `GameWorld.AotSerializationSmoke()` (round-trip tous archétypes, byte-identique) appelé par
`AotComponentProbe` + test JIT. **AOT probe PASS** : `IsDynamicCodeSupported=False`, restored 5, byte-identique →
dispatch `Add<T>` + `MemoryMarshal` rootés.
**VS-05** · `done` ✅ — Sandbox `AGAPANTHE_SAVE=<path>` (snapshot après build, world.Save flush interne) /
`AGAPANTHE_LOAD=<path>` (force scène planète, charge assets puis `SetupPlanetScene(spawnEntities:false)` + `world.Load`
— contrat Option 1). `GameWorld.LiveEntityCount` public.
**Gate Wave 2 — preuve d'intégration cross-process** : save planète (2 entités, 314 o) → **reload process neuf** →
**captures byte-identiques** (`0034af33…`) en **JIT ET AOT**, 0 validation, 0 leak. **345 tests** (+1).

### Wave 3 — audits + verdict · `done` ✅ (verdict humain PASS)
**Double audit (session 22)** — les deux **PASS**, 0 🔴 :
- `csharp-lowlevel` **PASS** (0 🔴/🟠) : dispatch AOT rooté, blittable sûr, `Load` robuste (troncature/mask hors-plage), 0 leak, déterminisme total. 5 🟡.
- `engine-architect` **PASS** (1 🟠, 6 🟡) : étanchéité Arch OK, remap `Parent` correct, archétypes reconstruits fidèles (Arch clé par ensemble), `_nextGlobalId` restauré sans bump, seam Option 1 bien matérialisé.

**Findings appliqués** :
- 🟠 garde build **`ComponentRegistry.All.Count ≤ 32`** (u32 mask ; test, spec §4 tenue).
- 🟡 garde **GlobalId dupliqué** (`_live.TryAdd` lève sur fichier forgé).
- 🟡 doc endianness reformulée (header LE ne détecte pas un mismatch ; sûreté = invariant all-LE) — code + spec §4.2.
- 🟡 commentaires : padding déterministe de `LocalTransform`, invariant `InstanceSlot⟺MeshRef`, `Load` échoué → World à jeter.
- 🟡 `.gitignore` : `*.ppm` / `*.sav`.
- 🟡 notés/déférés : source-unique émergente (switch vs registre), `GlobalId` écrit ×2 (inoffensif), Generation Option 1 non exploité (→ [backlog], fingerprint d'assets futur), garde fresh-world plus permissive (inoffensif).

**Gate Wave 3** : **346 tests** (+1), 0 warning/validation/leak, **AOT probe PASS**, **round-trip cross-process byte-identique JIT+AOT** (`0034af33…`). **Verdict humain dû** (save/load fonctionnel).

## Rollback Point
Avant que Wave 1 touche un fichier : commit `ba498f6` (arbre propre, P3-M8 poussé).

## Clôture (CONVERGE)
Double audit findings appliqués · verdict humain · maj AVANCEMENT/BACKLOG/CLAUDE · board archivé
`archive/board-session22-VS1.md` · commit sur demande.
