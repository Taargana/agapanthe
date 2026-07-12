# Absolute-Human Board — Agapanthe Session 9 (P2-M0 : Gate AOT + verdict Arch)

**Status**: **DONE — P2-M0 PASSÉ 2026-07-12.** Gate AOT franchi, double audit PASS (0 critique), **verdict : ✅ GO sur Arch**. Le Sandbox publie et tourne en NativeAOT (byte-identique M8), risque n°1 Silk.NET résolu (registration GLFW explicite), Arch.Persistence NO-GO (→ source-gen maison Phase 3), masquage du rapport 0-leak neutralisé. Spec amendée (§3.4 contrainte rooting, §5 tests). À archiver → board-session9-P2M0.md.
**Créé**: 2026-07-12
**Spec**: docs/plans/2026-07-12-phase2-foundations-design.md §2.1 (règles AOT + SPIR-V hors-ligne), §3.1 (modules), §6.1 (gate P2-M0 détaillé)
**Board persistence**: git-tracked
**Sessions passées**: S1-S8 → .absolute-human/archive/ (S8 = board-session8-M8.md, Phase 1 close)

## Intake (interview absolute-brainstorm + 2 relectures critiques — actées, pas de re-brainstorm)

- **Cadre Phase 2** : transformer le viewer Phase 1 en moteur. Fondations SEULES (pas de physique/audio/gameplay/slice). Critère de sortie de phase : milliers d'entités qui bougent, cullées, à 10 000 km de l'origine, en NativeAOT, 0 leak, zéro alloc/frame.
- **Deux règles obligatoires nouvelles (rétroactives Phase 1)** : (1) **code AOT-pur** (`<PublishAot>` + `<IsAotCompatible>`, warnings IL = erreurs) ; (2) **chemin SPIR-V hors-ligne** (shaderc = luxe de dev, prod = précuit).
- **P2-M0 est un GATE, pas une formalité** (spec §6.1) : Arch n'a jamais démontré sa compatibilité NativeAOT, et l'AOT vient d'être décrété obligatoire. On ne se marie pas à une dépendance irréversible sur une intuition.
- **Ordre du risque (contre-intuitif, tranché en relecture)** : le risque n°1 n'est PAS Arch, c'est **Silk.NET.Windowing/Input** (découverte de plateforme par réflexion, `EngineWindow.cs:42`) — et il est dans la Phase 1. Puis Silk.NET.Shaderc.Native. Puis Arch/Arch.Persistence.

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, aucun Vk* hors Graphics, IDisposable + DeletionQueue N+2, zéro alloc/frame sur le hot path, ResourceTracker (leak = échec), tout message de validation = bug.
- **NOUVEAU** : AOT-pur (warnings IL2xxx/IL3xxx = erreurs) ; SPIR-V précuit en prod.
- Run Windows (RTX 5070 Ti) : `$env:AGAPANTHE_MAX_FRAMES=3; $env:AGAPANTHE_CAPTURE="check.ppm"; dotnet run --project samples/Sandbox`.
- Baseline capture de référence (byte-identique) : SHA256 `24001B240C6C6B956F3F1AC6ABC1FE1D2CAA8914D9013169DFB049027E3309B7`.

## Rollback Point

`264772e` (fin Phase 1, avant les commits Phase 2) — spec Phase 2 = `f62e82b`.

## Objectif de P2-M0 (spec §6.1)

Publier le Sandbox en **NativeAOT**, qu'il **tourne**, avec **0 warning IL** — et rendre un **verdict go/no-go sur Arch**. Si un maillon casse en AOT, on le sait AVANT d'avoir écrit une ligne de moteur dessus.

## Task Graph

