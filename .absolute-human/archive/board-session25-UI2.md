# Absolute-Work Board — Agapanthe Session 25 (UI-2 : DebugOverlay + profiler CPU)

**Status**: ✅ **CLOS** — 3 vagues + **double audit** (`csharp-lowlevel` PASS-with-concerns · `engine-architect`
PASS-with-concerns **3,8/5**, aucun 🔴) avec **tous les findings 🟠 appliqués** et les 🟡 de correctness ·
CONVERGE fait (`AVANCEMENT`/`BACKLOG`/`CLAUDE`) · feu vert humain · commité.

**Gates à la clôture** : **468/468 tests** (3 runs consécutifs) · 0 warning · **HDR `12638edd`
inchangé** · **capture UI overlay-masqué `034213575932dabcff41c2e0c72addfa`** reproductible · 0 leak · 0 message de
validation · **0 hazard** (sync validation active) · **AOT PASS** (`AotProfilerSmoke` ajouté, `StbTrueTypeSharp`
absent du publish).

### Corrections d'audit appliquées (résumé)

- 🟠 **Bracket de mesure** : il se fermait DANS `RecordCommandBuffer`, donc avant `vkEndCommandBuffer`/submit/present
  (angle mort), et ne se fermait **jamais** sur les frames où `DrawFrame` sort tôt (resize) → l'overlay affichait
  « 0 B » en vert pendant qu'une swapchain était recréée par frame. → **`FrameOrchestrator.EndFrame()`** appelé
  **après** `DrawFrame` : un seul correctif pour les deux, et c'est exactement le bracket du banc.
- 🟠 **Échelle du graphe d'alloc** : suivait le `Max` de la fenêtre → un pic de warm-up à 400 Kio rendait une
  régression de 512 B/frame **invisible 4 s** (0,04 px). Échelle plancher + toute barre non nulle ≥ 1 px.
- 🟠 **Hypothèse mono-thread ancrée** (`Debug.Assert` + clamp ≥ 0) : le compteur est PAR THREAD, et un delta négatif
  se lirait comme un graphe parfaitement sain. MP-0 est le jalon qui casserait ça.
- 🟠 **`FrameStats` déplacé vers l'orchestrator** : possédé par l'overlay, il n'existait pas sans police sur disque.
- 🟠 **`TextBuilder` extrait en public dans `Agapanthe.Ui`** : primitif 0-alloc réutilisable, il était `internal`.
- 🟠 **Flakiness** : cause réelle = init paresseuse d'état partagé × parallélisme xUnit (le compteur lui-même est
  exact ; le JIT tiered alloue en natif). → `AllocationProbe` : 3 rondes, assertion sur le **minimum**.
- 🟡 NaN traversait `Math.Clamp` dans `Sparkline` (primitif **public**) · octets exacts en `long` pour le texte
  (`float` perd la précision entière > 16 Mio) · `stackalloc` dimensionné sur l'entrée (~36 Kio de memset/frame
  évités — **`[SkipLocalsInit]` écarté** : il exige `AllowUnsafeBlocks`, or `Agapanthe.Ui` est managé pur) ·
  commentaire mort + `using` inutile · probe AOT étendue.

### Dette déclarée, NON traitée (volontaire)

