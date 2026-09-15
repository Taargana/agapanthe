# Agapanthe — Plan complet & état d'avancement

**Mis à jour** : 2026-09-12 (session 37 — **Contenu-3c-3 CLOS → Contenu-3c CLOS (3/3) → domaine Contenu
ENTIÈREMENT CLOS** : `ground_quad` + migration `drive` (dernière scène hand-codée) + nettoyage final — après
cette session, toutes les scènes du Sandbox sont des données cuites `SceneRecipe`, zéro recipe hand-codée
restante ; `.agscene` v3→v4 (`SceneSystemKind.DriveControl`, 1ᵉʳ système sans spawn, champs probe relaxés en
optionnels) ; nouveau `MaterializeResult.SpawnedEntities` ; nouvelle capacité d'authoring `[[entity]] body =
true` (n'existait pas avant) ; double audit PASS-with-concerns ×2, **2 🔴/🟠 trouvés indépendamment par les
deux et corrigés** (reprise `drive_control` cassée au lieu de no-op ; corps physiques dupliqués sur modèle
multi-mesh) + fermeture de la dette `world_origin` accumulée sur les 3 phases ; 831 tests, capture `drive`
pinned + verdict visuel PASS, 0 leak/0 validation) · 2026-09-12 (session 36 — **Contenu-3c-2 CLOS** : `LandingChallenge` system kind + fabrique +
migration `planet-challenge`, plus un correctif générique de reprise (`SceneMaterializer.Materialize(bool
spawnEntities)`) qui ferme un 🔴 trouvé par le double audit — `AGAPANTHE_LOAD` était write-only pour toute
scène `SceneRecipe` déclarant un `[physics]`/entités, y compris `planet-drop` depuis 3c-1, jamais détecté
faute de protocole de reprise vérifié pour cette scène-là ; vérifié bout-en-bout en live sur JIT et NativeAOT ;
6 findings 🟠 supplémentaires appliqués ; 797 tests, capture `planet-challenge` pinned + verdict visuel PASS,
0 leak/0 validation) · 2026-09-12 (session 35 — **Contenu-3c-1 CLOS** : 1ᵉʳ des 3 sous-phases gatées de Contenu-3c —
`.agscene` v2 (attracteur newtonien, caméra `Fixed` +2 champs, 2 modes environnement, `[[system]]`), **2
registres** (`ISceneSystemFactory` client via `IGame` + générateurs procéduraux cook-time-only), 3 caméras
planète bespoke collapsent en trig cuite, `UvSphereGenerator` (cook-time, byte-identique à
`Primitives.UvSphere`), `planet`/`planet-drop` migrées vers `SceneRecipe` générique, `HeadlessSim` refuse
(exit 1) toute scène à systèmes ; spec 4,30/5, double audit `csharp-lowlevel`+`engine-architect`
PASS-with-concerns, **1 🔴 trouvé par les deux et corrigé** (`SceneMaterializer` oubliait
`WithAttractor(...)` → `planet-drop` tournait à gravité nulle, invisible dans la capture pinned — 2 tests de
régression ajoutés) + 4 findings 🟠 appliqués ; 780 tests, captures/snapshots inchangés JIT==NativeAOT, 0
leak/0 validation, verdict visuel humain PASS) · 2026-09-11 (session 34 — **Contenu-3b CLOS → domaine Contenu CLOS (3/3)** : format de scène
déclaratif `.agscene`/`.agprefab` (TOML cook-side via Tomlyn → blob binaire déterministe, patron `.agmodel`) lu
par un `SceneLoader` GPU-free (nouveau `Agapanthe.Scene` = `{Core, World, Assets}`) — **client et serveur
partagent enfin le code de peuplement** : `HeadlessSim --scene <clé>` charge le même fichier que le Sandbox ;
la famille `model` (single/grid/cluster) devient data, `.agmodel` bump v2 (bounds par mesh précalculés) ;
`ModelSceneRecipe` supprimée → `SceneRecipe(string)` générique ; spec 4,4/5, double audit `csharp-lowlevel`
4,1/5 + `engine-architect` 4,1/5 PASS-with-concerns (aucun 🔴, 12 findings 🟠 appliqués : durcissement des
formats cuits contre un count forgé, `ResolveMeshRefs` dirty-avant-boucle, validations `LocalMat`/bounds
NaN/direction-lumière nulle, TOML clés-inconnues + précision double, offset de prefab tourné, incrémentalité
cook + clés via `AssetKey.FromContentPath`) ; 740 tests, captures re-épinglées + verdict visuel humain PASS
sur 4 scènes, `HeadlessSim --scene` JIT==NativeAOT, 0 leak/0 validation) · 2026-09-09 (session 33 — **Contenu-3a CLOS** : `AssetRef` (composant #13, 1ᵉʳ managé du projet, porte un `MeshRefKey`) remonte l'identité d'asset dans la simulation ; snapshot **v4** sérialise `AssetRef`, `MeshRef` devient un cache render dérivé, `MeshRefIdentifier` supprimé → **un `Save` headless émet de vraies `AssetKey`** ; upgrade v3→v4 en place ; `SceneContext` → `SimSceneContext` headless-safe + `PresentationSceneContext` nullable + garde-fou `RequestRestore`/`ApplyPendingRestore` ; spec 4,55/5, double audit 3,9+4,0 PASS-with-concerns, **1 🔴 corrigé** (`LandingChallengeSystem` seedait sur monde vide à la reprise → seed paresseux) ; 696 tests, captures inchangées, `HeadlessSim` re-épinglé JIT==AOT `6a13dd54…`/`f6053226…`, Contenu-3 décomposé en 3 sous-jalons, verdict visuel PASS) · 2026-09-08 (session 32 — **Contenu-2 CLOS** : cook offline + content manifest — `tools/AssetCooker` (glTF → blob `.agmodel` autonome, `DeflateStream`, déterministe) + `content.agmanifest` binaire + `AssetCatalog` runtime ; **le runtime ne parse plus glTF** (migré dans `src/Agapanthe.Assets.Pipeline`, cook-side, non-AOT) ; `AssetsPipelineIsolationTests` scanne tous les `.csproj` ; l'arg CLI du Sandbox devient une clé ; **capture `model` re-épinglée `9030f6a6…`** — la ref S30 `df55d444…` avait dérivé, un `git stash` prouve que le contenu cuit rend à l'octet près comme le chemin remplacé ; double audit `csharp-lowlevel` 4,1/5 + `engine-architect` 4,2/5 PASS-with-concerns, aucun bloquant, findings appliqués ; 689 tests, captures HDR/UI + HeadlessSim inchangées, Sandbox + HeadlessSim JIT == AOT ; verdict visuel PASS S33) · 2026-09-07 (session 31 — **Contenu-1 CLOS** : identité d'assets stable — `AssetKey` (`Core`, path-based, style `res://`) + `ModelKeyIndex` (`Rendering`, GPU-free) + snapshot **v3** (`MeshRef` = `(clé, index mesh local, index material local)` re-résolu au load via un délégué `MeshRefResolver` de l'hôte ; `World` ne réfère toujours pas `Rendering`) ; **solde la dette VS-1 « ordre de chargement d'assets différent casse en silence »** pour mesh + material ; v1 ET v2 refusés ; domaine Contenu décomposé en 3 sous-jalons ; le mécanisme a trouvé son propre bug (ordre S30) dans son run de validation ; `HeadlessSim` re-épinglé JIT==AOT `80ced166…` (1842 o) / `cf01492e…` (210 o) ; double audit `csharp-lowlevel` + `engine-architect` **4,3/5 PASS-with-concerns chacun, aucun bloquant**, findings contenus appliqués ; 659 tests, captures inchangées, 0 leak/0 validation ; verdict visuel PASS S33) · 2026-09-07 (session 30 — **`Agapanthe.App` CLOS** : `Program.cs` 2360→22 l, `AppHost` + contrat `IGame`/`ISceneRecipe`, `SimulationSettings` = pas fixe source unique, premier `UniverseId` stampé ; double audit 4,2/5, 1 🔴 exit-code trouvé-et-corrigé ; 617 tests ; verdict visuel PASS S33) · 2026-09-06 (session 29 — **MP-0d CLOS → MP-0 CLOS (4/4)** : input → commandes horodatées ; `SimCommand`/`InputSnapshot` blittables (56/40 o, offsets épinglés) + `SimCommandQueue` (drain compacté avant handlers, ré-entrant safe) + `InputMap` déclaratif + phase input dans `SimulationHost.Tick` avant `Stage.Input` + `DiscardedCommandCount` ; `GameWorld.SetBodyVelocity` (garde `Has<Velocity>`, audit LL 🔴) ; `HeadlessSim --drive` = gate JIT==AOT `97e786f0…` ; `Key.B` Sandbox passe par `Commands.Enqueue` ; double audit `engine-architect` 4,3/5 + `csharp-lowlevel` 1 🔴 trouvé-et-corrigé ; 589 tests, captures inchangées, 0 leak/0 validation) · 2026-09-05 (session 28 — **MP-0c CLOS** : autorité du temps — `FixedTimestepAccumulator` (`Agapanthe.Engine`) découple la vitesse de sim du framerate (`Advance`→N pas fixes, clamp 250 ms, garde de ratio, non-fini→0 + `SanitisedInputCount`, `AdvanceFrame`) ; `FrameIndex`→`TickIndex` partout + off-by-one `CurrentTick` corrigé (`Math.Max(0L,TickIndex-1)` = dernier tick exécuté, épinglé) ; `PhysicsSystem.RatesMatch` + assert, `PhysicsSettings.FixedDt` inchangé ; captures **inchangées** via dt synthétique quand `AGAPANTHE_MAX_FRAMES` posé (prédiction du brainstorm corrigée) ; `AccumulatorEquivalenceTests` = équivalence tick-count (entiers) ; double audit PASS-with-concerns ×2 (4,2/5), aucun 🔴, 8 findings appliqués ; 558 tests, `HeadlessSim` JIT==AOT inchangé, 0 leak/0 validation) · 2026-09-02 (session 27 — **MP-0b CLOS** : identité d'entité — `ContactPairKey` 128 bits (clé de contact) + `GlobalIdRange` (plage d'allocation) + `UniverseId`/snapshot v2 (identité par fichier, 5 cas de réconciliation testés) ; double audit W4 a trouvé et corrigé un 🔴 (perte silencieuse d'entité sur collision d'id au `Load`) ; hash `HeadlessSim` re-épinglé `7e8dc68f…` (v2, 1868 o), désormais gardé par un test et non plus seulement par la prose ; verdict humain PASS ; 530 tests, captures inchangées) · 2026-08-13 (session 26 — **MP-0a CLOS** : headless split ; `Agapanthe.Engine` ne référence plus que `{Core, World}`, nouveau `Agapanthe.Engine.Render`, `SimulationHost` extrait, `samples/HeadlessSim` simule en NativeAOT **sans GPU** ; MP-0 décomposé en 4 sous-jalons ; double audit PASS-with-concerns ×2 [4,2/5], aucun 🔴 ; 491 tests, captures inchangées) · 2026-08-13 (session 25 — **UI-2 CLOS** : overlay debug in-view + profiler CPU ; `FrameStats`/`Sparkline`/`TextBuilder` + `DebugOverlaySystem`, le HUD `window.Title` et son hack de cession disparaissent, **gate 0-alloc visible en continu à l'écran**, bascule `F3` ; double audit PASS-with-concerns ×2 ; 468 tests, AOT PASS · **synchronization validation activée** et 3 hazards préexistants corrigés) · 2026-08-11 (session 25 — **UI-1 CLOS** : texte à l'écran ; FontCooker SDF hors-ligne + `.agfont` + `Agapanthe.Ui` GPU-free + passe UI ; double audit PASS-with-concerns ×2, verdict humain PASS ; 435 tests, AOT PASS) · 2026-08-03 (session 25 — **RÉORIENTATION cap « vrai engine »** [backlog §4quater] : artefact = le moteur, multijoueur serveur autoritaire, MP-0 = prochain jalon ; VS-4/VS-5 en pause · **brainstorm Texte & UI terminé**, spec `plans/2026-08-03-text-ui-design.md`, 3 jalons UI-1/2/3) · 2026-07-26 (session 24 — **VS-3 CLOS** : glu gameplay = défi d'atterrissage planétaire ; `QuerySurfaceContacts` + règle latchée `LandingChallengeRule` + scène `planet-challenge` (input→spawn→règle→save/resume) ; double audit PASS / PASS-with-concerns 4/5, verdict humain PASS ; 372 tests, AOT PASS) · 2026-07-26 (session 23 — **VS-2 CLOS** : spawn runtime différé (`SpawnBodyDeferred`) + gravité newtonienne (attracteur radial + sol radial) ; double audit PASS-with-concerns [4,5/5], verdict visuel PASS ; scène `planet-drop`, 355 tests, AOT PASS) · 2026-07-24 (session 22 — **VS-1 CLOS** : sérialisation du World save/load, double audit PASS, verdict humain PASS) · 2026-07-23 (session 20 — **P3-M7 CLOS** : buffers device-local + réduction du raster d'ombre 4×→~1× ; double audit PASS, verdict visuel PASS incl. soleil bas ; A+B ~15,3 → ~8,0 ms ≈ ×2) · 2026-07-23 (session 19 — **P3-M6 CLOS** : slots persistants dirty-trackés + cull d'ombre GPU ; double audit PASS, verdict visuel PASS ; voir « Point de reprise ») · 2026-07-14 (session 14 — **vérifs humaines de la Phase 2 soldées** : banc M4 PASS with concerns [perf → P3-M1], précision M3 PASS, hot reload M1 PASS) · 2026-07-13 (session 13 — **PHASE 2 CLOSE** — frustum culling + montée en charge : 10 000 entités cullées à 10 000 km, 0 alloc/frame, en NativeAOT ; double audit signe la clôture) · **Machines de dev** : macOS (Apple M3, MoltenVK) + Windows 11 (RTX 5070 Ti, Vulkan 1.3 core) · **Cibles** : Windows / Linux / macOS

## Vision

Moteur de jeu Vulkan en C# from scratch. **Phase 1 (TERMINÉE, 8/8)** : toute la chaîne graphique 3D, d'une fenêtre vide à une scène PBR complète — glTF, metallic-roughness, multi-lumières, ombres, skybox/IBL, hot reload shaders. **Phase 2 (TERMINÉE, 5/5)** : viewer → moteur — ECS (Arch), coordonnées `double` + camera-relative à origine quantifiée, couture render-list sans types GPU, frustum culling, montée en charge (10k entités cullées à 10 000 km, 0 alloc, NativeAOT). **Phase 3 (à venir)** : gameplay — lifecycle/scheduler, physique, sérialisation, audio.

Spec Phase 1 : [docs/plans/2026-07-02-graphics-engine-design.md](plans/2026-07-02-graphics-engine-design.md) · **Spec Phase 2** : [docs/plans/2026-07-12-phase2-foundations-design.md](plans/2026-07-12-phase2-foundations-design.md) · Suivi de session : [.absolute-human/board.md](../.absolute-human/board.md) (+ archives par session)

## Phase 2 — Fondations scalables (CLOSE — 5/5)

**Cadre** (spec Phase 2) : la Phase 1 a livré un **viewer**, pas un moteur (`Mesh.WorldTransform` figé → rien ne bouge ; `Scene` mélange possession GPU et draw list). La Phase 2 pose **les fondations qui ne se retrofitent pas** : ECS (**Arch**), coordonnées monde `double` + camera-relative rendering, couture render-list (handles, pas de types GPU dans le monde), frustum culling. **Cible de sortie** : des milliers d'entités qui bougent, cullées, **à 10 000 km de l'origine sans trembler**, en NativeAOT, 0 leak. Horizon lointain (non construit ici, mais non condamné) : univers persistant streamé multi-serveurs.

**Deux règles obligatoires nouvelles (rétroactives Phase 1)** : (1) code **AOT-pur** (`IsAotCompatible` sur les libs, `PublishAot` Sandbox, warnings IL = erreurs) ; (2) **chemin SPIR-V hors-ligne** (shaderc = luxe de dev, prod = précuit).

**Jalons Phase 2** :
| # | Livrable | État |
|---|---|---|
| P2-M0 | Gate AOT + verdict Arch | ✅ **PASSÉ** (S9) — double audit PASS, Arch validé |
| P2-M1 | Chemin SPIR-V hors-ligne | ✅ **PASSÉ** (S10) — double audit PASS, prod sans shaderc (prouvé Windows AOT) |
| P2-M2 | Couture ECS : Arch + `ResourceRegistry` + 2 listes (passthrough, sans culling) — refactor **byte-identique** | ✅ **PASSÉ** (S11) — double audit PASS, capture byte-identique Debug + AOT |
| P2-M3 | `Double3` + camera-relative rendering (précision grande distance) | ✅ **PASSÉ** (S12) — double audit PASS conditionnel, **10 000 km == origine, bit-pour-bit** |
| P2-M4 | Frustum culling + montée en charge (= critère de sortie) | ✅ **PASSÉ** (S13) — **PHASE 2 CLOSE** · 10k cullées à 10 000 km, 0 alloc, AOT, double audit signe |
| P2-M5 | Audits + clôture | ✅ **absorbé par M4** — le double audit de clôture a été mené dans W4 |

**P2-M0 (gate AOT) — acquis clés** :
- Le Sandbox **publie et tourne en NativeAOT** (binaire natif 4,3 Mo, capture **byte-identique à M8**). Prérequis : PATH avec `vswhere` (VS Installer) sinon le linker ILC échoue (code 123, non lié à l'AOT).
- **Risque n°1 (Silk.NET.Windowing par réflexion) matérialisé puis résolu** : trimmé par l'AOT → `PlatformNotSupportedException` → fix par enregistrement explicite `GlfwWindowing/GlfwInput.RegisterPlatform()` (ctor statique EngineWindow).
- **Verdict Arch : GO** pour l'ECS (World/Query/ParallelQuery/System validés en vrai AOT). **Arch.Persistence : NO-GO** (incompat binaire + MessagePack alpha/CVE + Utf8Json abandonné) → sérialisation **maison source-gen** en phase ultérieure.
- **Contrainte AOT dure pour P2-M2 (les 2 audits convergent, pas de la dette molle)** : Arch instancie les tableaux de composants `T[]` par voie générique que l'ILC ne pré-génère pas → échec runtime `'T[]' is missing native code` **SANS warning au publish**, pouvant frapper `Add`/`CommandBuffer` et **corrompre l'état partiellement**. → le **registre de composants doit être source unique** et **générer lui-même le rooting** (`new T[1]` par type, source-gen), gardé par un **test qui tourne sous AOT**. Converge avec la sérialisation maison → **un seul générateur**.
- EngineWindow **retiré du ResourceTracker** (objet plateforme ≠ ressource GPU) + rapport 0-leak émis **avant** le teardown GLFW → le crash Silk.NET au shutdown (M8-14, upstream) ne peut plus masquer le gate.
- **AOT prouvé Windows UNIQUEMENT** → à re-prouver Linux/macOS.

**P2-M1 (SPIR-V hors-ligne) — LIVRÉ (S10)** :
- **W1** : `ShaderCompiler` charge shaderc **paresseusement** (au 1er vrai miss, pas au ctor) + fix collatéral d'un gate de fuites flaky (collection xUnit non-parallélisable). Les 2 instances (Renderer + IblGenerator) couvertes.
- **W2** : mode **cache-only** (`precompiledOnly` — miss = `GraphicsException` explicite, pas de repli shaderc, §4) + fabrique `ShaderCompiler.CreateForBuild()` (source unique du `#if DEBUG` : Debug=full / Release=cache-only) ; `ShaderHotReloader` gardé `#if DEBUG` (Release : 0 watcher).
- **W3** : précompilateur build `tools/ShaderPrecompiler` (réutilise `ShaderCompiler`+`ShaderIncludeResolver` → clés **identiques** au runtime), 2 targets MSBuild (pré-cook incrémental → staging `obj/` ; ship `.spv` en Content vers `.shadercache/`), **non-ProjectReference** du Sandbox (pas de contamination AOT). `StripShadercFromRelease` retire la lib native shaderc du Release.
- **Preuve** : publish **Release/AOT win-x64** → binaire 4,47 Mo, **shaderc absent**, 15 `.spv` livrés, run cache-only → aucun miss / aucune exception (clés matchent), 0 leak, **capture byte-identique M8 (24001B24…)**. Debug : hot reload conservé, aucun « loading shaderc » (cache chaud). 207 tests, 0 warning.
- **Double audit PASS** (csharp-lowlevel + engine-architect, 0 critique) + 8 durcissements appliqués (ctor internal, garde `EnsureShaderc`, `_shaderc` volatile ARM64, IblGenerator→`CompileFileResolved`, Inputs MSBuild `**\*`, strip cross-platform, tool récursif + catch élargi). Détail : board session 10.

**P2-M2 (couture ECS) — LIVRÉ (S11)** · Détail : [.absolute-human/archive/board-session11-P2M2.md](../.absolute-human/archive/board-session11-P2M2.md)

**Ce qui change** : `Scene` (qui mélangeait possession GPU + draw list + bounds) est **débranchée en deux** — `ResourceRegistry` (possession, Rendering) + **`GameWorld`** (les entités, ECS **Arch 2.1.0**, nouveau projet `Agapanthe.World`). Le helmet est dessiné **entièrement via l'ECS** : entités → systèmes → 2 listes triées → **handles** résolus au draw. **Aucun type GPU dans le monde, aucun type Arch hors de World** (`PrivateAssets="compile"` → garantie *mécanique*, pas conventionnelle).

- **Livré** : `Double3`/`Double3Bounds`/handles/`RenderItem`/`RenderList` (Core, GPU-free) · 7 composants + `ComponentRegistry` (rooting AOT) + `GameWorld` (seule API ; Arch confiné) · systèmes **1** (propagation hiérarchie + **détection de cycle**) et **2** (agrégation bounds) · `CollectRenderLists` **passthrough** (culling = M4) · `tools/AotComponentProbe` (gate AOT).
- **🎯 Critère de sortie TENU** : capture **Debug ET NativeAOT** = **byte-identique** à la baseline M8 (`24001B24…`) → le refactor est **pur**. 0 validation, 0 leak (135), **234 tests** (5/5 runs), 0 warning.
- **Rooting AOT** (le risque n°1) : `Root<T>()` roote `new T[1]` **et** enregistre le type — impossible d'enregistrer sans rooter. Le **source-gen + analyzer** prévus sont **abandonnés** (P2-M0 avait prouvé `new T[1]` suffisant) → registre à la main + test de complétude par réflexion + probe AOT. *L'architecte a validé : « le meilleur arbitrage du jalon ».* Source-gen reporté Phase 3 (avec la sérialisation).
- **Course Arch fermée par construction** : Arch attribue les ids de types dans un état **global, sans verrou**, au premier contact (`World.Create`) → 2 mondes créés en parallèle = **tableaux de composants mésalignés** (composant relu **tout à zéro**, reproductible). Fix : `Root<T>` force `Component<T>.ComponentType` **sous notre verrou**, avant tout monde. *Prouvé : tests World en parallèle **5/5** vs **3/3 d'échec** avant.* Contrat mono-thread gardé par `AssertOwnerThread` (`[Conditional("DEBUG")]`, zéro coût Release).
- **Leçon de guerre (le gate était vert par chance)** : le repli `(0,0,0)` des bounds était appliqué **par mesh** — or **zéro n'est pas l'élément neutre d'une union**. Un mesh **vide** tirait donc les bounds à l'origine : un modèle à (1000,1000,1000) aurait vu son extent **doublé** (cadrage + ombre faux). Invisible sur le casque (aucun mesh vide) → **trouvé par l'audit, pas par le gate**. Le fold vide (∞ inversés) est neutre ; le repli est désormais **global**.
- **Piège byte-identique évité** : `Scene.BoundsCenter/Diagonal` calculaient **en float** ; calculer en `double` puis narrower donne **jusqu'à 1 ULP d'écart** → caméra et matrice d'ombre décalées. Partout où les bounds sont consommées : **narrow d'abord, arithmétique float ensuite**.
- **`Span.Sort(structComparer)` alloue** (~88 B/appel : il boxe le comparateur) → tri maison `RenderList.SortByKey`. Trouvé par le test zéro-alloc.

**P2-M3 (camera-relative rendering) — LIVRÉ (S12)** · Détail : [.absolute-human/archive/board-session12-P2M3.md](../.absolute-human/archive/board-session12-P2M3.md)

- **Le résultat** : la capture headless du casque **à 10 000 km de l'origine** est **identique bit-pour-bit** à celle prise à l'origine (0 canal sur 2 764 800). Le critère prévu (≤ 1 LSB/canal) est donc **dépassé**. Anti-faux-positif : le log imprime `eye at 9999999.99751842` — une valeur qu'un `float` **ne peut pas représenter** (son ULP vaut 1 m à 1e7), ce qui prouve que l'origine est réellement appliquée et qu'on ne compare pas deux runs identiques.
- **Le mécanisme** : le transform du monde est **coupé en deux** — la position vit en `Double3` (composant `WorldPosition`), la rotation/échelle reste une `Matrix4x4` float. Elles ne se recombinent que dans `CollectRenderLists`, **seul point du code** où une coordonnée monde devient une coordonnée GPU, et où la soustraction `objet − caméra` se fait **en double avant le cast**. `RenderView` (Core) porte l'**origine unique de la frame** : monde, lumières et fit d'ombre soustraient tous la même valeur, par construction.
- **Les lumières ponctuelles sont en `Double3`**, reconverties camera-relative **à chaque frame** (les différer était impossible : le shader compare les positions des lumières à des positions de surface déjà relatives). **Aucun shader n'a changé** : l'œil packé à zéro fait que `V = normalize(eyePos − worldPos)` devient `normalize(−worldPos)` tout seul.
- **Fit d'ombre sur le frustum caméra** (`ShadowFit`, GPU-free et testable), plafonné par `Renderer.ShadowDistance`, **mais jamais plus large que la scène** (min des deux sphères) : fitter le frustum sur une petite scène gaspillerait la shadow map. Sphère (et non 8 coins) pour l'invariance en rotation ; snap sur la grille de texels **ancrée au monde** pour l'invariance en translation.
- **Dettes rouges de M2 soldées en ouverture** : handles avec **génération** (un handle périmé → `GraphicsException`, jamais un draw silencieusement faux) et `ResourceRegistry` **globale** en slot-map (avant, `MeshHandle(0)` de deux modèles se collisionnaient — bloquant pour M4).
- **Ce que les audits ont attrapé** (corrigé) : le **snap texel était un no-op** (il quantifiait un centre qui, exprimé relativement à la caméra, ne bouge jamais en translation → la shadow map glissait en continu) · les **casters en amont étaient clippés** (0,5·r de marge) → ombres disparaissant sans erreur · **`Unload` fuyait un descriptor set par matériau, définitivement**, et **le gate « 0 leak » passait en mentant** (il compte les pools, pas les sets) → allocateur **par modèle**, et le chemin, qui n'avait **aucun appelant**, tourne désormais sous le gate réel (`AGAPANTHE_UNLOAD_TEST=N` : 20 cycles, 842 ressources créées **et** détruites).
- **Écarts au plan, assumés** : W2 (lumières) **absorbé par W1** · byte-identique vs M8 **perdu comme prévu** (la translation sort de la matrice de vue → l'ordre des opérations flottantes change ; 14 pixels sur 921 600 dépassent 1 LSB, tous sur le bord d'ombre) · le chemin « fit frustum » **n'est pas exercé par une capture** (la scène du casque emprunte toujours le chemin « scène ») — couvert par 9 tests unitaires, **c'est la condition posée par l'architecte** : scène large = **tâche 1 de M4**.
- **Métriques** : 257 tests · 0 warning · 0 message de validation · 0 leak · probe NativeAOT PASS (8 composants rootés).
- **Env vars nouvelles** : `AGAPANTHE_WORLD_ORIGIN="x,y,z"` (place le modèle en `double` — l'image doit être identique où qu'il soit) · `AGAPANTHE_UNLOAD_TEST=N` (N cycles Load/Unload sous le gate de leak).

**P2-M4 (frustum culling + montée en charge) — LIVRÉ (S13), CLÔT LA PHASE 2** · Détail : [.absolute-human/archive/board-session13-P2M4.md](../.absolute-human/archive/board-session13-P2M4.md)

- **Le résultat** (critère de sortie §6.2, chaque gate vérifié) : **10 000 entités** (un seul upload) · **2556 visibles** après cull caméra · **0 B alloc/frame** (animation incluse) · **bit-identique à 10 000 km** avec caméra ET entités en mouvement (maille alignée) · **tourne en NativeAOT** · 0 validation · 0 leak.
- **Ce qui a été bâti** : `Frustum` (Core, GPU-free, 6 plans Gribb-Hartmann) · `Bounds` → **sphère locale** transformée par frame · **origine quantifiée** (snap 1024 m dans `RenderView` — l'œil vit à `EyeRelative` dans la cellule ; débloque le buffer d'instances persistant et la stabilité physique de la Phase 3) · ordre de frame inversé (`ShadowFit` avant la collecte, pour culler les casters contre le **volume de lumière**) · skybox reconstruit depuis la rotation de vue seule (origin-exact) · culling linéaire (sphère vs 2 frustums) · `SortKey` matériau + tie-break + **tri radix LSD** · `AnimateDrawables<T>` (écriture directe, zéro-alloc, AOT-safe).
- **Précision reformulée (D3, mesurée)** : « loin == origine » est **bit-exact ssi le déplacement est un multiple de la maille**, sinon visuellement indiscernable (le « ≤ 1 LSB » du plan est faux pris à la lettre sur une scène spéculaire — propriété de rendu, pas faute de précision).
- **Audits de clôture** : `engine-architect` PASS sans réserve ; `csharp-lowlevel` FAIL conditionnel **levé** — M1 (`MaxAxisScale` sous-couvrait le rayon sous shear → faux négatif de culling) corrigé par la **σ_max exacte** (`MathHelpers.MaxStretch`), tight (casque bit-identique), 3 tests de régression.
- **Écart assumé** : cull+collect **3,7 ms JIT-Release / ~6 ms AOT** à 10k, > cible **indicative** 1 ms — dette perf comprise (~80 % = liste d'ombres à 10 000 casters ; cull lumière conservateur sur scène plate, safe).
- **Métriques finales de phase** : 275 tests · 0 warning · 0 message de validation · 0 leak · probe NativeAOT PASS.

## Décisions structurantes (verrouillées)

| Sujet | Choix |
|---|---|
| Bindings | Silk.NET (Vulkan + GLFW + input) — le reste from scratch |
| Baseline GPU | Vulkan 1.2 + dynamic_rendering + synchronization2 (chemin 1.3 core sur MoltenVK) |
| Maths | System.Numerics (convention row-vector) + helpers clip-space Vulkan (Y-flip, Z [0,1]) |
| Shaders | GLSL → SPIR-V à l'exécution (shaderc), hot reload prévu M8 |
| Abstraction GPU | Couche mince mono-backend — aucun type `Vk*` ne sort de `Agapanthe.Graphics` |
| Mémoire GPU | Allocateur from scratch (free-list par blocs 64 MiB, dedicated au-delà de 32 MiB) |
| Assets | glTF 2.0 parsé from scratch, StbImageSharp pour les images ; DTO CPU sans dépendance GPU |
| Discipline mémoire | IDisposable partout, destruction différée N+2 frames (DeletionQueue non-capturante), zéro alloc managée par frame, ResourceTracker (leak = échec du run) |
| Qualité | Tout message de validation layer = bug. xUnit sans GPU pour maths/allocateur/parsing |
| Runtime | .NET 10, TreatWarningsAsErrors |

## Modules

```
Sandbox ──► Rendering ──► Graphics ──► Core
   │            │              (seul projet référençant Silk.NET.Vulkan)
   │            └────► Assets ──► Core   (GPU-free : parsing testable sans GPU)
   └──────► World ─────────────► Core   (ECS Arch — SEUL projet référençant Arch, P2-M2)
Platform ──► Core   (fenêtre GLFW, input, capture souris)

tools/ShaderPrecompiler (P2-M1, SPIR-V hors-ligne) · tools/AotComponentProbe (P2-M2, gate rooting AOT)
```
**Rendering ne référence PAS World** : le monde remplit une `RenderList` (type **Core**, GPU-free) que le Renderer consomme → le Renderer ne connaît pas l'ECS, et Arch (`PrivateAssets="compile"`) ne fuit chez personne.

## Jalons — état

| # | Livrable | État | Session |
|---|---|---|---|
| M0 | Fenêtre, instance/device/swapchain, ResourceTracker | ✅ | S1 |
| M1 | Triangle (pipeline, shaderc runtime, frames-in-flight) | ✅ | S1 |
| M2 | Mesh 3D : depth, descriptors, UBO caméra, push constants, caméra libre | ✅ | S2 |
| M3 | GpuAllocator, staging uploads, textures + mipmaps + samplers | ✅ | S3 |
| M4 | Loader glTF, tangentes, Scene/Material/Renderer, fixtures Khronos | ✅ | S4 |
| M5 | PBR Cook-Torrance + 3 lumières HDR + ACES tone mapping | ✅ validé visuellement | S5 |
| M6 | Shadow mapping directionnel (D32 2048², PCF 3×3 manuel, slope-scaled bias) | ✅ | S6 |
| M7 | IBL compute (cubemap, irradiance, prefiltered, BRDF LUT) + skybox | ✅ validé visuellement | S7 |
| M8 | Hot reload shaders (+includes), labels RenderDoc, confort souris, audit final | ✅ validé (hot reload live < 1 s) | S8 |

**→ PHASE 1 CLOSE (8/8 jalons).** Chaque jalon a clos sur : Sandbox propre (0 message validation, 0 leak), tests verts, double audit agent (csharp-lowlevel + architecte) PASS, board archivé.

## État courant (fin session 8 — Phase 1 close)

**Ce qui tourne** : `dotnet run --project samples/Sandbox` → DamagedHelmet en **PBR complet + IBL + ombres** (Cook-Torrance GGX, normal mapping, AO, emissive, 3 lumières HDR, shadow mapping directionnel PCF, **IBL image-based** — irradiance diffuse + prefiltered specular + BRDF LUT — **skybox** environnement, tone mapping ACES), caméra libre 6DOF (lissage exponentiel dt-indépendant), capture souris OS-confinée, **hot reload des shaders à chaud (< 1 s)**, **labels RenderDoc** sur les passes. Contrôles : +/− exposition, L pivote la lumière clé, N cycle 9 vues debug, PageUp/Down/Home/End sensibilité. `dotnet run … -- MetalRoughSpheres.glb` pour la grille metallic×roughness ; `AGAPANTHE_HDRI=<path.hdr>` change l'environnement.
*(macOS : préfixer `DYLD_LIBRARY_PATH=/opt/homebrew/lib`.)*

**Debug headless** : `AGAPANTHE_CAPTURE=sortie.ppm` dump le target HDR tonemappé, `AGAPANTHE_VIEW="x,y,z"` reproduit un angle caméra, `AGAPANTHE_MAX_FRAMES=N` auto-ferme, `AGAPANTHE_IBL_TEST=<préfixe>` génère l'IBL et dump les faces/maps, `AGAPANTHE_SHADER_RELOAD_TEST=1` force un reload des 4 passes et logge le wall-time (mesure du budget < 1 s sans fenêtre). GpuReadback + Renderer.SaveHdrCapture.

**Métriques (fin Phase 1)** : 58 commits · 100 fichiers C# · ~13 200 lignes (src+samples) · **205 tests xUnit** · 15 shaders GLSL · **14 audits agents (2 par jalon M2-M8), tous PASS** · gate permanent 0 warning / 0 message de validation / 0 leak.

**Acquis techniques clés** :
- Allocateur GPU testé sans GPU (seam `IMemoryBackend`), stats mémoire au shutdown
- DeletionQueue zéro-allocation (payload 4×ulong + destructeurs statiques, offset+memType bit-packés 40/24) — **tout** passe par elle : images, buffers, pipelines, shader modules
- Upload staging synchrone explicite (jamais de submit caché) + chaîne de mips par blits
- Assets 100 % CPU : glTF/GLB source-gen STJ, matrices colonne-major→row-vector prouvées par test, génération de tangentes Lengyel prouvée sur DamagedHelmet
- Multi-passes : CommandList.BeginRendering/TransitionImage publics, FrameRenderer = pur frame-sync, chaîne scène→HDR Rgba16Sfloat→ACES→swapchain sRGB (fix WAR sur l'HDR partagée entre frames in flight)
- Shader PBR : GGX + Smith height-correlated + Schlick, TBN avec fallback anti-NaN, atténuation KHR_lights_punctual, std140 triple-vérifié (C# ↔ GLSL par réflexion)
- **Seam par-passe** (M8) : `Passes/` — chaque passe possède le volatil (shaders + pipeline + desc-template + fichiers source résolus), le Renderer garde le stable + les `Record*`. Le God-object est résorbé ; c'est ce qui rend le hot reload possible.
- **Hot reload** (M8) : résolveur `#include` maison → cache disque keyé par le hash du source **résolu** (atomique + self-healing) → watcher sur le dossier source → recréation du pipeline au bord de frame, ancien en DeletionQueue. Échec de compile = log + ancien pipeline conservé.

**Leçon de guerre M5 (front face)** : le culling supprimait les faces avant — `FrontFace.Clockwise` venait d'un calcul de winding omettant le signe moins de la formule Vulkan (qui compense le Y-down du framebuffer). Avec le Y-flip baké dans la projection, glTF CCW = CCW visuel = CCW Vulkan → `CounterClockwise`. Invisible sur M2 (cube convexe fermé non éclairé ≈ identique en culling inversé) et M4 (Cull None). Diagnostic par captures headless comparées (bug → Cull None → fix).

**Leçon de guerre M8 (le protocole humain trouve ce que les audits ratent)** : deux audits agents PASS n'avaient pas vu que le Renderer loggait « hot-reloaded » **même quand la compilation échouait** (`Reload` était `void` et avalait l'exception → l'appelant ne pouvait pas distinguer succès et échec). Il a fallu une **session réelle à la fenêtre**, avec un vrai shader cassé, pour que le log se contredise à l'écran. Le comportement était correct ; c'est l'*observabilité* qui mentait. Les audits lisent le code, le protocole humain lit les symptômes.

## Fin session 7 — M7 livré (IBL & skybox)

**Verdict visuel PASS** (revue humaine 2026-07-10) : helmet réfléchit l'environnement + ciel visible, MetalRoughSpheres rangée metallic net→flou correcte. L'IBL remplace l'ambiant constant — remède au métal sombre en place.

**Livré en 5 vagues** :
- Graphics (S6) : ImageUsage.Storage, DescriptorKind.StorageImage, GpuImage cubemap (ViewType.Cube + vues par mip/face via CreateMipView, possédées), ComputePipeline + CommandList.Dispatch, GraphicsDevice.SubmitImmediate.
- Assets : HdrImageLoader (Radiance .hdr float via StbImageSharp), HdrImageAsset.
- W3 : **IblGenerator** (4 kernels compute equirect→cube 512² / irradiance 32² / prefiltered 128²×8 mips GGX importance-sample / BRDF LUT 512² RG16F Karis) en un seul SubmitImmediate, **IblMaps** disposable. Gén. ~135 ms.
- W4 : **skybox** (triangle plein-écran far-plane fusionné dans le scope scène, DepthTest LessOrEqual sans Write), set 0 bindings 3/4/5 + **ambiant IBL** dans mesh.frag (kd·irradiance·albedo + prefilteredLod(R, roughness·maxMip)·(F0·brdf.x+brdf.y), Fresnel roughness-aware, ×AO).
- W5/W6 : override AGAPANTHE_HDRI, captures docs/visual-checks, audits (0 critique, 3 findings mémoire + 1 archi corrigés).

**Acquis techniques M7** :
- Half-float pour l'équirect stagé (MoltenVK ne filtre pas linéairement le 32-bit float) ; ToHalf clampe Half.MaxValue + scrub NaN (HDRI brillants → +Inf sinon).
- `ImageLayoutState.ShaderReadOnlyCompute` = ShaderReadOnlyOptimal mais stage compute (hand-off env→kernels lecteurs compute→compute).
- `TransitionImage(GpuImage)` full-subresource (mips×layers) ; no-op pour les cibles 1/1 pré-M7.
- IblGenerator réutilisable (pipelines/layouts/samplers) ; Generate() possède le transitoire (equirect, uploader, pool descripteurs) ; try interne libère sur échec (finding M1).

**Découverte plateforme** : rien de nouveau côté MoltenVK au-delà du half-float ; imageCubeArray évité (vues 2D-array par face, un seul cube).

## Fin session 8 — M8 livré (hot reload, labels, confort, audit final) → **PHASE 1 CLOSE**

**Verdict humain PASS with concerns** (2026-07-12, Windows/RTX 5070 Ti) — protocole : [docs/visual-checks/2026-07-12-m8-hot-reload.md](visual-checks/2026-07-12-m8-hot-reload.md).

**Critère de sortie (spec §6) TENU** : édition d'un shader à chaud, app tournante → **224 ms au pire (1re compile, cache froid), ~2 ms ensuite** (≪ 1 s) · audit csharp-lowlevel **0 finding critique**. L'échec de compilation se comporte comme exigé (§4) : l'app ne crashe pas, le rendu est conservé, l'erreur shaderc est précise, la correction recharge normalement. 0 validation, 0 leak (157 ressources) malgré 4 reloads.

**Livré en 4 vagues + tail** :
- W0 (archi) : `PipelineLayoutBuilder` partagé · **destruction différée des pipelines et shader modules** (ils étaient détruits immédiatement — prérequis non anticipé du hot reload) · résolveur `#include` + clé de cache = hash du source résolu · API `CommandList` debug labels (no-op Release-safe, UTF-8 alloc-free).
- W1 (le nœud) : **seam par-passe** — `Passes/` avec `IReloadablePipeline` + base `ReloadablePass` + Shadow/Scene/Skybox/Tonemap + `IblResources`. Ctor du Renderer : 178 → 90 lignes. **Refactor byte-identique.**
- W2 : **hot reload** — `ShaderHotReloader` (watcher sur le dossier *source*, callback sans Vulkan, debounce) + `Renderer.PollShaderReload()` (early-out zéro-alloc) appelé avant `DrawFrame`.
- W3 : debug labels sur les 4 passes + les 4 kernels IBL · confort souris (lissage exponentiel dt-indépendant, sensibilité ∝ FovY).
- Tail : 2 audits **PASS** · durcissement des 3 findings MEDIUM · fix du log mensonger trouvé par le protocole humain.

**Acquis techniques M8** :
- L'invariant de sûreté du reload a été **démontré** (audit) : l'ancien pipeline est détruit à N+2 alors que son dernier usage possible est N-1 → **marge d'une frame entière**. Il tient *parce que* le reload se fait au bord de frame, avant tout recording.
- Cache `.spv` **atomique** (write-tmp + move) et **self-healing** (blob tronqué → recompile) : un process tué pendant l'écriture ne peut plus empoisonner tous les runs suivants.
- Un seul comparateur de chemins OS-aware pour tout le système (le hot reload en dépend sur les FS sensibles à la casse).

## Dette d'ouverture Phase 2 (issue de M8)

Détail complet : [.absolute-human/archive/board-session8-M8.md](../.absolute-human/archive/board-session8-M8.md) → « Dette d'ouverture PHASE 2 ».

**Validations manquantes** (trous de preuve, pas des défauts constatés) :
- 🔴 **Linux jamais validé** (rattrapage M4, toujours dû). Pas de machine disponible le 2026-07-12 → trou **assumé** pour ne pas bloquer la clôture. Le fix du comparateur de chemins OS-aware est couvert par un test unitaire qui *simule* la sensibilité à la casse, mais **n'a jamais tourné sur un vrai Linux**, pas plus que le watcher inotify. **Premier item dès qu'une machine Linux est disponible.**
- 🟠 Labels RenderDoc **non observés** dans RenderDoc (émission garantie par construction).
- 🟠 Feel souris **non jugé** à la main (lissage correct par construction).

**Findings d'audit non corrigés (hors périmètre M8)** :
- Invariant du reload garanti par **convention** seulement : `PollShaderReload()` est public ; un appelant qui l'invoquerait en cours de recording détruirait un pipeline in-flight silencieusement → poser une garde debug.
- **Crash rare au shutdown** (`0xC0000005` dans `GlfwEvents.Dispose` de Silk.NET) : observé 1 fois, **non reproductible** (12 runs → 0 repro). N'affecte ni le rendu ni les ressources GPU mais **masque le rapport ResourceTracker** quand il frappe. Ne pas patcher à l'aveugle.

**Limites actées du hot reload** : éditions **interface-compatibles** uniquement (changer un binding → set-layout figé → validation error, restart requis) · les 4 shaders **compute IBL ne sont pas surveillés**.

**Dette héritée (phase 2)** : immutable samplers (comparateur hardware) avec CSM · parsing `KHR_lights_punctual` depuis le glTF · texel-snapping des ombres · prefilter env single-mip (fireflies possibles sur HDRI contrasté) · instancing multi-mesh · MikkTSpace si artefacts · auto-exposure · upload async.

## P3-M1 — Instancing (SSBO) + solde des 2 dettes de culling (2026-07-14, session 14)

Spec : [2026-07-14-p3m1-instancing-culling-design.md](plans/2026-07-14-p3m1-instancing-culling-design.md).

Les transforms des entités visibles sont **compactées chaque frame dans un storage buffer host-visible** (un par frame-in-flight, `InstanceBufferRing`) que le vertex shader indexe par `gl_InstanceIndex` ; la liste triée est **batchée par (matériau, mesh)** → **un draw instancié par batch**, `firstInstance` servant d'offset. Les deux dettes de culling de M4 sont soldées : `AggregateBounds()` est recalculé **par frame**, et les shadow casters sont testés contre un **`ExtrudedShadowFrustum`** (frustum caméra étendu vers la lumière, ANDé avec le volume de lumière).

**Mesures (banc `grid:100x100`, 10 000 entités)** : draw calls **12 556 → 2** (1 scène + 1 ombre) · shadow casters **10 000 → ~5 000** · cull+collect **Release JIT 3,7 → ~2,0 ms**, **NativeAOT ~6 → ~2,2 ms** · **0 alloc/frame**, 0 leak, 0 message de validation, 0 warning, **284 tests verts**, AOT PASS.

**Corrections issues du double audit de clôture** (findings appliqués, pas reportés) :
- 🔴 **Règle ε du wedge inversée** : le code jetait les plans **exactement parallèles** au rayon — or ce sont eux qui ferment le wedge latéralement. Avec un **soleil au zénith et une caméra à plat** (la config la plus banale du moteur), les 4 plans latéraux tombaient et le wedge **ne cullait plus rien** ; le banc y échappait par accident (soleil non aligné sur un axe). Corrigé (garde à la borne, biais keep) + test zénith qui l'épingle.
- 🔴 **Fit d'ombre redevenu instable** : `ShadowFit` ne snappait pas la branche « scène » (« une scène statique ne peut pas shimmer » — hypothèse tuée par les bounds désormais recalculées par frame). Le rayon est maintenant **quantifié** (16 crans par octave) et le centre **snappé au texel dans les deux branches** → le fit est une fonction en escalier, plus de crawl des bords d'ombre. Conséquence assumée : la capture casque n'est **plus bit-identique** à P2 (0,25 % de canaux, décalage sub-texel des ombres) — rendu vérifié intact.
- Clé de tri **mesh-major** pour la liste d'ombre (la passe depth ne lie aucun matériau → plus de sur-découpe) · SSBO qui **rétrécissent** après 60 frames sous le quart de leur capacité · rebind du set 1 seulement au changement de matériau · pool de descripteurs persistant déclarant `StorageBuffer` · `GpuBuffer.Write` en multiplication 64 bits.

**Dette ouverte par P3-M1 (→ P3-M2, rendu GPU-driven)** :
- 🔴 Le cull GPU **n'est pas « une ligne de shader »** : il impose `DrawIndexedIndirect` + `BufferUsage.Indirect` + la feature **`drawIndirectFirstInstance`** (qui, elle, n'est pas gratuite). Piste : porter l'offset de batch en **push constant** → neutralise du même coup le risque `baseInstance` sur MoltenVK.
- 🔴 **L'ordre des systèmes vit dans le Sandbox** (`PropagateTransforms → AggregateBounds → ComputeLightViewProj → CollectRenderLists`) : dette #1 soldée *à l'appel*, pas dans le moteur → premier client du **scheduler**.
- 🔴 `ShadowFit.UpstreamExtent` dérive des **bounds globales** : une entité qui bouge à 10 000 km fera vibrer la plage de profondeur de la shadow map de tout le monde (mord dès la physique) → la dériver de la **liste de casters**.
- 🟠 Slots persistants dirty-trackés (les 2 SSBO fusionnent, `RenderItem.WorldTransform` devient mort) · cull CPU O(n) ~2 ms AOT à 10k (c'est ce que le cull GPU rembourse) · plafond **16 bits** mesh/matériau (limite dure documentée) · `SortKey` sans profondeur.

## P3-M2 — Scheduler de systèmes + lifecycle d'entités (`Agapanthe.Engine`) (2026-07-14, session 15)

Spec : [2026-07-14-p3m2-scheduler-lifecycle-design.md](plans/2026-07-14-p3m2-scheduler-lifecycle-design.md). Commit socle : `90627a5`.

**Nouveau projet `Agapanthe.Engine`** — la seule couche qui marie World + Rendering (ne référence pas Platform, ne possède rien). L'**ordre de frame a quitté le Sandbox** : l'invariant `propagate → aggregate → fit → cull → draw` vit dans `FrameOrchestrator` + `SceneViewSystem`, exécuté par un `SystemScheduler` à étages (`Input → Simulation → PostSimulation → Render`), ordre = donnée testable. Deux interfaces disjointes (`ISystem`/`TickContext` sans GPU, `IRenderSystem`/`RenderContext`). `Tick` tourne **hors** de `DrawFrame` (sinon un resize sauterait la simulation — D1.a). Le spin/churn du banc sont devenus des `ISystem` applicatifs.

**Lifecycle d'entités (D2)** : `Spawn`/`SpawnDeferred`/`Despawn`/`SetParent`/`IsAlive` publics sur `GameWorld`, tout différé à une **barrière de fin d'étage**, **`Despawn` cascade** sur les descendants (scan `Parent` à point fixe). `EntityRef` porte désormais le **`GlobalId`** (`ulong`, identité durable qui précède la création), résolu via une map `_live` — le hot path ne la touche jamais. **Pas de `CommandBuffer` d'Arch** : file de commandes propre au World (le buffer d'Arch 2.1.0 invalide ses handles au playback, ne se reset pas, ne résout pas les refs dans les composants — correction D2 v3 après audit du code décompilé). Scope resserré (YAGNI) : pas d'`AddComponent<T>` générique public, `SetParent` seul (pooling/prefabs → backlog).

**Cull d'ombre deux passes (D3)** : le wedge extrudé est **infini vers l'amont** → un caster à 10 000 km piloterait `UpstreamExtent` et ferait exploser la précision de profondeur. Corrigé par un **7ᵉ plan de coupe** bornant le wedge à `ShadowCasterDistance` (ancré sur la sphère du frustum). Circularité fit↔cull cassée en deux passes : passe 1 (`CollectRenderLists`) cull wedge borné + `casterBounds` + tableau parallèle de sphères ; fit (footprint sur `sceneBounds`, profondeur sur `casterBounds`) ; passe 2 (`CompactShadowCasters`) compaction contre le volume de lumière puis tri.

**Mesures** : banc `grid:100x100` **Release JIT ET NativeAOT** — draws **2+2**, **0 alloc/frame** (banc + mode churn), 0 leak, 0 validation · **311 tests** · 0 warning · **NativeAOT PASS** (probe `AotComponentProbe` + Sandbox) · **capture bit-identique `9790D95D`** (D3 est un no-op observable sur la scène par défaut ; le fix est prouvé à l'échelle par test unitaire — `eyeDistance` reste borné vs >1e6 sinon).

**Double audit de clôture PASS** (`csharp-lowlevel` + `engine-architect`, aucun FAIL/MAJEUR) — findings mineurs appliqués : **garde F7** (`_pass1ShadowList` : `CompactShadowCasters` refuse une liste qui n'est pas celle du dernier `CollectRenderLists` — un contrat non gardé serait violé au premier split-screen/CSM), **test D3.a resserré** (borne liée au mécanisme, plus `*10` arbitraire), **spec nettoyée** (§2/§3.2 et récit `Stage.Input`).

**Dette ouverte / non-bloquants notés** :
- 🔴 **Linux/macOS toujours jamais validés** (AOT + SPIR-V hors-ligne Windows-only) — **P3-M0**, toujours le premier item.
- 🟠 **CSM devra sortir l'état de passe-1 (`_casterSpheres`) du World** (contrainte F7 : une seule `RenderView`/frame, désormais **gardée**).
- 🟠 Cascade despawn en **O(profondeur × N_parent)** *quand un despawn est en attente* (re-scan complet par itération du point fixe) — invisible à l'échelle actuelle, à surveiller avec des hiérarchies profondes (physique/gameplay). Corrigeable par une file de travail BFS, au prix d'une liste d'enfants (refusée par le design).
- 🟠 Verdict visuel humain P3-M1 **et** P3-M2 encore dus (P3-M2 bit-identique → non bloquant pour la clôture technique).
- (Report P3-M1 : cull GPU = `DrawIndexedIndirect` + `drawIndirectFirstInstance` ; slots persistants dirty-trackés ; `SortKey` sans profondeur ; plafond 16 bits mesh/matériau.)

## P3-M8 — Premier pas planétaire : reversed-Z + sphère + scène planète/Soleil à l'échelle (2026-07-24, session 21)

Spec : [2026-07-23-p3m8-planetary-first-step-design.md](plans/2026-07-23-p3m8-planetary-first-step-design.md). La **seconde scène de référence** (à côté de la grille de casques) : une planète et un Soleil à l'échelle **1/2 uniforme** (réel÷2 : planète 3 185,5 km, Soleil 348 170 km, distance **7,48e10 m**), qui met enfin à l'épreuve ce pour quoi les fondations `double`/camera-relative ont été bâties — surface planétaire (~1e7 m) et Soleil (7,48e10 m) **dans un seul frustum, sans z-fighting**.

**Le blocage structurel soldé — reversed-Z global.** `MathHelpers.PerspectiveVulkanReversed` (dérivation clip-space exacte `z→w−z` : `M33=−1−M33 ; M43=−M43`) mappe near→NDC 1, far→NDC 0 ; couplé au depth **D32 float** cleared à 0 et au test **`GreaterOrEqual`**, il répartit la précision quasi-uniformément sur un ratio near/far planétaire. **Comparateur depth par pipeline** (`GraphicsPipelineDesc.DepthCompare`, défaut `LessOrEqual` back-compat) : passes caméra (Scene, Skybox) en `GreaterOrEqual`, **passe d'ombre laissée en `LessOrEqual`** → **le CSM est totalement découplé** (ShadowFit reconstruit ses coins depuis les scalaires `FovY/near/far`, jamais depuis la matrice ; `Frustum` label-swap near↔far mais volume identique → culling invariant, prouvé par audit).

**La scène (`AGAPANTHE_SCENE=planet`).** `Primitives.UvSphere` (sphère unité, normales analytiques, tangentes longitude, winding CCW, `ushort` avec garde d'overflow) → `BuildSphereModel` (rayon baké dans les positions locales) → planète (albedo bleu-vert) + Soleil (emissive fort) en `Double3`. **Sun-only, physiquement fidèle** : le Soleil est une **sphère de plasma = seule lumière**, donc une **point light co-localisée avec l'entité Soleil** (inverse-carré, `I = irradiance·d²` ; la lumière part physiquement de l'étoile, pas d'un vecteur abstrait) ; **env noir** (`BuildBlackEnvironment` → IBL 0 + skybox noir) ; ambient 0. À 7,48e10 m les rayons arrivent quasi-parallèles → terminateur net, mais lié à la position du Soleil. La surface du Soleil reste purement émissive (ses normales pointent dehors → `dot(N,L)<0` depuis son centre → la point light ne l'éclaire pas). Échelle 1/2 uniforme = **taille angulaire réelle du Soleil (~0,53°)**.

**Mesures / gates** : **333 tests verts**, 0 warning, 0 message de validation, 0 leak, **NativeAOT PASS**, **GPU==CPU MATCH** (casque/grille 401, planète 2), **0 alloc/frame**. Captures headless casque + grille (rebaselinées sous reversed-Z, verdict visuel humain PASS — pas de régression) + scène planète (croissant + terminateur lisse, nuit noire, Soleil disque lointain sans z-fighting, fond noir). **Rebaseline assumé** : le hash mono/grille change sous reversed-Z ; équivalence prouvée par **audit du diff** (culling invariant, aucun z-test inversé) + verdict visuel, jamais masquée. Env vars : `AGAPANTHE_PLANET_{RADIUS,PHASE,ALT,FOV}`, `AGAPANTHE_SUN_{DIR,RADIUS,DISTANCE}`.

**Double audit de clôture** — les deux **PASS**, 0 🔴/🟠 :
- `csharp-lowlevel` **PASS** : 0 alloc/frame préservé (tout le code allocateur est load-time), chemins AOT-purs, ressources GPU possédées/libérées, **précision point light saine** (I≈2e22 sans overflow, 1/d²≈1.8e-22 sans underflow, `Range=0` désactive le cutoff). 2 🟡 **appliqués** (garde overflow `ushort` dans `UvSphere` + test ; fallback vecteur nul dans `EnvVector3`).
- `graphics-3d` **PASS** : matrice reversed-Z exacte, comparateur par pipeline correct, **culling invariant sous reversed-Z** (labels near/far échangés, volume identique, AND symétrique → 0 faux-négatif — le risque n°1 écarté), CSM confirmé insensible, skybox/z-fighting sains. 1 🟡 **appliqué** (commentaires d'échelle périmés `7,48e10`/`1/2 uniforme`) ; 1 🟡 **noté, aucune action** (garde normalisation `Frustum` `1e-8` conservatrice + pré-existante, hors régime de la scène — mord seulement à un near sub-mètre).

**Dette ouverte / notée** :
- 🔴 **Linux/macOS toujours jamais validés** (P3-M0), premier item de fond.
- 🟡 **Garde normalisation `Frustum` (`Frustum.cs:105`)** : un near sub-mètre avec far≈1e11 rendrait le plan far non normalisé (conservateur — jamais de faux-négatif) ; à traiter si un near < 1 m apparaît.
- 🟠 **Ombres planétaires analytiques** (backlog §2.2) : au pas 1 le CSM se no-op à 3e6 m (nuit = `dot(N,L)`) ; les éclipses/terminateur avancé viennent avec l'atmosphère (backlog §3, §4bis pas 3).
- Suite planétaire (backlog §4bis) : orbites képlériennes (pas 2), LOD sphérique + atmosphère (pas 3).

## VS-1 — Sérialisation du World (save/load snapshot) (2026-07-24, session 22)

Spec : [2026-07-24-vs1-world-serialization-design.md](plans/2026-07-24-vs1-world-serialization-design.md). **Premier jalon de la Vertical Slice** (backlog §4ter) — la **preuve de persistance** (DoD item 3) : `world.Save(Stream)` / `world.Load(Stream)`, round-trip **byte-identique, déterministe, AOT-pur, 0 leak**, et la scène planète qui se recharge à l'identique dans un **process neuf**.

**Format binaire blittable** (`WorldSerialization.cs`, partial `GameWorld` — GPU-free, aucun type Arch ne fuit) : header `AGWD`/version/componentCount/nextGlobalId/entityCount, puis chaque entité **triée par GlobalId** (ordre total → save déterministe) avec un `presenceMask` u32 sur l'ordre `ComponentRegistry.All` et les octets blittables de chaque composant présent, **sauf `InstanceSlot`** (runtime, réassigné au rebuild) et **`Parent` écrit comme le GlobalId du parent** (l'`Entity` Arch est un handle mémoire non-persistable). Dispatch par-composant = 3 switches concrets (Has/Write/ReadAdd) → **AOT-safe** (instanciations statiquement visibles), `MemoryMarshal` + `BinaryPrimitives` LE. **Décisions** (spec §3, déléguées à Claude) : **seam GPU = handles reproductibles** (Option 1 : le caller recharge les mêmes assets d'abord ; clés d'asset stables déférées), **pas de générateur** (blittable → ni réflexion ni source-gen — le « un seul générateur » du backlog supposait un rooting source-generated inexistant), style écrit-à-la-main de `ComponentRegistry`.

**Load** (World frais) : header vérifié → `_nextGlobalId` restauré **sans bump** ; passe 1 create avec le GlobalId sérialisé + `InstanceSlot=-1` sur les drawables ; passe 2 `LinkParent` par GlobalId (réutilise l'existant) ; `_structuralDirty=true`. Arch clé les archétypes par **ensemble** de composants → `Create+Add` reconstruit exactement l'archétype d'origine. Robustesse : magic/version/count/**tronqué (EOF)**/**mask hors-plage**/World non vide/**GlobalId dupliqué** → `WorldSerializationException` typée.

**Intégration Sandbox** : `AGAPANTHE_SAVE=<path>` (snapshot après build de scène) / `AGAPANTHE_LOAD=<path>` (force scène planète, charge les assets puis `SetupPlanetScene(spawnEntities:false)` + `world.Load` — contrat Option 1). `GameWorld.LiveEntityCount` public.

**Gates** : **346 tests** (round-trip tous archétypes, **byte-identique `Save(Load(bytes))==bytes`**, remap Parent, déterminisme, 7 cas d'erreur, garde d'ordre + garde ≤32), 0 warning, 0 validation, 0 leak, **AOT probe PASS** (`AotSerializationSmoke`, `IsDynamicCodeSupported=False`), **round-trip cross-process byte-identique JIT+AOT** (`0034af33…`, save planète 2 entités / 314 o → reload process neuf). **Double audit PASS** (`csharp-lowlevel` 0 🔴/🟠 · `engine-architect` 1 🟠 + 6 🟡, tous appliqués : garde ≤32, garde GlobalId dupliqué, doc endianness, invariants commentés, `.gitignore`). **Verdict humain fonctionnel PASS.**

**Dette / notée** : source-unique émergente (le triple switch DUPLIQUE l'ordre du registre — gardé par le test d'ordre figé + `Save` qui lève sur index inconnu, mais pas une table unique) · `GlobalId` sérialisé ×2 (clé + composant 0, inoffensif) · **Generation d'Option 1 non exploitée** (un ordre de chargement d'assets différent casse en silence — futur *fingerprint d'assets* fourni par le caller, backlog) · garde fresh-world plus permissive que « fraîchement construit » (inoffensif, `Load` écrase l'état stale).

## VS-3 — Glu gameplay : défi d'atterrissage planétaire (2026-07-26, session 24)

**CLOS.** Double audit **PASS** (`csharp-lowlevel`) / **PASS-with-concerns 4/5** (`engine-architect`), findings
appliqués ; verdict humain PASS. Livre la **glu de gameplay minimale** de la Vertical Slice : câble **input → spawn
(VS-2) → règle d'état → save/resume (VS-1)** sous l'ancre planétaire, sans nouveau chemin de rendu (HUD in-view = VS-4).

**Trois couches, layering respecté** : le cœur (`Agapanthe.World`) ne gagne qu'une **requête spatiale générique**
`QuerySurfaceContacts(C, R, band, zoneC, zoneR) → LandingCounts(Total, Airborne, InZone)` (itère `BodyDesc`, 0-alloc
prouvé par test, aucun type Arch ne fuit, « posé = SUR la surface » sans seuil de vitesse — robuste au glissement
frictionless) ; la **règle pure latchée** `LandingChallengeRule.Evaluate(counts, shotsIssued, prev)` vit dans
`Agapanthe.Engine` (testable GPU-free : Won si `InZone≥N`, Lost si budget épuisé **et** `Airborne==0`, terminal
monotone intra-session) ; le `LandingChallengeSystem` (Camera + `window.Title`) dans le Sandbox.

**Boucle** (`AGAPANTHE_SCENE=planet-challenge`) : voler au-dessus d'une planète, `B` largue une probe **radiale sous la
caméra** (`n̂=normalize(camPos−C)`, chute newtonienne VS-2), poser **N=3** dans une zone-cible en **≤ M=6** tirs. Beacon
émissif drawable (pas un body → jamais compté). Système en **PostSimulation** (positions post-physique) ; `TryShoot`
gardé par `_shotsIssued<M` (pas la query — un probe droppé est dans `_pendingSpawn` jusqu'au barrier). Titre reconstruit
**seulement** sur changement `(InZone, shotsIssued, status)` → 0-alloc régime stable.

**Save/resume relaunch-only** (`F5`→`world.Save`, flush intégré ; relaunch `AGAPANTHE_LOAD`) : **zéro octet ajouté au
snapshot VS-1** — `_shotsIssued` re-semé du body count post-Load (probes jamais despawn → count=tirs), `landed/airborne`
dérivés de la query, statut re-évalué une fois. Précondition : mêmes constantes de scène — **épinglées dans les profils
Rider** `planet-challenge` (audit A2).

**Métriques** : **372 tests** (17 VS-3 GPU-free + probe AOT) · 0 warning · scène headless **0 validation / 0 leak**
(226 resources) · **NativeAOT PASS** (`QuerySurfaceContacts` rooté). Double audit findings : A2 (constantes épinglées)
+ `F5` I/O gardée = corrigés ; A1 (latch non-monotone au reload) + A3 (log non ré-émis) + churn frictionless = réconciliés
en dette documentée. Spec : [plans/2026-07-26-vs3-landing-challenge-design.md](plans/2026-07-26-vs3-landing-challenge-design.md).

**Dette léguée** : 🟠 **latch Won/Lost non-monotone à travers un reload** (`_status` re-dérivé du monde ; un probe qui
glisse hors/dans la zone entre save et reload peut un-win/un-lose — faible probabilité ; alternative `.state` 1 octet
déférée) · 🟡 churn du titre au bord de zone (glissement sans friction) · 🟡 offset radial `≈r` dans `InZone` (by-design).

## VS-2 — Spawn runtime différé + gravité newtonienne (2026-07-26, session 23)

Spec : [2026-07-25-vs2-spawn-runtime-newtonian-gravity-design.md](plans/2026-07-25-vs2-spawn-runtime-newtonian-gravity-design.md). **Deuxième jalon de la Vertical Slice** — solde la **dette P3-M3** (`SpawnBody` immédiat → impossible de spawner un corps en cours de simulation sans muter les archétypes sous l'itération de `StepPhysics`) et, sur décision humaine en cours d'interview, ajoute une **gravité newtonienne** minimale pour donner une démo d'intégration cohérente (sonde larguée vers une planète).

**Spawn différé** (`GameWorld.Physics.cs` / `GameWorld.cs`) : `SpawnBodyDeferred(spec, v, invMass, e, r)` = jumeau runtime de l'immédiat `SpawnBody`, enfilé sur la file de commandes du World (`CommandKind.SpawnBody` + 4 champs plats sur `StructuralCommand`) et appliqué à la **barrière structurelle** de fin d'étage. `MaterialiseBody(...)` = **point de matérialisation unique** partagé immédiat/différé (anti-drift). Handle `IsAlive` immédiat, intégré la **même frame** (spawner en `Stage.Input` → barrière → `StepPhysics` en `Stage.Simulation`, §3.4). `InstanceSlot=-1` → rebuild de slots au prochain collect.

**Gravité newtonienne** (`PhysicsSettings.cs` / `GameWorld.Physics.cs`) : attracteur unique **dans les settings** (`WithAttractor(C, μ, R)`, PAS un composant ECS → ordre `ComponentRegistry.All` et masque VS-1 intouchés). `StepPhysics` branche sur `Mu>0` : pass 1 gravité radiale inverse-carré `a=−μ·(p−C)/|p−C|³` (double, cast float velocity, **garde singularité `r2>1e-18`**), pass 3 **sol radial** `|p−C|−r<R` (push-out à `R+r`, réflexion de la vitesse normale, **rest-clamp `2·(μ/R²)·dt`** — PAS `gravity.Y`, sinon micro-bounce éternel). **`μ=0` → chemin uniforme byte-identique** (scène `drop` P3-M3 protégée). Pas d'orbites (Euler symplectique + velocity `float`), pas de n-body — hors scope assumé.

**Intégration Sandbox** : `AGAPANTHE_SCENE=planet-drop` (planète+Soleil ½ réel de P3-M8, attracteur au centre, caméra proche surface `FramePlanetDropCamera` side-lit), `ProbeDropSystem` déterministe en `Stage.Input` (`AGAPANTHE_DROP_EVERY=N`) **+ keypress `B`** (hors gate déterministe). Tunables : `AGAPANTHE_PLANET_MU`, `_DROP_HEIGHT`, `_PROBE_RADIUS`, `_DROP_SUN_OFF`, `_DROP_CAM_BACK/_CAM_HEIGHT`. Profils Rider dans `samples/Sandbox/Properties/launchSettings.json`.

**Gates** : **355 tests** (spawn différé : handle vivant avant flush / matérialisé à la barrière / non intégré avant flush ; gravité radiale : accel vers C, symétrie angulaire, inverse-carré, sol radial lift/reflect, settling, déterminisme ; régression μ=0 byte-identique), 0 warning, 0 validation, 0 leak (217 resources), **0 alloc hot path** (spawner 0-alloc hors drop), **NativeAOT PASS** (`AotRootingSmoke iterated 13`, exécute le contact du sol radial sous ILC). **Capture headless déterministe byte-identique** (intra-binaire — casts double→float ⇒ pas de byte-identité pixel cross-JIT/AOT, hors scope). **Double audit PASS-with-concerns** (`csharp-lowlevel` PASS-w-c · `engine-architect` **4,5/5** ; les deux ont trouvé indépendamment le **même 🟠** singularité `r2≈0`, corrigé + 🟡 m1/m3/m4 appliqués). **Verdict visuel humain PASS.**

**Dette / notée** : pas de **lifetime/rest-cull** des corps runtime → croissance non bornée (m2, à traiter pour l'ancre persistante) · garde `r2>ε` retourne accel=0 au barycentre (sain ; pour l'univers persistant, envisager un échec *bruyant* si un corps mobile atteint le centre) · broadphase cellule planétaire (cloud clusterisé O(n²), OK pour la démo).

## Reprise — où repartir

> Trajectoire long terme (CSM, rendu GPU-driven, nuages volumétriques, atmosphère, ombres planétaires analytiques,
> physique) : **[BACKLOG.md](BACKLOG.md)** — chaque item dit *ce qui casse sans lui* et *à quelle échelle il devient
> obligatoire*.

> ### ✅ **MP-0a CLOS (S26)** — headless split : la simulation tourne sans GPU
> Spec : **[plans/2026-08-13-mp0a-headless-split-design.md](plans/2026-08-13-mp0a-headless-split-design.md)**
> (approuvée **4,15/5** après 2 tours ; v1 notée 2,85 « Major Gaps » — le finding fatal était que la spec avait cité
> un *commentaire* au lieu du code exécutable). Double audit **PASS-with-concerns ×2** (`engine-architect` 4,2/5),
> **aucun 🔴**, tous les 🟠 appliqués. Détail : [archive/board-session26-MP0a.md](../.absolute-human/archive/board-session26-MP0a.md).
>
> **MP-0 est DÉCOMPOSÉ en 4 sous-jalons** (décision humaine S26) : ils ne partagent ni fichiers ni risques, et un
> double audit portant sur 4 sous-systèmes hétérogènes ne vaudrait rien. **L'ordre exécuté inverse celui du backlog**,
> qui classait par sévérité : le coût du split est **strictement croissant** avec la taille d'`Engine`, alors que les
> deux 🔴 identité ne sont pas cassés aujourd'hui et ne le deviennent **qu'au moment du partitionnement**.
>
> **Livré** : `Agapanthe.Engine` réduit à `{Core, World}` · nouveau **`Agapanthe.Engine.Render`** (RenderContext,
> IRenderSystem, `RenderSystemScheduler`, FrameOrchestrator, UiRenderSystem, DebugOverlaySystem) · **`SimulationHost`**
> (monde + schedule + bracket de mesure, ne nomme aucun type de rendu) · **`samples/HeadlessSim`** NativeAOT, 1,68 Mo,
> 0 B/frame, **snapshot JIT == AOT** (`7c889fec`) · **6 gates d'architecture** automatisés · `AggregateBoundsSystem` et
> `_sceneBounds` **supprimés** (écrits chaque frame, lus par personne depuis P3-M5 — un commentaire périmé les avait
> fait survivre 11 sessions).
>
> **Ce que le jalon a démontré empiriquement** : le gate d'architecture **statique** (lit le `.csproj`) et le gate de
> **closure d'assemblies** ne sont pas redondants. Mutation : ré-ajouter `ProjectReference Rendering` **sans utiliser
> aucun type** → statique FAIL, closure VERT (le compilateur élide la référence inutilisée). Sans le statique,
> l'échec serait survenu au commit *suivant* et aurait été imputé au mauvais changement.
>
> **Gates** : **491 tests** (3 runs) · 0 warning · **HDR `12638edd` et UI `03421357` inchangés** · 0 leak
> (233 resources) · 0 validation · AOT PASS · closure `Agapanthe.Engine` = `{Core, World}`.
>
> **Dette / décisions au dossier** : ✅ ~~`CurrentTick.FrameIndex` post-incrément~~ + ~~`FrameIndex` → `TickIndex`~~
> **résolus MP-0c (S28)** · 🟠 deux relâchements de gel déclarés et testés (`_frozen` dédoublé) · 🟡 `Camera` reste
> dans `Rendering` · 🟡 renommage `Engine.Render` → `Engine.Presentation` non fait.
>
> ### ✅ **MP-0b CLOS (S26-27)** — identité d'entité
> Spec : **[plans/2026-08-13-mp0b-entity-identity-design.md](plans/2026-08-13-mp0b-entity-identity-design.md)**
> (2 tours de revue scorée : tour 1 4,0/5 APPROVED WITH FINDINGS / 12 findings appliqués, tour 2 **4,4/5** APPROVED
> / 3 findings appliqués). **4 vagues** (W1 clé de contact seule → W2 plage d'allocation → W3 snapshot v2 + identité
> d'univers → W4 double audit + corrections), chacune gardant les hashes de capture `12638edd`/`03421357`
> **inchangés** jusqu'à la fin. Détail complet par vague : archive
> **[.absolute-human/board.md](../.absolute-human/board.md)** (« Résultat W1/W2/W3/W4 »).
>
> **Ce que les deux tours de revue ont attrapé, et qui n'était pas dans le brainstorm** :
> (1) la clé de contact est une clé d'**ORDRE**, pas d'identité de paire — `_pairKey` n'est écrit qu'en
> `GameWorld.Physics.cs:333` et lu que par `Array.Sort` en `:347`, le contenu des paires voyage dans `_pairPacked` ;
> le défaut réel est la **dépendance au bloc d'ids** (deux nœuds partant de blocs différents calculent des états
> différents), pas une collision de paires · (2) la couverture AOT vient du **3ᵉ corps** de `AotRootingSmoke`
> (`GameWorld.cs:583-588` → 3 paires), **pas** du pas à 2 corps `:570-575` : `Array.Sort` sort avant
> `ArraySortHelper<,>` quand `length <= 1` · (3) le gate « hashes de capture inchangés » ne vaut que parce que la
> scène de capture forme un **tas** (`Sandbox/Program.cs:2118-2132`, 35 largages) — sinon un hash identique ne
> prouverait rien · (4) **`7c889fec…` était un MD5** : l'algorithme n'avait **jamais** été consigné avant cette
> session, retrouvé par mesure.
>
> **Livré** : **`ContactPairKey`** (`src/Agapanthe.World/ContactPairKey.cs`, W1) — struct 128 bits comparable
> (`IComparable<T>` contraint, jamais boxé), remplace le packing 32 bits `(gid_j<<32)|(uint)gid_k` qui collisionnait
> silencieusement dès que les ids cessaient d'être denses · **`GlobalIdRange`** (`GlobalIdRange.cs`, W2) — plage
> `[Start, EndExclusive)`, `Default = [1, ulong.MaxValue)` bit-pour-bit l'ancien compteur ; les 7 anciens sites
> `_nextGlobalId++` collapsent dans un seul `NextId()` qui lève bruyamment à l'épuisement · **`UniverseId`**
> (`UniverseId.cs`, W3) — identité **par snapshot** (deux `ulong`, pas un `Guid` — mixed-endian), défaut vide et
> **non aléatoire** (un `Guid` tiré aurait cassé le déterminisme JIT-vs-AOT). **Snapshot v2** : en-tête 40 octets
> (`magic|version=2|componentCount|universeId(16)|nextGlobalId|entityCount`), v1 refusé avec message dédié,
> réconciliation d'univers à **5 cas** testés (both `None` → inchangé · snapshot set/monde `None` → adopte · monde
> set/snapshot `None` → garde · both set égaux → confirmé · both set différents → **throw**, `WorldSerializationException`).
> `Load` gagne **`SnapshotAllocatorPolicy`** (`AdoptFromHeader` | `KeepMine`) — **PAS** un `GlobalIdRange?`
> (corrigé en W4, voir audit ci-dessous) — et retourne **`SnapshotLoadResult(UniverseOutcome, EntityCount)`**.
>
> **Double audit W4 (`csharp-lowlevel` + `engine-architect`) — le 🔴 qu'il a attrapé** : la première version de
> `Load` acceptait un `allocatorOverride: GlobalIdRange?` qui pouvait recouvrir des ids déjà chargés depuis le
> snapshot ; les trois points de matérialisation d'entité écrivaient `_live[globalId] = entity` par **indexeur**,
> donc un spawn ultérieur réémettant le même id **écrasait silencieusement** l'entrée — l'entité chargée restait
> vivante et simulée mais devenait indespawnable et disparaissait du `Save` suivant (qui itère `_live`). Corrigé :
> les trois sites passent par un nouveau `RegisterLive` (`TryAdd` + exception nommée sur collision), testé par un
> repro exact de l'auditeur. L'architecte a aussi trouvé que `allocatorOverride` conflait « ignore l'en-tête » et
> « voici une nouvelle plage » — un bail (fixé à la construction) redevenait réassignable par un paramètre de
> désérialisation ; remplacé par l'énumération sans donnée ci-dessus, le bail redevient un fait à un seul point de
> vérité. PASS-with-concerns 4,1/5 côté architecture (aucun 🔴), dette explicitement notée : aucun hôte de
> production ne nomme encore d'univers (Sandbox/HeadlessSim chargent en `UniverseId.None`) — le **mécanisme** du
> 🔴 « deux mondes inmergeables » est fermé, son **usage** attend le premier hôte réel (`Agapanthe.App`/MP-0c-d).
>
> **`HeadlessSim` re-épinglé** : format v2 → **`7e8dc68f5a25914c84677a7a53ad3a58`**, **1868 octets** (v1 :
> `7c889fec0df503fe8137ef6c28c7751a`, 1852 octets — delta +16 = `UniverseId`). Vérifié **JIT == NativeAOT**
> (publish `win-x64` self-contained) avant ET après les corrections W4 — inchangé, l'API pure ne touche pas le
> format sur fil. Reproduit par
> `dotnet run --project samples/HeadlessSim -c Debug -- --ticks 600 --bodies 8 --save <path>` puis
> `Get-FileHash -Algorithm MD5`. **Gate désormais dans un test** (`HeadlessSimSnapshotFormatTests.cs`), plus
> seulement dans la prose — c'était le trou que la redécouverte de `7c889fec` en session 26 avait signalé.
>
> **Gates** : **530 tests** · 0 warning · **HDR `12638edd` et UI `03421357` inchangés** (4 vérifications, une par
> vague) · 0 leak (233 ressources) · 0 validation · AOT PASS. Double audit PASS-with-concerns (architecture
> 4,1/5) / 🔴 trouvé et corrigé (bas niveau) → **verdict humain PASS**.
>
> **Dette laissée** : 🟠 aucun hôte de production ne stampe encore d'`UniverseId` (attend `Agapanthe.App`) · 🟡 pas
> de renouvellement de bail avant épuisement de `GlobalIdRange` (additif, pas de refonte requise) · fusion de deux
> univers rendue **détectable**, pas **implémentée** (hors scope assumé).
>
> ### ✅ **MP-0c CLOS (S28)** — autorité du temps : le tick de simulation découplé de la frame de rendu
> Spec : **[plans/2026-09-05-mp0c-time-authority-design.md](plans/2026-09-05-mp0c-time-authority-design.md)**
> (APPROVED **4,4/5**, 2 tours ; v1 à 3,7 NEEDS REVISION — le test d'équivalence échouait en `float` `3f/60f ≠
> 3f*(1f/60f)` d'1 ULP → 59 vs 60 ticks ; « les hashes de capture vont changer » était **faux**, aucun code prod ne
> lit `ctx.DeltaSeconds`). Détail par vague : **[.absolute-human/board.md](../.absolute-human/board.md)** (§Progress log).
>
> **Livré** : **`FixedTimestepAccumulator`** (`Agapanthe.Engine`, décision 7) — `Advance(host, dt) → int` accumule le
> dt wall-clock et pilote `SimulationHost.Tick` N pas fixes ; `AdvanceFrame` = `BeginFrame` + `Advance` (séquence
> d'un hôte real-time, ce que `FrameOrchestrator.Tick` appelle) · **clamp d'entrée 250 ms** + **garde de ratio ctor**
> (`max/fixed > 1024` → throw : `fixed≈1e-9` boucle infinie, soustraction float = no-op) · **non-fini/négatif → `0f`**
> (échec inerte, jamais 15 ticks physique silencieux) + `SanitisedInputCount` · `Sanitise` extrait (`float.IsFinite`
> **avant** `MathF.Min` qui propage NaN) · **`FrameIndex` → `TickIndex`** partout · **off-by-one `CurrentTick`
> corrigé** (`Math.Max(0L, TickIndex-1)` = dernier tick exécuté, épinglé pour la 1ʳᵉ fois) · `PhysicsSystem.RatesMatch`
> extrait + `Debug.Assert`, `PhysicsSettings.FixedDt` **inchangé** (décision 3) · `SimulationHost.LastFrameTickCount`
> (latché en `EndFrame`) + loggé au banc.
>
> **Déterminisme des captures** (décision 2, prédiction du brainstorm **corrigée** — pas une des 8 décisions) :
> `AGAPANTHE_MAX_FRAMES` (> 0) → dt synthétique = 1 tick/frame → **hashes INCHANGÉS** (HDR `12638edd`, UI `03421357`,
> byte-identiques ×3 runs). Un run déterministe **DOIT** poser `AGAPANTHE_MAX_FRAMES`.
>
> **Vérification** : `AccumulatorEquivalenceTests` — **20×`Advance(3·Fixed)` == 60×`Advance(1·Fixed)` == 60 ticks**
> (assertion primaire = **entiers** ; positions = garde de câblage car `PhysicsSystem` ignore `DeltaSeconds`) +
> catch-up `FrameOrchestrator`-shape via `AdvanceFrame` · `AotComponentProbe` exerce l'accumulateur
> (`AotAccumulatorSmoke: 1 + 3 ticks`).
>
> **Gates** : **558 tests** · 0 warning · **HDR `12638edd` / UI `03421357` inchangés** (3 runs) · `HeadlessSim`
> `7e8dc68f5a25914c84677a7a53ad3a58` **JIT == AOT** inchangé (`HeadlessSimSnapshotFormatTests` passe sans édition) ·
> `AotComponentProbe` PASS · 0 leak (233) · 0 validation. Double audit **PASS-with-concerns ×2** (`engine-architect`
> 4,2/5), **aucun 🔴** — 8 findings 🟠/🟡 appliqués (rename params, garde de ratio, `Sanitise`+compteur, `NaN→0`,
> `AdvanceFrame` testable, latch `LastFrameTickCount`, probe AOT, test 0-alloc multi-branches).
>
> **Dette laissée** (board §Deferred Work) : interpolation visuelle (l'accumulateur détient déjà le facteur de blend
> `_accumulated/FixedDeltaSeconds`) · `FixedTimestepAccumulator.Reset()` (Load in-process, pause) · **pas fixe = 4
> littéraux `1f/60f` indépendants → le rendre une donnée de config de la simulation est un prérequis netcode** · `bool
> HasTicked` (désambiguïse `TickIndex == 0`) · ancrage thread dans `Tick`.
>
> ### ✅ **MP-0d CLOS (S29)** — input → commandes horodatées : l'input n'atteint la sim qu'en tant que commande
> **Dernier sous-jalon de MP-0 → MP-0 CLOS (4/4).** Spec :
> **[plans/2026-09-06-mp0d-input-commands-design.md](plans/2026-09-06-mp0d-input-commands-design.md)** (approuvée
> 4,30/5, v1 3,33 NEEDS WORK — 2 🔴 + 6 🟠, toutes citations exactes). 4 vagues, feu vert humain entre chaque.
>
> **Livré** — `Agapanthe.Engine` (closure `{Core, World}` **intacte**, `EngineIsHeadlessTests` vert) :
> - `SimCommand` (`readonly record struct`, `[Sequential]`, **56 o** — offsets épinglés par test) : `long
>   TargetTick` · `byte Kind` **opaque** (l'app définit ses constantes, l'engine n'interprète jamais) · `EntityRef
>   Target` · **`Double3 Vector`** · `float Scalar` · `uint Flags`. `SimCommandHandler` delegate.
> - `InputSnapshot` (`[Sequential]`, **40 o**) : `Held`/`Pressed`/`Released` (edges) + `InputAxes` =
>   `[InlineArray(4)] float` (**1ʳᵉ utilisation projet**, AOT-safe, exercée par le probe).
> - `InputMap` déclaratif : `BindButton(bit, kind, OnPress|OnRelease|WhileHeld)` + `BindAxisVector(kind, x, y, z)`
>   (une commande/tick, `Vector` = les axes liés). Le binding axis laisse **`Target = default`** — un client n'a
>   pas autorité sur *quelle* entité ; l'ownership routing vivra dans `ApplyCommand` (`EntityRef` ctor `internal`).
> - `SimCommandQueue` : insertion sort stable (`>` strict = FIFO à tick égal), `DrainUpTo(tick, handler)` **lève le
>   préfixe dû dans un scratch et compacte AVANT d'appeler les handlers** → un handler peut `Enqueue` sans
>   corruption (sa commande attend le drain suivant) ; drain ré-entrant → throw ; garde owner-thread
>   `[Conditional("DEBUG")]` qui **lève** (patron `GameWorld.AssertOwnerThread`).
> - `InputTranslation.Emit` : static, stateless, 0-alloc, pas de `prev`.
>
> `SimulationHost.Tick` gagne une phase **avant `Stage.Input`** : `SampleInput()` (1×/tick) → `InputTranslation.Emit`
> → `Commands.DrainUpTo(_scheduler.TickIndex, ApplyCommand ?? _discard)`. Drain avant toute stage → `ApplyCommand`
> mute sans query en vol, `StepPhysics` voit la vélocité le même tick. `ApplyCommand` null →
> **`DiscardedCommandCount`** (signal Release, patron `SanitisedInputCount` de MP-0c — **pas** un `Debug.Assert`).
> L'accumulateur MP-0c **inchangé**.
>
> `GameWorld.SetBodyVelocity(EntityRef, Vector3)` : gardes standard **+ `Has<Velocity>()` → throw** (Arch `Set<T>`
> sur le mauvais archétype = écriture hors-bornes silencieuse, et `SimCommand.Target` est une donnée externe —
> audit LL 🔴).
>
> **Démos** : `HeadlessSim --drive` (séquence `InputSnapshot` scriptée, **le gate déterministe**, MD5
> `97e786f0455a53d856b9ba4affca1003` / 208 o, **JIT == AOT**, épinglé dans `HeadlessSimSnapshotFormatTests`) ·
> Sandbox `AGAPANTHE_SCENE=drive` (interactif, **non épinglé**, caméra fixe, WASD + `X` brake) · `Key.B`
> (planet-drop/challenge) → `orchestrator.Simulation.Commands.Enqueue` stampé `TickIndex` portant `camera.Position`,
> routé par `ApplyCommand` vers `TryShoot`/`DropOne`.
>
> **Gates** : **589 tests** (+31) · 0 warning · HDR **`12638edd`** / UI **`03421357`** **inchangés** (×3) ·
> `HeadlessSim` défaut `7e8dc68f…` **inchangé** · `HeadlessSim --drive` `97e786f0…` **JIT == AOT** ·
> `AotComponentProbe` PASS (`IsDynamicCodeSupported=False`) · 0 leak / 0 validation. Double audit :
> `engine-architect` PASS-with-concerns **4,3/5** (aucun 🔴) · `csharp-lowlevel` PASS-with-concerns (**1 🔴
> trouvé-et-corrigé** : `SetBodyVelocity` sans `Has<Velocity>`). Findings 🟠 appliqués : file ré-entrante,
> `DiscardedCommandCount`, offsets de champs testés.
>
> **Dette laissée** (board §Deferred) : extraire un `SimulationInput` de `SimulationHost` **au 2ᵉ concern** input
> (receive réseau / ownership routing / replay log), pas avant · purge `Commands` sur un `GameWorld.Load`
> in-process (avec la dette `FixedTimestepAccumulator.Reset()`) · cap anti-flood de `SimCommandQueue` ·
> `OriginatorId` / identité de pair · format fil (send/recv) · le clone `RunDrive` dans les tests.
>
> ### ✅ **`Agapanthe.App` CLOS (S30)** — premier hôte de production : `Program.cs` (2360 l) → `AppHost` + contrat `IGame`
> Spec : **[plans/2026-09-07-agapanthe-app-design.md](plans/2026-09-07-agapanthe-app-design.md)** (APPROVED **4,40/5**,
> **3 tours** — v1 3,13 référence de projet circulaire `App↔Platform` ; v2 3,88 test factice ; v3 4,40 + décision adapter).
> Détail par vague : **[.absolute-work/board.md](../.absolute-work/board.md)**.
>
> **Livré** : nouveau **`src/Agapanthe.App`** — `IWindow` (abstrait le backend fenêtre, expose `Silk.NET.Input.Key`),
> `IGame` (Title / Scenes / DefaultScene / `Universe => UniverseId.None` DIM) + `ISceneRecipe.Build(SceneContext)`,
> `HostOptions.FromEnvironment(Func<string,string?>?)`, **`AppHost.RunClient(IGame, IWindow, string[], HostOptions?)`**
> (bootstrap GPU + frame loop fixed-step + harness capture + **teardown ordre-strict M4-11 = le gate 0-leak**, portés
> verbatim ; `BuildTeardown` = liste ordonnée que le `finally` itère ET qu'un test assert). **`App` NE référence PAS
> `Platform`** (sinon Vulkan entre transitivement dans un leaf) → `EngineWindowAdapter : IWindow` vit dans
> `samples/Sandbox` ; `EngineIsHeadlessTests` a une `[InlineData]` sur `App.csproj` (allowlist statique MP-0a).
> **Couture composition-root MP-0a exercée pour de vrai** : `SimulationHost.CreateDefault(world, SimulationSettings.Default)`
> puis `FrameOrchestrator.CreateDefault(sim, world, …)`. **`SimulationSettings { FixedDeltaSeconds }` sur `SimulationHost`
> = définition unique du pas fixe** (`ResolveAccumulatorStep` le lit ; recipes + `HeadlessSim` le lisent ; le `const
> FixedDt` de `HeadlessSim` supprimé ; `PhysicsSettings` garde son littéral, réconcilié par `RatesMatch`). **Dette MP-0b
> payée** : `AppHost.ResolveUniverse` = `options.Universe ?? game.Universe` → `new GameWorld(GlobalIdRange.Default,
> resolved)` — premier hôte qui stampe un `UniverseId` ; `SandboxGame` reste `None` → hashs inchangés ; override
> `AGAPANTHE_UNIVERSE`. **Sandbox thin** : `Program.cs` **2360 → 22 l** ; **5 recipes** (`ModelSceneRecipe` couvre
> `model`/`grid:`/`drop:`, `Planet`/`PlanetDrop`/`PlanetChallenge` partagent `PlanetStage`, `Drive`) + `Content/` +
> `Cameras/` + `Systems/` + `Tools/IblTestTool`. Caméra free-fly : recipes → `ctx.Window.Updated` (verbatim, pas
> `SampleInput`) via `RecipeInput.WireFreeFly` ; clavier host = Escape/sensibilité/exposition/N/L/F3, `B`/`X`/`F5` → recipes.
>
> **Métriques** : **617 tests** (+28) · 0 warning · captures HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI
> `034213575932dabcff41c2e0c72addfa` **inchangées** ×3 · `model` baseline `df55d444b74c7aa94fd0ab18d795cc9c` ·
> `HeadlessSim` défaut `7e8dc68f5a25914c84677a7a53ad3a58` (1868 o) + `--drive` `97e786f0455a53d856b9ba4affca1003`
> **inchangés** · **Sandbox + HeadlessSim JIT == NativeAOT** · `AotComponentProbe` PASS · 0 leak / 0 validation ·
> une scène inconnue sort en **exit 1** (JIT + AOT).
> Double audit : `engine-architect` PASS-with-concerns **4,2/5** (**1 🔴 trouvé-et-corrigé** — `RunClient` renvoyait 0
> sur échec d'init, `clean` écrasé par l'étape Report du teardown → flag `failed` + `return clean && !failed`) ;
> `csharp-lowlevel` PASS-with-concerns **aucun 🔴** (teardown ordre EXACT vérifié, hot path 0-alloc, extractions fidèles).
> **Tous les 🔴/🟠/🟡 contenables corrigés** (2 passes) : isolation par étape du teardown, `[InlineData]` `App↛Platform`,
> `HostOptions.Scene`/`SavePath`/`VerifyCull`/`ShaderReloadTest` (`RunClient` ne lit plus aucune env var), surface
> morte `IWindow` retirée, garde de longueur `Ppm`, `IWindowSurfaceTests` (équivalent du test 5), dédups.
> **Verdict visuel humain : PASS** (2026-09-09, S33 — verdict partagé S30+S31+S32).
>
> **Découvertes / notes** : (1) `App→Platform` aurait tiré `Rendering/Graphics/Silk.NET.Vulkan` transitivement dans
> `Platform`, un leaf Vulkan-free → adapter dans Sandbox, contrainte de layering, pas de la spéculation ; (2) l'ordre
> des captures byte-identique à travers un déplacement de 2360 lignes **entre assemblies** (JIT==AOT) — la baseline
> `model` enregistrée en W1 *avant* de bouger quoi que ce soit était le bon réflexe ; (3) les recipes planet chargent
> le glTF **après** `SetupPlanetScene` (l'ancien `Program.cs` avant) → un `.save` **pré-refactor** peut ne plus résoudre
> ses handles (seam Option 1 VS-1) ; la nouvelle build est auto-cohérente.
>
> **Dette laissée** (board §Deferred, S30 — après les corrections) : 🟠 **`SceneContext` indissociablement client** —
> tout `required` → une recipe ne se construit pas headless, `HeadlessSim` partage 0 code de peuplement ; **mord à la
> 2ᵉ slice + `RunDedicatedServer`** · 🟠 `EngineWindowAdapter` (~75 l) → projet `Platform.App` dédié à l'app n°2 · 🟠
> helpers caméra/lumière/sol/sky → `App` au jalon contenu · 🟡 exit code modèle-introuvable 2→1 (check game-specific,
> vit dans la recipe) · 🟡 `FrameOrchestrator` param optionnel médian retiré (note API) · 🟡 ordre de chargement
> d'assets planet changé (un `.save` pré-refactor peut ne plus résoudre).
>
> ### ✅ **Contenu-1 CLOS (S31)** — identité d'assets stable : `AssetKey` + snapshot v3
> Spec : **[plans/2026-09-07-content-asset-identity-design.md](plans/2026-09-07-content-asset-identity-design.md)**
> (APPROVED **4,60/5**, 2 tours). Détail par vague : **[.absolute-work/board.md](../.absolute-work/board.md)**.
>
> **Le domaine Contenu est DÉCOMPOSÉ en 3 sous-jalons** (comme MP-0, décision humaine S31 ; feu vert entre chaque) :
> **1. identité d'assets stable** (ce jalon) · 2. cook offline + manifest + graphe de dépendances · 3. prefabs & scènes
> déclaratifs (`.agscene`/`.agprefab`). Ordre forcé : les scènes (3) référencent des assets cookés (2) par clé, keyés
> avec le scheme `AssetKey` (1).
>
> **Livré** : **`AssetKey`** (`readonly record struct`, `Core`, path-based, style Godot `res://` — `AssetKey.None =>
> default`, `Value` null ssi None, la **casse compte**, normalise `\`→`/` / trim / collapse `//`, rejette `.`/`..`/
> leading-`/`). **`MeshRefKey(AssetKey Key, int LocalMesh, int LocalMat)`** (`Core`). **`ModelKeyIndex`** (`internal`,
> `Rendering`, **GPU-free** — map bidirectionnelle `AssetKey ⇄ MeshHandle[]/MaterialHandle[]`, extrait de
> `ResourceRegistry` pour être testable sans `GraphicsDevice`). **Snapshot v3** : header 40 o inchangé + **key table**
> triée ordinal (`string.CompareOrdinal`, byte-exact cross-machine, validée strictement croissante au load) ; `MeshRef`
> passe de 2 handles bruts blittables (16 o) à `keyIdx | localMesh | localMat` (12 o), re-résolu au load via un délégué
> **`MeshRefResolver`** fourni par l'hôte ; `WriteComponent`/`ReadAndAddComponent` case 5 supprimé (dispatch accidentel
> = throw) ; **v1 ET v2 refusés** avec messages dédiés. **`Agapanthe.World` ne référence toujours PAS `Rendering`** :
> les délégués `MeshRefIdentifier`/`MeshRefResolver` ne nomment que des types `Core` ; l'hôte branche
> `registry.IdentifyMeshRef`/`.ResolveMeshRef` (patron `SnapshotAllocatorPolicy`/`SnapshotLoadResult`). Sans résolveur
> → tout `MeshRef` en `MeshHandle.Invalid` (`HeadlessSim` inchangé) ; avec résolveur, il **DOIT lever** sur une clé
> non chargée. **Solde la dette VS-1 « ordre de chargement d'assets différent casse en silence »** pour mesh + material.
>
> **Le mécanisme a trouvé son propre bug (W3)** : `PlanetStage.Build` restaurait le snapshot **avant** que la recipe
> n'enregistre son beacon/probe → `GraphicsException` bruyante nommant la clé — exactement le bug fermé, rendu loud
> dans son propre run de validation. Corrigé en scindant `PlanetStage.Build` + `RestoreIfRequested` (appelé par chaque
> recipe **après** l'enregistrement de ses assets propres).
>
> **Métriques** : **659 tests** (+ ~17) · 0 warning · captures HDR `12638eddd7f3f67ab161b298ffbcd15e` / UI
> `034213575932dabcff41c2e0c72addfa` **inchangées** · `HeadlessSim` re-épinglé **JIT == NativeAOT** : défaut
> `80ced166fdf3076119a62970f63d683b` (1842 o, était `7e8dc68f…` 1868 — +6 key table, −8×4 MeshRef) · `--drive`
> `cf01492e8a9688b666e01d2ef9d63869` (210 o) · `AotComponentProbe` PASS JIT+AOT (exerce désormais le chemin key table) ·
> round-trip `planet-challenge`/`planet-drop` rend, 0 leak / 0 validation.
> Double audit : `csharp-lowlevel` **4,3/5 PASS-with-concerns** · `engine-architect` **4,3/5 PASS-with-concerns**,
> **aucun bloquant**. Findings contenus appliqués (couverture AOT du key table via `AotSerializationSmoke` ; table
> strictement croissante + UTF-8 strict au load ; `ModelKeyIndex.Identify(Invalid)` → `None` ; `ModelKeyIndex.Add`
> atomique + throw nommé au lieu d'un overwrite silencieux ; +9 tests). **Verdict visuel humain : PASS** (2026-09-09, S33 — partagé S30+S31+S32).
>
> **Dette laissée** (board §Deferred + backlog §4quater) : 🟠 **identité d'asset côté simulation** — `MeshRef` est
> render-local, un `Save` headless écrit `None` → un serveur autoritaire ne peut pas émettre d'état visuellement
> reconstructible ; un composant sim-side `AssetRef` dont `MeshRef` est la projection client = **la décision structurante
> de Contenu-3** · 🟠 clés Sandbox dérivées de `Path.GetFileName` → clés relatives à une racine de contenu (Contenu-2) ·
> 🟠 `RestoreIfRequested` = étape obligatoire non gardée (lié à la scission `SceneContext` S30).
>
> ### ✅ **Contenu-2 CLOS (S32)** — cook offline + content manifest
> Spec : **[plans/2026-09-08-content-asset-cook-design.md](plans/2026-09-08-content-asset-cook-design.md)**
> (APPROVED **4,24/5**, relecteur séparé 2 tours). Détail par vague : **[.absolute-work/board.md](../.absolute-work/board.md)**.
>
> **Livré** : format **`.agmodel`** (blob cuit autonome — meshes SoA + materials + images RGBA8 décodées,
> `DeflateStream(Optimal)`, déterministe même-machine, reader `public` / writer `internal`, patron `.agfont`) ·
> **`content.agmanifest`** (index binaire `AssetKey → {blob relatif, kind, hash[32]}`, trié ordinal, garde
> strictement croissant) · **`AssetCatalog`** runtime (`Open(root)` / `LoadModel(key) → ModelAsset`, GPU-free,
> sans cache, possédé par `AppHost`, exposé via `SceneContext.Catalog`, tolérant à un manifest absent) ·
> **`tools/AssetCooker`** (Exe, patron `FontCooker`) + `CookRunner` (glob `content/**/*.glb`, key =
> `AssetKey.FromContentPath`, skip incrémental content-hash via un sidecar `.cookstate`, purge des blobs
> orphelins) · target MSBuild **`CookAssets`** dans `Sandbox.csproj` (jumeau `CookFonts`). **Le runtime ne
> parse plus glTF** : `GltfLoader` + `Gltf/*` + `AccessorReader` + `TangentGenerator` + `ImageLoader` migrés
> par `git mv` dans **`src/Agapanthe.Assets.Pipeline`** (cook-side, PAS `IsAotCompatible`, tire `StbImageSharp`) ;
> `Agapanthe.Assets` runtime garde les DTOs + readers + `AssetCatalog` + `HdrImageLoader` (+ `StbImageSharp`
> pour l'HDR — **gain AOT partiel assumé**). L'arg CLI du Sandbox devient une **clé** ; plus de chargement glTF
> par chemin arbitraire.
>
> **`AssetsPipelineIsolationTests`** (patron MP-0a) : scan de **tous** les `.csproj` du dépôt + check réflexif
> sur l'assembly `Agapanthe.Assets` chargé → `Assets.Pipeline` **et** `tools/AssetCooker` référencés uniquement
> par le cooker + les tests. `AssetKey.FromContentPath` (Core) solde la dette audit Contenu-1.
>
> **Métriques** : **689 tests** (+ ~30) · 0 warning · capture **`AGAPANTHE_SCENE=model` re-épinglée
> `9030f6a64e9587b05d5abb99b487b1b9`** (2 764 816 B), JIT == AOT — **PAS la ref S30 `df55d444…`** : un
> `git stash` du source W2 (arbre pré-W2, toujours `GltfLoader.Load`) produit **la même** `9030f6a6…`, donc
> le contenu cuit rend à l'octet près comme le chemin qu'il remplace et la ref S30 avait déjà dérivé.
> `planet-drop` HDR `12638edd…` / UI `03421357…` **inchangées** · `HeadlessSim` `80ced166…` / `cf01492e…`
> **inchangés** · `AotComponentProbe` PASS · **Sandbox + HeadlessSim JIT == NativeAOT**.
> Double audit : `csharp-lowlevel` **4,1/5** · `engine-architect` **4,2/5**, PASS-with-concerns, **aucun
> bloquant** — findings appliqués (bornes de count avant alloc + plafond 1 GiB dans `AgModelFormat.Read` ;
> `CookRunner` globe `.glb` seul — un `.gltf` avec siblings non trackés ne peut pas shipper un blob périmé en
> silence ; `blobPath` validé comme la clé ; indices d'images + tangentes finies validés ; catalog tolérant ;
> garde d'isolation étendue à `tools/AssetCooker`). **Verdict visuel humain : PASS** (2026-09-09, S33 — `model` cuit
> byte-identique `9030f6a6…`, `MetalRoughSpheres` résout via le manifest, `planet-drop` tourne sur `AssetCatalog.Empty` ;
> partagé S30+S31+S32).
>
> **Dette laissée** (board §Deferred + backlog §4quater) : 🟠 **Contenu-2b** — `.gltf` + graphe de deps
> (siblings), HDR/textures/fonts standalone dans le catalog (`AssetKind.Environment` réservé), décodeur RGBE
> maison pour sortir `StbImageSharp` du runtime · 🟠 blob = 21 Mo pour DamagedHelmet (RGBA8) → **séparation
> géométrie/images groupée avec BCn** (jalon rendu), réversible via `AgModelFormat.Version` · 🟠 ~170 l de
> targets de cook dans `Sandbox.csproj` → `build/Agapanthe.Cook.targets` à l'app n°2 · 🟠 `content/` sous
> `samples/Sandbox/` (bloqué par une collision de casse FS avec `Content/`) · 🟡 `HostOptions.VerifyContentHashes`.
>
> ### ✅ **Contenu-3a CLOS (S33)** — `AssetRef` sim-side + snapshot v4 + scission `SceneContext`
> Spec : **[plans/2026-09-09-content-3a-sim-asset-identity-design.md](plans/2026-09-09-content-3a-sim-asset-identity-design.md)**
> (approuvée **4,55/5**, revue scorée 3,2 → 4,55). Contenu-3 décomposé en **3 sous-jalons** (interview S33) :
> **3a** fondation (aucun format) · **3b** `.agscene`/`.agprefab` TOML→blob cuit + `SceneLoader` · **3c** 3
> registres de fabriques + migration des 5 recipes. Feu vert humain entre chaque.
>
> **Livré** : `AssetRef` (composant **#13**, 1ᵉʳ composant **managé** du projet, porte un `MeshRefKey` ;
> W0-spike prouve Arch+NativeAOT OK) · snapshot **v4** (sérialise `AssetRef`, `MeshRef` devient un cache render
> dérivé au load, `MeshRefIdentifier` **supprimé** → `Save` headless émet de vraies `AssetKey` sans callback,
> upgrade v3→v4 en place via une fixture `world-v3.save`) · `SceneContext` → `SimSceneContext` (headless-safe,
> gardé par un test réflexif) + `PresentationSceneContext` (nullable) · garde-fou restore
> `RequestRestore(path)`/`ApplyPendingRestore` (les 3 `RestoreIfRequested` épars disparaissent).
>
> **Gates** : **696 tests** · 0 warning · captures `model` `9030f6a6…` / `planet-drop` HDR `bc8440ab…`
> **inchangées** (`git stash` A/B prouve le no-op rendu ; `12638edd`/`03421357` de `CLAUDE.md` = dérive
> cross-env, re-baseline S33) · `HeadlessSim` re-épinglé **JIT == NativeAOT** avec de vraies clés :
> `6a13dd54c1db32d35a15332bff0395e7` (1857 o) / `--drive` `f6053226f8c13b55589b29be103a8e66` (231 o) ·
> `AotComponentProbe` count 13 PASS · Sandbox + HeadlessSim JIT == AOT · 0 leak · 0 validation.
>
> **Double audit** : `csharp-lowlevel` **3,9/5** + `engine-architect` **4,0/5** PASS-with-concerns. **1 🔴
> trouvé par les deux** : `LandingChallengeSystem` seedait `_shotsIssued` dans son ctor ; D7 ayant déplacé le
> restore *après* `Build`, une reprise seedait sur un monde vide → **seed paresseux au 1ᵉʳ tick** + test
> d'ordre. 10 des 12 findings 🟠/🟡 appliqués.
>
> **Dette laissée** (board §Deferred) : coût GC de mark du `AssetRef[]` managé non mesuré (fallback D1-alt) ·
> règle « rien dans `Build` ne lit le contenu du monde » à inscrire dans `ISceneRecipe` · 5+ copies
> champ-à-champ de spec à supprimer avec le `SceneLoader` (3b) · `Agapanthe.App` → `App` + `App.Client` avant
> `RunDedicatedServer` · identité d'asset non-modèle toujours hors snapshot. **Verdict visuel humain : PASS** (2026-09-09).
>
> ### ✅ **Contenu-3b CLOS (S34)** — `.agscene`/`.agprefab` TOML→blob cuit + `SceneLoader` — **Contenu CLOS (3/3)**
> Spec : **[plans/2026-09-09-content-3b-declarative-scenes-design.md](plans/2026-09-09-content-3b-declarative-scenes-design.md)**
> (approuvée **4,4/5**, revue scorée 3,3 → 4,4).
>
> **Livré** : format binaire déterministe **`.agscene`** (`AGSC`, patron `.agmodel`/`.agfont` — reader
> `public`/writer `internal`, `DeflateStream`, `Reader` ref struct bornée) produit hors-ligne depuis un
> authoring **TOML** (Tomlyn, cook-side uniquement — `AssetsPipelineIsolationTests` garde la confiner) ·
> nouveau projet **`Agapanthe.Scene`** (`{Core, World, Assets}`, GPU-free, allowlisté) avec
> `SceneMaterializer.Materialize` (le code de peuplement **unique** client+serveur — `MeshHandle.Invalid` +
> `AssetRef`, `PhysicsSettings?` en sortie, jamais un `PhysicsSystem` — `Engine` reste hors de la closure) et
> `SceneLoader.LoadHeadless` · **`GameWorld.ResolveMeshRefs(MeshRefResolver)`** (nouveau, réutilise le
> mécanisme Contenu-3a côté client) · **`HeadlessSim --scene <clé>`** charge exactement le fichier cuit du
> Sandbox · la famille `model` (single/grid/cluster) **devient data** (`content/scenes/{model,grid,drop,
> metalrough}.toml`, `content/prefabs/helmet.toml` en include cook-time inliné) — `SceneCompiler.EmitGrid`/
> `EmitCluster` déroulent les directives au cook (le jitter `Hash(i)` de l'ancien `SpawnDropScene` est copié
> puis le code Sandbox original **supprimé**, 0 appelant) · **`.agmodel` bump v2** : bounds par mesh
> précalculés au cook (`MeshBounds.Compute`), `SceneBuilder` garde un fallback pour le procédural ·
> `ModelSceneRecipe` supprimée → `SceneRecipe(string sceneName)` générique.
>
> **Gates** : **740 tests** · 0 warning · captures **re-épinglées + verdict visuel humain PASS** sur les 4
> scènes `model`/`grid`/`drop`/`metalrough` (`9a010fc3…`/`c314e6a1…`/`2fbf87fa…`/`c4cf4605…`, nouvelle
> composition studio-HDR + lumière déclarative) ; `planet-drop` inchangée (`bc8440ab…`) · `HeadlessSim
> --scene headless-default` **JIT == NativeAOT** `8a5c0463…`, défaut `6a13dd54…` inchangé · Sandbox +
> HeadlessSim + `AotComponentProbe` AOT PASS · 0 leak · 0 validation.
>
> **Double audit** : `csharp-lowlevel` **4,1/5** + `engine-architect` **4,1/5** PASS-with-concerns, **aucun
> 🔴**. 12 findings 🟠 appliqués cette session : durcissement `ReadCount` des deux formats cuits
> (`minRecordBytes`, contre un count forgé dimensionnant un tableau de références au-delà de ce que le blob
> compressé pourrait produire) · `ResolveMeshRefs` pose `_structuralDirty` **avant** la boucle (une
> résolution partielle qui lève ne doit pas laisser le buffer de slots désynchronisé) · validation `LocalMat`
> côté headless (symétrique à `LocalMesh`) · convention mesh-sans-matériau alignée sur `ResourceRegistry`
> (`Materials.Count`, pas `0`) · rejet NaN sur les bounds v2 et direction-lumière nulle au cook · TOML :
> rejet des clés inconnues, chemin `Doubles()` dédié (l'ancien quantifiait silencieusement `world_origin`
> via `float[]`) · offset de membre de prefab tourné par la transform d'instance · incrémentalité du cook
> incluant le hash des prefabs · clés de scène via `AssetKey.FromContentPath` (régression exacte du fix
> Contenu-1 « répertoire jeté », pour les scènes cette fois). `ModelContent.SpawnGrid`/`SpawnDropScene`
> (0 appelant) supprimées.
>
> **Dette laissée** (board §Deferred) : câblage `MaterializeResult.Physics/RestorePath` dupliqué par hôte
> (`Engine` ne peut nommer `MaterializeResult`, `App` porte Vulkan — forme concrète de la décision
> `App`/`App.Client` à venir) · `ResourceRegistry.Unload` a désormais **0 appelant** (besoin d'un chemin
> d'exercice GPU réel, idéalement au reload de scène 3c) · `SceneLoader.LoadHeadless` appelé aussi par le
> client malgré son nom · `AGAPANTHE_VIEW` lu directement dans `SceneCameraApplier` (brèche mineure S30) ·
> `ModelContent.ResolveModelKey` + l'arg CLI modèle du Sandbox restent (déviation D10 documentée — `drive`
> les utilise encore, migre en 3c). **Verdict visuel humain : PASS** (2026-09-09).
>
> ### ✅ **Contenu-3c-1 CLOS (S35)** — registres de fabriques + migration `planet`/`planet-drop`
> Spec : **[plans/2026-09-11-content-3c-scene-systems-design.md](plans/2026-09-11-content-3c-scene-systems-design.md)**
> (approuvée **4,30/5**, revue scorée 3,70 → 4,30). 1ᵉʳ des **3 sous-phases gatées** de Contenu-3c (3c-1 infra +
> `planet`/`planet-drop` · 3c-2 `LandingChallenge` + `planet-challenge` · 3c-3 `ground_quad` + `drive` + cleanup).
>
> **Livré** : **`.agscene` v2** (`ScenePhysics` gagne un attracteur newtonien optionnel `Mu`/`AttractorCenter`/
> `SurfaceRadius`, `SceneCamera.Fixed` gagne `MoveSpeed`/`ShadowDistance`, `SceneEnvironmentMode` +
> `ProceduralSky`/`Black`, nouveau `[[system]]` — `SceneSystemKind.ProbeDrop` seul déclaré, pas de
> pré-déclaration spéculative de `LandingChallenge`, confirmé par les deux audits) · **2 registres, pas 3**
> (décision verrouillée en interview) — `ISceneSystemFactory` côté client (`IGame.SceneSystems`, sur
> `PresentationSceneContext.SceneSystemFactories` après déplacement post-audit) et un registre de
> **générateurs procéduraux cook-time-only** (`Agapanthe.Assets.Pipeline/Procedural/`, invisible à
> `IGame`/au runtime) · **`UvSphereGenerator`** réimplémente `Primitives.UvSphere` en SoA cook-side (vérifié
> byte-identique par test dédié) → sphères planète/soleil/probe deviennent de vrais blobs `.agmodel` cuits ·
> **3 caméras planète bespoke collapsent en trig cuite** dans `SceneCamera.Fixed` (0 nouvelle logique caméra
> runtime, juste 2 champs copiés) · `SceneMaterializer.BuildRuntimeTemplate` (nouveau, public, GPU-free) —
> un système client résout les vrais handles via `ResourceRegistry.ResolveMeshRef` sur ce patron ·
> `HeadlessSim` refuse (exit 1) toute scène déclarant des systèmes (concept client-only, jamais un run
> partiel silencieux) · `PlanetSceneRecipe`/`PlanetDropSceneRecipe` supprimées, `planet`/`planet-drop`
> deviennent `SceneRecipe("planet"/"planet-drop")` génériques + `content/procedural/*.toml` +
> `content/scenes/{planet,planet-drop}.toml` (nombres cuits dérivés d'un script jetable rejouant les
> anciennes formules à leurs défauts env-var).
>
> **Gates** : **780 tests** · 0 warning · captures `planet`/`planet-drop` **pinned + verdict visuel humain
> PASS** (`99e2f4a3…`/`81ddf074…`) · `model`/`grid`/`drop`/`metalrough`/`headless-default`/`planet-challenge`/
> `drive` tous re-vérifiés inchangés · `--scene planet-drop` → exit 1 confirmé (D9) · Sandbox + HeadlessSim +
> `AotComponentProbe` **JIT == NativeAOT** sur toutes les captures/snapshots, re-confirmé après les
> corrections d'audit · 0 leak · 0 validation.
>
> **Double audit** : `csharp-lowlevel` + `engine-architect` PASS-with-concerns, **1 🔴 trouvé indépendamment
> par les deux et corrigé** — `SceneMaterializer.Materialize` parsait/compilait/round-trippait l'attracteur
> (`Mu`/`AttractorCenter`/`SurfaceRadius`) mais n'appelait jamais `PhysicsSettings.WithAttractor(...)` :
> `planet-drop` tournait avec **zéro gravité**, chaque probe restait immobile — bug invisible dans la
> capture pinned (aucune probe n'y apparaît, elle ne spawn que sur `B`), découvert uniquement par l'audit,
> pas par le protocole visuel. Corrigé + 2 tests de régression ajoutés (`SceneMaterializerTests` —
> csharp-lowlevel avait explicitement signalé le trou de couverture `ScenePhysics → PhysicsSettings`).
> 4 autres findings 🟠 appliqués : bounds check `localMat` manquant dans `BuildRuntimeTemplate` · garde de
> suffixe `.toml` dans `CookRunner` (un near-miss de wildcard Win32 pouvait mal-keyer un fichier) ·
> `SceneSystemFactories` déplacé de `SimSceneContext` vers `PresentationSceneContext` (nommer
> `PresentationSceneContext` dans `Create` aurait transitivement réintroduit un type GPU/fenêtre dans le
> contexte headless-safe) · garde single-slot `SimulationHost.ApplyCommand` centralisée dans `SceneRecipe`
> au lieu d'être dupliquée par fabrique · warning `AGAPANTHE_LOAD` ajouté pour toute scène cuite sans bloc
> `[restore]` (vrai depuis Contenu-3b, pas une régression 3c-1 — le §3.5 du spec l'affirmait à tort
> "inaffecté").
>
> **Dette laissée** (board §Deferred, → 3c-2/3c-3) : validation `ProbeRadius`/`Every` · sémantique sentinelle
> `MoveSpeed`/`ShadowDistance` (0 = dérivation dynamique, pas documenté comme contrat formel) · fabrique
> dupliquée sur un même `Kind` prend silencieusement la première trouvée · `ApplyEnvironment` sans bras
> `default` explicite · script `bake_planet.cs` non committé (traçabilité des nombres cuits). **Verdict
> visuel humain : PASS** (2026-09-12).
>
> ### ✅ **Contenu-3c-2 CLOS (S36)** — `LandingChallenge` + migration `planet-challenge` + correctif générique de reprise
> Spec : `docs/plans/2026-09-11-content-3c-scene-systems-design.md` §9 (ajouté cette session — 3c-1 avait
> délibérément différé ces champs, YAGNI ; §9 les concrétise pour le vrai consommateur).
>
> **Livré** : `.agscene` v2→v3 — `SceneSystemKind.LandingChallenge` + `SceneSystem` gagne
> `ZoneCenter`/`ZoneRadius`/`SurfaceBand`/`DropHeight`/`TargetCount`/`ShotBudget`/`QuicksavePath`
> (`AttractorCenter`/`SurfaceRadius` **délibérément pas dupliqués** — déjà sur `ScenePhysics` depuis 3c-1, la
> fabrique les lit sur `MaterializeResult.Physics`) · `LandingChallengeSystemFactory` (client) construit la
> classe `LandingChallengeSystem` **inchangée** (le seed paresseux au 1ᵉʳ tick, fix 🔴 Contenu-3a, survit
> intact) · `PlanetChallengeSceneRecipe`/`PlanetStage`/`PlanetContent.cs` (entièrement mort) /
> `SandboxCameras.FramePlanetChallengeCamera` supprimés · `planet-challenge` devient `SceneRecipe(...)` +
> `content/procedural/beacon.toml` + `content/scenes/planet-challenge.toml`.
>
> **Gates** : **797 tests**, capture `planet-challenge` pinned + verdict visuel PASS (`ea6ba910…`), les 6
> autres scènes re-vérifiées inchangées après 2 re-cooks complets forcés, `--scene planet-challenge` → exit 1
> confirmé (D9), Sandbox + HeadlessSim JIT == NativeAOT, 0 leak / 0 validation.
>
> **Double audit** : PASS-with-concerns ×2, **1 🔴 trouvé indépendamment par les deux** — la reprise
> `AGAPANTHE_LOAD`/F5-quicksave de `planet-challenge` était cassée par la migration (et le même mécanisme
> cassait déjà `planet-drop` depuis 3c-1, jamais détecté faute de protocole de reprise humain sur cette
> scène-là). `SceneRecipe`/`SceneMaterializer` peuplaient toujours la scène sans condition, contrairement à
> l'ancien `PlanetStage.Build` (`spawnEntities: !loadMode`) ; `GameWorld.Load` refuse un monde peuplé. **Décision
> présentée à l'humain** (le correctif touche toute scène `SceneRecipe`, pas que celle-ci) — **option (a)
> choisie : corriger maintenant**. `SceneMaterializer.Materialize` gagne `bool spawnEntities = true` ;
> `SceneRecipe.Build` calcule `spawnEntities = sim.Options.LoadPath is not { Length: > 0 }` et restaure
> depuis ce chemin runtime en priorité sur tout `[restore]` cuit. **Vérifié bout-en-bout en live** (save 3
> entités → relance `AGAPANTHE_LOAD` → 0 spawnées puis 3 restaurées, 0 leak), reproduit sur `planet-drop` et
> sur le binaire NativeAOT ; les 7 captures pinnées restent inchangées (le correctif ne s'active que si
> `AGAPANTHE_LOAD` est posé). 6 findings 🟠 supplémentaires appliqués (garde d'attracteur de fabrique
> vérifiait `Physics is null` au lieu de `Mu > 0`, `AttractorSurfaceRadius > 0` pas validé au cook, aucune
> validation numérique sur les nouveaux champs, `QuicksavePath` non validé, `CookerVersion` pas bumpé,
> commentaire périmé). **Dette laissée** : champ commun `ProbeModel`/`ProbeRadius` `required` gênera un futur
> système non-spawneur · `world_origin` pas appliqué à `AttractorCenter`/`Centre`/`ZoneCenter` (inerte
> aujourd'hui). **Verdict visuel humain : PASS** (2026-09-12).
>
> ### ✅ **Contenu-3c-3 CLOS (S37)** — `ground_quad` + migration `drive` + nettoyage final — **Contenu-3c CLOS (3/3), domaine Contenu entièrement clos**
> Spec : `docs/plans/2026-09-11-content-3c-scene-systems-design.md` §11 (ajoutée cette session).
>
> **Livré** : après cette phase, **toutes les scènes du Sandbox tournent sur `SceneRecipe` cuit — zéro recipe
> hand-codée restante**. `.agscene` v3→v4 : `SceneSystemKind.DriveControl` — 1ᵉʳ système qui ne spawne rien,
> forçant `SceneSystem.ProbeModel`/`ProbeLocalMesh`/`ProbeLocalMat`/`ProbeRadius` (`required` depuis 3c-1) à
> devenir optionnels (ferme la dette 🟡 F2 de 3c-2). Nouveau `MaterializeResult.SpawnedEntities` (parallèle à
> `Entities`, capture l'`EntityRef` que `SpawnBody` jetait avant). **Nouvelle capacité d'authoring découverte en
> cours de route** : `[[entity]]` n'avait aucun support de corps physique — ajout de `body`/`velocity`
> réutilisant les champs déjà présents pour `[[cluster]]`. Nouveau `GroundQuadGenerator` (cook-time, moved
> verbatim). Nouveau `DriveControlSystemFactory` câble l'`InputMap`/`SampleInput`/`ApplyCommand` de l'ancien
> `DriveSceneRecipe`. Garde single-slot de `SceneRecipe` élargie à `InputMap`/`SampleInput` (ferme la dette 🟡
> F4 de 3c-2). Supprimés (0-appelant vérifié) : `DriveSceneRecipe.cs`, `ModelContent.cs`, `SandboxCameras.cs`
> (tous deux entièrement morts), `BenchSpinSystem.cs`/`ChurnSystem.cs`, `RecipeInput.WireFreeFly`. `drive`
> devient `SceneRecipe("drive")` + `content/procedural/ground.toml` + `content/scenes/drive.toml`
> (`models/DamagedHelmet.glb` fixé — D6, perte déjà acceptée).
>
> **Gates** : **831 tests**, capture `drive` pinned + verdict visuel PASS (`9030f6a6…`), les 7 autres scènes
> re-vérifiées inchangées après 2 re-cooks complets forcés, `--scene drive` → exit 1 confirmé (D9, nomme
> `DriveControl`), Sandbox + HeadlessSim JIT == NativeAOT, 0 leak / 0 validation.
>
> **Double audit** : PASS-with-concerns ×2 — vague de clôture, donc revue aussi de la dette 🟡 accumulée sur
> tout Contenu-3c. **2 🟠 trouvés indépendamment par les deux et corrigés** : `drive_control` + une reprise en
> attente plantait au démarrage (contredisant le spec, qui promettait un no-op) — `DriveControl` ne peut
> fondamentalement pas résoudre « le corps à l'index N du cook » après une reprise (contrairement au seed
> paresseux de `LandingChallengeSystem`, qui ne dérive qu'un compte) → rejeté explicitement au cook-time et au
> runtime plutôt que de tenter un no-op cassé · `[[entity]] body = true` sur un modèle multi-mesh ou un prefab
> multi-membres spawnait silencieusement N corps co-localisés qui se pénètrent mutuellement → rejeté au
> cook-time sauf exactement une entité produite. 5 findings 🟡 supplémentaires appliqués (`body`/`velocity`
> fuyaient sur `[[grid]]`/`[[cluster]]`, modèle-probe `None` non-`DriveControl` différait vers un échec tardif,
> état mutable sur fabrique singleton, `FirstOrDefault` silencieux sur fabrique dupliquée) et — **seule dette
> 🟡 accumulée sur les 3 phases jugée digne de fermeture avant clôture du domaine, recommandation des deux
> audits** — `world_origin` pas appliqué à `AttractorCenter`/`Centre`/`ZoneCenter`, désormais rejeté au
> cook-time. `CookerVersion` bumpé à `contenu3c-3`. **Dette laissée** (hors scope, backlog) : environnement
> procédural réel (Contenu-2b) · nom d'entité→`GlobalId` (D8) · validation `ProbeRadius`/`Every` · scripts de
> dérivation non committés. **Verdict visuel humain : PASS** (2026-09-12).
>
> ### ✅ **Slice-2 CLOS (S38)** — 2ᵉ slice dissemblable : `samples/TopDown`, caméra orthographique réelle, `Agapanthe.Platform.App` partagé
> Spec `docs/plans/2026-09-12-slice2-topdown-design.md`, approuvée **4,3/5** (revue scorée séparée, 8/8
> affirmations factuelles vérifiées contre le code réel ; 1 vrai écart de Consistency trouvé et corrigé dans le
> spec avant décomposition). Le test de généralité le moins cher du backlog §4quater : une 2ᵉ app délibérément
> dissemblable (`samples/TopDown`, caméra orthographique fixe overhead sur un diorama plat) pour révéler ce qui
> était silencieusement câblé en dur pour la forme planétaire/perspective de VS-1/2/3 et tout Contenu-3c.
>
> **Livré** : `Camera` gagne une **vraie** projection orthographique (`CameraProjection.Perspective`/
> `Orthographic`, `MathHelpers.OrthographicVulkanReversed` — dérivation reversed-Z distincte de la perspective
> car `w=1` constant en orthographique contre `w=z_view` en perspective, prouvée par TDD : la 1ʳᵉ tentative a
> copié la formule perspective, le test l'a attrapée immédiatement). `.agscene` v4→v5 : `SceneCamera.Fixed`
> gagne `Projection`/`OrthoWidth`/`OrthoHeight`. `EngineWindowAdapter` + `DriveControlSystemFactory` extraits de
> Sandbox-`internal` vers un nouveau projet partagé **`src/Agapanthe.Platform.App`** — Sandbox et TopDown
> référencent désormais les mêmes classes, zéro duplication (vérifié par un déplacement pur : les 8 captures
> Sandbox restent byte-identiques). Ombres CSM explicitement hors scope (`ShadowFit` suppose un cône
> perspective, faux pour une boîte orthographique) — `topdown.toml` a `casts_shadow = false` sur ses 4 entités.
> Caméra fixe overhead, réutilise `DriveControlSystemFactory` tel quel (pas de nouveau système caméra-suit-body).
>
> **2 vrais bugs de généralité trouvés en testant live, pas en relisant le code** : `SceneRecipe.cs` (code
> partagé `Agapanthe.App`, utilisé par toute app) avait `"Sandbox: "` codé en dur dans 2 lignes de log — corrigé
> en `"AppHost: "`. Le puck de `topdown.toml` n'avait pas `casts_shadow = false` explicite (défaut `true`,
> violait D3 silencieusement) — corrigé.
>
> **Gates** : **854 tests**, 0 warning, capture `topdown` pinned + **verdict visuel humain PASS** (`31ea748d…` —
> 3 sphères de même rayon rendent en cercles de même taille apparente quelle que soit leur profondeur x/z,
> preuve qu'il n'y a aucun raccourcissement en perspective), les 8 captures Sandbox re-vérifiées inchangées,
> `HeadlessSim --scene topdown` → exit 1 confirmé (D9, nomme `DriveControl`), les 3 binaires (Sandbox/TopDown/
> HeadlessSim) **JIT == NativeAOT** sur toute capture/snapshot, 0 leak / 0 validation partout.
>
> **Double audit** : `csharp-lowlevel` **4,3/5** + `engine-architect` **4,2/5**, PASS-with-concerns, **aucun 🔴**
> — les deux ont vérifié indépendamment et symboliquement la dérivation reversed-Z orthographique (correcte) et
> le déplacement `Agapanthe.Platform.App` (byte-identique, diff `HEAD` ne montre que namespace/visibilité). **4
> findings trouvés par les deux et corrigés** : `projection`/`ortho_*` silencieusement ignorés sur une caméra
> `frame-bounds` (même classe de bug que le `body`/`velocity` fuyant sur `[[grid]]`/`[[cluster]]` fermé en
> 3c-3) · `Agapanthe.Platform.App` sans entrée d'allowlist statique (`EngineIsHeadlessTests`) alors que sa
> raison d'être est d'être le seul point de rencontre Platform+App · 2 commentaires périmés nommant le
> `samples/Sandbox/EngineWindowAdapter` supprimé · l'orthographique n'a aucune correction d'aspect ratio
> automatique (la perspective l'a gratuitement via `FovY`+`AspectRatio`) — `OrthoHeight = 0` devient la
> sentinelle « dérive de `OrthoWidth`/`AspectRatio` » (convention `MoveSpeed`/`ShadowDistance`), le hack manuel
> `ortho_height = 22.5` du TOML disparaît. **1 finding csharp-lowlevel** : `OrthoWidth`/`OrthoHeight` validés au
> cook seulement, pas au read `.agscene` ni dans `Camera` — un blob forgé ou un `new Camera` nu pouvait produire
> une matrice NaN/Inf sans message de validation ; gardes symétriques ajoutées. **Dette versée au backlog**
> (assumée, pas un manque) : le garde-fou D3 (ombres) ne repose que sur une convention TOML sans filet de code
> · `DriveControl` est maintenant 2 scènes/2 apps refusées par `HeadlessSim` — signal de conception pour le
> netcode, pas un bug · exposition HDR câblée en dur pour le studio HDRI du Sandbox, aucun champ `.agscene` ·
> `skybox.vert`/`FreeCameraController` faux en orthographique (mineur, non exercé) · coût de build ×3 (chaque
> app cuit tout `content/` dans son propre `obj/`) · garde `Frustum.Normalize` `1e-8` dégrade plus tôt à
> l'échelle planétaire orthographique (requalifie la dette pré-existante, ne l'ajoute pas).
>
> ### ✅ **UI-3 CLOS (S39)** — timestamps GPU + seam `FrameProfiler` — **Texte & UI ENTIÈREMENT CLOS (3/3)**
> Dernier des 3 jalons Texte & UI (UI-1/UI-2 clos S25). Spec `docs/plans/2026-09-13-ui3-gpu-timestamps-design.md`,
> approuvée **4,15/5** après 2 tours (round 1 : 1 écart factuel + 1 vrai trou de conception, tous deux
> corrigés).
>
> **Livré** : ferme la dette explicite d'UI-2 — les timestamps GPU arrivent ~2 frames en retard
> (`FramesInFlight=2`) et auraient désynchronisé `FrameStats`/`FrameSeries` (append-only, 0-alloc, testées)
> retrofit naïvement — **résolu par découplage total** : `Agapanthe.Engine`/`FrameStats` restent intouchés
> (`git diff` vide), la série GPU vit entièrement côté `Agapanthe.Rendering`/`Agapanthe.Engine.Render`. Nouveau
> **`QueryPool`** (`Agapanthe.Graphics`, `VkQueryPool` timestamp, disposal différé — patron `Sampler`)
> instrumente les **4 régions debug-label existantes** (`Shadow`/`Scene`/`Tonemap`/`UI`, aucune nouvelle région)
> via `CommandList.WriteTimestampBegin`/`End` + `ResetQueryPool`. Détection de capacité par la **queue
> graphique réellement utilisée** (`TimestampValidBits`, pas le feature bit global — précaution MoltenVK) ;
> `Renderer.SupportsGpuTimestamps` ANDe `GraphicsDevice.SupportsGpuTimestamps` et `HostOptions.GpuTimestampsEnabled`
> (`AGAPANTHE_GPU_TIMESTAMPS=0`). Lecture **une fois par frame, non-bloquante, par région indépendamment**
> dans `GpuPassTimingsMs` (4 champs `float?`) ; `DebugOverlaySystem` affiche la ligne par-passe + un graphe,
> absence propre quand non supporté.
>
> **Gates** : **861 tests**, 0 warning, capture masquée **byte-identique avant/après** (`git stash` A/B,
> `b5382ac6…`) — preuve que l'instrumentation ne touche aucun pixel — les 9 captures HDR inchangées, verdict
> visuel humain PASS (overlay GPU actif + chemin dégradé), **JIT == NativeAOT** sur les 3 binaires, 0 leak /
> 0 validation.
>
> **Double audit** `csharp-lowlevel` + `graphics-3d` (déviation assumée du duo standard, décidée au pré-spec
> S25) — **3 🔴 trouvés et corrigés** : inversion de slot (lisait le slot en vol au lieu du slot
> fence-garanti-terminé — race + appariement croisé possible) · `TOP_OF_PIPE` en begin (≡ `NONE` en premier
> scope sync2 — régions cumulatives jusqu'à ~4× le vrai coût GPU) · lecture de queries jamais reset sur les
> 2 premières frames (`VUID-vkGetQueryPoolResults-None-09401`, violation systématique). 5 findings 🟠
> appliqués (masquage `TimestampValidBits`, gardes `QueryPool.ReadResultsNonBlocking`, suppression de 4
> `FrameSeries` écrites-jamais-lues, total non enregistré à 0 franc, suffixe `ms` manquant). `graphics-3d` a
> documenté 4 risques MoltenVK réels pour P3-M0 (versés au backlog, inactionnables sans matériel Apple).
> **Verdict visuel humain : PASS** (2026-09-13).
>
> ### ✅ **Queries physiques CLOS (S40)** — raycast + layer mask
> Premier item §4quater après la clôture entière du domaine Texte & UI. Spec
> `docs/plans/2026-09-14-physics-queries-raycast-design.md`, approuvée **4,2/5** après 3 tours (round 1 : un
> précédent fabriqué pour `QueryLayer` — un split `WithNone<T>()` inexistant dans ce codebase, corrigé pour
> citer le vrai précédent `NoShadowCast` ; round 2 : la correction elle-même avait fabriqué un
> `ImportedEntitySpec.CastsShadow` inexistant, corrigé).
>
> **Livré** : `GameWorld.TryRaycast`/`RaycastAll` (nouveau `GameWorld.Queries.cs`) contre **tout drawable** via
> le composant `Bounds` existant (pas seulement `RigidBody`) · `QueryLayer { uint Mask }` optionnel (composant
> #14, absent ⇒ `AllLayers`, single-query + `Has<QueryLayer>()` inline) · nouvelle grille de broadphase
> (séparée de la grille physique), reconstruite par appel, DDA sur voisinage 3×3×3 · `Ray`/`RaySphereIntersect`
> (`Agapanthe.Core`, résolus en `double`) · `ImportedEntitySpec.Layer` (atteint `SpawnImported` **et**
> `SpawnDeferred`, contrairement à l'ancien `castsShadow`) · authoring TOML `layer` (`.agscene` v5→v6, rejeté
> sur `[[grid]]`/`[[cluster]]`) · `Camera.ScreenPointToRay` (perspective **et** orthographique, un premier jet
> perspective-only trouvé et corrigé par le double audit) · démo `Key.F` (crosshair écran-centre — `IWindow`
> n'expose aucun événement de clic, D6 réinterprété ; câblé une fois dans `AppHost`, Sandbox + TopDown).
>
> **Imprévu en cours de route** : ajouter le 14ᵉ composant a fait échouer 43 tests via un `Debug.Assert`
> inconditionnel figé sur le compte v4 dans `WorldSerialization.cs` — corrigé par un bump **v4→v5** purement
> additif, 3 hashes `HeadlessSim` re-épinglés. `CookRunner.CookerVersion` non bumpé pour le saut `.agscene`
> v5→v6 (même classe de bug qu'un finding déjà audité en Contenu-3c-2), trouvé pendant la vérification et corrigé.
>
> **Gates** : **904 tests** (+45), 0 warning, 0 régression, les 9 captures pinnées re-vérifiées byte-identiques
> par preuve A/B `git stash` (5 tombent exactement sur les hashes déjà épinglés), `HeadlessSim`/Sandbox
> **JIT == NativeAOT** avant **et** après la passe de correctifs d'audit, 0 leak / 0 validation. **Verdict
> visuel humain PASS** (scène `grid`, 2026-09-13) : `F` sur une entité → hit + distance plausible, `F` vers le
> vide → « no hit ».
>
> **Double audit** `csharp-lowlevel` (**3,6/5**) + `engine-architect` (**4,1/5**), PASS-with-concerns tous les
> deux, forte convergence (3 findings trouvés indépendamment par les deux). **1 🔴 trouvé-et-corrigé** :
> `maxDistance = +Infinity` passait le garde existant et faisait boucler la marche DDA à l'infini — un appel
> parfaitement naturel (« cast aussi loin que possible ») rendait le process infrangible ; corrigé en
> rejetant `IsInfinity`. Findings 🟠 corrigés : coût de marche DDA non borné (désormais bornée à l'AABB des
> cellules occupées + arrêt anticipé) · `RaycastHit.Point` narrowait en `float` (contredisait « résolu en
> double ») · direction dégénérée non validée (NaN/nulle pouvait empoisonner le résultat ou lever une
> exception non documentée) · tie-break non déterministe à distance égale · `RaySphereIntersect` instable au-
> delà de ~1e8 m (réécrit, vérifié à 7,48e10 m) · bug orthographique de `ScreenPointToRay` (ci-dessus) ·
> `EnsureStampCapacity` réallouait à chaque appel dès qu'un monde grossissait · `Layer` perdu par 2 sites de
> copie champ-à-champ restants · `layer = 0` authoré silencieusement inatteignable, rejeté au cook. Détail
> complet dans le spec §8 et `.absolute-work/archive/board-session40-physicsqueries.md`.
>
> ### ✅ **Queries de formes CLOS (S41)** — sphere overlap
> Deuxième item §4quater après les queries physiques (raycast + layer mask, S40). Spec
> `docs/plans/2026-09-14-shape-queries-overlap-design.md`, approuvée **4,4/5** après 2 tours (round 1 : 3,4/5
> NEEDS WORK — un vrai défaut de cohérence interne dans le raisonnement du dédup de cellules, corrigé et
> re-vérifié indépendamment au round 2).
>
> **Livré** : `GameWorld.OverlapSphere` (nouveau, `GameWorld.Queries.cs`) — « qu'y a-t-il dans cette sphère ? »,
> réutilise **intégralement, sans modification**, `GatherCandidates`/`BuildGrid` de S40 (confirmé par `git diff` :
> 0 ligne touchée) · `OverlapHit(EntityRef, double Distance)` (pas de `Point`, D2) · démo `Key.G` (Sandbox+TopDown,
> même câblage que `Key.F`).
>
> **Gates** : **922 tests** (+5), 0 warning, 0 régression, les 9 captures pinnées re-vérifiées byte-identiques
> (toutes tombent exactement sur les hashes déjà connus de S40), JIT == NativeAOT confirmé avant/après audit,
> verdict visuel humain PASS (`G` près d'une entité → compte + distance plausibles, `G` loin de tout → 0).
>
> **Double audit** `csharp-lowlevel` (**3,8/5**) + `engine-architect` (**4,0/5**), PASS-with-concerns —
> convergence très forte (même risque 🔴 et même bug pré-existant trouvés indépendamment par les deux). **1 🔴
> trouvé-et-corrigé** : le balayage de cellules n'était borné par rien lié à la query elle-même (`cellSize` dicté
> par le plus gros objet du monde, pas par le rayon demandé) — un appel naturel pouvait balayer des milliards de
> cellules vides ; corrigé par une estimation en `double` du volume balayé avant tout calcul `long`, avec repli
> sur un scan linéaire direct quand la grille coûterait plus cher que le nombre de candidats. **Un vrai bug
> pré-existant trouvé dans `RaycastAll`** (livré avec S40, déjà audité et clos) — la garde de capacité de buffer
> n'était vérifiée qu'après la fin de la chaîne d'une cellule, pas dans la boucle elle-même, pouvant lever une
> `IndexOutOfRangeException` contredisant son propre contrat documenté ; trouvé par comparaison avec la garde
> correcte d'`OverlapSphere`, corrigé en miroir. Findings 🟠 : dédup `_qVisitedStamp` restauré (aliasing de hash
> possible) · `ValidateCenter` ajouté (un `center` non-fini retournait silencieusement 0) · seuil de chevauchement
> élargi en `double` (même classe de défaut que S40 sur `RaycastHit.Point`) · tri à distance égale rendu
> déterministe (clé secondaire `GlobalId`, même classe de bug que S40 avait fermée une fois pour `TryRaycast`
> mais jamais reportée sur les jumeaux basés sur le tri). **Dette laissée** : `OverlapHit` ne porte ni centre ni
> rayon (probable point de friction au premier vrai consommateur, D2 déjà approuvé) · `Double3.Distance` paie sa
> racine carrée sur chaque candidat rejeté (micro-optimisation) · deux chemins de query de région évoluent
> désormais indépendamment (`OverlapSphere`/`QuerySurfaceContacts`), signalé pas fusionné. **Verdict visuel
> humain : PASS** (2026-09-14).
>
> ### ✅ **Queries de formes — box overlap CLOS (S42)** — AABB
> Troisième item §4quater après les queries physiques (S40) et sphere overlap (S41). Spec
> `docs/plans/2026-09-14-shape-queries-box-overlap-design.md`, approuvée **4,6/5** après 1 tour (aucune
> fabrication trouvée, chaque citation de réutilisation vérifiée contre le code réel).
>
> **Livré** : `GameWorld.OverlapBox` (AABB seule, D1 — pas de rotation, `Double3` n'a ni dot/cross product ni
> quaternion) réutilise intégralement `GatherCandidates`/`BuildGrid` (confirmé `git diff` : 0 ligne touchée) et
> `OverlapHit` (D2, `Distance` = centre-boîte → centre-candidat). Démo `Key.H`.
>
> **Gates** : **937 tests** (+1), 0 warning, 0 régression, 9 captures re-vérifiées byte-identiques, JIT ==
> NativeAOT confirmé avant/après audit, verdict visuel humain PASS.
>
> **Double audit** `csharp-lowlevel` (**3,7/5**) + `engine-architect` (**4,1/5**), PASS-with-concerns — **les
> deux ont prouvé par mutation, indépendamment, le même défaut sérieux** : le chemin grid-walk entier n'était
> exercé par aucun des 14 tests livrés (repli scan systématique, estimation minimale toujours ≥27 cellules) ; le
> test-vitrine « marge de bordure » ne prouvait rien. **Même défaut retrouvé, en silence, dans les tests
> d'`OverlapSphere` de S41** — jamais détecté avant cette comparaison inter-jalons. Corrigé aux deux endroits
> (leurres pour forcer le chemin grid) et **re-vérifié personnellement par mutation**. Nouveau test de
> couverture grid-path ajouté, également vérifié par mutation. Findings 🟠/🟡 : tests « énorme sur monde épars »
> réécrits avec candidats vraiment dispersés · inversion `min > max` couvre désormais les 3 axes ·
> `ValidateBox`/`TryOverlapBox`/`OverlapHit` peaufinés. **Dette versée au backlog** : `TryRaycast`/`RaycastAll`
> n'ont aucun repli de coût analogue (réel, pré-existant, hors scope). **Verdict visuel humain : PASS**
> (2026-09-15).
>
> ### ▶️ Reprise — autre item du backlog §4quater
> Les domaines **Contenu**, **Slice-2**, **Texte & UI**, **queries physiques (raycast + layer mask)** et
> **queries de formes (sphere + box overlap)** sont tous CLOS. Voir `BACKLOG.md` §4quater pour les items
> restants (audio, job system, netcode…).
>
> ### Contexte — **Cap moteur** (réorientation S25)
> **Vertical Slice CLOSE dans son intention** : VS-1 (S22) · VS-2 (S23) · VS-3 (S24) ont prouvé l'intégration
> `input → spawn → physique → règle → save/load`. **VS-4 (HUD) et VS-5 (audio) sont EN PAUSE** (décision humaine S25).
>
> **Nouveau cap — [backlog §4quater](BACKLOG.md)** : faire d'Agapanthe un **vrai engine** (l'artefact = le moteur, pas
> un jeu). Ancrages humains : généraliste **mais** spécialement sims spatiales grande échelle (et un Stardew-like doit
> être faisable) · **multijoueur pensé maintenant, serveur autoritaire** · **massif/persistant visé, petite coop
> possible** → la topologie est un choix de **déploiement**, jamais d'architecture.
>
> **Constat mesuré (S25)** : `Engine` = 10 types publics / 6 fichiers, contre **`Sandbox/Program.cs` = 2 164 lignes**
> qui contiennent bootstrap + contenu + 5 scènes + 4 caméras + input + gameplay. **La couche « engine » est dans le
> Sandbox.** Test qui tranche : *peut-on faire un 2ᵉ jeu différent sans éditer le moteur ?* → non, aujourd'hui.
>
> **MP-0 (prochain jalon, décisions quasi irréversibles)** — détail et justification dans §4quater :
> 1. 🔴 **`GlobalId` = compteur local** (`_nextGlobalId = 1`/monde) → collision inter-process, shards inmergeables.
>    Proposition : **64 bits partitionné** (poids fort = shard, solo = 0).
> 2. 🔴 **Clé de contact physique** `(_pGid[j] << 32) | (uint)_pGid[k]` **écrase l'ID sur 32 bits** → couplé au point 1,
>    collision **silencieuse** entre shards. Fix : deux clés parallèles, ordre déterministe préservé.
> 3. ~~🟠 **Split headless**~~ ✅ **LIVRÉ (MP-0a, S26)**.
> 4. ~~🟠 **Input → commandes horodatées**~~ ✅ **LIVRÉ (MP-0d, S29)** — `SimCommand`/`SimCommandQueue`/`InputMap` +
>    phase dans `SimulationHost.Tick` + `HeadlessSim --drive`.
> 5. ~~🟠 **Tick de simulation découplé**~~ ✅ **LIVRÉ (MP-0c, S28)** — `FixedTimestepAccumulator`. L'interpolation
>    visuelle reste 🟡 (jalon dédié).
>
> **Ensuite** : `Agapanthe.App` (host + contrat `Game`, extraction de `Program.cs`) → contenu (identité d'assets stable,
> cook, prefabs/scènes, data-driven) → **2ᵉ slice dissemblable** (top-down — test de généralité) → texte/UI, audio,
> queries physiques, job system → netcode réel.
>
> ### ✅ **UI-1 CLOS (S25)** — du texte à l'écran
> Spec : **[plans/2026-08-03-text-ui-design.md](plans/2026-08-03-text-ui-design.md)** (approuvée 4,4/5 après 2 tours
> de revue scorée). Premier des 3 jalons Texte & UI ; **UI-2 (overlay + profiler CPU) et UI-3 (timestamps GPU)
> restent à faire**. Double audit **PASS-with-concerns ×2** (aucun 🔴 ; 7 🟠 + 5 🟡 appliqués) + verdict humain PASS.
>
> **Livré** : `tools/FontCooker` (rasterisation SDF **hors-ligne**, `StbTrueTypeSharp` pur managé → **zéro dépendance
> native, y compris dans l'outil**) · format **`.agfont`** binaire déterministe (patron VS-1) · nouveau projet
> **`Agapanthe.Ui` GPU-free** (draw list, shaping avec seam `Rune`, layout, `Measure`) · `BlendMode` +
> `PixelFormat.R8Unorm` dans Graphics · `UiPass` + `FontResources` + `Renderer.LoadFont`/`DrawUi` ·
> `UiRenderSystem` · **capture swapchain** (`AGAPANTHE_CAPTURE_UI`).
>
> **Un atlas SDF sert toutes les tailles** (34/20/16/14/11 px), accents Latin-1, panneau translucide sans halo,
> alignements, multi-ligne. **192 glyphes, atlas 1024², em 64, spread 8.**
>
> **Métriques** : 435 tests · 0 warning · 0 validation · 0 leak (232 resources) · **NativeAOT PASS** avec
> `StbTrueTypeSharp` **absent du publish** · hashes : non-régression blending **`12638edd`** (inchangé — preuve que
> `BlendMode.Opaque` par défaut n'a touché aucun pipeline existant), capture UI **`6e14b23e`**.
>
> **Découvertes notables** : (1) `AGAPANTHE_CAPTURE` lit la cible **HDR**, donc l'UI dessinée après le tonemap lui
> était **structurellement invisible** → capture swapchain ajoutée (décision humaine) ; (2) `UiQuad` faisait 40 o
> côté C# contre un stride std430 de **48** — désynchronisation silencieuse dès le 2ᵉ quad, figée à 48 + test ;
> (3) **barrière manquante** entre tonemap et passe UI (2 render pass instances sans changement de layout, donc
> aucune dépendance émise) ; (4) spread SDF 4 trop faible → couverture bornée [0,066 ; 0,934] à 11 px, texte délavé
> et voile gris — porté à 8.
>
> **Dette laissée** : 🟠 **synchronization validation non activée** — le gate « 0 message de validation » ne peut
> structurellement **pas voir** les hazards de synchro, ce qui est exactement la classe du finding (3) ci-dessus ;
> à activer en UI-2 · `MaxStorageBuffers` 12/16 utilisés (UI-2/UI-3 déborderont) · pas d'échec bruyant si aucun
> format sRGB de swapchain · troncature silencieuse au-delà de 256 glyphes par appel · test d'alignement `UiQuad`
> qui verrouille la taille mais pas les offsets.
>
> ### ✅ **UI-2 CLOS (S25)** — overlay debug in-view + profiler CPU
> Deuxième des 3 jalons Texte & UI ; **UI-3 (timestamps GPU) reste à faire**. Double audit **PASS-with-concerns ×2**
> (`csharp-lowlevel` · `engine-architect` **3,8/5**, aucun 🔴 ; tous les 🟠 appliqués) + feu vert humain.
> Détail de session archivé : **[archive/board-session25-UI2.md](../.absolute-human/archive/board-session25-UI2.md)**.
>
> **Livré** : `FrameStats`/`FrameSeries` (ring circulaire, agrégats) et `DebugOverlaySystem` dans `Engine` ·
> `Sparkline` + `TextBuilder` (0-alloc, public) dans `Ui` · overlay in-view **remplaçant le HUD `window.Title`**
> (et son hack de cession VS-3), bascule **`F3`** · `AGAPANTHE_OVERLAY=0` pour démarrer masqué.
> **Le gate 0-alloc est désormais visible en continu à l'écran** : `alloc 0 B/frame` en vert, rouge dès qu'une frame
> alloue.
>
> **Ce que l'overlay a trouvé sur lui-même** : il affichait 272-288 B/frame là où le banc rapportait 0 B — sa fenêtre
> de mesure englobait le pump d'événements Silk.NET/GLFW. Puis l'audit a montré que la correction fermait le bracket
> **dans** `RecordCommandBuffer`, donc avant submit/present, et **jamais** sur les frames où `DrawFrame` sort tôt
> (resize) : « 0 B » en vert pendant qu'une swapchain était recréée par frame. → `FrameOrchestrator.EndFrame()`
> appelé **après** `DrawFrame`, exactement le bracket du banc.
>
> **Gates** : **468 tests** (3 runs) · 0 warning · **HDR `12638edd` inchangé** (non-régression scène) · capture UI
> overlay-masqué `03421357` reproductible · 0 leak · 0 validation · **0 hazard** (sync validation active) ·
> **NativeAOT PASS** (`AotProfilerSmoke` ajouté, `StbTrueTypeSharp` absent du publish).
> ⚠️ **La capture UI overlay-visible ne peut pas être byte-identique** (timings réels) — décision humaine : le gate
> déterministe porte sur l'overlay masqué, l'overlay est validé par verdict humain ; diff inter-runs mesuré à
> 432 px / 921 600, tous dans le panneau.
>
> **Dette déclarée** : seam `FrameProfiler` reporté à **UI-3** — les timestamps GPU arrivent à N+2 et casseront
> `Record(float, long)` ; le refactor appartient au jalon qui en connaîtra la forme. `DebugOverlaySystem` reste sans
> tests pour la même raison (dépendance à l'orchestrator concret). 🟡 le coût propre de l'overlay est inclus dans le
> `ms` qu'il affiche · `UiDrawList` 2048 quads = 80 Kio, à 6 % du seuil LOH — ne pas doubler sans y penser.
>
> ### ▶️ **Ensuite** : **UI-3** (timestamps GPU, `QueryPool`, dégradation gracieuse — abandonnable sans rien casser)
> ou **MP-0** (fondations d'autorité — **sans spec, brainstorm à faire d'abord**). Arbitrage humain.

**Point de reprise (2026-07-23, session 20)** : **P3-M7 buffers device-local + réduction du raster d'ombre 4× — CLOS.**
Double audit PASS (`csharp-lowlevel` PASS · `graphics-3d` PASS with concerns ; 0 🔴/🟠, findings 🟡 appliqués),
**verdict visuel humain PASS** (incl. le cas **soleil bas**, exigé par l'audit). Referme les **deux dettes perf
déférées de P3-M6**. **(A) device-local** : nouveau `CommandList.CopyBuffer` async intra-frame (core `vkCmdCopyBuffer`,
pas `CmdCopyBuffer2` = KHR/1.3, risque MoltenVK) ; le buffer de candidats persistant garde un **staging host-visible
= miroir** et copie les ranges dirty vers un **device-local** ; les buffers d'instances (scène+ombre) passent
device-local sans staging (GPU write+read). **(B) raster d'ombre** : un **7ᵉ plan de coupe near-side en profondeur-vue**
par cascade fait **tuiler** les cascades au lieu de s'emboîter → chaque caster dans ~1 cascade au lieu de ~4
(**cascade 0 exemptée** pour préserver l'anti-popping P3-M6 ; marge 25% de tranche > bande de fondu 10%). **Bonus** :
`ReadBackShadowVisible` (comptage d'ombre par cascade) ferme la dette de gate GPU==CPU de l'ombre de P3-M6.
Métriques (AOT `grid:100x100`) : **A+B ~15,3 → ~8,0 ms (≈ ×2)** · shadow-verify **total ≈ 1×/caster** (par cascade
`[28,123,701,4092]`, vs ~4× avant) · draws **2+4** · **0 alloc/frame** · GPU scène == CPU (`2576 MATCH`) · mono
**bit-identical `4848F93F`** · 0 leak · 0 validation · **325 tests** · NativeAOT PASS.
Spec : [2026-07-23-p3m7-device-local-shadow-raster-design.md](plans/2026-07-23-p3m7-device-local-shadow-raster-design.md) ·
protocole visuel : [visual-checks/2026-07-23-p3m7-device-local-shadow-raster.md](visual-checks/2026-07-23-p3m7-device-local-shadow-raster.md).
Env var nouvelle : `AGAPANTHE_SUN="x,y,z"` (direction du soleil ; petit `|y|` = soleil rasant, pour le test de light leak).
**Dette léguée** : `UpstreamExtent` par cascade complet reste déféré ([backlog §2.0bis](BACKLOG.md)) — la marge du
near-cut est calée sur l'épaisseur de tranche, pas la longueur d'ombre ; un soleil **très** rasant reste le cas
limite (jugé PASS au protocole). Chemin device-local/transfer **non exécuté sur MoltenVK** (dette P3-M0).

**Point de reprise antérieur (2026-07-23, session 19)** : **P3-M6 slots persistants + cull d'ombre GPU — CLOS.** Double audit
PASS (`graphics-3d` PASS · `csharp-lowlevel` PASS with concerns ; 0 🔴/🟠, findings 🟡 appliqués), **verdict visuel
humain PASS**. Referme **deux dettes fraîches** : (1) la régression sort/upload O(n) de P3-M4 ([backlog §1](BACKLOG.md)) —
le buffer de candidats est désormais **persistant** (`PersistentInstanceBuffer`, F copies host-visible + miroir CPU
autoritatif + sync-before-use), le gather + radix sort ne tournent qu'au **rebuild structurel** (spawn/despawn/edit
mesh-matériau/re-snap d'origine) et une frame ordinaire ne patche que les slots **dirty** (O(dirty), marqués aux 3
surfaces de mutation du World : animation, physique, propagation) ; (2) le cull par cascade CPU de P3-M5 ([§2.0bis](BACKLOG.md))
passe **en compute** (`shadow_cull.comp`, compaction atomique par région (cascade, mesh-batch)) → scan CPU O(n×4) et
**4 `RenderList` managées (~12 Mo) supprimés**, `CollectShadowCasters` retiré. **~200 lignes de code mort du wedge**
retirées. Métriques : **322 tests** · 0 warning · 0 validation · 0 leak · **0 alloc/frame @10k AOT** (draws **2+4**) ·
GPU scène visible == CPU (`ReadBackSceneVisible` MATCH) · mono **bit-identical `4848F93F`** · NativeAOT PASS.
Spec : [2026-07-23-p3m6-persistent-slots-gpu-shadow-cull-design.md](plans/2026-07-23-p3m6-persistent-slots-gpu-shadow-cull-design.md).
**Slot stable ssi `SortKey` sans profondeur** (condition de validité inscrite dans `InstanceSlot` — casse le jour où la
transparence ajoute la profondeur, backlog §0). **Dette léguée** ([backlog §1/§2.0bis](BACKLOG.md)) : le **raster ombre 4×**
et les **buffers host-visible → device-local** restent (déférés exprès, risque gate visuel / principal levier perf restant) ;
le cull d'ombre n'a pas de readback GPU==CPU (asymétrie avec la scène, dette de test notée). Banc animé ~15 ms (10k spinnés
= pire cas ; le gain se voit sur scène statique → dirty vide).

**Point de reprise antérieur (2026-07-19, session 18)** : **P3-M5 CSM — CLOS.** Double audit PASS with concerns (`csharp-lowlevel` + `graphics-3d`), findings majeurs appliqués, verdict visuel humain PASS. Quatre cascades dans un **atlas 2×2** de la carte 4096² existante (2048²/cascade, mémoire d'ombre **inchangée**) : split pratique (λ=0.5) sur `Cascades.MaxDistance` (200 m), fit par tranche de frustum — **caméra seule, donc plus de circularité fit↔casters** → le **wedge two-pass P3-M2 est retiré** (`CompactShadowCasters`/`_casterSpheres`/garde F7 supprimés) au profit d'un cull simple par cascade. `mesh.frag` : sélection par profondeur vue, PCF 5×5 **clampé à la tuile**, fondu inter-cascade 10 %, **fondu de distance 20 %**, debug `DEBUG_CASCADE` (touche N). Nouveau composant **`NoShadowCast`** (le sol reçoit mais ne projette pas). Métriques : **334 tests** · 0 warning · 0 validation · 0 leak · **0 alloc/frame @10k AOT** (draws 2+4) · **11,4 ms/frame** vs ~8 ms en P3-M4 (**coût CSM ~3,4 ms, assumé**) · NativeAOT PASS. Commit `aec752b`. **Ce que ça corrige** : les 4 artefacts du constat visuel P3-M4 (zone rectangulaire, anneaux d'acné, coupure d'ombre franche, ombres bridées) — [protocole](visual-checks/2026-07-19-p3m5-csm.md). **Deux calibrations nées du protocole** : plafond `ShadowDistance = 50 m` de la session 16 **levé** (il bridait le CSM au quart de sa portée — le workaround avait survécu à sa justification) et **fondu de distance** (supprime l'« horizon d'ombre »). **Dette** : portée finie par construction → [backlog §2.2bis](BACKLOG.md) (plus de cascades / RT hors macOS — **bloqué MoltenVK, sources citées** / ray marching, qui attend un terrain) · depth bias unique pour des cascades de densités très différentes (à surveiller).

**Point de reprise antérieur (2026-07-19, session 17)** : **P3-M4 rendu GPU-driven — CLOS.** Cull compute de la scène (`scene_cull.comp`, frustum-cull + compaction atomics) + draw indirect (`vkCmdDrawIndexedIndirect`, offset de batch en push constant → MoltenVK-safe). Gate : **GPU visible == CPU (2557 @10k AOT)**, mono bit-identique `9790D95D`, 0 alloc/frame @10k AOT, draws 2+2, NativeAOT PASS, 321 tests, double audit PASS. Env vars : `AGAPANTHE_CULL_VERIFY=1` (compte GPU vs CPU). **Dette P3-M4 → [backlog §1](BACKLOG.md)** : (C) slots persistants dirty-trackés (prochain jalon GPU-driven, rembourse la régression A+B sort/upload O(n)) · buffers GPU-produits en device-local · compaction atomique = 2ᵉ verrou transparence. **Verdict visuel PASS avec dette d'ombre** : artefacts sol sur grille (empreinte shadow map + acné sur sol plat, `eyeDistance` 248 m car plan de sol = caster ; moiré herbe) diagnostiqués **préexistants, pas le cull** — correctif = CSM ([backlog §2]) ou mitigation ground-non-caster. Bonus : **HUD debug barre de titre** (fps/ms/draws/candidates/GC MB). **Prochaine tâche : voir le choix ci-dessous** (CSM / slots persistants (C) / mitigation ombre / autre).

**Point de reprise antérieur (2026-07-19, session 16)** : **P3-M3 physique v1 — CLOS. W1→W4 livrés, double audit PASS, findings appliqués, verdict visuel humain PASS** (le glissement des casques — pas de roulement — est la limite v1 linéaire attendue, rotation/inertie au backlog §4). Corps rigides linéaires déterministes (gravité, intégration à dt fixe, collision sphère↔sol + sphère↔sphère, broadphase grille uniforme 0-alloc, résolution triée `(GlobalId)`). Métriques : 320 tests · 0 warning · 0 validation · 0 leak · **0 alloc/frame @1000 corps AOT** · NativeAOT PASS · reproductible run-à-run ET Debug≡AOT (`19D1A629`). `UpstreamExtent` exercé sous mouvement réel, wedge borné (P3-M2 D3) tient. Env vars : `AGAPANTHE_SCENE=drop:N` · `AGAPANTHE_PHYSICS=1`. Détail : board S16 + spec [2026-07-19-p3m3-physics-design.md](plans/2026-07-19-p3m3-physics-design.md). **Dette P3-M3 → [backlog §4](BACKLOG.md)** (SpawnBodyDeferred, plafond `GlobalId<2³²`, accumulateur, solver quality). **Prochaine tâche = le rendu GPU-driven** reporté de P3-M1 (cull compute + draw indirect, [backlog §1](BACKLOG.md)). **P3-M0 — validation Linux/macOS repoussée** (décision humaine : pas de machine, plus tard).

**Fix Sandbox (session 16, appliqué + validé)** — constat visuel humain de fin de session 15 sur une **grille de casques** (`AGAPANTHE_SCENE=grid:20x20`) : (a) « chaque casque a sa propre lumière » et (b) artefacts d'ombre en anneaux sur le sol. **Diagnostic : configuration du Sandbox, pas une régression moteur** (la capture de référence reste bit-identique, un casque seul est correct). **Corrigé dans `samples/Sandbox/Program.cs` ; verdict visuel humain PASS (2026-07-19, `grid:5x5` : éclairage uniforme, ombres nettes, 0 anneau).**
- (a) `SetupLights` montait un rig studio 3-points **mis à l'échelle sur la diagonale de la scène** (gonflée par le plan de sol) → point lights à ~450 m avec atténuation en carré inverse → dégradé de luminosité à travers la foule. **Fix appliqué** : `SetupLights(..., multiInstance: rows*cols > 1)` → sur scène multi-instances, `PointCount = 0` (soleil + IBL seuls) ; rig conservé pour le showcase mono-modèle.
- (b) `FrameCamera` faisait `renderer.ShadowDistance = diagonal * 4f` → la shadow map 4096² s'étalait sur ~500 m (~0,12 m/texel) → aliasing rasant sur le sol plat. **Fix appliqué** : `ShadowDistance = Min(Max(diagonal*4, 1), 50)` — plafond universel (no-op sur un casque 2 m : `diagonal*4 ≈ 14 m < 50`, capture mono-modèle **bit-identique `9790D95D`**) ; les casters lointains cessent de projeter, ce que `ShadowCasterDistance` (D3) gère sans popping.
- **Ce que ça révèle côté moteur** : une **cascade d'ombre unique ne peut pas être à la fois nette et longue portée** — plafond structurel, le vrai correctif est le **CSM**, déjà au [backlog §2](BACKLOG.md). À surveiller après le plafond : si de l'acné d'ombre subsiste, le depth bias (calibré pour une scène de 2 m) devra s'exprimer en espace texel de shadow map — durcissement moteur mineur, à constater, pas à postuler.

**Branche** : `phase2-foundations`. Commits P2-M4 : `12a07e3` (W0 Frustum+sphère+banc) · `7d9428a` (W1 origine quantifiée + ordre de frame + skybox origin-exact) · `c5b7da7` (W2 culling) · `458e017` (W3 radix + SortKey) · `99076c1` (W4 banc + AnimateDrawables) · `2827777` (durcissements audits : σ_max exact).

## Roadmap Phase 3 — état au 2026-07-19 (session 18)

**Livrés :**
| # | Jalon | Session | Ce qu'il a prouvé |
|---|---|---|---|
| P3-M1 | Instancing (SSBO) + 2 dettes de culling | S14 | 12 556 → 2 draws à 10k |
| P3-M2 | Scheduler + lifecycle + `Agapanthe.Engine` | S15 | L'ordre de frame est un invariant du moteur, pas du Sandbox |
| P3-M3 | Physique v1 (corps rigides linéaires) | S16 | Simulation déterministe, reproductible run-à-run, 0 alloc |
| P3-M4 | Rendu GPU-driven (cull compute + draw indirect) | S17 | Le cull quitte le CPU ; GPU visible == CPU (2557 @10k) |
| P3-M5 | **CSM** (4 cascades, atlas 2×2, `NoShadowCast`) | S18 | Ombres nettes près **et** loin ; les 4 artefacts du constat P3-M4 corrigés |
| P3-M6 | **Slots persistants dirty-trackés + cull d'ombre GPU** | S19 | Le CPU ne re-trie/re-upload plus tout (chemin incrémental O(dirty)) ; le cull d'ombre quitte le CPU (12 Mo managés disparus) — double audit PASS, verdict visuel PASS |
| P3-M7 | **Buffers device-local + réduction raster d'ombre 4×** | S20 | Les buffers GPU quittent l'host-visible (PCIe) ; le raster d'ombre passe de ~4× à ~1×/caster — A+B ~15,3 → ~8,0 ms (≈ ×2), double audit PASS, verdict visuel PASS (incl. soleil bas) |
| P3-M8 | **Premier pas planétaire (reversed-Z + sphère + scène planète/Soleil 1/2)** | S21 | Surface planétaire + Soleil à 7,48e10 m dans **un** frustum sans z-fighting ; reversed-Z global découplé du CSM ; Soleil = sphère de plasma = seule lumière (point light co-localisée) — double audit PASS, verdict visuel PASS |

**Ouverts, par ordre de recommandation** (chaque ligne dit *ce qui casse sans lui*) :

1. 🔴 **P3-M0 — Validation Linux/macOS.** Toujours le premier item sur le fond : AOT et SPIR-V hors-ligne sont
   **prouvés Windows uniquement**, donc « fondations cross-platform » reste une hypothèse. **Bloqué** : pas de
   machine (décision humaine, reporté S16). *Le seul item dont l'ancienneté grandit sans qu'on puisse agir.*
2. 🟠 **GPU-driven shadow cull + slots persistants** ([backlog §1](BACKLOG.md), §2.0bis). Le meilleur rapport
   valeur/effort aujourd'hui : il rembourse **deux** dettes d'un coup — le cull par cascade quasi inopératoire de
   P3-M5 (~4× les casters rasterisés) **et** la régression sort/upload O(n) de P3-M4 (le CPU trie/upload encore
   les 10k candidats par frame). *Mord : déjà au banc, et à 40k il domine.*
3. 🟠 **Terrain** ([backlog §5](BACKLOG.md)) — le sol est un quad plat. Prérequis de **beaucoup** : ray marching
   pour les ombres lointaines (§2.3, la piste retenue avec l'humain), relief au soleil rasant, scènes crédibles.
4. ~~🟠 **Scène de test « planète / système solaire à l'échelle 1/2 »**~~ ✅ **pas 1 livré en P3-M8** (S21) : sphère
   planétaire à l'échelle **1/2 uniforme** + **reversed-Z** (le depth range à `near/far ≈ 1e11` soldé) + jour/nuit
   par `dot(N,L)` d'une point light co-localisée avec la sphère-Soleil. La précision `double` a tenu (ULP ≈ 17 µm à
   `7,5e10` m), c'était bien le depth qui cassait — et il est réglé. **Suite** (backlog §4bis) : orbites képlériennes
   (pas 2), LOD sphérique + atmosphère (pas 3). *La scène planète est désormais l'ancre de la [Vertical Slice §4ter](BACKLOG.md).*
5. 🟡 **PCSS** ([backlog §2.1bis](BACKLOG.md)) — pénombre à largeur variable, partage la sélection de cascade
   avec le CSM tout juste livré. Qualité pure, pas de dette remboursée.
6. 🟡 **Sérialisation source-gen** (partage le générateur du rooting AOT ; parallélisable). **Audio** en dernier.

> **Cap moyen terme (formalisé S21) — [Vertical Slice, backlog §4ter](BACKLOG.md).** Le premier chemin de bout en bout
> (preuve d'intégration, **ancre planétaire**, Windows d'abord) : free-fly autour de la planète/Soleil P3-M8 + un
> élément **spawné au runtime** + **save/load** du monde + HUD minimal. Consomme, dans l'ordre : **VS-1 sérialisation**
> (la grosse pièce, item 6 ci-dessus), **VS-2 spawn runtime** (`SpawnBodyDeferred`, dette P3-M3), VS-3 glu gameplay,
> VS-4 HUD, VS-5 audio (stretch). P3-M0 (Linux) reste un prérequis **non bloquant**. C'est le jalon qui fait passer de
> « moteur avec fondations » à « moteur qui a fait tourner un monde de bout en bout ».

## Reprise — recommandations immédiates (écrit 2026-07-20, fin session 18)

**Où on en est** : arbre propre, tout commité (`9aa8b3f`), 334 tests verts, 0 warning / 0 validation / 0 leak.
Rien n'est en cours, aucun jalon ouvert. Le board S18 est archivé.

**Les trois candidats, avec ce qu'ils coûtent vraiment :**

- **A. GPU-driven shadow cull + slots persistants** ([backlog §1](BACKLOG.md) + [§2.0bis](BACKLOG.md)) —
  **le meilleur rapport valeur/effort**, et le seul qui rembourse **deux dettes d'un coup** : le cull par cascade
  quasi inopératoire de P3-M5 (~4× les casters rasterisés) et la régression sort/upload O(n) de P3-M4. Les deux
  mordent **déjà** au banc (part des 11,4 ms) et dominent à 40k. *Terrain connu, risque faible, gain mesurable.*
- **B. Premier pas planétaire** ([backlog §4bis](BACKLOG.md)) — le plus **excitant** et le plus **révélateur** :
  il valide (ou infirme) la thèse « fondations pour un univers persistant ». Mais il ouvre un problème
  **structurel non résolu** (le depth range) → prévoir une vraie phase d'instruction avant de coder.
  ✅ **Échelle tranchée (humain, S18)** : **deux facteurs distincts** — tailles et distances. Départ proposé :
  tailles **1/2**, distances **1/10** (Terre 3 186 km, 1 UA → `1,5e10` m). Garde de grandes coordonnées — la valeur
  de test — tout en rendant la planète atteignable. Détail et justification : [backlog §4bis](BACKLOG.md).
- **C. Terrain** ([backlog §5](BACKLOG.md)) — prérequis de B (LOD sphérique) *et* du ray marching pour les ombres
  lointaines (§2.3). Le plus gros morceau des trois.

**Ma recommandation si on reprend à froid** : **A d'abord** (court, referme deux dettes fraîches, laisse le moteur
plus propre qu'on ne l'a trouvé), **puis B** en commençant par le pas 1 isolé. Faire B avant A revient à empiler
une nouvelle échelle de problèmes sur un chemin de rendu dont on sait déjà qu'il gaspille 4× le travail d'ombre.

**Avant d'ouvrir quoi que ce soit** : relire [backlog §2.0bis](BACKLOG.md) (dette fraîche P3-M5, dont ~200 lignes
mortes à supprimer ou documenter) et la note « **ne pas ajouter de bias par cascade** » — c'est un piège dans lequel
il serait naturel de tomber en retouchant les ombres.

**Dette transverse à ne pas perdre de vue** : rotation/friction physique ([§4](BACKLOG.md)) · transparence
**doublement verrouillée** (`SortKey` sans profondeur **+** compaction atomique qui scramble l'ordre) ·
~200 lignes mortes laissées par le retrait du wedge (§2.0bis) · crash shutdown Silk.NET (upstream).

**Dette léguée par la Phase 2 (détail : board S13), par « quand ça mord »** :
- ~~🔴 `AggregateBounds` plié une fois~~ ✅ **soldé en P3-M1** (recalcul par frame) ; ~~ordre de frame dans le Sandbox~~ ✅ **soldé en P3-M2** (`FrameOrchestrator` + scheduler).
- ~~🔴 Cull du volume de lumière conservateur~~ ✅ **soldé en P3-M1** (`ExtrudedShadowFrustum` ANDé au volume de lumière ; CSM = vrai correctif plus tard).
- 🔴 **Linux/macOS jamais validés** (AOT + SPIR-V hors-ligne Windows-only) — premier item P3.
- 🟠 `SortKey` sans profondeur (pas de front-to-back opaque ; transparence future **fausse** sans tri profondeur) · déterminisme du tri exige `(matériau, RenderOrder)` globalement unique · propagation O(n·d) déférée (hiérarchies profondes) · pas d'API `Despawn`.
- 🟡 `AssertOwnerThread` Debug-only vs futur job system · **crash shutdown Silk.NET reproductible** (`AGAPANTHE_UNLOAD_TEST=20`, ~2/10, après le rapport propre — garder le gate CI keyé sur la ligne de rapport, pas l'exit code) · pas d'assertion CI du critère de sortie.

**Vérifs humaines de la Phase 2 — SOLDÉES (2026-07-14, session 14, Windows/RTX 5070 Ti)** · protocoles : [p2m4](visual-checks/2026-07-14-p2m4-bench-skybox.md) · [p2m3](visual-checks/2026-07-14-p2m3-precision-camera.md) · [p2m1](visual-checks/2026-07-14-p2m1-hot-reload-live.md).
- (a) **P2-M4** — banc `grid:100x100` + skybox W1 : **PASS with concerns**. Cull + skybox corrects et stables ; concern = **FPS bas sur la grande grille** → dette perf **assumée** de M4 (amplifiée par le run Debug + validation layers : 74–92 ms/frame vs 3,7 ms JIT-Release / ~6 ms AOT). **Remboursement = P3-M1.**
- (b) **P2-M3** — précision + feel caméra : **PASS**. Découverte des preuves headless : **l'alignement sur la maille (snap 1024 m) gouverne le bit-exact, pas la magnitude** — offset aligné à 10 M km (0,029 % de canaux ≠) plus proche du bit-exact qu'offset non aligné à 10 000 km (0,925 %) ; les deux indiscernables à l'œil. 1e15 visiblement dégradé (double qui casse).
- (c) **P2-M1** — hot reload Debug live : **PASS** (reload < 1 s confirmé en fenêtre, budget headless 0,9–2,2 ms/passe).

**Env vars du banc (P2-M4)** : `AGAPANTHE_SCENE=grid:NxN` (réplique le modèle en grille — un upload) · `AGAPANTHE_CULL_STATS=1` (mode banc : caméra dans la scène, spin déterministe, log visibles/total + temps + alloc).

**Run de sanity Debug** (0 validation / 0 leak) :
```powershell
$env:AGAPANTHE_MAX_FRAMES=3; $env:AGAPANTHE_CAPTURE="check.ppm"; dotnet run --project samples/Sandbox
```
**Publish + run NativeAOT** (Windows ; `vswhere` sur le PATH requis) :
```powershell
$env:PATH="C:\Program Files (x86)\Microsoft Visual Studio\Installer;$env:PATH"
dotnet publish samples/Sandbox/Sandbox.csproj -r win-x64 -c Release
samples/Sandbox/bin/Release/net10.0/win-x64/publish/Sandbox.exe   # 0 validation, 0 leak, capture byte-identique 24001B24…
```
*(macOS : préfixer `DYLD_LIBRARY_PATH=/opt/homebrew/lib` pour les runs Debug ; NativeAOT non validé hors Windows — voir dette.)*

**Contexte hors dépôt** : les projets de smoke-test/probe Arch (P2-M0) sont dans le scratchpad de session (non versionnés) — jetables, à recréer si besoin depuis la spec.

## Dette d'ouverture Phase 2 — mise à jour session 10

Détail P2-M0 : [.absolute-human/archive/board-session9-P2M0.md](../.absolute-human/archive/board-session9-P2M0.md) → « Dette issue de P2-M0 ». En bref, à traiter dans les jalons Phase 2 :
- 🔴 **Rooting AOT des composants = contrainte de conception P2-M2** (registre source-unique → source-gen du rooting → test AOT). Pas de la dette molle : échec runtime silencieux au publish, corruption partielle possible.
- 🔴 **Linux jamais validé** (dette M4) **+ AOT prouvé Windows-only** → re-prouver sur Linux/macOS dès qu'une machine est dispo.
- **Sérialisation maison source-gen** (Arch.Persistence NO-GO) → phase ultérieure, même générateur que le rooting.
- **CI** : le gate 0-leak doit keyer sur la **ligne de rapport**, pas l'exit code (otage du crash Silk.NET au shutdown).
- ~~**shaderc encore embarqué** sous AOT~~ → **retiré de la prod par P2-M1** (mode cache-only + `StripShadercFromRelease`).
- **Dette issue de P2-M1** (détail : board session 10) : (a) pas d'**assertion automatique** du critère de sortie §6 (lib native absente / shaderc jamais chargé reposent sur l'œil humain) → test + gate CI ≤ P2-M5 ; (b) chemin hors-ligne + strip **prouvés Windows uniquement** → re-prouver Linux/macOS (nom de lib natif `.so`/`.dylib` déjà couvert dans le target, non testé) ; (c) **includes non exercés** (resolver + clé include-aware en place, corrects par construction mais aucun shader n'a de `#include` → ajouter un cas avant de s'y fier).

**Dette issue de P2-M2** (détail : board session 11) — **à traiter en ouverture de M3/M4** :
- 🔴 **Handles sans génération** : `MeshHandle(int)` est un index nu → après un unload/reload (streaming), un handle périmé résoudra **silencieusement une autre ressource** (contredit §3.2 « handle déchargé = erreur »). Types **publics** → corriger **avant** que gameplay/sérialisation s'y accrochent.
- 🔴 **`ResourceRegistry` mono-modèle** : les handles sont des index **relatifs à un registre** → `MeshHandle(0)` de deux modèles se **collisionnent**. **Bloquant pour les 10 000 entités de M4.** Slot-map global (free-list + génération) = **le même changement** que le point ci-dessus. À trancher **avant** d'écrire le culling.
- 🔴 **Tie-break du tri en M4** (*le vrai piège*) : quand `SortKey` portera matériau/pipeline/profondeur, les **ex æquo deviendront la norme** et leur ordre suivra l'itération Arch (non déterministe) → **le déterminisme byte-identique se perdra silencieusement**. La clé 64-bit doit **inclure un tie-break stable en bits de poids faible** (`RenderOrder`/`GlobalId`) — un tri stable ne suffit pas.
- 🟠 **Séquencement M4 contre-intuitif** : le fit d'ombre sur **frustum caméra** (§3.5) doit précéder (ou accompagner) la bascule `Bounds` monde→locale — sinon la boîte (plus lâche) **déplace silencieusement la matrice d'ombre**. `ImportedEntitySpec` devra porter une AABB **locale**.
- 🟠 **Propagation O(n·d)** : chaque entité re-walke toute sa chaîne parent (+ scan linéaire du walk-stack). N'alloue pas (gate vert) mais **catastrophique à 10k** → passe unique ordonnée par profondeur en M4.
- 🟠 **`Parent` pendant** : dès que la destruction d'entités arrivera (M3), un `Parent` vers une entité morte → exception, ou **pire** : lecture silencieuse d'une **autre** entité (recyclage d'ids). À traiter avec l'API de destruction.
- 🟠 **`SortByKey` = insertion sort O(n²)** → radix LSD 64-bit (scratch réutilisé) en M4.
- 🟡 **`DrawScene` à 8 paramètres**, dont `sceneBounds` **transitoire** → introduire un agrégat **`RenderView`** en M3 : il portera l'**origine camera-relative**, le point le plus facile à désynchroniser (lumières, caméra et monde doivent soustraire **exactement** la même origine, sinon dérive silencieuse).
- 🟡 **`AggregateBounds` ne doit PAS devenir un système par frame** : après le fit caméra, plus aucun lecteur → **requête à la demande**, pas un maillon de la chaîne §3.5 (défaut de la spec, pas du code).
- 🟡 **Gates CI manquants** : assertion **automatique** du byte-identique (SHA vérifié à la main aujourd'hui) ; `publish -p:TrimmerSingleWarn=false` périodique **assertant que les assemblies fautives sont exactement `{Collections.Pooled}`** (sinon `NoWarn IL3053` masquera un futur tiers).
- 🟡 **Zéro-alloc à re-mesurer en M3** : le test valide un monde à **archétypes figés** ; créer/détruire des entités en jeu allouera côté Arch. · `AotRootingSmoke` exerce les types **en dur** → le piloter par `All` avant d'ajouter un composant.
- Reste hérité de M8 (voir « Dette d'ouverture Phase 2 (issue de M8) » plus haut) : invariant du reload par convention, feel souris/labels RenderDoc non observés, crash GLFW shutdown upstream, etc.
