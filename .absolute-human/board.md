# Absolute-Work Board — Agapanthe (entre jalons)

**Status**: 🟢 **Arbre propre, aucun jalon en cours.** Dernier clos : **UI-2** (overlay debug in-view + profiler CPU),
session 25 — archive : [`archive/board-session25-UI2.md`](archive/board-session25-UI2.md).

## Où repartir

**Lire d'abord** : [`docs/AVANCEMENT.md`](../docs/AVANCEMENT.md) § « Reprise — où repartir », puis
[`docs/BACKLOG.md`](../docs/BACKLOG.md) (chaque item dit *ce qui casse sans lui* et *à quelle échelle il mord*).

**Deux candidats, arbitrage humain requis** :

| Jalon | État | Ce qu'il coûte / rapporte |
|---|---|---|
| **UI-3** — timestamps GPU | **Spec écrite et approuvée** (4,4/5), 3ᵉ des 3 jalons Texte & UI | `QueryPool` net-neuf dans Graphics, instrumentation par passe, série GPU dans le profiler. **Détection de capacité + dégradation gracieuse** exigées (macOS/Linux jamais validés, P3-M0). **Abandonnable sans rien casser.** Emporte le refactor du seam `FrameProfiler` — dette déclarée d'UI-2 : les timestamps arrivent à N+2 et casseront `FrameStats.Record(float, long)`. |
| **MP-0** — fondations d'autorité | **Sans spec — brainstorm à faire d'abord** | Le vrai cap (backlog §4quater). Contient **2 décisions 🔴 quasi irréversibles** : `GlobalId` compteur local → 64 bits partitionné, et la clé de contact physique qui écrase l'ID sur 32 bits. Plus le split headless, l'input → commandes horodatées, le tick sim découplé de la frame. |

Le cap §4quater place **MP-0 devant**. UI-3 est le petit morceau qui finit une série et solde une dette nommée.

## Conventions du projet (rappel)

.NET 10 · `dotnet build` / `dotnet test` · `TreatWarningsAsErrors` · aucun type `Vk*` hors `Agapanthe.Graphics` ·
aucun type Arch hors `Agapanthe.World` · NativeAOT-pur.

**Gates bloquants** : 0 warning · **0 message de validation** (sync validation active depuis `141e374`) ·
0 leak ResourceTracker · 0 alloc/frame en régime stable · NativeAOT PASS · **double audit** (`csharp-lowlevel` +
`engine-architect`, ou `graphics-3d` si le jalon touche une passe GPU) · **verdict visuel humain**.

**Env vars utiles** : `AGAPANTHE_MAX_FRAMES=N` · `AGAPANTHE_CAPTURE=out.ppm` (HDR — **ne voit pas l'UI**) ·
`AGAPANTHE_CAPTURE_UI=out.ppm` (image présentée) · `AGAPANTHE_OVERLAY=0` (overlay masqué au démarrage — c'est le
mode du gate déterministe) · `AGAPANTHE_SYNC_VALIDATION=0` · `AGAPANTHE_SCENE=planet-drop|planet-challenge|grid:NxN`.
AOT publish : préfixer le PATH avec le VS Installer (`vswhere.exe`).

**Commits/push sur demande explicite UNIQUEMENT.**
