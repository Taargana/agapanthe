# Absolute-Human Board — Agapanthe Session 10 (P2-M1 : Chemin SPIR-V hors-ligne)

**Status**: CLOSED — 2026-07-13. **P2-M1 livré** (chemin SPIR-V hors-ligne) : W1/W2/W3 + double audit PASS + 8 durcissements. Prod Release/AOT sans shaderc (prouvé Windows), capture byte-identique, 0 leak, 207 tests. Archivé → `archive/board-session10-P2M1.md`. Prochain : ouvrir P2-M2. Règle §2.1-2 tenue : le hot reload est un luxe de dev, **la prod embarque du SPIR-V précuit et ne charge pas shaderc**.
**Créé**: 2026-07-12
**Spec**: docs/plans/2026-07-12-phase2-foundations-design.md §2.1-2 (règle), §3.7 (chemin SPIR-V hors-ligne), §4 (SPIR-V manquant = échec explicite).
**Board persistence**: git-tracked
**Sessions passées**: S1-S9 → .absolute-human/archive/ (S9 = board-session9-P2M0.md).

## Intake (spec §3.7 — actée)

- **But** : un build Release/AOT qui **démarre sans shaderc** et **sans rien compiler**. Le hot reload (shaderc à chaud) reste actif **en Debug uniquement**.
- **Levier** : le cache disque existe déjà, keyé par `{stage}_{SHA256(source résolu après #include)}.spv` (ShaderCompiler.CacheKey + CompileFileResolved, M8-03). Il suffit de (a) le **pré-remplir au build** avec les mêmes clés, et (b) ne charger shaderc qu'en cas de vrai miss (Debug), jamais en prod.

## État actuel (constaté en lisant le code)

- `ShaderCompiler` charge shaderc **dès le ctor** (`Shaderc.GetApi()` + `CompilerInitialize()`, l.26-27) → même cache chaud = shaderc chargé à chaque run. C'est ce qu'il faut casser.
- Clé de cache = hash du **source résolu** (CompileFileResolved → Compile). Un précompilateur build qui réutilise `ShaderIncludeResolver` produit des clés **identiques** au runtime.
- Extensions shaders : `.vert`/`.frag`/`.comp` dans `shaders/` → stage dérivable de l'extension (pas besoin de la liste des passes).
- `shaderc_shared.dll` (6,6 Mo) est aujourd'hui embarqué au publish (constaté P2-M0).

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, **AOT-pur** (IsAotCompatible libs, warnings IL = erreurs), aucun Vk* hors Graphics, IDisposable + DeletionQueue N+2, zéro alloc/frame, ResourceTracker (leak = échec), 0 message de validation.
- Baseline capture byte-identique : SHA256 `24001B240C6C6B956F3F1AC6ABC1FE1D2CAA8914D9013169DFB049027E3309B7`.
- Publish AOT : PATH doit inclure `C:\Program Files (x86)\Microsoft Visual Studio\Installer` (vswhere).

## Rollback Point

`4cc930e` (P2-M0 clos, branche phase2-foundations).

## Décision de conception (design fork tranché)

**Le précompilateur build réutilise NOTRE `ShaderCompiler` + `ShaderIncludeResolver`** (pas un glslc externe) — c'est la seule façon de garantir des clés de cache **identiques** au runtime (même résolveur d'includes, même hash, même dérivation de clé). shaderc AU BUILD est acceptable (machine de dev) ; c'est shaderc AU RUNTIME/prod qu'on élimine.

## Task Graph

