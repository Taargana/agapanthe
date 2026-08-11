# Absolute-Work Board — Agapanthe (entre jalons)

**Status**: ⚪ **UI-1 CLOS et archivé** (`archive/board-session25-UI1.md`). En attente du prochain jalon.

**Sessions passées** : S1–S25 → `archive/` (S22 = VS-1, S23 = VS-2, S24 = VS-3, S25 = UI-1 — tous clos).
**Rollback point** : `2c7b2be` (dernier commit poussé). **UI-1 n'est PAS encore commité.**

## ⚖️ Décision qui attend l'humain : MP-0 ou UI-2 ?

Les deux sont prêts à démarrer, et **aucun ne bloque l'autre**.

**MP-0 — fondations d'autorité** (backlog §4quater, **pas de spec — brainstorm à faire**)
Les 5 décisions quasi irréversibles : 🔴 `GlobalId` compteur local → 64 bits partitionné · 🔴 clé de contact physique
qui écrase l'ID sur 32 bits → deux clés parallèles · 🟠 split headless (`Engine` référence `Rendering`+`Graphics`) ·
🟠 input → commandes horodatées · 🟠 tick sim découplé de la frame.
*Atout* : la spec UI-1 lui a légué un **cahier des charges d'entrée pour l'input** (annexe de
`docs/plans/2026-08-03-text-ui-design.md`) — capture inconditionnelle au clic, enum `Key` maison, DPI, etc.

**UI-2 — DebugOverlay + profiler CPU** (spec **déjà écrite et approuvée**, prêt pour `absolute-work`)
`FrameStats` (ring de frame time, **octets alloués par frame = le gate 0-alloc rendu visible en continu**), graphes
en rects, bascule `F3`, remplace le HUD `window.Title` et supprime le hack de cession de titre.
*Prérequis* : ✅ **fait** — la synchronization validation est active, donc toute passe ajoutée par UI-2 sera vérifiée.

## Dettes reportées (à garder en vue)

- ~~🟠 Synchronization validation non activée~~ ✅ **soldée (S25)** : active par défaut en Debug
  (`AGAPANTHE_SYNC_VALIDATION=0` pour l'éteindre). A révélé et fait corriger **3 hazards préexistants** de la boucle
  de frame (acquire au mauvais stage · signal de présent trop tôt · `Undefined` sans access mask sur le depth
  partagé). Captures inchangées → aucun pixel affecté.
- 🟠 `MaxStorageBuffers` : 12 des 16 descripteurs per-frame consommés — UI-2/UI-3 déborderont.
- 🟠 **latch Won/Lost VS-3 non-monotone à travers un reload** · **pas de lifetime/rest-cull des corps runtime** (VS-2).
- 🟡 Pas d'échec bruyant si aucun format sRGB de swapchain · troncature silencieuse > 256 glyphes par appel ·
  test d'alignement `UiQuad` qui verrouille la taille mais pas les offsets · **fingerprint d'assets** au load VS-1.
- 🔴 **Linux/macOS jamais validés** (P3-M0).

## Project Conventions

.NET 10 · `dotnet build` / `dotnet test` · `TreatWarningsAsErrors` · Aucun type `Vk*` hors `Agapanthe.Graphics` ·
aucun type Arch hors `Agapanthe.World` · NativeAOT-pur · **0 warning · 0 message de validation · 0 leak
ResourceTracker · 0 alloc/frame régime stable**. Env : `AGAPANTHE_MAX_FRAMES=N` · `AGAPANTHE_CAPTURE=out.ppm` (cible
HDR, **ne voit pas l'UI**) · `AGAPANTHE_CAPTURE_UI=out.ppm` (image présentée, overlays compris).
AOT publish : préfixer le PATH avec le VS Installer (`vswhere.exe`).
**Commits/push sur demande explicite UNIQUEMENT.**

## Pour démarrer un jalon

**absolute-brainstorm** si le jalon n'a pas de spec (MP-0), sinon **absolute-work** directement sur la spec
existante (UI-2 → `docs/plans/2026-08-03-text-ui-design.md`).
