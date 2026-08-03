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

## ✅ Brainstorm Texte & UI — TERMINÉ + spec **APPROUVÉE 4,4/5** (S25), implémentation non commencée

**Spec** : [`docs/plans/2026-08-03-text-ui-design.md`](../docs/plans/2026-08-03-text-ui-design.md) — 10 décisions
verrouillées en interview. **Revue scorée indépendante passée** : v1 3,6/5 (Needs Work) → 14 findings appliqués →
**v2 4,4/5 Approved**, 6 résiduels appliqués. Prête pour l'implémentation, **UI-1 exécutable directement via
absolute-work**.

Décisions clés : périmètre = **texte & overlay SANS interactivité** (l'UI interactive est bloquée derrière l'input →
MP-0) · cook **hors-ligne** (imposé par le code : Release interdit la compilation runtime des shaders) · atlas **SDF
via `StbTrueTypeSharp`** = **zéro dépendance native même dans l'outil** (FreeType écarté) · **Latin + kerning avec
seam de shaping** (`Rune`) · nouveau projet **`Agapanthe.Ui` GPU-free** + **`tools/FontCooker`** + format **`.agfont`**
binaire mono-fichier · livrable = primitif + **DebugOverlay** + **profiler CPU & GPU**.

**3 jalons séquencés** (feu vert humain entre chacun) :
- **UI-1** — texte à l'écran (`R8Unorm` + `BlendMode` dans Graphics, FontCooker, `.agfont`, `Agapanthe.Ui`, `UiPass`)
- **UI-2** — DebugOverlay + profiler CPU (frame time, **alloc/frame = le gate 0-alloc visible en continu**) ;
  remplace le HUD `window.Title` et supprime le hack de cession de titre
- **UI-3** — timestamps GPU (`QueryPool`, détection de capacité + dégradation gracieuse, **abandonnable**)

**À faire au démarrage de la prochaine session (automode)** :
1. ~~Revue scorée~~ ✅ **faite** (4,4/5 Approved, findings appliqués).
2. **Arbitrer l'ordre** : MP-0 d'abord (ordre du backlog §4quater) ou UI-1 d'abord ? La spec a tranché l'analyse :
   `Agapanthe.Ui` est **immune** au split headless de MP-0 et `UiRenderSystem`/`Profiler` sont **pré-affectés à la
   moitié « render »** → **UI-1 avant MP-0 ne crée aucun rework**, et donne des diagnostics à l'écran qui rendront le
   travail de MP-0 (découplage tick/frame, timing des commandes) bien plus observable. **Décision humaine.**
3. **absolute-work** sur le jalon retenu → génère le board de session.

## Pour démarrer un jalon

**absolute-brainstorm** si le jalon n'a pas de spec (lira `docs/AVANCEMENT.md` § Reprise + `docs/BACKLOG.md` §4quater),
sinon **absolute-work** directement sur la spec existante.

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