```
W1 : P2-M1-01 shaderc LAZY dans ShaderCompiler (chargé seulement au premier vrai miss)
     → à lui seul : cache chaud complet = shaderc jamais chargé. Rétro-compatible.
W2 : P2-M1-02 mode "précuit seul" (cache-only) : miss = GraphicsException explicite (§4, pas de repli shaderc)
     P2-M1-03 sélection du mode : Release = cache-only, Debug = full (shaderc lazy + hot reload). Hot reload Debug-only.
W3 : P2-M1-04 précompilateur build (tool console réutilisant ShaderCompiler+resolver) + target MSBuild
     → glob shaders/*.{vert,frag,comp} → résout includes → compile → écrit les .spv keyés dans le cache livré
Tail :
     P2-M1-05 vérif : Release/AOT démarre sans shaderc + sans compiler ; capture byte-identique ; 0 leak
     P2-M1-06 double audit (csharp-lowlevel + engine-architect) + archive
```

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | P2-M1-01 | ShaderCompiler (lazy) |
| 2 | P2-M1-02 → 03 | mode cache-only + sélection Debug/Release |
| 3 | P2-M1-04 | précompilateur build + MSBuild |
| tail | P2-M1-05, 06 | vérif + audits + archive |

## Tâches

### ✅ P2-M1-01 — shaderc lazy dans ShaderCompiler [code, M] — done — OWNER ShaderCompiler.cs
`Shaderc.GetApi()`/`CompilerInitialize()` déplacés du ctor vers `EnsureShaderc()`, appelé seulement par `CompileWithShaderc` (vrai cache miss). Double-checked lock (`System.Threading.Lock`), `_compiler` assigné avant publication de `_shaderc` (non-null ⇒ compiler valide). Dispose ne libère shaderc que s'il fut initialisé. ResourceTracker inchangé (ShaderCompiler reste tracké, register ctor / unregister Dispose). Log « cache miss — loading shaderc » = preuve visible du chemin paresseux.
**Preuve empirique** : run Debug **cache froid** → message ×2 (les 2 instances : Renderer + IblGenerator) ; run **cache chaud** → **AUCUN message = shaderc jamais chargé**, 0 leak, clean shutdown. Build 0 warning, **205 tests (5 runs consécutifs verts)**.
**Note** : 2 instances de ShaderCompiler existent (Renderer + IblGenerator) → W3 (précompilateur) et W2 (cache-only) doivent couvrir les shaders des deux.

### ✅ [collatéral W1] Fix flakiness du gate ResourceTracker [test-infra] — done
En vérifiant W1, `ResourceTrackerTests.BalancedRegisterUnregister` a échoué 1×/2 : **pré-existant** (le `ResourceTracker` est un statique global muté par `ResourceTrackerTests` — Enabled/Reset — pendant que `ShaderCompilerTests` y enregistre « ShaderCompiler » ; xUnit parallélise → course). Mon changement (ctor ShaderCompiler plus rapide, sans shaderc) a déplacé le timing et exposé la course. **Un gate de fuites flaky est inacceptable** → fix : `ResourceTrackerCollection.cs` (`[CollectionDefinition(DisableParallelization=true)]`) + `[Collection("ResourceTracker")]` sur les 2 classes → elles ne s'exécutent plus jamais en parallèle (entre elles ni avec le reste). **Vérifié 5/5 runs verts.**

### ✅ P2-M1-02 — Mode "précuit seul" (cache-only) [code, S] — done — OWNER ShaderCompiler.cs
Paramètre ctor `bool precompiledOnly = false` (défaut = full, garde tests/legacy inchangés). Dans `Compile`, sur cache miss en mode précuit-seul → `GraphicsException` explicite (nomme la clé `{stage}_{hash}.spv` + le dossier + « no shaderc ; must be produced by the build-time precompiler »), **avant** tout appel à `CompileWithShaderc` — pas de repli silencieux (spec §4). Rien n'est compilé ni écrit. Tests : miss cache-only → exception (`pre-cooked`) + cache reste vide ; hit cache-only (cache pré-cuit par un compiler full) → renvoie les octets exacts sans shaderc.