```
W1 (audit AOT de l'existant — dans l'ordre du RISQUE, spec §6.1) :
   P2-M0-01 <IsAotCompatible> sur toutes les libs + <PublishAot> Sandbox + warnings IL = erreurs
   P2-M0-02 RISQUE N°1 : Silk.NET.Windowing/Input en AOT (EngineWindow.cs:42, découverte par réflexion)
            → si casse : plan B (registration explicite / P/Invoke GLFW maison) spec §6.1
   P2-M0-03 Silk.NET.Shaderc.Native sous PublishAot (packaging natif ; soldé pour de bon en P2-M1)
W2 (smoke-test Arch — isolé, jetable) :
   P2-M0-04 console app : Arch World + composants + Query + Arch.System + ParallelQuery
   P2-M0-05 Arch.Persistence : snapshot + restore d'une entité, EN AOT  ← le vrai juge de paix
W3 (dette Phase 1 qui menace les gates Phase 2) :
   P2-M0-06 crash GlfwEvents.Dispose au shutdown (masque le rapport ResourceTracker → menace le gate 0-leak de CHAQUE jalon)
Tail :
   P2-M0-07 VERDICT écrit go/no-go Arch + audits si go
```

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | P2-M0-01 → 02 → 03 | séquentiel (02 dépend de 01 ; c'est le risque n°1, à traiter tôt) |
| 2 | P2-M0-04, 05 | app console jetable, isolée du moteur |
| 3 | P2-M0-06 | dette GLFW |
| tail | P2-M0-07 | verdict + décision |

## Tâches

### P2-M0-01 — Config AOT du projet [infra, S] — partiel
`<PublishAot>true</PublishAot>` ajouté au Sandbox (Sandbox.csproj). Prérequis tooling VÉRIFIÉS : SDK 10.0.103 + VS2022 Community MSVC 14.44 (link.exe) présents. **Piège d'environnement trouvé** : le shell doit avoir `C:\Program Files (x86)\Microsoft Visual Studio\Installer` (vswhere.exe) sur le PATH, sinon le linker ILC échoue (`vswhere n'est pas reconnu`, code 123) — ce n'est PAS un problème d'AOT.
**RESTE (décision de gate)** : `<IsAotCompatible>` sur les libs + warnings IL = erreurs. Recensement des warnings IL fait (voir ci-dessous) : **tous tiers (Silk.NET), aucun de `Agapanthe.*`** → IL2104 (Silk.NET.Windowing.Common / Input.Common, trim assembly-level), IL3000/IL3002 (Silk.NET.Core.Loader.DefaultPathResolver + Microsoft.Extensions.DependencyModel : single-file / Assembly.Location — **bénins**, le résolveur retombe sur AppContext.BaseDirectory, ce qui explique que glfw3/shaderc soient trouvés au runtime). Le gate permanent est donc trivial côté NOTRE code (déjà propre) ; reste à décider comment neutraliser le bruit tiers (NoWarn ciblé vs TrimmerSingleWarn).

### ✅ P2-M0-02 — RISQUE N°1 : Silk.NET.Windowing/Input en AOT — **RÉSOLU (plan B option a)**
**Le risque s'est matérialisé** exactement comme la relecture l'avait prédit : `System.PlatformNotSupportedException: Couldn't find a suitable window platform (none registered)` à `Window.Create` — le trimmer AOT supprime la découverte de plateforme par réflexion. **Fix (spec §6.1 plan B-a)** : enregistrement EXPLICITE via `GlfwWindowing.RegisterPlatform()` + `GlfwInput.RegisterPlatform()` dans un ctor statique de `EngineWindow` (API confirmée par le code source Silk.NET : `RegisterPlatform()` fait `Window.Add(new GlfwPlatform())`, un appel statique direct que le trimmer préserve). Packages concrets `Silk.NET.Windowing.Glfw` + `Silk.NET.Input.Glfw` 2.23.0 ajoutés au projet Platform. **Résultat : binaire natif de 4,3 Mo, il TOURNE** (fenêtre résolue, GPU RTX 5070 Ti, IBL 27 ms, exit 0, no leaks) et la **capture est BYTE-IDENTIQUE à la baseline M8** (`24001B24…`) → AOT ne change rien au rendu. Aucune régression sur une décision verrouillée (Silk.NET reste le binding).

### ✅ P2-M0-03 — Silk.NET.Shaderc.Native sous PublishAot — CONSTAT
`shaderc_shared.dll` (6,6 Mo) embarqué à côté de l'exe natif et **fonctionne au runtime sous AOT** (l'IBL, qui compile ses 4 kernels compute via shaderc, s'est générée sans erreur). État AOT actuel : OK. La règle SPIR-V hors-ligne (P2-M1) le retirera de la prod.

### P2-M0-02 — RISQUE N°1 : Silk.NET.Windowing/Input en AOT [infra, M] — todo — OWNER Platform/EngineWindow.cs
Silk.NET 2.x découvre ses plateformes fenêtrage/input **par réflexion** (`EngineWindow.cs:42`, `Window.Create`). C'est le point de rupture AOT le plus probable de tout le projet. Publier en AOT et **lancer réellement** le Sandbox (fenêtre + input). AC : la fenêtre s'ouvre, l'input répond, 0 warning IL bloquant. **Si ça casse** : appliquer le plan B (spec §6.1) — (a) registration explicite de la plateforme Silk.NET, (b) P/Invoke GLFW maison dans Platform, (c) dernier recours PublishAot serveur-only (affaiblit la règle → décision humaine). Remonter le résultat.

### P2-M0-03 — Silk.NET.Shaderc.Native sous PublishAot [infra, S] — todo
Vérifier le packaging de la lib native shaderc sous NativeAOT (chargée aujourd'hui au runtime pour compiler les shaders). Note : la règle SPIR-V hors-ligne (P2-M1) la retire de la prod — ici on constate juste l'état AOT actuel. AC : recensé, statut clair (fonctionne / à retirer via P2-M1).

### ✅ P2-M0-04 — Smoke-test Arch (core) EN AOT — **GO (avec un caveat mécanique)**
App console jetable (scratchpad, hors dépôt) : Arch 2.1.0 + Arch.System 1.1.0 + ZeroAllocJobScheduler 1.1.2. Sous JIT : les 4 stages passent (spawn 1000, Query séquentielle, **ParallelQuery = le MT exigé**, BaseSystem). Sous **vrai NativeAOT** (`IsDynamicCodeSupported=False`) : d'abord **ÉCHEC** `NotSupportedException: 'Position[]' is missing native code or metadata` sur `World.Create` — **et AUCUN warning IL au publish** (ça compile propre, ça casse au runtime : le piège exact qu'un gate doit attraper). Cause : Arch instancie les tableaux de composants (`T[]`) par voie générique que l'ILC ne pré-génère pas. **Fix trouvé** : rooter explicitement le type tableau de chaque composant (`GC.KeepAlive(new Position[1])`…) → **les 4 stages PASSENT en AOT, exit 0**. **Caveat** : il faut un mécanisme de rooting par type de composant (liste manuelle maintenant ; un source generator l'émettra en Phase 2 à partir du registre de composants). Mécanique, borné, non bloquant.

