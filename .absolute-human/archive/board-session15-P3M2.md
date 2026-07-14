# Absolute-Human Board — Agapanthe Session 15 (P3-M2 : Scheduler + lifecycle, `Agapanthe.Engine`)

**Status**: ✅ **CLOSED (2026-07-14)** — V0→V5 livrés. Double audit **PASS** (`csharp-lowlevel` + `engine-architect`,
aucun FAIL/MAJEUR ; findings mineurs appliqués : garde F7 `_pass1ShadowList`, test D3.a resserré, spec nettoyée).
311 tests · 0 warning · 0 validation · 0 leak · 0 alloc/frame (banc + churn) · **NativeAOT PASS** (probe + Sandbox
grid:100x100) · capture bit-identique `9790D95D`. **Verdict visuel humain encore dû** (capture bit-identique à
`d1671f3`, donc non bloquant pour la clôture technique). Commit socle : `90627a5`.
*(Historique : INTAKE 3 décisions humaines · SPEC v2 après relecture v1 2,9/5 · correction D2 v3 après audit Arch 2.1.0.)*
**But** : l'**ordre de frame quitte le Sandbox** et devient un invariant du moteur, exécutable et testable ; les
entités gagnent un **cycle de vie** (`Despawn`, structurel différé, reparentage) ; la plage de profondeur de l'ombre
cesse de dépendre des bounds globales. C'est le socle que la physique attend.
**Spec** : [docs/plans/2026-07-14-p3m2-scheduler-lifecycle-design.md](../docs/plans/2026-07-14-p3m2-scheduler-lifecycle-design.md)
**Baseline de rendu** : `d1671f3` · **Sessions passées** : S1–S14 → `archive/` (S14 = P3-M1, clos).

## Décisions verrouillées (détail : spec § Journal des décisions)

- **D0** — Nouveau projet **`Agapanthe.Engine`** : la seule couche qui marie World + Rendering. World reste GPU-free,
  Rendering reste ECS-free. Engine **ne référence pas Platform** et **ne possède rien** (l'ordre de teardown du
  Sandbox est le gate 0-leak). *(décision humaine)*
- **D1** — Scheduler à **étages + systèmes enregistrés** (`Input → Simulation → PostSimulation → Render`) ; l'ordre
  devient une **donnée**. **Deux** contextes/interfaces (`ISystem`/`TickContext`, `IRenderSystem`/`RenderContext`) —
  sinon les systèmes de simulation verraient des types Graphics. `Input` = étage **vide** côté moteur. *(décision humaine)*
