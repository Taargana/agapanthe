# Absolute-Work Board — Agapanthe (entre jalons)

**Status**: 🟢 **Arbre propre, aucun jalon en cours.** Dernier clos : **MP-0a — headless split** (session 26),
archive : [`archive/board-session26-MP0a.md`](archive/board-session26-MP0a.md).

## Où repartir

**Lire d'abord** : [`docs/AVANCEMENT.md`](../docs/AVANCEMENT.md) § « Reprise — où repartir », puis
[`docs/BACKLOG.md`](../docs/BACKLOG.md) §4quater (le cap moteur).

**MP-0 est décomposé en 4 sous-jalons ; le premier est livré.** Les trois autres n'ont **pas de spec** — chacun
demande son brainstorm (`absolute-brainstorm`) puis sa revue scorée avant `absolute-work`.

| Sous-jalon | Ce qu'il coûte / ce qu'il débloque |
|---|---|
| **MP-0b — identité** | 🔴 `GlobalId` compteur local → **64 bits partitionné** (poids fort = shard, solo = 0) et 🔴 clé de contact physique qui écrase l'ID sur 32 bits (`GameWorld.Physics.cs:333`) → deux clés parallèles. **Entièrement contenu dans `Agapanthe.World`** (80 références, aucune hors du projet) et **non affecté par le split**. Emporte un bump de version du snapshot VS-1. `RenderStageNeutralityTests` lui offre un harnais de régression gratuit. |
| **MP-0c — autorité du temps** | Tick sim découplé de la frame (accumulator + interpolation). ⚠️ **Ce n'est pas une dette à solder mais un ÉCHANGE de modèle de déterminisme** : aujourd'hui les captures sont déterministes *par frame count* (`PhysicsSettings.cs:9-14`, délibéré) → **les baselines de capture seront invalidées**. Emporte aussi `FrameIndex` → `TickIndex` et le post-incrément de `CurrentTick`. |
| **MP-0d — input → commandes** | Le split a **créé le seam** : le type commande horodatée + la file vivent dans `Engine` (un serveur reçoit des commandes, il n'échantillonne rien), drainés par un système en `Stage.Input` — que `Stage.cs:17-22` décrit déjà comme vide côté moteur. L'échantillonnage reste dans `Platform`/l'application. Règle aussi la fuite de `Silk.NET.Input.Key` dans l'API publique d'`EngineWindow`. |

**Alternative** : **UI-3** (timestamps GPU, `QueryPool`) — spec déjà écrite et approuvée, abandonnable sans rien
casser, et solderait le seam `FrameProfiler`.

## Conventions du projet (rappel)

.NET 10 · `TreatWarningsAsErrors` · aucun type `Vk*` hors `Agapanthe.Graphics` · aucun type Arch hors
`Agapanthe.World` · **`Agapanthe.Engine` ne référence que `{Core, World}`** — `EngineIsHeadlessTests` l'impose
(allowlist statique sur le `.csproj` + « aucun `PackageReference` » + closure d'assemblies + surface publique).

**Gates bloquants** : 0 warning · 0 message de validation · 0 leak ResourceTracker · 0 alloc/frame en régime stable ·
NativeAOT PASS · **double audit** (`csharp-lowlevel` + `engine-architect`, ou `graphics-3d` si une passe GPU est
touchée) · **verdict visuel humain**. **Commits/push sur demande explicite UNIQUEMENT.**

## Deux pièges d'invocation (payés en session 26)

> **Captures** : `AGAPANTHE_SCENE=planet-drop AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_OVERLAY=0 **AGAPANTHE_DROP_EVERY=12**`,
> 1280×720, Debug. Attendus : HDR `12638eddd7f3f67ab161b298ffbcd15e`, UI `034213575932dabcff41c2e0c72addfa`.
> **`AGAPANTHE_DROP_EVERY` vaut 30 par défaut** — l'omettre change la scène et donc les deux hashes.
>
> **AOT publish** : préfixer le PATH avec le dossier de **`vswhere.exe`**
> (`/c/Program Files (x86)/Microsoft Visual Studio/Installer`), pas avec le dossier MSVC.

## Autres env vars

`AGAPANTHE_CAPTURE` (HDR — **ne voit pas l'UI**) · `AGAPANTHE_CAPTURE_UI` (image présentée) · `AGAPANTHE_OVERLAY=0` ·
`AGAPANTHE_SYNC_VALIDATION=0` · `AGAPANTHE_SCENE=planet|planet-drop|planet-challenge|drop|grid:NxN` ·
`AGAPANTHE_PHYSICS=1` · `AGAPANTHE_SAVE`/`AGAPANTHE_LOAD` · `AGAPANTHE_CULL_STATS=1`.
Serveur : `dotnet run --project samples/HeadlessSim -- --ticks N [--bodies N] [--load f] [--save f]`.
