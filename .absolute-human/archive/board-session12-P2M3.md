# Absolute-Human Board — Agapanthe Session 12 (P2-M3 : Camera-relative rendering)

**Status**: CLOSED (2026-07-13) — P2-M3 PASSÉ, double audit PASS conditionnel. Voir « CLÔTURE » en bas.
**But** : **le monde ne tremble plus à 10 000 km.** Le GPU ne sait faire que du `float32` (ULP ≈ **1 m** à 1e7 m) ⇒ on garde le monde en `double` et on met **la caméra à l'origine** : le GPU ne voit que `objet − caméra`, toujours petit. On commence par les **2 dettes rouges** de P2-M2 (handles/registry) : elles touchent des types **publics** et M3 va bâtir dessus.
**Créé**: 2026-07-13
**Spec**: docs/plans/2026-07-12-phase2-foundations-design.md §3.3 (camera-relative — table d'impact CPU/shaders), §3.6 (Double3), §5 (preuve de précision), §6 P2-M3 (critère).
**Board persistence**: git-tracked
**Sessions passées**: S1-S11 → .absolute-human/archive/ (S11 = board-session11-P2M2.md).

## Intake (spec §3.3 — actée)

- **Principe** : la caméra est **toujours à l'origine**. `objet_monde − caméra_monde` calculé **en `double` sur le CPU**, dans la boucle qui construit les listes de rendu (celle que P2-M2 vient de livrer — c'est cet ordre qui rend M3 bon marché).
- **Les shaders bougent peu** : `mesh.vert` **inchangé** (il ne lit jamais `camera.position` — seule la *sémantique* de `push.model` change) · `mesh.frag` : `camera.position` = `vec3(0)` → **`V = normalize(-worldPos)`** (plus simple qu'avant) · `skybox.vert`, IBL, tonemap **intacts** (directions seulement).
- **C'est le CPU qui casse**, pas le GPU. 5 sites lisent des positions monde **absolues en float** :

| Site | Correction |
|---|---|
| `Renderer.ComputeLightViewProj` | **Fitte sur le frustum CAMÉRA**, plus sur les bounds monde (§3.5) — *obligatoire dès M3* : les bounds absolues en float cassent à 1e7 m |
| **Stockage** des lumières ponctuelles (`Lights.cs`, réécrites chaque frame) | Stockées en **`Double3`**, converties en **relatif-caméra À CHAQUE FRAME**. ⚠️ **Corriger `SetupLights` ne suffit PAS** : sans conversion par frame, elles **dérivent dès que la caméra bouge** |
| `Program.SetupLights` / `FrameCamera` | Consomment les bounds `Double3` |
| `FreeCameraController` | Arithmétique en **`Double3`** — sinon on reperd la précision juste après l'avoir gagnée |
| `Camera.Position` / `Camera.ViewMatrix` | → `Double3` ; view **rotation seule** (translation nulle par construction) |

- `CameraUniforms.Position` **reste dans l'UBO** (toujours 0) : l'identité du bloc doit rester byte-identique entre `mesh.vert` et `mesh.frag`.

## Critère de sortie (spec §6 P2-M3, §5)

**Rendu à 10 000 km == rendu à l'origine** :
- **unitaire** : les transforms relatifs caméra sont **exactement égaux** (comparaison **bit-à-bit**) ;
- **capture GPU** : **≤ 1 LSB par canal** — ⚠️ **le byte-identique strict n'est PLUS le critère** (la reconstruction `double→float` ne retombe pas nécessairement sur les mêmes bits ; exiger l'identité binaire serait un critère **faux**). C'est **assumé**, pas subi.
- Gate permanent : 0 validation, 0 leak, publish AOT OK, tests verts.

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, **AOT-pur**, **aucun Vk* hors Graphics**, **aucun type Arch hors World**, IDisposable + DeletionQueue N+2, **zéro alloc/frame**, ResourceTracker (leak = échec), 0 message de validation.
- Baseline M8/M2 : `24001B240C6C6B956F3F1AC6ABC1FE1D2CAA8914D9013169DFB049027E3309B7` (référence **jusqu'à W1** ; ensuite le critère devient ≤ 1 LSB).
- Publish AOT : PATH doit inclure `C:\Program Files (x86)\Microsoft Visual Studio\Installer` (vswhere).

## Rollback Point

`e0c8ffc` (P2-M2 clos, branche phase2-foundations).

## Task Graph

```
W0 (dettes rouges) ──► W1 (origine + RenderView) ──► W2 (lumières) ──► W3 (fit ombre frustum) ──► W4 (caméra) ──► W5 (preuve + audits)
```
**L'ordre est un contrat** : W0 d'abord (types publics, M3 bâtit dessus) ; W3 (fit ombre) **doit** précéder toute montée en distance, sinon la matrice d'ombre est le prochain site à casser.

## Waves

| Vague | Contenu | Critère de vérification |
|---|---|---|
| **W0** 🔴 | **Dettes rouges P2-M2** : handles **avec génération** (index+generation) + **`ResourceRegistry` global** (slot-map free-list, plusieurs modèles). | **Capture reste byte-identique `24001B24…`** (refactor pur) ; handle périmé/hors-plage → `GraphicsException` (test) ; 0 leak. |
| **W1** | `Double3 Camera.Position` + view **rotation seule** + agrégat **`RenderView`** (Core) portant **l'origine camera-relative** (source unique — lumières/caméra/monde doivent soustraire *exactement* la même) + soustraction dans `CollectRenderLists`. | Rendu **à l'origine** inchangé (≤ 1 LSB) ; unitaire : transform relatif **bit-exact** à 1e7 m. |
| **W2** | Lumières : `Double3` dans `SceneLights` + **conversion relatif-caméra par frame** (le piège §3.3) ; `mesh.frag` `V = normalize(-worldPos)` ; `CameraUniforms.Position` = 0. | Lumières **ne dérivent pas** quand la caméra bouge (test + capture) ; hot reload shaders OK. |
| **W3** | `ComputeLightViewProj` **fitte sur le frustum caméra** (sphère englobante, distance d'ombre max) — plus sur les bounds monde. | Ombres correctes à l'origine **et** à 1e7 m ; pas de pop. |
| **W4** | `FreeCameraController` en `Double3` (intégration du déplacement). | Déplacement à 1e7 m sans saccade/perte de précision. |
| **W5** | **Preuve de précision** (§5) : capture **à 1e7 m** vs **à l'origine** ≤ 1 LSB/canal + unitaire bit-à-bit · publish AOT · double audit · archive. | **Critère de sortie du jalon.** |

## Tâches

### P2-M3-00 — 🔴 Dettes rouges : handles avec génération + registry global [code, L] — todo — OWNER Core/Handles.cs, Rendering/ResourceRegistry.cs, SceneBuilder.cs
**Pourquoi maintenant** (audit P2-M2, 2 findings MAJEURS) : (1) `MeshHandle(int)` est un **index nu** → après un unload/reload, un handle périmé résout **silencieusement une autre ressource** (contredit §3.2 « handle déchargé = erreur ») ; (2) le registry est **par modèle** → `MeshHandle(0)` de 2 modèles **se collisionnent** → **bloquant pour les 10k entités de M4**. **C'est le même changement** : slot-map global (free-list + génération). Types **publics** → le coût ne fera que monter.
**AC** : `Resolve` d'un handle périmé (slot recyclé) ou hors-plage → `GraphicsException` explicite (tests) · 2 modèles chargés simultanément sans collision de handles (test) · **capture byte-identique `24001B24…`** (c'est encore un refactor pur) · 0 leak.

### P2-M3-01 — `RenderView` + origine + `Double3 Camera.Position` [code, M] — todo [dep 00]
Agrégat `RenderView` (Core) : `{ Double3 Origin, RenderList Render, RenderList ShadowCasters, … }` — **une seule source pour l'origine** (audit : « le point le plus facile à désynchroniser en M3 »). `Camera.Position` → `Double3` ; `ViewMatrix` **rotation seule**. `CollectRenderLists` soustrait l'origine (`Double3.ToVector3(origin)`). Résorbe aussi les 8 paramètres de `DrawScene`.

### P2-M3-02 — Lumières en `Double3` + conversion par frame [code, M] — todo [dep 01]
`SceneLights`/`PointLight` stockent en `Double3` ; `Renderer` les convertit en **relatif-caméra à chaque frame** dans `LightsUniforms` (⚠️ le piège explicite de la spec : corriger `SetupLights` seul = **dérive** dès que la caméra bouge). `mesh.frag` : `V = normalize(-worldPos)` ; `CameraUniforms.Position` = 0 (bloc conservé).

### P2-M3-03 — Fit d'ombre sur le frustum caméra [code, M] — todo [dep 01]
`ComputeLightViewProj` : sphère englobante du **frustum caméra** (bornée par une distance d'ombre max), **plus** les bounds monde. Prérequis du CSM (hors portée). ⚠️ **Doit précéder la bascule `Bounds` locale de M4** (sinon la boîte plus lâche déplace silencieusement la matrice d'ombre).

### P2-M3-04 — `FreeCameraController` en `Double3` [code, S] — todo [dep 01]
Intégration du déplacement en `double` — sinon la précision gagnée est reperdue à la 1re frame de mouvement.

### P2-M3-05 — Preuve de précision + audits + archive [test, M] — todo [dep 02,03,04]
`AGAPANTHE_ORIGIN="1e7,0,0"` (nouveau ?) ou fixture : capture **à 10 000 km** vs **à l'origine** → **≤ 1 LSB/canal** ; unitaire : transforms relatifs **bit-à-bit égaux**. Publish AOT. Double audit (`csharp-lowlevel` : zéro-alloc/précision/AOT ; `engine-architect` : RenderView, origine unique, prêt pour M4). Archive → board-session12-P2M3.md.

## Dette (rappel P2-M2 — traitée ou à traiter)

- 🔴 handles sans génération + registry mono-modèle → **c'est W0 de ce jalon**.
- 🔴 **tie-break du tri en M4** : quand `SortKey` portera matériau/profondeur, les ex æquo suivront l'itération Arch (non déterministe) → déterminisme perdu **silencieusement**. Clé 64-bit doit inclure un **tie-break stable** (`RenderOrder`/`GlobalId`).
- 🟠 propagation O(n·d) · `Parent` pendant (dès la destruction d'entités → **M3 si on l'ajoute**) · `SortByKey` O(n²) → radix M4.
- 🟡 `AggregateBounds` = requête à la demande (pas un système/frame) · gates CI (byte-identique auto, IL3053) · zéro-alloc à re-mesurer si le monde bouge · `AotRootingSmoke` piloté par `All` avant tout nouveau composant.
- AOT prouvé Windows-only → Linux/macOS.

## Log

- 2026-07-13: **Session 12 ouverte — P2-M3 (camera-relative).** Ordre acté : **W0 = les 2 dettes rouges** (handles+registry, types publics), puis origine/RenderView → lumières → fit ombre frustum → caméra → preuve. Critère : **≤ 1 LSB/canal** à 1e7 m (le byte-identique strict cesse d'être le critère après W1 — assumé, pas subi). En attente feu vert pour W0.

---

## CLÔTURE — P2-M3 PASSÉ (2026-07-13)

**Status**: CLOSED. Double audit **PASS conditionnel** (`csharp-lowlevel` + `engine-architect`), condition = tâche 1 de M4 (scène large, cf. dette ci-dessous).

**Résultat central** : capture headless à **10 000 km IDENTIQUE bit-pour-bit** à celle prise à l'origine (0 canal sur 2 764 800). Le critère « ≤ 1 LSB » est donc **dépassé** : on a l'égalité exacte. Anti-faux-positif : le log affiche `eye at 9999999.99751842`, valeur qu'un `float` **ne peut pas représenter** (ULP = 1 m à 1e7), ce qui prouve que l'origine est réellement appliquée.

**Commits** : `0d3d0ae` (W0 dettes rouges) · `8ef912c` (W1+W2 camera-relative, lumières) · `0d3670a` (W3 fit ombre frustum) · `62df5ea` (W4 caméra) · `fc1d876` (durcissements audits).

**Écarts au plan, assumés** :
- **W2 absorbé par W1** : impossible de différer les lumières. Dès que les meshes passent en camera-relative, des lumières ponctuelles en float absolu éclairent à côté (le shader compare des positions relatives à des positions absolues).
- **Byte-identique vs M8 perdu**, comme prévu (la translation sort de la matrice de vue → l'ordre des opérations flottantes change). 0,14 % des canaux, dont 14 pixels > 1 LSB, tous sur le bord d'ombre (bascule d'un tap de PCF).
- **Chemin « fit frustum » non exercé par une capture** : la scène du casque emprunte toujours le chemin « scène » (plus petit). Couvert par 9 tests unitaires. → **condition de l'architecte**.

**Gates finaux** : 257 tests · 0 warning · 0 message de validation · **0 leak** (135 ressources ; et **842 créées ET détruites sur 20 cycles Load/Unload**) · probe NativeAOT PASS (8 composants rootés).

**Ce que les audits ont trouvé (corrigé dans `fc1d876`)** :
- 🔴 le **snap texel anti-shimmer était un no-op** : il quantifiait le centre du frustum *relatif à la caméra*, or la vue est rotation-seule → ce centre ne bouge jamais en translation. La shadow map glissait en continu. Grille désormais ancrée au **monde** ; seule la **phase** transite par les coordonnées absolues (en `double`) — router le vecteur lui-même par la rotation float amplifiait son erreur ~1e-7 en **mètres** à 1e7 m (3,65 texels, mesurés).
- 🔴 **casters en amont clippés** (marge de seulement 0,5·r) → ombres qui disparaissent sans erreur. Plage de profondeur mesurée sur l'extension réelle du monde.
- 🔴 **`Unload` fuyait un descriptor set par matériau, définitivement** (un `DescriptorAllocator` ne libère jamais un set isolé) → la boucle de streaming, raison d'être de la registry, grossissait la mémoire descripteur sans borne, et **le gate « 0 leak » passait en mentant** (il compte les pools, pas les sets). Allocateur **par modèle**. `Unload` n'avait **aucun appelant** → désormais exercé sous le gate réel (`AGAPANTHE_UNLOAD_TEST=N`).
- 🟠 `Load` sans `try/catch` autour du minting (ressources GPU orphelines) · pas de garde `_disposed` sur `Resolve` (use-after-free) · invariant « `WorldTransform` sans translation » non gardé · œil câblé en dur à `Vector3.Zero`.

## Dette léguée à P2-M4 / P2-M5 (issue des audits)

**Bloquant, tâche 1 de M4** :
- 🔴 **Scène large de test** (ex. `AGAPANTHE_SCENE=grid:32x32`, le même mesh instancié — gratuit maintenant que les handles sont globaux). Sans elle, le chemin « fit frustum » reste du code non exercé, et le banc de montée en charge n'existe pas.

**Décisions de conception à prendre AVANT d'écrire la boucle de culling** :
- 🔴 **`Bounds` → sphère locale** (`Vector3 Center` + `float Radius`, 16 o au lieu de 48). L'AABB **monde statique** actuelle est fausse dès qu'une entité tourne ou bouge ; transformer une AABB par frame donne une boîte gonflée. Une sphère est invariante en rotation et se teste en 6 dots.
- 🔴 **Ordre de la frame inversé** : `ShadowFit` tourne aujourd'hui *dans* `DrawScene`, donc **après** `CollectRenderLists` — or les casters doivent être cullés contre le **volume de lumière**, qui n'existe pas encore à ce moment. Hisser `ShadowFit` avant la collecte ; introduire un type `Frustum` (6 plans) **dans Core** (c'est le World qui l'utilise).
- 🔴 **Tri** : `SortKey` (sémantique) et l'algo (insertion O(n²) → radix LSD) sont **le même changement** — ne pas les séparer en deux vagues. Trier des paires `(clé, index)` puis gather, pas des `RenderItem` de 88 o. Tie-break stable obligatoire (`RenderOrder`/`GlobalId`), sinon le déterminisme part silencieusement.
- 🟠 **Ne pas faire référencer `World` par `Rendering`** au moment du culling (interface Core, ou orchestration par le Sandbox). C'est la seule frontière que M4 peut détruire.
- 🟠 `AggregateBounds` plié une fois au chargement : dès que le monde bouge, soit il est faux, soit c'est un reduce O(n)/frame **entièrement gaspillé** (dans un grand monde la sphère scène est toujours plus grande que le frustum). Dirty-flag.

**Arbitrage à trancher (engage la Phase 3)** :
- 🟠 **Origine continue (actuelle) vs origine quantifiée** (snap sur une grille de 256 m / 1 km). L'origine continue interdit de fait tout buffer d'instances persistant / culling GPU-driven, et **la physique de Phase 3 exigera une origine par palier** (caches de contact, warm starting, sleeping). Coût du changement **aujourd'hui : ~5 lignes** ; il croît avec chaque consommateur. Si on garde le continu, l'inscrire comme dette **assumée**, pas implicite.

**Plus tard (M5 / Phase 3)** :
- 🟡 `AssertOwnerThread` est `[Conditional("DEBUG")]` → le contrat mono-thread n'est plus vérifié en Release, la config où tournera le job system.
- 🟡 `_models` de la registry ne recycle pas ses ids · `FitSceneSphere` utilise la demi-diagonale (sur-dimensionne jusqu'à ×√3 sur une scène plate — un terrain).
- 🟡 **Crash au shutdown (M8-14) désormais REPRODUCTIBLE** : `AGAPANTHE_UNLOAD_TEST=20` le déclenche ~2 runs sur 10 (0/5 sans). `0xC0000005` dans `Silk.NET.Input` → GLFW (`GetDelegateForFunctionPointer`), **après** le rapport de leak propre. Les cycles ne le causent pas : ils augmentent la pression GC, donc sa probabilité. Une dette « non reproductible » vient de gagner une recette de repro.
- 🟡 AOT/SPIR-V hors-ligne toujours **prouvés Windows uniquement** → Linux/macOS (dette P2-M1).

## Log (suite)

- 2026-07-13: **P2-M3 CLOS.** W0→W4 livrés + durcissements des 2 audits. Preuve : 10 000 km == origine, **bit-pour-bit**. Vérif visuelle humaine (feel caméra après wrap du yaw, ombre) **encore due**.