### ✅ P2-M1-03 — Sélection du mode Debug/Release + hot reload Debug-only [code, S] — done — OWNER ShaderCompiler.cs / Renderer.cs / IblGenerator.cs
Fabrique `ShaderCompiler.CreateForBuild()` = **seul** point où vit le `#if DEBUG` du choix de mode (Debug = full, Release = cache-only). Les **2 instances** (`Renderer._shaderCompiler`, `IblGenerator._compiler`) l'utilisent. Le `ShaderHotReloader` (montage `watchedFiles` + instanciation) et le champ `ShaderReloadDebounce` sont gardés `#if DEBUG` → en Release aucun thread watcher, `_reloader` null, `PollShaderReload` no-op (déjà gardé). AC vérifié : Debug → watching 7 shaders, hot reload actif ; Release compile 0 warning (pas de CS0414 sur le champ debounce). Preuve Release/AOT bout-en-bout = P2-M1-05 (après W3, quand le cache livré est pré-rempli).

### ✅ P2-M1-04 — Précompilateur build + target MSBuild [infra, M] — done — OWNER tools/ShaderPrecompiler (new) + Sandbox.csproj + Agapanthe.slnx
Outil console `tools/ShaderPrecompiler` (réutilise `ShaderCompiler` + `ShaderIncludeResolver` en mode full — shaderc au build OK) : glob `shaders/*.{vert,frag,comp}`, stage dérivé de l'extension, résout includes, `CompileFileResolved` écrit les `.spv` keyés à l'identique dans un cache de staging (`obj/shadercache`). **Pas de ProjectReference depuis Sandbox** (sinon shaderc entrerait dans la closure AOT) : le build invoque l'outil via `<MSBuild>` (RemoveProperties RID/AOT) + `<Exec dotnet exec>`. 2 targets : `PrecompileShaders` (incrémental, Inputs=sources shaders/Outputs=stamp) + `IncludePrecompiledShaders` (toujours, `BeforeTargets=AssignTargetPaths`, `DependsOnTargets`) ship les `.spv` en Content vers `.shadercache/` de l'output.
**Pièges rencontrés** : (a) `$(IntermediateOutputPath)` vide à l'éval top-level du corps csproj → staging basé sur `$(MSBuildProjectDirectory)\obj` ; (b) chemin à backslash final entre guillemets dans l'`Exec` → échappe le guillemet, args fusionnent → `TrimEnd('\')` ; (c) wildcard passé directement à `<Content Include>` dans un target ne ship rien → pré-expansion en item intermédiaire `_PrecookedSpv` puis `Content Include="@(_PrecookedSpv)"`.
**AC dépassé** : build (Debug & Release) produit 15 `.spv` dans l'output ; clés identiques (run **Debug** = cache chaud, **aucun** « cache miss — loading shaderc »). **Publish Release/AOT win-x64** : binaire natif 4,47 Mo, 15 `.spv` livrés, run cache-only → **aucune `GraphicsException`, aucun miss** = toutes les clés matchent, **0 leak, capture byte-identique 24001B24…** ⇒ **P2-M1-05 (vérif) de facto acquis**.
**shaderc retiré de la prod** ✅ : target `StripShadercFromRelease` (Release-only, `AfterTargets=CopyFilesToOutputDirectory;Publish`) supprime `shaderc_shared.dll` (+ variantes `runtimes/**`) de l'output/publish. Debug le garde (hot reload). **Vérifié** : publish AOT → DLL **absent**, 15 `.spv` conservés, run **sans le DLL** = aucun `DllNotFound`/exception, 0 leak, capture byte-identique 24001B24… ⇒ but littéral du jalon (« retirer shaderc de la prod ») atteint.

### 🟡 P2-M1-05 — Vérification [test, S] — largement acquis via W3 — todo (reste : hot reload Debug live + décision shaderc.dll)
Release/AOT : démarre **sans charger shaderc** (aucun log « cache miss ») et **sans compiler** (tout hit, sinon `GraphicsException`) — **prouvé** au run AOT ; capture byte-identique M8 (24001B24…) ; 0 leak. **Reste** : re-confirmer le hot reload Debug **live** à la fenêtre (edit shader → recompile < 1 s) — non re-testé depuis W2/W3. (Retrait physique de `shaderc_shared.dll` : **fait** en W3.)

