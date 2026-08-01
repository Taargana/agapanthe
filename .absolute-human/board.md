# Absolute-Work Board — Agapanthe (entre jalons)

**Status**: ⚪ **VS-3 CLOS et archivé** (`archive/board-session24-VS3.md`). En attente du prochain jalon.

**Sessions passées** : S1–S24 → `archive/` (S22 = VS-1, S23 = VS-2, S24 = VS-3 — tous clos).
**HEAD attendu** : VS-3 commité (finition + docs) au-dessus de `09637b5` (VS-2).

## Prochain jalon — MP-0 (fondations d'autorité) · cap « vrai engine »

**RÉORIENTATION session 25** — la Vertical Slice a prouvé l'intégration (**VS-1 · VS-2 · VS-3 CLOS**) ;
**VS-4 (HUD) et VS-5 (audio) sont EN PAUSE**. Nouveau cap : **backlog §4quater — faire d'Agapanthe un vrai engine**.

**Ancrages humains (S25)** : l'artefact = **le moteur** · généraliste **mais** spécialement sims spatiales grande
échelle (Stardew-like doit rester faisable) · **multijoueur maintenant, serveur autoritaire** · **massif/persistant
visé, petite coop possible** → topologie = choix de **déploiement**, jamais d'architecture.

**MP-0 — les 5 décisions quasi irréversibles** (détail : `docs/BACKLOG.md` §4quater) :
1. 🔴 `GlobalId` = compteur local → **64 bits partitionné** (poids fort = shard ; solo = 0)
2. 🔴 clé de contact physique `(gid << 32) | (uint)gid` **écrase l'ID sur 32 bits** → deux clés parallèles
3. 🟠 **split headless** (`Engine` référence `Rendering` + `Graphics`)
4. 🟠 **input → commandes horodatées**
5. 🟠 **tick de simulation découplé** de la frame (accumulator + interpolation)

**Puis** : `Agapanthe.App` (host + contrat `Game`) → contenu (assets stables, cook, prefabs/scènes, data-driven) →
**2ᵉ slice dissemblable** (top-down, test de généralité) → texte/UI, audio, queries physiques, job system → netcode.

**Brainstorm demandé en parallèle (S25)** : **texte & UI** — piste humaine FreeType + XAML baké à la compilation ;
points à trancher : cook offline vs runtime, MSDF vs bitmap, shaping (FreeType n'en fait pas), deux couches d'UI
(immédiate/debug vs retenue/jeu), source generator XAML→C# (AOT-pur).

**Pour démarrer** : **absolute-brainstorm** (lira `docs/AVANCEMENT.md` § Reprise + `docs/BACKLOG.md` §4quater),
puis absolute-work générera le board de session.

## Dettes reportées (à garder en vue)

- 🟠 **latch Won/Lost VS-3 non-monotone à travers un reload** (état re-dérivé du monde ; un-win/un-lose possible si un
  probe glisse hors/dans la zone entre save et reload — alternative `.state` 1 octet déférée).
- 🟠 **pas de lifetime/rest-cull des corps runtime** (VS-2 m2 — croissance non bornée ; nécessaire pour l'ancre persistante).
- 🟡 **fingerprint d'assets** au load VS-1 (`Generation` non validée → ordre de chargement différent casse en silence).
- 🔴 **Linux jamais validé** (P3-M0, non bloquant pour la slice).

## Project Conventions (rappel)

.NET 10, `dotnet build` / `dotnet test`, `TreatWarningsAsErrors`. Aucun type Arch hors `Agapanthe.World`. Aucun `Vk*`
hors `Agapanthe.Graphics`. NativeAOT-pur (dispatch switch concret). Gates bloquants : 0 warning · 0 message de
validation · 0 leak ResourceTracker · 0 alloc/frame régime stable. Commits/push **sur demande explicite uniquement**.
AOT publish : préfixer PATH avec le VS Installer (`vswhere.exe`). Board archivé par session dans `.absolute-human/archive/`.