- **D2** — Lifecycle : `Spawn`/`Despawn`/structurel différé/reparentage. **`Despawn` = CASCADE** sur les descendants
  (le lien est enfant→parent, aucune liste d'enfants ; sans ça `ComputeWorld` marche dans un slot recyclé).
  `IsAlive` faux **dès la mise en file**. Pooling/prefabs → backlog. *(décision humaine)*
- **D3** — Ombre : **borne amont explicite** (`ShadowCasterDistance` + plan de coupe du wedge — il est **infini** vers
  la lumière, c'est ce que la v1 ratait), `UpstreamExtent` depuis les **casters** (`FitSceneSphere` garde
  `sceneBounds`), circularité cassée en **deux passes** (wedge → fit → compaction).

## Critère de sortie

Ordre des étages garanti et testé · `Despawn` cascade sans corruption de la propagation · un caster à 10 000 km en
amont **ne fait pas exploser** `eyeDistance` (le test qui compte) · **0 B/frame au banc ET en mode churn** ·
capture **bit-identique à `d1671f3`** en fin de V3 · 0 warning / 0 validation / 0 leak / **NativeAOT PASS** · double
audit PASS.

## Vagues

| # | Contenu | Gate |
|---|---|---|
| V0 | Décision D3 écrite (borne amont, deux passes) — **papier** | Spec relue |
| V1 | ✅ Projet `Agapanthe.Engine` + `.slnx` + scheduler + tests d'ordre. Aucun changement de rendu. | ✅ Build 0 warn, 295 tests verts |
| V2 | ✅ Lifecycle World (cascade, sémantique intra-étage, **file de commandes propre au World** — pas le `CommandBuffer` d'Arch, cf. correction D2), mode churn, smoke AOT étendu | ✅ 308 tests + 0 B/frame en churn + run churn 0 leak / 0 validation |
| V3 | ✅ `FrameOrchestrator` + `SceneViewSystem` ; l'ordre quitte le Sandbox ; spin + churn deviennent des `ISystem` ; `Tick` hors `DrawFrame` (D1.a) | ✅ **Capture bit-identique `d1671f3`** (SHA `9790D95D…`) + 308 tests + 0 alloc/frame + 0 leak |
| V4 | ✅ D3 : plan de coupe amont (7ᵉ plan) + ancre, deux passes (`CollectRenderLists`/`CompactShadowCasters`), `UpstreamExtent` depuis `casterBounds`, `ShadowCasterDistance` | ✅ 311 tests (dont « 10 000 km amont ») + capture **bit-identique `9790D95D`** (D3 no-op sur la scène par défaut ; eyeDistance 72.7 m loggé) + grid 50² 0 alloc/frame 0 leak |
| V5 | ✅ Banc Release+AOT grid:100x100, double audit, findings appliqués, clôture, archive | ✅ draws 2+2 · 0 alloc/frame · 0 leak · 0 validation · **NativeAOT PASS** · audits PASS |

**Dépendances** : V4 après V3 (la couture doit exister avant qu'on change ce qu'elle transporte). V2 et V4 touchent
toutes deux `GameWorld.cs` → **séquentielles**.

## Risques

- **F1** — Alloc/frame cachée : le délégué `Action<CommandList, FrameContext, SwapchainTarget>` doit être **caché dans
  un champ** (invisible aux tests unitaires, seul le banc le voit) ; `CommandBuffer` Arch **réutilisé**.
- **F2** — Le gate 0-alloc actuel **n'exerce pas** le lifecycle → mode churn obligatoire, sinon le gate ment.
- **F3** — AOT : la probe est un **projet séparé** ; le chemin structurel (despawn + flush + cascade) doit y passer.
- **F4** — Fidélité : V3 bit-identique (non négociable) ; V4 autorisée à différer, **justifiée par un diff
  d'`eyeDistance` loggé**.
- **F5** — `Engine` god-object : il ne possède rien, ne dispose rien.

## Dette restante à la clôture (prévision)

Verdict visuel humain de **P3-M1** toujours dû (protocole `docs/visual-checks/2026-07-14-p3m1-instancing-shadows.md`) ·
Linux/macOS jamais validés (P3-M0, bloqué : pas de machine) · rendu GPU-driven (backlog §1) · CSM + PCSS (backlog §2).

## Log

- 2026-07-14: **Session 15 ouverte — P3-M2.** INTAKE : 3 décisions humaines (projet `Engine` · scheduler à étages ·
  lifecycle sans pooling). SPEC v1 écrite → relecture indépendante `engine-architect` : **2,9/5 NEEDS WORK**, 2 points
  **bloquants** confirmés contre le code : (a) le wedge est **non borné vers l'amont** → D3 déplaçait le bug au lieu de
  le corriger ; (b) `RenderItem` ne porte **pas de sphère** → le « rejet final au record » était infaisable et aurait
  cassé les runs du draw instancié. Plus : `Despawn` d'un parent corrompt `ComputeWorld`, collision `FrameContext`,
  étage `Input` infaisable sans arête `Engine → Platform`. **SPEC v2** écrite (11 corrections), en relecture.
- 2026-07-14: Pooling + prefabs **écartés du périmètre** (décision humaine) → inscrits au [backlog §4](../docs/BACKLOG.md).
- 2026-07-14: **Fin de session — constat visuel humain sur une grille de casques** (`grid:20x20`) : dégradé de lumière
  par casque + anneaux d'ombre sur le sol. Diagnostic : **config Sandbox** (rig studio + `ShadowDistance` mis à l'échelle
  sur la diagonale), **pas de régression moteur** ; le plafond réel est le **CSM** (backlog §2). Fix Sandbox **approuvé
  mais non appliqué** → repris en tête de la session 16 (détail : `docs/AVANCEMENT.md` § Reprise).