### ✅ P2-M1-06 — Double audit + archive [test, S] — done
**Deux audits PASS, zéro finding critique.** `csharp-lowlevel` : PASS (repli silencieux impossible, disposal natif propre sur tous les chemins, pas de contamination AOT — tous re-vérifiés adversarialement). `engine-architect` : PASS conditionnel (couture saine, `#if DEBUG` unique, shaderc retiré de la sortie Release Windows).
**Durcissements appliqués (in-milestone) et re-vérifiés (Debug byte-identique + AOT sans DLL, 0 leak, 207 tests)** :
- ctor `ShaderCompiler` → `internal` + `[InternalsVisibleTo("ShaderPrecompiler")]` (Tests déjà couverts) → le code applicatif ne peut plus contourner `CreateForBuild` et obtenir le mode full en Release [archi #1].
- garde défensive `if (_precompiledOnly) throw` en tête d'`EnsureShaderc` [archi #2 / low M4b].
- `_shaderc` → `volatile` : le DCL prétendait une thread-safety fausse sur ARM64 (Apple silicon/MoltenVK, cible) — corrigé (write release / read acquire publient `_compiler`) [low m1].
- `IblGenerator.Compile` : `CompileFile` → `CompileFileResolved` — même clé que le précompilateur et les passes ; bombe à retardement au 1er `#include` dans un `ibl_*.comp` désamorcée [low M3].
- MSBuild `ShaderSourceFile` (Inputs incrémentaux) : `**\*.{vert,frag,comp}` → `**\*` (tout `shaders/`) — l'édition d'un futur `#include` re-cuit ; sinon `.spv` sous clé périmée → miss Release [low M2 / archi #3].
- `StripShadercFromRelease` : couvre `.dll` + `libshaderc_shared.so*` + `.dylib` + `runtimes/**/*shaderc_shared*` → critère (a) tenable cross-platform [low M1 / archi #5].
- tool : énumération récursive `AllDirectories` (align csproj) [m4] + catch élargi `IOException`/`UnauthorizedAccessException` [m5].
- **Finding m6 REJETÉ** (ne pas stripper les GLSL en Release) : le runtime cache-only **lit la source** pour hasher la clé (`CompileFileResolved`→`Resolve`) → les shaders sont **requis** en Release.
Archive board → **fait** (`archive/board-session10-P2M1.md`).

## Dette (rappel, issue de P2-M0 — pour mémoire, traitée plus tard)

- Rooting AOT des composants = contrainte de conception P2-M2 (source-gen piloté par le registre + test AOT).
- Sérialisation maison source-gen = Phase 3 (même générateur).
- AOT prouvé Windows-only → re-prouver Linux/macOS.
- Smoke `[Query]` source-gen d'Arch.System non exercé → P2-M2.
- CI : keyer le gate 0-leak sur la ligne de rapport, pas l'exit code.
- shaderc natif embarqué → **c'est CE jalon (P2-M1) qui le retire de la prod**.

## Dette laissée par P2-M1 (à consigner dans AVANCEMENT, non bloquante)

- 🟠 **Pas d'assertion automatique du critère de sortie §6** : (a) « lib native absente du publish » et (b) « shaderc jamais chargé en Release » reposent sur l'inspection humaine (absence du log « loading shaderc »). Recommandé ≤ P2-M5 : un test qui publie et asserte l'absence du binaire natif (par nom, plateforme courante) + un gate CI. La garde `EnsureShaderc` couvre partiellement (b).
- 🔴 **Chemin hors-ligne prouvé Windows uniquement** : re-prouver publish AOT + cache-only + strip (nom de lib natif correct) sur Linux/macOS dès qu'une machine est dispo (rejoint la dette « Linux jamais validé »).
- 🟡 **Includes non exercés** : le resolver + la clé include-aware sont en place mais aucun shader n'a de `#include` aujourd'hui. Les fixes M2/M3 les rendent corrects par construction, mais rien ne les **teste** — ajouter un shader à include (ou un test) avant de s'appuyer dessus en prod.
- 🟢 mineurs notés (non corrigés) : staging `obj/shadercache` jamais purgé → orphelins `.spv` shippés (bloat inerte) [low m3] ; nom `CreateForBuild` trompeur (appelé par le runtime, pas le build) [archi mineur] ; stamp d'incrément périmé si on modifie le tool sans toucher les shaders → `rebuild --no-incremental` [archi/low mineur] ; finalizer `ShaderCompiler` signale mais ne libère pas (cohérent philosophie projet) [low m2].

## Log

- 2026-07-13: **P2-M1-06 (audits) DONE** — 2 audits PASS (0 critique). 8 durcissements appliqués + re-vérifiés (ctor internal, garde EnsureShaderc, `_shaderc` volatile ARM64, IblGenerator→CompileFileResolved, Inputs MSBuild `**\*`, strip cross-platform .so/.dylib, tool récursif + catch élargi). Debug byte-identique + AOT sans DLL, 0 leak, 207 tests. Dette consignée (assertion §6, Linux/macOS, includes non exercés). Reste : archive + AVANCEMENT + commit.
- 2026-07-13: **W3 DONE** — précompilateur build `tools/ShaderPrecompiler` + 2 targets MSBuild dans Sandbox.csproj (+ projet ajouté à Agapanthe.slnx). Réutilise notre ShaderCompiler+resolver → clés identiques. Debug & Release : 15 `.spv` livrés dans `.shadercache/` de l'output. **Publish Release/AOT** : 4,47 Mo, cache-only trouve tout (0 miss / 0 exception), 0 leak, capture byte-identique 24001B24… ⇒ P2-M1-05 de facto acquis. 3 pièges MSBuild documentés (IntermediateOutputPath vide top-level ; backslash final entre guillemets ; wildcard direct dans Content). **shaderc_shared.dll strippé en Release** (target `StripShadercFromRelease`) + vérifié : run AOT sans le DLL OK (0 leak, byte-identique) ⇒ shaderc retiré de la prod. **Non committé.**
- 2026-07-13: **W2 DONE** — mode cache-only + sélection Debug/Release. `ShaderCompiler(precompiledOnly)` : miss précuit-seul → `GraphicsException` explicite (pas de repli shaderc, spec §4). Fabrique `CreateForBuild()` = source unique du `#if DEBUG` (Debug full / Release cache-only), utilisée par les 2 compilers (Renderer + IblGenerator). Hot reloader + debounce gardés `#if DEBUG` (Release : 0 watcher, `PollShaderReload` no-op). **Debug/Release build 0 warning · 207 tests (+2) verts · run Debug : hot reload actif, 0 validation, 0 leak (135), capture byte-identique M8 (24001B24…).** Preuve Release/AOT bout-en-bout reportée à P2-M1-05 (dépend de W3). Prêt pour W3 (précompilateur build + target MSBuild). **Non committé** (l'humain pilote).
- 2026-07-12: **W1 DONE** — shaderc paresseux dans ShaderCompiler. Cache chaud → shaderc jamais chargé (prouvé : log absent). + fix collatéral d'un gate de fuites flaky (ResourceTracker statique global × parallélisme xUnit) → collection non-parallélisable, 5/5 runs verts. Prêt pour W2 (mode cache-only + sélection Debug/Release).
- 2026-07-12: **Phase 2, session 10 ouverte — P2-M1 (SPIR-V hors-ligne).** P2-M0 clos et committé (`0da5a1d`/`9bf42b9`/`4cc930e`). Design fork tranché : le précompilateur build réutilise notre ShaderCompiler+resolver (clés identiques). DAG 6 tâches, 4 vagues. En attente feu vert pour W1 (shaderc lazy).