### ❌ P2-M0-05 — Arch.Persistence EN AOT — **NO-GO → source-gen maison (spec §2 « à défaut »)**
`ArchBinarySerializer` **ne se charge même pas sous JIT** contre Arch 2.1.0 : `TypeLoadException: Could not load type 'Arch.Core.Utils.ComponentType' from assembly 'Arch, Version=2.0.0.0'` — **incompat binaire** (Arch.Persistence 2.0.0 compilée contre Arch 2.0.0 ; les packages Extended sont en retard sur le core). Dépendances : **MessagePack 2.6.100-alpha** (préversion + CVE NU1902) **+ Utf8Json** (abandonné 2020, `Reflection.Emit`, hostile AOT). Aucune version d'Arch.Persistence n'évite ces deux-là. **Verdict** : ne pas dépendre d'Arch.Persistence. La sérialisation (streaming/réplication, Phase 3+) sera **maison, source-gen, sans MessagePack** — la spec §2 l'avait explicitement anticipé. **Sans impact sur le critère de sortie Phase 2** (la persistance n'y sert pas).

### ✅ P2-M0-06 — Dette : crash GlfwEvents.Dispose au shutdown — RÉSOLU (masquage neutralisé structurellement)
**Diagnostic** : le vrai défaut n'était pas le crash (rare, non reproductible, **interne à Silk.NET** — désenregistrement de son propre callback curseur, access violation native **non rattrapable** par try/catch managé), mais le **couplage** : le rapport ResourceTracker était émis APRÈS `window.Dispose()` (Program.cs:294 après :291), et dans EngineWindow.Dispose le `Unregister` venait après le `_window.Dispose()` crashy. L'ordre de dispose input→window était déjà correct (hypothèse « mauvais ordre » écartée). L'hypothèse « garder les delegates vivants » est **non actionnable** : les delegates qui crashent sont ceux de Silk.NET, pas les nôtres.
**Fix (fallback sanctionné par la tâche)** : (1) `EngineWindow` **retiré du ResourceTracker** — c'est un objet plateforme, pas une ressource GPU ; le tracker documente « GPU resource lifetimes ». Ça découple le gate de fuites GPU du teardown natif. (2) Program.cs : `ResourceTracker.Report()` émis **avant** `window.Dispose()` (juste après `device.Dispose()`, où tout le GPU est déjà détruit) ; le teardown GLFW natif est désormais la **toute dernière** action → un crash y est impuissant à masquer le rapport. `EngineWindow` reste `using var` (dispose déterministe).
**Vérif** : build 0 warning (le `using Agapanthe.Core` devenu inutile retiré), 205 tests, **capture byte-identique** (SHA 24001B24… ; 135 ressources = 136 −1 EngineWindow). **40 runs (30 AOT + 10 Debug) : rapport présent 40/40, exit 0 40/40, crash non reproduit.** Le crash Silk.NET sous-jacent reste un **problème upstream à surveiller** (non reproductible, uncatchable), mais il ne peut plus fausser un gate.

### ✅ P2-M0-07 — Verdict go/no-go Arch + audits — **GATE PASSÉ**
**Double audit PASS (0 critique)** — la décision irréversible Arch est validée et actable.
- **csharp-lowlevel : PASS** (empirique : build/test/publish AOT + run natif exit 0 / 0 validation / 0 leak / capture byte-identique + smoke Arch 4/4 en vrai AOT). Retrait EngineWindow du tracker = **sain** (erreur de catégorie corrigée, tracker = GPU-only, `using var` unique à dispose déterministe, filet finalizer Debug-only sans valeur ici). Registration GLFW = **correcte** (ctor statique thread-safe garanti par le runtime, avant tout `Window.Create` ; idempotente ; aucun type Vk*/GLFW ne fuit). NoWarn = **bien scopé** (IL2104/IL3000/IL3002 = bruit tiers bénin ; les codes AOT-dangereux **IL2026/IL3050** restent actifs). Aucun impact hot path.
  - **Probe indépendant → caveat rooting DURCI** : la faille frappe AUSSI `entity.Add<C>()` et **le CommandBuffer** (le chemin même que la spec §3.4 impose pour les changements structurels) ; elle est **silencieuse au publish** (aucun warning IL) ; et surtout elle peut **corrompre l'état partiellement** (Add<C> lève PUIS laisse l'entité à moitié migrée → `Get<C>` rend une valeur périmée). Donc un composant ajouté **au runtime** (pas à l'init) donnerait une défaillance **différée en plein gameplay**, pas au démarrage. Fix suffisant confirmé : rooter `new T[1]` **par type de composant** couvre Add/Remove/Create/CommandBuffer.
  - **Trou de couverture** : le smoke exerce une Query manuelle, PAS l'attribut source-gen `[Query]` d'Arch.System (la feature phare) → à prouver en P2-M2.
- **engine-architect : PASS with concerns** — Arch « GO » solide et actable ; le gate a fait exactement son travail (matérialiser le risque n°1 puis le résoudre ; **attraper un échec AOT runtime sans warning** avant l'irréversible). Contrat §6.1 honoré (ordre du risque, verdict, plan B). Deux CONCERN **à ne PAS classer en dette molle** (voir Dette ci-dessous).

**VERDICT DU GATE : ✅ GO sur Arch. Phase 2 continue sur Arch.** Persistance = maison source-gen (Phase 3, prévu §2). Les 5 changements M0 sont propres, build vert avec analyzers, frontières et décisions verrouillées intactes.

## Dette issue de P2-M0 (contraintes pour la suite — les 2 audits convergent)

- 🔴 **[CONTRAINTE DE CONCEPTION P2-M2, pas dette molle] Rooting AOT des tableaux de composants.** Arch instancie `T[]` par voie générique que l'ILC ne pré-génère pas → `NotSupportedException` **silencieuse au publish** (aucun warning IL), pouvant frapper `Create`/`Add`/`Remove`/**CommandBuffer** (§3.4) et **corrompre l'état partiellement** (défaillance différée si un composant n'est ajouté qu'au runtime). **Le registre de composants doit être source unique de vérité et générer lui-même le rooting** (`new T[1]` par type, source-gen), **exécuté à l'init**, **gardé par un test qui tourne sous AOT**. Converge avec la sérialisation maison (ci-dessous) → **un seul générateur** piloté par le registre. À inscrire dans le contrat de P2-M2.
- **Sérialisation maison source-gen** (Arch.Persistence NO-GO) → Phase 3, même générateur que le rooting.
- 🟠 **AOT prouvé Windows UNIQUEMENT** — NativeAOT + registration GLFW explicite + packaging shaderc natif non validés sur Linux/macOS(MoltenVK), alors que le cross-platform est verrouillé. Le « GO » repose sur une preuve mono-plateforme. Se cumule avec la dette Linux M4.
- 🟠 **Couverture du smoke** : l'attribut source-gen `[Query]` d'Arch.System (feature phare) non exercé → à prouver en P2-M2.
- **Code de sortie otage du crash Silk.NET** : le rapport 0-leak sort toujours (fix W3), mais un AV au teardown GLFW fait sortir le process en code de crash (jamais faux PASS, seulement faux FAIL) → **le contrôle CI de chaque jalon doit keyer sur la LIGNE de rapport, pas sur l'exit code**.
- **shaderc natif encore embarqué sous AOT** → retiré par P2-M1 (spec §3.7).
- **Toolchain AOT** : le publish exige `vswhere` sur le PATH (`C:\Program Files (x86)\Microsoft Visual Studio\Installer`) sinon linker ILC échoue (code 123, non lié à l'AOT) → à câbler en CI.
- **Ordre « report avant teardown GLFW »** garanti par convention dans Program.cs (documenté) → tout futur point d'entrée (serveur headless) doit le reproduire ; envisager un helper de shutdown partagé.
- **NoWarn Sandbox** couvre aussi Program.cs (mais seulement les codes bénins ; les dangereux restent actifs) → surveiller si Program.cs gagne de la réflexion.

## Deferred Work (hérité Phase 1 — à traiter en Phase 2)

- Invariant du reload garanti par convention (`PollShaderReload()` public) → garde debug `_device.IsRecording` (phase 2).
- Validation Linux (rattrapage M4) → **déférée**, pas de machine.
- Findings mineurs M8-09/M8-10 (voir board-session8).
- Dé-aplatissement de l'import glTF → hors portée Phase 2 (spec §7 ; aplatissement conservé, `Parent` exercé par hiérarchie synthétique).

## Log

- 2026-07-12: **P2-M0 PASSÉ — gate franchi, jalon clos.** Double audit PASS 0 critique (csharp-lowlevel : PASS + probe indépendant durcissant le caveat rooting ; engine-architect : PASS with concerns). Verdict : **✅ GO sur Arch** pour l'ECS. 2 CONCERN convergents actés en contraintes/dette : (1) rooting des composants = contrainte de conception P2-M2 (source-gen piloté par le registre + test AOT), (2) AOT prouvé Windows-only → re-prouver Linux/macOS. Spec amendée (§3.4 rooting, §5 tests reclassés). RESTE : archiver le board → board-session9-P2M0.md. Ensuite P2-M1 (SPIR-V hors-ligne).
- 2026-07-12: **W3 DONE** — crash GlfwEvents au shutdown : masquage du rapport ResourceTracker **neutralisé structurellement** (EngineWindow retiré du tracker = objet plateforme ≠ ressource GPU ; rapport émis avant le teardown GLFW). 40 runs (30 AOT + 10 Debug) : rapport 40/40, exit 0 40/40, byte-identique. Crash upstream Silk.NET non reproduit, tracé. **Toutes les vagues techniques de P2-M0 faites.** RESTE : P2-M0-07 = verdict formel écrit + double audit + clôture M0.
- 2026-07-12: **W1 DONE (gate AOT permanent figé)** + **W2 DONE (verdict Arch rendu).** W1 : `IsAotCompatible` sur les 5 libs (notre code 0 warning IL), NoWarn ciblé du bruit tiers Silk.NET → le Sandbox publie en NativeAOT SANS override, tourne, capture byte-identique M8. **W2 — verdict : ✅ GO sur Arch pour le core ECS** (World/Query/**ParallelQuery MT**/System passent en vrai AOT), avec 2 caveats documentés : (1) rooting des tableaux de composants requis pour l'AOT (mécanique, source-gen plus tard ; piège à runtime SANS warning au publish → trouvé grâce au gate) ; (2) **Arch.Persistence NO-GO** (incompat binaire + MessagePack alpha/CVE + Utf8Json abandonné) → sérialisation maison source-gen en Phase 3, comme prévu spec §2. RESTE : W3 (P2-M0-06 crash GlfwEvents) + P2-M0-07 clôture formelle M0.
- 2026-07-12: **W1 quasi-DONE — le gros risque de la Phase 2 est levé.** Le risque n°1 (Silk.NET.Windowing par réflexion) s'est matérialisé sous AOT (`PlatformNotSupportedException`) PUIS a été résolu par enregistrement explicite GLFW (plan B-a). Binaire natif 4,3 Mo qui tourne, 0 leak, **capture byte-identique à M8**. shaderc natif OK sous AOT. Tooling VS2022 présent (piège vswhere/PATH documenté). Warnings IL restants = tous Silk.NET (bénins single-file), aucun de notre code. RESTE de W1 : figer le gate permanent (IsAotCompatible + warnings=erreurs + neutralisation du bruit tiers). PUIS W2 = smoke-test Arch (le vrai go/no-go ECS).
- 2026-07-12: **Phase 2 ouverte.** Spec de référence écrite (interview absolute-brainstorm + 2 relectures critiques : 2,9 → 4,0/5), committée `f62e82b` sur branche `phase2-foundations`. Board S8 archivé (Phase 1 close, 8/8). Session S9 = P2-M0, gate AOT bloquant. DAG 7 tâches, 4 vagues, ordonnées par RISQUE (Silk.NET.Windowing n°1, pas Arch). En attente feu vert pour W1.