🟠 **Seam `FrameProfiler`** (`engine-architect` F3/F5) : les `LastXxx` vont s'accumuler sur l'orchestrator et
**UI-3 cassera `Record(float, long)`** — les timestamps GPU arrivent à N+2 et désaligneraient les séries. Refactor
d'une demi-journée qui **appartient à UI-3**, où la forme du besoin sera connue plutôt que devinée.
`DebugOverlaySystem` reste sans tests pour la même raison (il dépend de l'orchestrator concret).
🟡 Coût propre de l'overlay inclus dans le `ms` qu'il affiche · `UiDrawList` 2048 quads = 80 Kio, à 6 % du seuil LOH
(85 000) — ne pas doubler sans y penser.
**Spec** : [`docs/plans/2026-08-03-text-ui-design.md`](../docs/plans/2026-08-03-text-ui-design.md) — **APPROVED 4,4/5**,
section « Milestones » → UI-2. **Ne pas re-litiger ses décisions.**
**Rollback point** : `141e374` (arbre propre, tout poussé).
**Sessions passées** : S1–S25 → `archive/` (S25 = UI-1, clos + poussé `d71607b`).

**But** : deuxième des 3 jalons Texte & UI. Transformer le HUD codé en dur dans l'échantillon en **feature de moteur
réutilisable**, et rendre le **gate 0-alloc visible en continu à l'écran** au lieu d'être vérifié seulement en banc.
**UI-3 (timestamps GPU, `QueryPool`) est HORS SCOPE.**

## Ce que le scan a établi (ne pas re-vérifier)

- ✅ **La dette `MaxStorageBuffers` (12/16) ne mord PAS ici** : UI-2 n'ajoute **ni passe ni descriptor set** — l'overlay
  écrit dans la `UiDrawList` existante, donc le même `UiPass` et le même set unique, déjà comptés. Aucune tâche infra.
- ✅ **`TickContext` porte `DeltaSeconds`** → le profiler tient dans un `ISystem`, sans plomberie côté application.
- ✅ **`Stopwatch.GetTimestamp`/`GetElapsedTime`** sont struct-based et sans allocation (déjà utilisés, `Renderer.cs:722`).
- ✅ Compteurs déjà exposés : `Renderer.LastSceneDrawCalls` / `LastShadowDrawCalls` / `LastSceneCpuVisible`.
- ⚠️ **Troncature silencieuse au-delà de 256 glyphes par appel** de `TextLayout` : un HUD de 6 lignes × 40 caractères
  ferait 240 glyphes en un seul appel — trop près du plafond. → **une ligne = un `DrawText`**, la limite disparaît.
- ⚠️ Le HUD actuel (`Program.cs:762-775`) est **throttlé à 0,25 s** et cède le titre au `LandingChallengeSystem`
  (`&& landingChallenge is null`). Les deux disparaissent : en 0-alloc, le HUD se rafraîchit **chaque frame**.

## Décisions de conception (à valider au gate)

- **`FrameStats` dans `Agapanthe.Engine`** : ring buffers de frame time **et** d'octets alloués. L'allocation se
  mesure par delta de `GC.GetAllocatedBytesForCurrentThread()` entre deux appels du système — appelé une fois par
  frame, le delta EST l'allocation d'une frame entière. **Le profiler ne doit rien allouer lui-même**, sans quoi il
  fausse la mesure qu'il affiche.
- **Le primitif de graphe va dans `Agapanthe.Ui`** (`ReadOnlySpan<float>` + rect → rects) : c'est de la logique pure,
  GPU-free et unit-testable, comme le reste de `Ui`. `DebugOverlay` (Engine) ne fait que composer texte + graphes.
- **Formatage 0-alloc** : `TryFormat` + `stackalloc` → `ReadOnlySpan<char>`. Jamais de `string` interpolée par frame.
- **Bascule `F3`** via `KeyPressed` (edge-triggered, déjà disponible) — **aucun besoin de la refonte d'input**, qui
  appartient à MP-0.

## Critère de sortie (DoD)

HUD in-view remplaçant `window.Title` (et le hack de cession supprimé) · graphes lisibles · **capture HDR
`12638edd` INCHANGÉE** (non-régression scène) · **hash de capture UI reproductible AVEC `AGAPANTHE_OVERLAY=0`**
= **`034213575932dabcff41c2e0c72addfa`** — décision humaine : un profiler affichant des timings réels ne peut pas
être byte-identique, donc le gate déterministe porte sur la passe UI et le chemin de capture (overlay masqué), et
l'overlay lui-même est validé par le verdict humain + la preuve que le diff inter-runs est confiné au panneau
(432 px / 921 600) · **0 alloc/frame prouvé, profiler
compris** · tests GPU-free (ring de stats + graphe) · **0 message de validation** (sync validation désormais active) ·
0 leak · NativeAOT PASS · **double audit `csharp-lowlevel` + `engine-architect`** (retour à la paire standard
`CLAUDE.md` : la déviation `graphics-3d` d'UI-1 était motivée par une nouvelle passe GPU, il n'y en a pas ici) ·
verdict visuel humain.

## Project Conventions

.NET 10 · `dotnet build` / `dotnet test` · `TreatWarningsAsErrors` · Aucun type `Vk*` hors `Agapanthe.Graphics` ·
aucun type Arch hors `Agapanthe.World` · NativeAOT-pur. **Gates bloquants** : 0 warning · 0 message de validation ·
0 leak ResourceTracker · 0 alloc/frame régime stable. Env : `AGAPANTHE_MAX_FRAMES=N` · `AGAPANTHE_CAPTURE=out.ppm`
(HDR, **ne voit pas l'UI**) · `AGAPANTHE_CAPTURE_UI=out.ppm` (image présentée) · `AGAPANTHE_SYNC_VALIDATION=0`.
AOT publish : préfixer le PATH avec le VS Installer (`vswhere.exe`).
**Commits/push sur demande explicite UNIQUEMENT.**

## Tâches (DAG + vagues)

### Wave 1 — Baseline + fondations pures (fichiers disjoints) — ✅ CLOSE

**UI-2-01** ✅ **DONE** · `infra` · S · deps: — · **doit précéder toute modification**
> **BASELINES** : HDR **`12638eddd7f3f67ab161b298ffbcd15e`** · UI **`6e14b23e3859d17a6ec27d82274735ba`**
> (`planet-drop`, DROP_EVERY=12, MAX_FRAMES=420, 1280×720). Conformes aux valeurs héritées d'UI-1.
> UI-2-07 doit retrouver le hash **HDR exact** (non-régression scène) ; le hash UI changera forcément (l'overlay
> remplace le HUD), seule sa **reproductibilité** est exigée.
Capturer les **deux baselines** avant de toucher au code : HDR (`AGAPANTHE_CAPTURE`, attendu `12638edd`) et UI
(`AGAPANTHE_CAPTURE_UI`, attendu `6e14b23e`), scène `planet-drop` figée. Les consigner ici.

**UI-2-02** ✅ **DONE** · `code`+`test` · M · deps: — · fichiers: `src/Agapanthe.Engine/FrameStats.cs` (nouveau), `tests/*`
Ring buffers de frame time (ms) et d'octets alloués, capacité fixe, écrasement circulaire. Expose `Record(dt)`,
les séries en `ReadOnlySpan<float>`, et des agrégats (dernier, moyenne, max) calculés **sans allocation**.
Tests GPU-free : remplissage, écrasement circulaire, agrégats sur série connue, **0-alloc-after-warmup**
(patron `tests/Agapanthe.Tests/SurfaceContactsTests.cs:97`).

**UI-2-03** ✅ **DONE** · `code`+`test` · S · deps: — · fichiers: `src/Agapanthe.Ui/Sparkline.cs` (nouveau), `tests/*`
Primitif de graphe GPU-free : `ReadOnlySpan<float>` + rect écran + échelle → barres via `UiDrawList.AddRect`
(texel blanc de l'atlas → même pipeline, même draw call). Gère série vide, valeurs identiques (échelle dégénérée),
et **borne le nombre de barres** à la largeur en pixels. Tests : nombre de quads, bornes du rect, série vide,
échelle plate, 0-alloc.
*(fichiers disjoints de UI-2-02 → parallèle-safe.)*

### Wave 2 — L'overlay moteur — ✅ CLOSE

> **DÉCOUVERTE : la fenêtre de mesure était fausse.** L'overlay affichait **272-288 B/frame** là où le banc
> (`AGAPANTHE_CULL_STATS`) rapportait **0 B**. Les deux étaient honnêtes : l'overlay mesurait d'un `PostSimulation`
> au suivant, englobant le pump d'événements Silk.NET/GLFW — du code que le moteur ne contrôle pas. Or le gate porte
> sur le **hot path du moteur**. Un indicateur perpétuellement rouge sans faute du moteur est inutile, et pire :
> il masquerait une vraie régression. → **`FrameOrchestrator` mesure désormais sa propre frame** (`Tick` → fin du
> render delegate), exactement le bracket du banc ; `FrameStats.Record` **reçoit** les mesures au lieu de les
> échantillonner. L'overlay lit la frame précédente (1 frame de retard, invisible sur un graphe).
>
> **2ᵉ correctif** : la ligne d'alloc était colorée sur le **peak**, qui reste 240 frames dans la fenêtre → rouge
> longtemps après le retour à la normale. Colorée sur la **frame courante**. Et la `UiDrawList` est pré-dimensionnée
> à 2048 quads : sa croissance par doublement pendant le remplissage des graphes apparaissait comme de l'allocation
> dans le readout même qui surveille l'allocation (peak **25128 B → 520 B**).
>
> **Résultat** : `alloc 0 B/frame` en vert sur scène statique, une barre rouge isolée marquant la seule frame qui a
> alloué. **HDR `12638edd` inchangé.**
>
> ⚠️ **La capture UI n'est PAS reproductible** — inhérent : l'overlay affiche des timings temps réel. Diff mesuré
> entre deux runs : **432 pixels sur 921 600 (0,0005 %), tous dans le panneau** ; la scène est identique.
> → DoD à ajuster (voir gate).
>
> ⚠️ **Flakiness observée** : un run de `dotnet test` a échoué (1/466) puis 4 runs consécutifs sont passés. Le test
> fautif n'a pas pu être identifié (sortie perdue). Suspicion : un des tests 0-alloc, sensible au bruit du host.
> **À signaler à l'audit** — un gate instable est un mauvais gate.

**UI-2-04** ✅ **DONE** · `code` · M · deps: UI-2-02, UI-2-03 · fichiers: `src/Agapanthe.Engine/DebugOverlaySystem.cs` (nouveau)
`ISystem` en `Stage.PostSimulation` : alimente `FrameStats` (dt + delta d'allocation), lit les compteurs du
`Renderer`, et compose l'overlay dans la `UiDrawList` de `UiRenderSystem` — **une ligne = un `DrawText`** (plafond
256 glyphes), plus les deux graphes. Propriété `Visible` (bascule). **Formatage `TryFormat`+`stackalloc`
exclusivement.** Quand invisible : ne dessine rien **et n'alloue rien**, mais **continue d'enregistrer les stats**
(sinon l'historique se troue et le graphe ment au rallumage).

**UI-2-05** ✅ **DONE** · `code` · S · deps: UI-2-04 · fichiers: `samples/Sandbox/Program.cs`
Enregistrer le système, brancher **`F3`** sur `Visible`, **supprimer le HUD `window.Title`** (throttle, compteurs
`hudElapsed`/`hudFrames`) **et le hack de cession** `&& landingChallenge is null`. Le titre devient statique.
*(`LandingChallengeSystem` garde le sien — c'est du gameplay, hors périmètre.)*

### Wave 3 — Tail (obligatoire)

**UI-2-06** · Self review du diff + **double audit** `csharp-lowlevel` (0-alloc du profiler LUI-MÊME, ring buffers,
`TryFormat`, absence de `string` par frame) + `engine-architect` (placement Engine vs Ui, contrat `ISystem`,
couplage au `Renderer`, réutilisabilité réelle hors Sandbox). Appliquer les findings 🔴/🟠.

**UI-2-07** · Requirements validation (DoD) + **Full verification** : `dotnet build` (0 warning) + `dotnet test` +
Sandbox headless (**0 validation avec sync validation active**, 0 leak) + **capture HDR identique à UI-2-01** +
hash de capture UI reproductible + **0 alloc/frame mesuré** + probe AOT PASS + **verdict visuel humain**.

## DAG

```
UI-2-01 (baselines)
UI-2-02 ─┐
         ├─► UI-2-04 ─► UI-2-05 ─► UI-2-06 ─► UI-2-07
UI-2-03 ─┘
Wave 1 (∥ safe)   Wave 2          Wave 3 (tail)
```

## Rollback Point

`141e374` (arbre propre, tout poussé).

## Deferred Work

Hors scope : **UI-3** (timestamps GPU `QueryPool`) · métriques GPU · graphes historisés sur disque · UI interactive
(→ MP-0) · clipping/scissor · multi-fontes.
Dettes non traitées ici : `MaxStorageBuffers` 12/16 (ne mord pas sur ce jalon) · troncature > 256 glyphes (contournée
par « une ligne = un `DrawText` », pas corrigée) · test d'alignement `UiQuad` sans offsets.

## Clôture (CONVERGE)

Findings appliqués · verdict humain · maj `AVANCEMENT`/`BACKLOG`/`CLAUDE` · board archivé
`archive/board-session25-UI2.md` · **commit sur demande**.
