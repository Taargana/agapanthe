# Absolute-Human Board — Agapanthe Session 10 (P2-M1 : Chemin SPIR-V hors-ligne)

**Status**: OPEN — session ouverte 2026-07-12. Deuxième jalon Phase 2. P2-M0 clos (gate AOT PASS, Arch validé). Règle §2.1-2 : le hot reload est un luxe de dev, **la prod embarque du SPIR-V précuit et ne charge pas shaderc**.
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

### P2-M1-04 — Précompilateur build + target MSBuild [infra, M] — todo — OWNER tools/ (new) + Sandbox.csproj/Directory.Build
Tool console (réutilise ShaderCompiler + ShaderIncludeResolver) : glob `shaders/*.{vert,frag,comp}`, dérive le stage de l'extension, résout les includes, compile, écrit les `.spv` **keyés à l'identique** dans le `.shadercache` livré à l'output. Target MSBuild qui l'exécute au build (avant la copie des Content). AC : build produit les .spv dans l'output ; clés identiques à celles que le runtime cherche.

### P2-M1-05 — Vérification [test, S] — todo
Release/AOT : démarre **sans charger shaderc** et **sans compiler** (tout est hit) ; capture byte-identique M8 ; 0 validation / 0 leak. Debug : hot reload toujours fonctionnel. Mesurer le gain de démarrage éventuel.

### P2-M1-06 — Double audit + archive [test, S] — todo
csharp-lowlevel (init paresseuse thread-safe, pas de fuite shaderc, AOT) + engine-architect (frontière, mode selection propre). Archive board → board-session10-P2M1.md.

## Dette (rappel, issue de P2-M0 — pour mémoire, traitée plus tard)

- Rooting AOT des composants = contrainte de conception P2-M2 (source-gen piloté par le registre + test AOT).
- Sérialisation maison source-gen = Phase 3 (même générateur).
- AOT prouvé Windows-only → re-prouver Linux/macOS.
- Smoke `[Query]` source-gen d'Arch.System non exercé → P2-M2.
- CI : keyer le gate 0-leak sur la ligne de rapport, pas l'exit code.
- shaderc natif embarqué → **c'est CE jalon (P2-M1) qui le retire de la prod**.

## Log

- 2026-07-13: **W2 DONE** — mode cache-only + sélection Debug/Release. `ShaderCompiler(precompiledOnly)` : miss précuit-seul → `GraphicsException` explicite (pas de repli shaderc, spec §4). Fabrique `CreateForBuild()` = source unique du `#if DEBUG` (Debug full / Release cache-only), utilisée par les 2 compilers (Renderer + IblGenerator). Hot reloader + debounce gardés `#if DEBUG` (Release : 0 watcher, `PollShaderReload` no-op). **Debug/Release build 0 warning · 207 tests (+2) verts · run Debug : hot reload actif, 0 validation, 0 leak (135), capture byte-identique M8 (24001B24…).** Preuve Release/AOT bout-en-bout reportée à P2-M1-05 (dépend de W3). Prêt pour W3 (précompilateur build + target MSBuild). **Non committé** (l'humain pilote).
- 2026-07-12: **W1 DONE** — shaderc paresseux dans ShaderCompiler. Cache chaud → shaderc jamais chargé (prouvé : log absent). + fix collatéral d'un gate de fuites flaky (ResourceTracker statique global × parallélisme xUnit) → collection non-parallélisable, 5/5 runs verts. Prêt pour W2 (mode cache-only + sélection Debug/Release).
- 2026-07-12: **Phase 2, session 10 ouverte — P2-M1 (SPIR-V hors-ligne).** P2-M0 clos et committé (`0da5a1d`/`9bf42b9`/`4cc930e`). Design fork tranché : le précompilateur build réutilise notre ShaderCompiler+resolver (clés identiques). DAG 6 tâches, 4 vagues. En attente feu vert pour W1 (shaderc lazy).
