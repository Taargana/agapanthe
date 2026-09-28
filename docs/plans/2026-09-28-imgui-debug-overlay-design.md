# ImGui natif dans Agapanthe — remplacement du debug overlay (F3), exclu du build Master, opt-in par consommateur

## Summary

Le debug overlay actuel (`DebugOverlaySystem`, F3, livré UI-1/UI-2/UI-3) est un panneau texte+graphes
en lecture seule, dessiné via `Agapanthe.Ui`/`TextLayout`/`UiDrawList`. L'humain veut que **tout
l'outillage de debug futur** vive désormais dans une vraie lib immediate-mode GUI (Dear ImGui), avec
de vrais widgets interactifs, plutôt que de continuer à étendre `UiDrawList`/`TextLayout` pour ça.

**Décision explicite : ImGui ne remplace PAS** le splash/menu de Club Architect (UI de gameplay) —
uniquement le debug tooling. Contrainte forte ajoutée en interview : **la lib ImGui ne doit jamais
être embarquée dans le build qui part réellement chez les joueurs.**

**Round 5 (précision de la contrainte)** : cette contrainte est reformulée précisément en introduisant
une 3ᵉ configuration `Master` (à côté de `Debug`/`Release`) — c'est **Master**, pas `Release`, qui
doit exclure ImGui. `Release` redevient un palier interne (QA/profiling) où le panneau debug reste
disponible. Voir D9 et D10 pour le détail du mécanisme MSBuild.

**Correction structurelle round 1** (score 3,2/5) : le premier jet câblait ImGui dans `Agapanthe.App`
derrière un `#if DEBUG`. Insuffisant pour un consommateur externe packagé — ce dépôt **pack en
configuration Debug** (`dotnet pack Agapanthe.slnx -c Debug`), donc un `#if DEBUG` dans le code
source d'`Agapanthe.App` serait figé au moment du pack, pas ré-évalué par Club Architect. Résolu :
`Agapanthe.DebugUi` n'est **jamais référencé par `Agapanthe.App`** (invariant qui tient encore après
le round 4 ci-dessous — seul le nombre de consommateurs autorisés à référencer `DebugUi` lui-même a
changé, pas cet invariant-là). Round 1 le limitait à `samples/Sandbox`/`samples/TopDown` (patron
`IblTestTool`, cf. `docs/plans/2026-09-27-club-architect-package-separation-design.md:118-119` — pas
`Program.cs` comme mal cité initialement) ; **révisé au round 4, voir plus bas**.

