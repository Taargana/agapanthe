# Absolute-Work Board — Agapanthe (entre jalons)

**Status**: ⚪ **VS-3 CLOS et archivé** (`archive/board-session24-VS3.md`). En attente du prochain jalon.

**Sessions passées** : S1–S24 → `archive/` (S22 = VS-1, S23 = VS-2, S24 = VS-3 — tous clos).
**HEAD attendu** : VS-3 commité (finition + docs) au-dessus de `09637b5` (VS-2).

## Prochain jalon — VS-4 (HUD minimal)

Cap : **Vertical Slice** (backlog §4ter). **VS-1 (sérialisation) · VS-2 (spawn + gravité newtonienne) · VS-3 (glu
gameplay / défi d'atterrissage) = CLOS.** Suivant : **VS-4 — HUD minimal** — un overlay texte in-view (premier nouveau
chemin de rendu depuis P3-M8) ; aujourd'hui le défi VS-3 ne parle que par `window.Title`. Puis VS-5 audio (stretch).

**Pour démarrer VS-4** : ouvrir l'interview de conception via **absolute-brainstorm** (lira `docs/AVANCEMENT.md` §
Reprise + `docs/BACKLOG.md` §4ter), puis absolute-work générera un nouveau board de session.

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