**Correction round 2** (score 3,1/5 — le déplacement structurel a ouvert un trou de cycle de vie
GPU et plusieurs incohérences d'interface) :
- 🔴 **aucun teardown GPU** n'était prévu pour les ressources d'`ImGuiDebugSystem` (pipeline, SSBO,
  atlas, contexte natif) — fuite garantie à chaque scene-switch et au shutdown. Résolu ci-dessous
  (le hook retourne un `IDisposable?`, disposé explicitement par `AppHost`).
- 🟠 **question de scope non tranchée, remontée à l'humain** : la correction round 1 supprime
  `DebugOverlaySystem` partout, donc Club Architect/ThinClient perdent F3 **même en Debug**, pas
  seulement en Release. **Réponse de l'humain : perte acceptée.** `DebugOverlaySystem` est supprimé
  sans remplacement pour ces hôtes — seuls les consommateurs qui optent explicitement dans `DebugUi`
  (Sandbox/TopDown au round 1 ; **révisé round 4** : n'importe quel consommateur peut désormais opter)
  ont un debug tooling.
- Plusieurs incohérences d'interface corrigées (détaillées dans le Decision Log) : placement du
  point d'extension (`IGame`, pas `HostOptions` — `HostOptions.with` ne compile pas), doublon avec
  `CaptureMouseOnClick` existant, timing de `ScrollDelta`, packaging des shaders, portée réelle de
  la garde `Configuration`/`PublishAot`, contamination du gate 0-alloc affiché par le panneau
  lui-même.

Cette intégration casse une décision verrouillée du projet (`CLAUDE.md`) : *"Bindings Silk.NET
(Vulkan + GLFW + input) ; le reste from scratch."* — précédent directement comparable : LiteNetLib
(Net-1), pas la toute première exception (Arch, StbImageSharp/StbTrueTypeSharp, Tomlyn,
Silk.NET.OpenAL sont déjà tiers).

**Round 3 (score 3,9/5, NEEDS WORK à la limite, AUCUN bloquant)** : le mécanisme de teardown du
round 2 est confirmé correct et implémentable (les deux seuls sites `new PresentationSceneContext`
du dépôt sont bien `AppHost.cs:183`/`:653`, aucun 3ᵉ site caché). 4 amendements ciblés appliqués
directement (recommandation explicite du reviewer plutôt qu'un 4ᵉ tour — le protocole plafonne à 3
itérations) : (1) les shaders passent en `EmbeddedResource` compilés via `ShaderCompiler.
CreateForBuild()` plutôt qu'un glob MSBuild fragile qui n'aurait de toute façon aucun effet en
Debug (le precompile ne sert QUE le mode cache-only de Release, confirmé contre
`ShaderCompiler.cs:66-73` — l'ancienne approche était à contre-emploi) ; (2) le test de switch de
scène en Verification se limite à Sandbox (`TopDownGame` n'a qu'une seule scène, confirmé) ; (3) les
fichiers à toucher sont `SandboxGame.cs`/`TopDownGame.cs` (les vrais `IGame`), pas `Program.cs` ; la
`ProjectReference` Sandbox/TopDown→DebugUi et la surcharge `ConfigureDebugTools` sont toutes deux
explicitement sous condition/`#if DEBUG` pour qu'aucun `DebugUi.dll` n'entre dans la publication AOT
Release de Sandbox ; (4) le point de dispose exact est nommé (juste après `frameRenderer.WaitIdle`),
avec une étape `TeardownTargets.DisposeDebugTools` nommée, la mise à jour du test épinglé
`AppHostContractTests.cs:340`, et une règle explicite de remise à `null`/idempotence du handle.

**Round 4 (changement de scope demandé par l'humain après relecture du spec approuvé au round 3)** —
question posée : le spec limitait ImGui à Sandbox/TopDown (outillage moteur), donc Club Architect et
tout futur jeu construit sur Agapanthe n'auraient jamais accès au panneau, même en Debug. L'humain
veut que **les jeux consommateurs puissent aussi l'activer en Debug**. Résolu sans reperdre l'acquis
du round 1 : le vrai problème du round 1 était un `#if DEBUG` **compilé dans un DLL partagé
pré-packé** (`Agapanthe.App.dll`, figé à la config du pack) — pas la simple existence d'une
dépendance Debug-only. Si `Agapanthe.DebugUi` devient un package normal que **chaque consommateur
référence lui-même** (`Condition="'$(Configuration)'=='Debug'"` dans SON PROPRE csproj, évaluée par
SA PROPRE build), c'est **cette référence conditionnelle côté consommateur** qui porte toute la
garantie — **pas** un `#if DEBUG` interne à `DebugUi` (correction round 4, vérifiée par audit :
`DebugUi` est lui-même packé par Agapanthe en config Debug, comme tout le reste — sa propre garde
interne, D9, est donc évaluée vraie une fois pour toutes au moment du pack et **gelée à ON dans le
nupkg publié**, exactement le figement du round 1, mais sans conséquence cette fois : un
consommateur ne restaure `DebugUi` QUE s'il l'a lui-même référencé conditionnellement en Debug — peu
importe que le contenu interne de `DebugUi` soit figé "actif", il n'est jamais tiré en Release). Ce
qui EST compilé par le consommateur, et reste donc sans staleness possible, c'est son propre
`ConfigureDebugTools` (`ClubArchitectGame.cs`, `SandboxGame.cs`...). `Agapanthe.App`
continue de ne JAMAIS référencer `DebugUi` en dur (le hook `IGame.ConfigureDebugTools` du round 2/3
était déjà conçu pour vivre côté consommateur, ce changement ne le retouche pas). **Contrepartie
explicitement acceptée par l'humain** : pour un consommateur externe (Club Architect), la garantie
"jamais en Release" devient une **discipline par consommateur** (bien écrire sa propre `Condition`,
patron standard de tout l'écosystème .NET pour une dépendance dev-only) plutôt qu'une impossibilité
**structurelle** — Agapanthe ne peut pas tester/gater le csproj d'un dépôt externe. Pour
Sandbox/TopDown (in-repo), la garantie reste aussi forte qu'avant (gatée par les tests du dépôt).

## Decision Log

- **D1 (révisé round 4) — Périmètre : debug uniquement, opt-in par consommateur.**
  `Agapanthe.Ui`/`TextLayout`/`UiDrawList`/`FontCooker`/`.agfont` restent inchangés (servent le
  splash/menu de Club Architect — le panneau ImGui ne remplace jamais l'UI de gameplay). `DebugUi`
  est disponible à **tout** consommateur — Sandbox/TopDown in-repo, Club Architect, un futur jeu —
  qui l'ajoute lui-même comme dépendance exclue du build `Master` (round 5 — disponible en Debug
  **et** Release) et implémente
  `IGame.ConfigureDebugTools`. `DebugOverlaySystem` est supprimé **sans remplacement automatique** :
  un consommateur qui n'opte pas explicitement dans `DebugUi` (ThinClient, HeadlessSim, ou Club
  Architect s'il ne fait pas la démarche) n'a plus AUCUN debug overlay (round 2, décision humaine —
  ThinClient/HeadlessSim n'ont pas vocation à l'utiliser ; Club Architect PEUT désormais l'ajouter,
  round 4).
- **D2 — Binding : `Hexa.NET.ImGui` (cœur seulement, PAS `.Backends`).** Ni Hexa.NET.ImGui ni
  ImGui.NET ne documentent explicitement NativeAOT/trimming. **Correction round 5 (audit) : ceci
  A des conséquences maintenant** — vrai uniquement tant qu'ImGui était Debug-only ; depuis que
  `Release` le garde (D9/D10), et que `Sandbox.csproj`/`TopDown.csproj`/`ClubArchitect.csproj` ont
  `PublishAot=true` fixé en permanence, **`dotnet publish -c Release` publie désormais un binaire
  NativeAOT contenant Hexa.NET.ImGui** — jamais vérifié empiriquement. Nouveau spike obligatoire
  (Verification, item 1(c)) : un publish Release AOT réel avec Hexa doit réussir (trim, P/Invoke,
  chargement du natif cimgui) avant que quoi que ce soit d'autre ne soit considéré acquis. Version
  exacte épinglée au même spike ; confirmer que l'API d'extraction d'atlas
  (`GetTexDataAsRGBA32` ou équivalent) existe bien sur la version réellement récupérée (ImGui 1.92+
  a changé la gestion d'atlas).
  - `.Backends` écarté : le backend Vulkan de référence veut du `VkDevice`/`VkCommandBuffer` brut
    (`Agapanthe.Graphics` n'en laisse jamais sortir), le backend GLFW toucherait `Platform` depuis
    une couche qui n'a pas le droit de la connaître.
- **D3 (révisé x3, round 4 : redevient packable) — Nouveau projet `Agapanthe.DebugUi`, jamais
  référencé par `Agapanthe.App`, mais désormais `IsPackable=true`** (comme tout autre projet moteur —
  packé/versionné par NBGV, publié sur le feed local `artifacts/package` au même titre
  qu'`Agapanthe.App`/`Agapanthe.Ui`). Tout consommateur — in-repo (Sandbox/TopDown, `ProjectReference`
  conditionnelle) ou externe (Club Architect, `PackageReference` conditionnelle) — l'ajoute lui-même
  avec `Condition="'$(Configuration)' != 'Master'"` (round 5 — corrige un texte resté à `=='Debug'`
  après le passage à `Master`) **dans son propre csproj**, évaluée par sa propre build (round 4 —
  c'est ce qui rend la staleness du round 1 impossible : le `#if !MASTER` vit désormais dans le code
  source du CONSOMMATEUR, jamais dans un DLL partagé pré-compilé). Référence
  `Core`, `Graphics`
  (`CommandList`/`GraphicsPipeline`/`GpuBuffer`/`DescriptorSetHandle`), `Engine`
  (`ISystem`/`Stage`/`TickContext`/`FrameStats`/`FrameSeries`), `Engine.Render`
  (`IRenderSystem`/`RenderContext`), `Rendering` (`Renderer.LastGpuPassTimingsMs`, nommé
  directement), **et `Agapanthe.App`** (round 2, 🟠2 : `IWindow` — dont `MousePosition`/
  `CharInput`/le knob de capture, D5 — vit dans `App` ; référence acyclique puisqu'`App` ne référence
  jamais `DebugUi`).
  - Point d'extension (round 2, 🟠5 — **`IGame`, pas `HostOptions`**) :
    ```csharp
    // Sur IGame, DIM par défaut null — mirroring IGame.FontPath/IGame.Universe exactement.
    Func<SimSceneContext, PresentationSceneContext, IDisposable?>? ConfigureDebugTools => null;
    ```
    Choisi plutôt que `HostOptions` car (a) `HostOptions` n'a pas d'opérateur `with` fonctionnel
    (`WithoutStartupOnlyPaths()` recopie chaque champ à la main faute de `with` — ajouter un champ
    l'y oublierait silencieusement, confirmé round 2) ; (b) Sandbox/TopDown ne construisent
    aujourd'hui aucun `HostOptions` explicite (`options: null` aux deux, confirmé round 2) — les
    forcer à en construire un pour un seul champ est plus de friction que d'implémenter le DIM sur
    leur `IGame` respectif, exactement le patron déjà établi par `FontPath`. Le délégué reçoit
    **les deux contextes** (pas seulement `PresentationSceneContext`) pour lire
    `sim.Options.OverlayVisible` (round 2, 🟠6 — voir D-capture-UI ci-dessous) et retourne un
    `IDisposable?` — **le mécanisme de teardown** (round 2, 🔴1, détaillé ci-dessous).
  - `AppHost.RunClient` invoque `game.ConfigureDebugTools?.Invoke(sim, presentation)` aux deux sites
    réels de construction de `PresentationSceneContext` — **`AppHost.cs:183`** (premier chargement)
    et **`AppHost.cs:653`** (`PerformHandOff`, confirmées par grep round 2 — les lignes ~150/~634
    citées au round 1 étaient en fait `orchestrator.Add(Stage.PostSimulation, debugOverlay)`, pas les
    sites de construction). **Appelé après `recipe.Build`** (round 2, mineur) — sinon un
    `IRenderSystem` de recette s'ajouterait après le panneau ImGui alors que l'ordre des
    `IRenderSystem`s est figé au premier tick.
  - **Teardown (round 2, 🔴1, résolu ; précisé round 3)** : `RunClient` étant `static`, c'est un
    **local hissé** `IDisposable? debugToolsHandle` capturé par les closures (patron `debugOverlay`,
    `AppHost.cs:59`), pas un "champ" au sens objet. À chaque invocation du hook (premier chargement
    ou switch), l'ancien handle (s'il existe) est disposé **immédiatement après
    `frameRenderer.WaitIdle`** (position exacte, round 3 — `AppHost.cs:848`), puis remis à **`null`**
    avant que le nouveau ne soit assigné (round 3 : évite un double-dispose si un throw ultérieur
    déclenche aussi le teardown final ; `ImGuiDebugSystem.Dispose()` est par ailleurs idempotente par
    construction, comme toute ressource `ResourceTracker` du moteur). Au shutdown final,
    `debugToolsHandle?.Dispose()` devient une étape nommée **`TeardownTargets.DisposeDebugTools`**
    dans `BuildTeardown` (`AppHost.cs:833-861`), juste après l'étape `WaitIdle` existante — même
    position qu'au switch, un seul point de vérité. **`AppHostContractTests.cs:340`
    (`BuildTeardown_LabelsAreInStrictM4Order`) doit être mis à jour** pour inclure ce nouveau label,
    exactement comme il l'a été pour l'étape scene-switch ajoutée précédemment (précédent réel,
    déjà appliqué une fois dans ce dépôt). `ImGuiDebugSystem` implémente `IDisposable` (dispose
    pipeline, SSBO, index buffer, atlas `GpuImage`, contexte ImGui natif — **pas de `ShaderCompiler`
    à disposer, round 5** : les shaders sont des `.spv` précompilés embarqués, plus un compilateur
    runtime) ; c'est ce qu'`IGame.ConfigureDebugTools` retourne.
  - `case Key.F3 when debugOverlay is not null: debugOverlay.Toggle();` (`AppHost.cs:299-300`) est
    supprimé — `debugOverlay` n'a plus d'autre usage dans `AppHost.cs` (confirmé par grep round 2 :
    lignes 59, 145-150, 299-300, 632-635 uniquement, aucune autre dépendance). Le toggle `F3` pour le
    panneau ImGui est câblé par **chaque consommateur qui opte dans `DebugUi`** (Sandbox/TopDown, ou
    Club Architect depuis le round 4), dans son propre délégué `ConfigureDebugTools`, via
    `presentation.Window.KeyPressed`.
  - **Côté in-repo (round 3, mis à jour round 5)** : c'est `SandboxGame.cs`/`TopDownGame.cs` (les
    vrais `IGame`) qui implémentent `ConfigureDebugTools`, pas `Program.cs` (qui ne fait qu'appeler
    `RunClient`). La surcharge est sous **`#if !MASTER`** (round 5 — remplace `#if DEBUG`,
    puisqu'`ImGuiDebugSystem` n'existe pas en `Master`, mais existe bien en `Release` désormais, D9) ;
    **la `ProjectReference` `samples/Sandbox`/`samples/TopDown` → `DebugUi` porte elle-même
    `Condition="'$(Configuration)' != 'Master'"`**, pour qu'aucun `DebugUi.dll` ne soit tiré dans la
    publication AOT du build `Master` (sinon même un no-op `DebugUi.dll` finirait dans le output
    publié, contredisant l'esprit de la garantie D9 même si son contenu est inerte).
  - **Côté externe (round 4, mis à jour round 5, Club Architect)** : même patron, translittéré en
    `PackageReference`. `ClubArchitect.csproj` gagne `<PackageReference Include="Agapanthe.DebugUi"
    Version="<pin>" Condition="'$(Configuration)' != 'Master'" />` (repack requis côté Agapanthe —
    nouveau commit, nouvelle version NBGV — puis bump de version côté Club Architect, patron déjà
    suivi pour `Agapanthe.Platform.App`). `ClubArchitect.csproj` gagne aussi `Master` dans ses propres
    `<Configurations>` (même bloc `Directory.Build.props`-style, répliqué dans ce dépôt séparé).
    `ClubArchitectGame.cs` implémente `ConfigureDebugTools` sous `#if !MASTER`, exactement comme
    `SandboxGame.cs`.
    - **🟠 Risque de divergence de version, à mitiger (round 4, audit)** : au pack, les
      `ProjectReference` internes de `DebugUi` (vers `App`/`Engine.Render`/`Rendering`/`Graphics`/
      `Engine`/`Core`) sont réécrites en dépendances NuGet épinglées à la version NBGV du commit de
      pack. Si `ClubArchitect.csproj` épingle `Agapanthe.DebugUi` à une version différente de sa
      propre `Agapanthe.Platform.App`, son build résout potentiellement **deux versions différentes
      du moteur** (remontée silencieuse, ou `NU1605`). **Mitigation** : une seule propriété MSBuild
      (`<AgapantheVersion>`) pilote toutes les `PackageReference Agapanthe.*` de
      `ClubArchitect.csproj`, jamais des versions écrites en dur indépendamment les unes des autres.
    - **Divergence `Configuration` vs symbole — résolue par construction (round 5)** : la
      préoccupation du round 4 (une configuration personnalisée pourrait désynchroniser `Condition`
      et le `#if`) visait `#if DEBUG`, un symbole **implicite** du SDK sans lien garanti avec le nom
      de la configuration. `MASTER` est désormais un symbole **propre au projet**, posé dans le MÊME
      bloc `PropertyGroup Condition="'$(Configuration)'=='Master'"` que la `Condition` elle-même
      (D9) — les deux sont définis atomiquement ensemble, ils ne peuvent pas diverger sauf
      intervention manuelle explicite (`-p:DefineConstants=MASTER` en ligne de commande, cas hors
      scope comme le reste de D9(b)).
    - **🟡 Restore propre — précondition explicite côté externe aussi** (round 4, audit) : le risque
      D9(a) (un restore IDE non ré-évalué par configuration) touche un consommateur externe au moins
      autant qu'in-repo — un restore Debug suivi d'un build Release sans nouveau restore réutiliserait
      le même `project.assets.json`. Même précondition que D9 : jamais `--no-restore`, jamais de
      config mélangée dans le même `obj/`.
  - **Invariant généralisé (round 4, audit)** : la règle de fond est « le projet qui porte la
    `Condition` et le `#if` doit être compilé par le consommateur final, jamais lui-même packé ». Un
    futur jeu qui scinderait son propre code en lib packée + exe, avec la référence conditionnelle
    posée sur la lib plutôt que sur l'exe final, reproduirait exactement le bug du round 1.
  - Nouveaux gates réflexifs (portée : dépôt Agapanthe uniquement, ne peuvent pas couvrir Club
    Architect) : scan `PackageReference` sur tout le dépôt Agapanthe (seul `DebugUi` porte
    `Hexa.NET.ImGui`, patron `EngineIsHeadlessTests.cs:186-195`). L'allowlist statique existante de
    `App.csproj`/`Platform.App.csproj` (`EngineIsHeadlessTests.cs:65-68`/`73-76`) est déjà **exacte**
    — ajouter `DebugUi` comme `ProjectReference` y ferait déjà échouer les tests existants
    automatiquement.
- **D4 (révisé x2) — Backend Vulkan maison, hybride.** Vertices dans une SSBO ring-buffer par frame
  réimplémentée dans `DebugUi` (`StorageBufferRing<T>` de `Rendering` est `internal`, non
  réutilisable), lues via `gl_VertexIndex` (mirroring `ui.vert`), **avec un vrai index buffer GPU**
  (`BindIndexBuffer`+`DrawIndexed`, layout `ImDrawIdx` direct). Un draw indexé par commande
  `ImDrawCmd`.
  - **🔴 round 1, confirmé nécessaire** : nouvelle méthode `CommandList.SetScissor(int x, int y, uint
    width, uint height)` (scissor seul, pas de re-pose du viewport comme le ferait
    `SetViewportScissorRect`). **Le clamp aux bornes du framebuffer doit vivre dans le backend
    `DebugUi`, PAS dans `CommandList`** (round 2, mineur — `CommandList` ne connaît pas la taille du
    framebuffer) : `ImDrawCmd.ClipRect` peut être négatif ou hors framebuffer (fenêtre partiellement
    hors écran), un `Rect2D`/`Offset2D` négatif est invalide côté Vulkan.
  - **Shaders `imgui.vert`/`imgui.frag` — révisé round 5 (round 3 invalidé par l'audit)** : PAS dans
    le dossier racine `shaders/` (round 2, 🟠8 — shippé tel quel par `Agapanthe.Graphics`'
    `build/Agapanthe.Graphics.targets:11` vers **tout** consommateur du package, y compris Club
    Architect). **La solution round 3 (compiler à la demande via `ShaderCompiler.CreateForBuild()`)
    est invalidée round 5** : `CreateForBuild()` bascule en mode `precompiledOnly: true` dès que
    `DEBUG` n'est PAS défini (`ShaderCompiler.cs:68-72`) — donc en Release **et** en Master. Sur un
    cache miss, `Compile(...)` lève `GraphicsException` (`ShaderCompiler.cs:142-147`). Ce choix
    n'était acceptable qu'au round 3, quand ImGui était strictement Debug-only ; depuis que Release
    garde ImGui (D9/D10), il casse littéralement le rendu en Release.
    **Solution retenue (round 5)** : les SPIR-V `imgui.vert.spv`/`imgui.frag.spv` sont **précompilés
    au build de `Agapanthe.DebugUi` lui-même**, via `tools/ShaderPrecompiler` (même outil que celui
    qui cuit `shaders/` pour le reste du moteur — patron déjà existant, appliqué à un dossier
    supplémentaire propre au projet `DebugUi`), et embarqués en `EmbeddedResource` **déjà compilés**
    (pas le GLSL source). Au runtime, `ImGuiVulkanBackend` lit directement ces `.spv` embarqués et les
    passe à `GraphicsPipeline` — **aucun compilateur shader au runtime, dans aucune configuration**
    (Debug/Release/Master), cohérent avec la posture "Release interdit la compilation runtime" déjà
    verrouillée pour le reste du moteur. Fonctionne aussi pour Club Architect quelle que soit SA
    configuration, puisque le `.spv` est figé dans le `.nupkg` de `DebugUi` au moment du pack (comme
    tout le reste de son contenu — round 4). Plus d'instance `ShaderCompiler` à créer/disposer dans
    `ImGuiDebugSystem` (simplification par rapport au round 3).
  - Fragment shader `srgbToLinear` avant sortie (patron `ui.frag`, swapchain sRGB). `BlendMode.
    AlphaBlend` existe déjà — aucun 2ᵉ changement requis dans `Agapanthe.Graphics`.
  - **`ColorAttachmentBarrier` avant `BeginRendering`** (round 2, mineur — patron `Renderer.cs:1524`)
    : `DrawUi` peut sortir tôt (`Renderer.cs:1510`), auquel cas le tonemap est le dernier écrivain de
    la cible couleur ; sans barrière, la synchronization validation signale un hazard.
  - `io.DeltaTime` alimenté par un **`Stopwatch` wall-clock réel**, pas `RenderContext.Tick`/
    `CurrentTick` (round 2, mineur — c'est le pas fixe de simulation, pas le delta de frame rendue).
  - Rendu après le pass UI existant (`LoadOp=Load`), comme `IRenderSystem` pur.
- **D-cadence — `ImGuiDebugSystem` implémente SEULEMENT `IRenderSystem`, jamais `ISystem`.**
  `Stage.PostSimulation` tourne 0 à N fois par frame rendue (fixed timestep) ; le cycle
  `NewFrame`→widgets→`Render()` doit s'exécuter exactement une fois par frame réellement rendue.
  Confiné entièrement à `IRenderSystem.Render(in RenderContext ctx)`.
- **D5 (révisé x2, 4 nouveaux membres nets sur `IWindow`, pas 5)** :
  - `Vector2 MousePosition { get; }` — position absolue, pixels framebuffer (mise à l'échelle HiDPI
    depuis `IMouse.Position`, en coordonnées fenêtre chez Silk.NET — détail d'implémentation).
  - `bool IsMouseButtonDown(MouseButton button)` — polling, patron `IsKeyDown(Key)`.
  - `Vector2 ScrollDelta { get; }` — accumulé sur la frame, **remis à zéro après `Rendered`, PAS
    après `Updated`** (round 2, 🟠3 — le cycle ImGui tourne dans `Render`/`Rendered`, qui arrive
    APRÈS la remise à zéro post-`Updated` existante pour `MouseDelta` ; réinitialiser au mauvais
    moment aurait rendu `ScrollDelta` systématiquement nul pour ImGui).
  - `event Action<char>? CharInput;`.
  - **PAS de nouveau membre de capture** (round 2, 🟠4, correction du round 1) : `EngineWindow`
    possède déjà `public bool CaptureMouseOnClick { get; set; }` (défaut `true`), consulté dans
    `OnMouseDown` (`EngineWindow.cs:141`/`252`) — inventer un `SuppressCameraCapture` inversé aurait
    créé un doublon sémantiquement confus, en plus de heurter
    `IWindowSurfaceTests.EveryIWindowMember_ExistsOnEngineWindow_ByNameAndShape`
    (`tests/Agapanthe.Tests/IWindowSurfaceTests.cs:17`). **`CaptureMouseOnClick` est promu sur
    `IWindow`** (déjà présent par nom/forme sur `EngineWindow`, donc le test de conformité passe
    sans changement côté implémenteur). `ImGuiDebugSystem.Render` pose
    `window.CaptureMouseOnClick = !io.WantCaptureMouse` chaque frame où il tourne visible, et le
    remet explicitement à `true` quand le panneau est masqué ou disposé (round 2 : sans ça, le flag
    resterait bloqué à `false` et la caméra resterait indéfiniment non-capturable après fermeture du
    panneau).
  - **Arbitrage clavier non traité en v1** (round 2, mineur, assumé) : rien n'empêche les raccourcis
    hôte/recette (F/G/K/Escape) de se déclencher quand ImGui a le focus clavier — acceptable tant
    qu'aucun widget v1 ne consomme de texte (D7 n'a qu'un bouton), documenté explicitement plutôt que
    silencieux.
  - Implémenté dans `EngineWindow` (Platform), `EngineWindowAdapter` (Platform.App — pur forwarding
    vers `EngineWindow`, ne "possède" rien lui-même), **et `ScopedWindow`** (`Agapanthe.App`, 3ᵉ
    implémenteur — gagne une 6ᵉ table de trampolines pour `CharInput`, patron des event trampolines
    existants).
- **D-capture-UI (nouveau, round 2, 🟠6)** — `ImGuiDebugSystem` respecte `sim.Options.OverlayVisible`
  exactement comme `DebugOverlaySystem` le faisait (démarre masqué si `AGAPANTHE_OVERLAY=0`) — sans
  ça, les captures UI déterministes existantes (`03421357`/`b5382ac6`, qui reposent sur l'overlay
  **masqué**) resteraient correctes par accident plutôt que par construction. **Le gate 0-alloc
  officiel reste mesuré overlay masqué** — convention déjà établie depuis UI-2 ("le gate déterministe
  porte sur l'overlay masqué"), qui résout aussi D10 ci-dessous sans mécanisme nouveau.
- **D6 — Arbitrage souris : `ImGuiIO.WantCaptureMouse` fait autorité**, via `IWindow.
  CaptureMouseOnClick` promu (D5) — mécanisme concret, sans doublon.
- **D7 — Panneau interactif dès le lot 1** : fenêtre déplaçable/redimensionnable + bouton "Close".
- **D8 — Contenu v1 = parité avec `DebugOverlaySystem`** (fps/frame-time+graphe, alloc/frame+peak+
  graphe coloré, draws/candidates, timings GPU). Sources de données inchangées.
  - `DebugOverlaySystem` supprimé (0 autre consommateur). `TextBuilder` (`Agapanthe.Ui`) supprimé
    (0 autre consommateur, **aucun test associé n'existe** — round 2 : correction de l'affirmation
    round 1 « + tests », pas de `TextBuilderTests.cs` dans le dépôt).
  - `Sparkline` (`Agapanthe.Ui`) — **PAS 0 consommateur** : `tools/AotComponentProbe/Program.cs:127`
    (`AotProfilerSmoke`) et `SparklineTests.cs` (existe bien, à supprimer explicitement). Suppression
    de `Sparkline` ⇒ `AotProfilerSmoke` réécrit pour exercer `FrameSeries.CopyChronological`
    directement (assertion sur `samples.Length`), sans passer par un rendu de quads.
  - **Breaking change reconnu** (round 2, mineur) : `Sparkline`/`TextBuilder` sont des types publics
    d'un package `Agapanthe.Ui` déjà publié/consommable — leur suppression est un changement cassant
    pour un consommateur externe théorique (aucun connu aujourd'hui, Club Architect ne les référence
    pas). `docs/text-and-ui.md` (qui les documente comme API publique, l.12/111/205-207/295) doit
    être mis à jour dans la même vague.
- **D9 (révisé x4, round 5 — introduction de la configuration `Master`)** — jamais dans le build qui
  part réellement chez les joueurs, **pourvu que chaque consommateur écrive correctement sa propre
  condition**. Plus de garantie structurelle universelle depuis round 4 (elle existait quand
  `DebugUi` était Sandbox/TopDown-only, D3) : un consommateur qui opte dans `DebugUi` porte la
  responsabilité de sa propre condition, exactement comme n'importe quelle dépendance dev-only de
  l'écosystème .NET. Un consommateur qui **n'opte jamais** reste structurellement à l'abri, puisqu'
  `Agapanthe.App` ne référence jamais `DebugUi`.
  - **Changement round 5, demandé explicitement par l'humain** : la porte n'est plus
    `Configuration=='Debug'` mais **l'absence du define `MASTER`** — un build `Release` "interne"
    (QA, profiling, staging) **garde le panneau ImGui**, seul un build `Master` (celui qui sort
    réellement vers les joueurs) l'exclut. Concrètement, **`Master` devient une 3ᵉ `Configuration`
    nommée**, aux côtés de `Debug`/`Release` — le SDK .NET ne lui donne aucun défaut implicite
    (contrairement à `Release`), donc `Directory.Build.props` doit les poser explicitement :
    ```xml
    <PropertyGroup>
      <Configurations>Debug;Release;Master</Configurations>
    </PropertyGroup>

    <!-- Master = codegen Release (Optimize=true) + TRACE (round 5 — le SDK pose DefineConstants=
         RELEASE;TRACE pour Release via une comparaison de chaîne interne à Microsoft.NET.Sdk qui ne
         matche pas "Master" ; sans ce report explicite, Master perdrait TRACE par rapport à Release,
         un défaut implicite jamais posé pour une config custom) + le define MASTER, que
         Agapanthe.DebugUi (et tout futur outillage dev-only) utilise pour s'auto-exclure du SEUL
         build qui part chez les joueurs. Un Release "ordinaire" (QA/staging/profiling) N'A PAS le
         define MASTER et garde ImGui. -->
    <PropertyGroup Condition="'$(Configuration)'=='Master'">
      <DefineConstants>$(DefineConstants);TRACE;MASTER</DefineConstants>
      <Optimize>true</Optimize>
      <DebugType>portable</DebugType>
    </PropertyGroup>
    ```
    `Agapanthe.DebugUi.csproj` : `<PackageReference Include="Hexa.NET.ImGui"
    Condition="'$(Configuration)' != 'Master'" />` — **round 5, correction d'un défaut sérieux
    trouvé par l'audit : la clause `AND '$(PublishAot)' != 'true'` du round précédent est
    supprimée.** Elle était déjà inerte pour un consommateur packagé (D9(b) : `DebugUi` étant packé
    en Debug, sa condition interne est évaluée une fois pour toutes au pack, `PublishAot` de
    l'appelant ne s'y propage jamais) — mais surtout, depuis que Release publie en NativeAOT avec
    Hexa à l'intérieur (voir D2), une build `-p:PublishAot=true` explicite retirerait Hexa tout en
    laissant le code `#if !MASTER` compilé, cassant la compilation. La seule porte doit être
    `!= 'Master'`. Tout code touchant `Hexa.NET.ImGui` passe de `#if DEBUG` à **`#if !MASTER`**
    (disponible en Debug ET Release, exclu seulement en Master). Idem pour la référence de chaque
    consommateur vers `DebugUi` (`Condition="'$(Configuration)' != 'Master'"`) et pour la surcharge
    `ConfigureDebugTools` de chaque `IGame` (`#if !MASTER` au lieu de `#if DEBUG`).
    - **🔴 Risque confirmé par audit round 5 — recensement exhaustif fait, pas renvoyé au spike** :
      introduire `Master` comme configuration **distincte** de `Release` ne fait PAS hériter
      automatiquement les protections déjà posées sur `Release` ailleurs dans ce dépôt. Une seule
      cible réelle existe, présente sur **3 fichiers**, toujours le même stripping
      `shaderc_shared.dll` (`StripShadercFromRelease`, `Condition="'$(Configuration)' ==
      'Release'"`, comparaison de chaîne littérale) : `samples/Sandbox/Sandbox.csproj` (l.180),
      `samples/TopDown/TopDown.csproj` (l.126), `samples/ThinClient/ThinClient.csproj` (l.124).
      Aucune autre condition de configuration n'existe dans les `*.csproj`/`*.props`/`*.targets` du
      dépôt (vérifié par audit). `Master` ne matche aucune des 3 : un build `Master` naïf
      shipperait `shaderc_shared.dll` alors que c'est précisément le build qui ne doit JAMAIS
      l'embarquer. **Correctif retenu, forme la plus simple** : chacune des 3 conditions devient
      `Condition="'$(Configuration)' != 'Debug'"` (plutôt qu'énumérer `Release OR Master` — les deux
      formulations sont équivalentes avec 3 configurations, celle-ci reste correcte si une 4ᵉ
      configuration de production apparaît un jour). Point rassurant confirmé par le même audit :
      les gardes C# `#if DEBUG`/`[Conditional("DEBUG")]` déjà présentes dans `GraphicsDevice`/
      `ResourceTracker`/`ShaderCompiler`/`Renderer`/`Log` sont **héritées automatiquement** sans
      aucun changement — `Master` ne définit pas `DEBUG`, donc ces couches de validation restent
      coupées exactement comme en Release aujourd'hui. Le 🔴 ne concernait que des comparaisons de
      chaîne MSBuild, pas le code C#.
    - **Ramené dans le périmètre du jalon (round 5, audit — refusé comme "hors scope" au tour
      précédent)** : ce dépôt et Club Architect ont jusqu'ici traité `Release` comme "le build qui
      compte" (CA-017 pour FontCooker/StbTrueTypeSharp, D9 pour ImGui aux rounds précédents). Depuis
      .NET 8, `PublishRelease=true` est le défaut du SDK — **un `dotnet publish` sans `-c` explicite
      produit toujours un Release**, jamais un Master. Si la commande canonique de sortie n'est
      documentée nulle part, quelqu'un qui tape `dotnet publish` en pensant shipper obtient un
      Release qui embarque encore ImGui — exactement le risque que toute cette contrainte existe pour
      fermer. **`CLAUDE.md`, section Commandes, gagne donc une ligne explicite** (dans le périmètre
      de ce jalon, pas différé) : `dotnet publish -r <rid> --self-contained -p:PublishAot=true
      -c Master` est la seule commande qui produit le build de sortie réel ; `-c Release` reste un
      palier interne (QA/profiling, ImGui inclus).
  - **Portée réelle de la garde, plus faible qu'elle n'y paraît (round 2, 🟠9, assumé et documenté
    plutôt que fermé)** : le restore NuGet n'est pas ré-évalué par configuration dans un restore
    solution/IDE — un `dotnet build -c Master --no-restore` après un restore Release garderait Hexa
    dans `project.assets.json` et copierait le natif cimgui dans l'output Master. **Mitigation** : la
    vérification (ci-dessous) exige un restore propre (jamais `--no-restore`, jamais un mélange de
    configurations dans le même dossier `obj/`) — documenté comme précondition explicite plutôt que
    fermé par de l'outillage supplémentaire (hors scope de ce spec). (L'ancien point (b), sur la
    clause `PublishAot` de la `PackageReference`, est caduc depuis que cette clause a été retirée —
    D9 ci-dessus — plus rien à documenter à ce sujet.)
  - `IsAotCompatible` non déclaré sur `Agapanthe.DebugUi.csproj` (patron `FontCooker`/
    `Agapanthe.Assets.Pipeline`).
  - **Risque à traiter au spike** : `Sandbox.csproj` a `PublishAot=true` fixé en permanence — les
    analyseurs trim/AOT tournent dès `dotnet build`, en erreur (`TreatWarningsAsErrors=true`), y
    compris en `Release` désormais (puisqu'ImGui y est présent). Référencer `DebugUi` (non
    `IsAotCompatible`) pourrait produire du bruit d'analyseur en Debug **et** en Release. Le spike
    tente d'abord une build nue ; des `NoWarn` (patron `IL2104;IL3000;IL3002;IL3053` déjà présent) ne
    sont ajoutés que si l'analyseur se plaint réellement — codes non devinés à l'avance.
  - **Invariant généralisé (round 4, conservé)** : le projet qui porte la condition d'exclusion et le
    `#if !MASTER` doit être compilé par le consommateur final, jamais lui-même packé — sinon on
    reproduit le figement du round 1 (voir D3, "Côté externe").
  - **🟡 Callout préexistant mais rendu visible par `Master` (round 5, audit)** : ce dépôt pack tout
    (y compris `Agapanthe.App`/`Engine.Render`/etc., pas seulement `DebugUi`) en configuration Debug.
    Club Architect consomme donc des DLL moteur compilées avec `DEBUG` défini, quelle que soit SA
    PROPRE configuration — y compris un build `Master` de Club Architect, qui embarque un moteur où
    la couche de validation Debug-only (`GraphicsDevice`/`ResourceTracker`/`Log`, patron `#if DEBUG`)
    reste active. Ce n'est pas un problème nouveau introduit par ce spec (déjà vrai avant ImGui), donc
    hors scope pour le corriger ici, mais la promesse "Master = build joueur propre" ne vaut pour
    l'instant que pour les projets *in-repo* (Sandbox/TopDown, qui compilent tout depuis les sources
    avec leur propre Configuration) — pas encore pleinement pour un consommateur externe tant que la
    configuration de pack d'Agapanthe n'est pas elle-même revue (differé, backlog).
  - **🟡 À vérifier au spike (round 5, audit)** : `Agapanthe.slnx` ne déclare aujourd'hui aucune
    `<Configurations>` propre — seules `Debug`/`Release` existent au niveau solution. Un
    `dotnet build`/`dotnet test Agapanthe.slnx -c Master` pourrait échouer ou ne rien construire
    correctement tant que la solution elle-même n'a pas été mise à jour
    (`<Configurations><BuildType Name="Debug"/><BuildType Name="Release"/><BuildType
    Name="Master"/></Configurations>` dans `Agapanthe.slnx`) — à confirmer/corriger au spike, pas
    supposé fonctionner par défaut.
  - **Vérification (allégée, manuelle, PAS un test xUnit qui shellerait `dotnet publish` à chaque
    run — round 2 confirme que c'est trop lourd)** : depuis un checkout propre, `dotnet publish -c
    Master` de Sandbox/TopDown **et de Club Architect** (round 4/5), grep du output pour confirmer
    l'absence de `Hexa.NET.ImGui`/`cimgui` — même méthode que la vérification FontCooker/
    StbTrueTypeSharp déjà faite pour Club Architect dans cette session, portée sur `Master` plutôt que
    `Release`. **Vérification additionnelle (round 5)** : `dotnet publish -c Release` (le palier
    QA/staging) DOIT au contraire montrer le panneau ImGui fonctionnel — confirmer que le changement
    de garde n'a pas accidentellement exclu ImGui de Release aussi. ThinClient/HeadlessSim restent
    structurellement hors de portée (ils n'ont jamais de raison d'opter dans `DebugUi`).
- **D10 (clarifié round 2, portée corrigée round 5)** — Gate 0-alloc n/a en `Master` (le code n'y
  existe pas). En **Debug et Release** (round 5 — les deux configurations où `DebugUi` est
  maintenant présent), le code touchant Hexa.NET.ImGui est toléré non-0-alloc — **mais cette
  tolérance ne doit jamais fausser le gate affiché à l'écran** : le bracket d'allocation existant
  (`FrameOrchestrator`, Tick→`EndFrame`) couvre `DrawFrame`, donc toute allocation ImGui apparaîtrait
  dans le compteur "alloc/frame" que le panneau lui-même affiche — auto-contamination. **Résolu sans
  mécanisme nouveau** : le gate 0-alloc officiel de ce projet est déjà, depuis UI-2, mesuré **overlay
  masqué** (`AGAPANTHE_OVERLAY=0` / panneau fermé) — convention préexistante, pas une invention de ce
  spec, et qui s'applique désormais aussi aux builds `Release` (pas seulement Debug). Documenté
  explicitement pour que ce ne soit pas redécouvert par surprise : un nombre affiché **panneau
  ouvert** n'est une preuve du gate dans AUCUNE configuration où `DebugUi` est présent.
- **D11 — Cross-platform : Windows-first**, cohérent avec la posture existante (P3-M0 toujours dû),
  sans impact sur le build `Master` (ImGui n'y a aucune empreinte).

## Fichiers touchés (représentatif)

- Nouveau `src/Agapanthe.DebugUi/` (`IsPackable=true`, round 4) : `ImGuiDebugSystem.cs` (`IRenderSystem` +
  `IDisposable`, PAS `ISystem`), `ImGuiVulkanBackend.cs` (SSBO vertices + index buffer + upload
  atlas), `Shaders/imgui.vert`/`imgui.frag` + leurs `.spv` **précompilés au build via
  `tools/ShaderPrecompiler`** (round 5 — remplace la compilation à la demande du round 3, invalidée
  par l'audit), embarqués en `EmbeddedResource` déjà compilés, `Agapanthe.DebugUi.csproj`.
- `src/Agapanthe.Graphics/CommandList.cs` — nouvelle méthode `SetScissor(int, int, uint, uint)`.
- `src/Agapanthe.App/IWindow.cs` — 4 nouveaux membres (`MousePosition`, `IsMouseButtonDown`,
  `ScrollDelta`, `CharInput`) + promotion de `CaptureMouseOnClick` (déjà réel sur `EngineWindow`,
  déclaré sur l'interface).
- `src/Agapanthe.App/IGame.cs` — nouveau DIM `ConfigureDebugTools`.
- `src/Agapanthe.App/AppHost.cs` — invoque le hook aux deux sites réels (`l.183`/`l.653`, après
  `recipe.Build`), gère le local hissé `debugToolsHandle` (dispose+remise à `null` juste après
  `frameRenderer.WaitIdle` au switch, nouvelle étape nommée `TeardownTargets.DisposeDebugTools` au
  même point au teardown final) ; supprime tout le câblage `debugOverlay`/`DebugOverlaySystem`
  (construction, `case Key.F3` `l.299-300`, ré-enregistrement post-switch `l.632-635`).
- `src/Agapanthe.App/ScopedWindow.cs`, `src/Agapanthe.Platform/EngineWindow.cs`,
  `src/Agapanthe.Platform.App/EngineWindowAdapter.cs` — implémentent D5 (promotion + 4 nouveaux
  membres) ; `EngineWindow.OnMouseDown` inchangé dans sa logique (`CaptureMouseOnClick` existe déjà).
- `Directory.Build.props` (round 5) — `<Configurations>Debug;Release;Master</Configurations>` +
  `PropertyGroup Condition="'$(Configuration)'=='Master'"` (`DefineConstants += TRACE;MASTER`).
- `Agapanthe.slnx` (round 5) — probable ajout de `Master` aux `<Configurations>` de la solution,
  à confirmer/corriger au spike (item 1).
- **(round 5)** Les 3 sites `StripShadercFromRelease` élargis de `Configuration=='Release'` à
  `Configuration != 'Debug'` : `samples/Sandbox/Sandbox.csproj:180`,
  `samples/TopDown/TopDown.csproj:126`, `samples/ThinClient/ThinClient.csproj:124`.
- **(round 5)** `CLAUDE.md`, section Commandes — nouvelle ligne documentant
  `dotnet publish -c Master` comme la commande de sortie réelle (ramené dans le périmètre du
  jalon, D9).
- `samples/Sandbox/SandboxGame.cs`, `samples/TopDown/TopDownGame.cs` (correction round 3 — pas
  `Program.cs`) — `ProjectReference` **conditionnelle `!= 'Master'`** vers `DebugUi`, implémentent
  `IGame.ConfigureDebugTools` sous `#if !MASTER` (construction `ImGuiDebugSystem`, câblage `F3` via
  `presentation.Window.KeyPressed`, respect `sim.Options.OverlayVisible`, retour de l'instance comme
  `IDisposable`).
- `tests/Agapanthe.Tests/AppHostContractTests.cs:340` (`BuildTeardown_LabelsAreInStrictM4Order`) —
  mis à jour avec le nouveau label de teardown.
- **(round 4/5, dépôt `club-architect`, séparé)** : `Directory.Build.props` (ou équivalent) gagne
  aussi `Master`. `ClubArchitect.csproj` gagne une `PackageReference` conditionnelle
  `!= 'Master'` vers `Agapanthe.DebugUi` ; `ClubArchitectGame.cs` implémente `ConfigureDebugTools`
  sous `#if !MASTER`.
- **(round 4)** `docs/plans/2026-09-27-club-architect-package-separation-design.md` (D5/D6) —
  `Agapanthe.DebugUi` devient le 14ᵉ projet packé, à ajouter à la liste existante ("13 projets
  packés").
- Supprimés : `src/Agapanthe.Engine.Render/DebugOverlaySystem.cs`, `src/Agapanthe.Ui/TextBuilder.cs`,
  `src/Agapanthe.Ui/Sparkline.cs` + `SparklineTests.cs` (après réécriture d'`AotComponentProbe`).
  `docs/text-and-ui.md` mis à jour (retrait des sections `Sparkline`/`TextBuilder`).
- Nouveau gate réflexif dans `tests/Agapanthe.Tests/` : scan `PackageReference` (seul `DebugUi` porte
  Hexa.NET.ImGui). Pas de nouveau test pour "App ↛ DebugUi" — déjà couvert par l'allowlist statique
  exacte existante.

## Verification

1. Spike (gate avant tout le reste) : (a) `Agapanthe.DebugUi` en Debug/JIT crée un contexte ImGui,
   `NewFrame`+`ImGui.Text(...)`+`Render()` produit un `ImDrawData` non vide ; (b) `Sandbox.csproj`
   (`PublishAot=true` fixé + `TreatWarningsAsErrors=true`) compile en Debug **et en Release** (round
   5 — les deux configs où `DebugUi` est référencé) avec la nouvelle `ProjectReference` sans erreur
   d'analyseur AOT/trim — sinon documenter les `NoWarn` réellement nécessaires, empiriquement ;
   (c) **nouveau, round 5, gate avant tout le reste au même titre que (a)/(b)** : `dotnet publish -c
   Release -r win-x64 --self-contained -p:PublishAot=true` de Sandbox réussit réellement avec Hexa
   présent (trim, P/Invoke, chargement du natif cimgui au runtime) — jamais vérifié avant ce round,
   supposé à tort "sans conséquence" par une version antérieure de D2.
2. (Remplacé par l'item 6 — vérifiait `-c Release`, contredisait le nouveau design où Release garde
   ImGui délibérément. Voir item 6 pour la vérification d'exclusion, désormais portée sur `Master`.)
3. Live run Debug (Sandbox) : F3 affiche le panneau, déplaçable/redimensionnable, bouton "Close" le
   ferme, clic dans le panneau ne déclenche pas la capture caméra FPS (D5/D6), clic en dehors la
   déclenche normalement, capture caméra relâchée si on ferme le panneau pendant qu'elle était
   suspendue. **Scene-switch réel sur Sandbox uniquement** (correction round 3 : `TopDownGame` n'a
   qu'une seule scène, `Key.K`/`StartLoad` de Sandbox visent `"grid"`/`"model"` — le test de switch
   n'a de sens que sur Sandbox) : l'ancienne instance `ImGuiDebugSystem` est bien disposée (0 leak
   `ResourceTracker`), la nouvelle fonctionne. 0 leak / 0 message de validation au shutdown normal
   aussi.
4. Contenu parité : fps/alloc/draws/GPU timings identiques aux données de `DebugOverlaySystem`
   (comparaison visuelle avant suppression).
5. `TextBuilder`/`DebugOverlaySystem`/`Sparkline`/`SparklineTests.cs` supprimés (0 référence
   restante), `AotComponentProbe` réécrit et re-vérifié JIT==AOT, `docs/text-and-ui.md` à jour.
6. Build **Master** Sandbox/TopDown/ClubArchitect (round 5 — remplace l'ancienne vérification
   "Release") : 0 warning, 0 erreur, gate 0-alloc inchangé (n/a en Master, D10 — le code `DebugUi`
   n'y existe pas), aucune régression sur les 9 captures pinnées. Club Architect gagne une dépendance NuGet
   conditionnelle `!= 'Master'` (round 4/5 — repack Agapanthe + bump de version, documenté comme un
   changement délibéré, pas silencieux) et **perd F3 uniquement en Master** — s'il n'ajoute pas la
   nouvelle dépendance, il perd F3 dans toutes les configurations.
6bis. Build **Release** Sandbox/TopDown/ClubArchitect (round 5, nouveau) : le panneau ImGui DOIT être
   présent et fonctionnel (0 warning, 0 erreur) — confirme que Release reste un palier QA/profiling
   avec l'outillage debug disponible, distinct de `Master`.
7. `AGAPANTHE_OVERLAY=0` sur Sandbox/TopDown/Club Architect (s'il a opté dans `DebugUi`) : le panneau
   ImGui démarre masqué, comme `DebugOverlaySystem` le faisait (D-capture-UI).
8. **Live run Club Architect, Debug et Release (round 4/5)** : repack Agapanthe + bump de version
   dans `ClubArchitect.csproj` + `dotnet restore` réussi, F3 affiche le panneau exactement comme sur
   Sandbox dans les deux configurations, 0 leak / 0 message de validation. Comme
   `ClubArchitect.csproj` a lui aussi `PublishAot` fixé en permanence, la même vérification qu'à
   l'item 1(b)/(c) s'applique : build Debug **et** publish Release AOT de Club Architect sans erreur
   d'analyseur AOT/trim ni échec runtime (`NoWarn` empiriques si nécessaire).

## Deferred / Out of scope

Inspecteur d'entités / arbre de scène. Docking/multi-fenêtre ImGui. IME/texte Unicode complexe
(arbitrage clavier ImGui vs raccourcis hôte, D5). Persistance `imgui.ini`. Validation Linux/macOS
réelle d'ImGui (suit P3-M0). Migration du splash/menu Club Architect vers ImGui (D1). Fermeture
outillée du risque de restore non-propre (D9, aujourd'hui une précondition documentée par
consommateur, pas un mécanisme gaté).
