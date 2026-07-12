# Protocole de validation M8 — hot reload, labels RenderDoc, souris, multi-OS (spec §3.6, §4, §6)

**Statut : partie 1 (hot reload live) PASS — revue humaine 2026-07-12, Windows / RTX 5070 Ti.**
Restent à dérouler : partie 2 (RenderDoc), partie 3 (souris), partie 4b (**Linux — pas de machine disponible**).

## Résultat de la session humaine du 2026-07-12 (partie 1)

Session réelle, fenêtre ouverte, édition de `mesh.frag` dans l'éditeur. Extrait de log :

```
ShaderHotReloader: watching 7 shader file(s) across 1 directory(ies) for hot reload.
Sandbox: ... Hot reload actif sur 'D:\MyProjects\agapanthe\shaders'.
Renderer: hot-reloaded ScenePass in 224.0 ms   ← 1re fois (cache froid)
Renderer: hot-reloaded ScenePass in 2.7 ms     ← ensuite (cache chaud)
[ERROR] ScenePass: shader recompilation failed, keeping the previous pipeline.
        mesh.frag:317: error: '' : syntax error, unexpected RIGHT_BRACE, expecting COMMA or SEMICOLON
Renderer: hot-reloaded ScenePass in 1.9 ms     ← récupération après correction
ResourceTracker: no leaks (157 resources created and destroyed).
```

| Critère | Verdict |
|---|---|
| Dossier **source** surveillé (pas `bin/`) | ✅ `D:\MyProjects\agapanthe\shaders`, 7 fichiers |
| Reload live sans relancer l'app | ✅ |
| **< 1 s** (critère de sortie §6) | ✅ **224 ms au pire** (1re compile, cache froid) — **~2 ms** ensuite |
| Réversible (annuler l'édition → retour normal) | ✅ |
| **Échec de compile : app ne crashe pas, rendu conservé, erreur shaderc précise** (§4) | ✅ |
| Récupération après correction | ✅ |
| 0 message de validation layer | ✅ |
| 0 leak malgré 4 reloads | ✅ 157 ressources créées/détruites |
| Resize de fenêtre pendant la session | ✅ swapchain recréée (958×1000) sans incident |
| Crash intermittent M8-14 | ✅ non reproduit |

### 🐛 Bug trouvé PAR ce protocole — corrigé le 2026-07-12

Le log montrait, **à la même milliseconde** :

```
[ERROR] ScenePass: shader recompilation failed, keeping the previous pipeline. ...
[INFO ] Renderer: hot-reloaded ScenePass in 6.5 ms (recompile + pipeline recreate).
```

Le *comportement* était correct (l'ancien pipeline était bien conservé), mais `IReloadablePipeline.Reload`
était `void` et avalait l'exception en interne → l'appelant ne pouvait pas distinguer succès et échec, et
loggait « hot-reloaded » **inconditionnellement**. Un log de debug qui ment sur ce qui s'est passé.

**Fix** : `Reload` retourne désormais `bool` (`false` = édition invalide, pipeline précédent conservé) ; le
Renderer ne logge le wall-time que sur un swap réellement effectué (les deux appelants : `PollShaderReload`
et `ReloadAllForTest`). Vérifié : 0 warning, 205 tests verts, capture byte-identique, 0 leak.

---

Dernier jalon de la Phase 1. Contrairement aux protocoles M5/M7 (purement visuels), M8 valide surtout du
**comportement** : le rendu ne doit *pas* changer (capture byte-identique à M7), c'est l'**outillage** qui est
nouveau. Les parties 1 et 4 ne sont pas automatisables — elles exigent une fenêtre, un éditeur et RenderDoc.

## Ce qui est déjà prouvé sans humain (ne pas refaire)

| Gate | Résultat (Windows / RTX 5070 Ti, 2026-07-12) |
|---|---|
| Build | 0 warning (TreatWarningsAsErrors) |
| Tests | 205 xUnit verts |
| Sandbox headless | 0 message de validation, 0 leak (136 ressources) |
| Non-régression rendu | Capture SHA-256 `24001B24…E3309B7` — **byte-identique à la baseline M7** |
| Reload (chemin forcé) | 0 leak (148 ressources), 1.0–3.5 ms par passe |

Le chiffre « < 1 s » est donc **déjà tenu sur le chemin mesuré**. Ce que l'humain valide ici, c'est la boucle
**complète et réelle** : sauvegarde dans l'éditeur → watcher → recompile → nouveau pipeline → pixels à l'écran.

---

## 1. Hot reload live — LE critère de sortie (spec §6)

**Commande** (Windows, machine courante) :

```powershell
dotnet run --project samples/Sandbox
```

*(macOS : `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox`)*

Au démarrage, la console doit afficher :

```
ShaderHotReloader: watching 7 shader file(s) across 1 directory(ies) for hot reload.
Sandbox: ... Hot reload actif sur 'D:\MyProjects\agapanthe\shaders'.
```

> ⚠️ Le dossier surveillé doit être le dossier **source** (`<repo>\shaders`), **pas** `bin\...\shaders`.
> Si la console affiche un chemin sous `bin\`, le hot reload est inerte → échec.

### 1a. Reload nominal (le test qui compte)

Laisse le Sandbox tourner, et **dans un autre éditeur** ouvre `shaders/mesh.frag`. Ligne **316** :

```glsl
    outColor = vec4(color, base.a);
```

Remplace-la par une teinte franche, impossible à confondre :

```glsl
    outColor = vec4(color * vec3(1.0, 0.2, 0.2), base.a);   // ROUGE — test hot reload
```

**Sauvegarde** et regarde la fenêtre **sans y toucher**.

| Critère | Attendu | OK ? |
|---|---|---|
| Le casque vire au rouge **sans relancer l'app** | La fenêtre se met à jour toute seule | ☐ |
| Délai perçu | **< 1 s** entre le Ctrl+S et le changement à l'écran (critère de sortie §6) | ☐ |
| Log console | `Renderer: ScenePass reloaded in XX ms (recompile + pipeline recreate)` | ☐ |
| Pas de freeze | Aucun à-coup / stutter visible au moment du swap | ☐ |
| Pas de validation | **Aucun** message de validation layer dans la console | ☐ |

Puis **annule l'édition** (remets la ligne d'origine), sauvegarde → le casque doit **revenir à la normale**,
toujours à chaud. (Ça prouve que le reload est réversible et pas un one-shot.)

### 1b. Échec de compilation — l'ancien pipeline survit (spec §4)

Toujours à chaud, casse volontairement le shader — par ex. supprime le point-virgule ligne 316, ou écris
`outColor = pouet;`. **Sauvegarde.**

| Critère | Attendu | OK ? |
|---|---|---|
| L'app **ne crashe pas** | Elle continue de tourner normalement | ☐ |
| Le rendu est **conservé** | L'image reste celle du dernier shader valide (pas d'écran noir, pas de figeage) | ☐ |
| Erreur loggée | Un `[ERROR]` avec le message de compilation shaderc (ligne/colonne de l'erreur GLSL) | ☐ |
| Récupération | Corrige l'erreur → sauvegarde → le shader se recharge normalement | ☐ |

C'est le comportement exigé par la spec §4 : **échec de compile = log + ancien pipeline conservé.**

### 1c. Les autres passes (rapide)

Même exercice, une passe au choix pour vérifier que le mapping fichier→passe fonctionne :
- `shaders/skybox.frag` → doit logger `SkyboxPass reloaded`, teinter le ciel.
- `shaders/tonemap.frag` → doit logger `TonemapPass reloaded`, affecter toute l'image.

| Critère | Attendu | OK ? |
|---|---|---|
| C'est **la bonne passe** qui recharge | Le log nomme la passe correspondant au fichier édité (pas les 4) | ☐ |

### Limite connue (attendue, pas un bug)

Le hot reload accepte les **éditions interface-compatibles** uniquement. Ajouter un binding, changer un
push-constant ou modifier un `layout(...)` change l'interface du shader alors que le descriptor set-layout
reste figé → **message de validation + rendu incorrect**. Dans ce cas : redémarrer l'app. C'est une limite
actée du jalon (un relayout dynamique est phase 2).

De même, les **4 shaders compute IBL** (`ibl_*.comp`) ne sont **pas** hot-reloadables (déféré) : seuls les 7
fichiers des 4 passes graphiques sont surveillés.

---

## 2. Labels RenderDoc

**Windows/Linux uniquement** — RenderDoc ne supporte pas Metal, donc rien à faire sur macOS.

1. Lancer le Sandbox **via RenderDoc** (Launch Application sur l'exe du Sandbox, ou attacher).
2. Capturer une frame (F12 par défaut).
3. Ouvrir l'Event Browser.

| Critère | Attendu | OK ? |
|---|---|---|
| Hiérarchie des passes | Les marqueurs **`Shadow`**, **`Scene`**, **`Tonemap`** apparaissent et regroupent leurs draws | ☐ |
| Sous-label imbriqué | **`Skybox`** est niché **à l'intérieur** de `Scene` (la skybox est fusionnée dans la passe scène depuis M7) | ☐ |
| Labels IBL | Les 4 kernels **`IBL: EquirectToCube` / `Irradiance` / `Prefilter` / `BRDF LUT`** apparaissent (au chargement — capturer la 1ʳᵉ frame, ou vérifier dans le submit immédiat) | ☐ |
| Pas de déséquilibre | Aucun scope non refermé (l'arbre d'événements n'est pas décalé) | ☐ |

> Les labels ne sont émis qu'avec `VK_EXT_debug_utils` actif (build **DEBUG** / validation activée). En Release
> ils sont no-op — c'est voulu.

---

## 3. Confort souris

Fenêtre en cours, **clic** pour capturer la souris (puis **Échap** pour libérer).

| Critère | Attendu | OK ? |
|---|---|---|
| Lissage | La rotation caméra est **débruitée**, sans jitter saccadé — mais **sans lag mou** perceptible (tau = 30 ms) | ☐ |
| Pas de dérive | Relâcher la souris arrête la rotation net (pas d'inertie fantôme) | ☐ |
| Re-capture propre | Échap puis re-clic → **aucun à-coup** de rotation au moment de la re-capture (`ResetLook`) | ☐ |
| Capture OS-confinée | Le curseur reste confiné à la fenêtre pendant la capture (non régressé) | ☐ |
| Sensibilité réglable | PageUp/PageDown/Home/End ajustent toujours la sensibilité | ☐ |

> **Sensibilité ∝ FOV** : câblée mais **no-op aujourd'hui** (aucun zoom FOV dynamique n'existe — `FovYReference`
> = 60° = le FOV nominal, donc facteur 1). Rien à observer ; la liaison deviendra utile dès qu'un zoom existera.

---

## 4. Validation multi-OS

### 4a. Windows — machine courante (RTX 5070 Ti, Vulkan 1.3 core)

Déjà couvert par tout ce qui précède + les gates headless. Cocher une fois les parties 1-3 faites.

| Critère | OK ? |
|---|---|
| Build + 205 tests verts | ☐ |
| Sandbox : 0 validation, 0 leak | ☐ |
| Hot reload live < 1 s | ☐ |
| Labels visibles dans RenderDoc | ☐ |

### 4b. Linux — **rattrapage de la validation M4 sautée**

C'est la vraie inconnue du jalon (jamais validé depuis M1). Prérequis : SDK Vulkan + validation layers + pilote.

```sh
dotnet build && dotnet test
AGAPANTHE_MAX_FRAMES=3 AGAPANTHE_CAPTURE=/tmp/check.ppm dotnet run --project samples/Sandbox
dotnet run --project samples/Sandbox            # puis test hot reload live (partie 1)
```

| Critère | Attendu | OK ? |
|---|---|---|
| Build + tests | 0 warning, 205 verts | ☐ |
| Sandbox | 0 message de validation, 0 leak | ☐ |
| Rendu | Le casque s'affiche correctement (PBR + IBL + ombres + skybox) | ☐ |
| **Chemins sensibles à la casse** | Le hot reload fonctionne (le comparateur de chemins est OS-aware depuis M8-13 — **c'est ici que ça se prouve**) | ☐ |
| Watcher inotify | Le watcher détecte bien les sauvegardes (sémantique inotify ≠ Windows) | ☐ |

> **Pourquoi ce point est critique** : l'audit M8-09 a trouvé (finding M2) que la dédup des `SourceFiles`
> utilisait un comparateur insensible à la casse **en dur**, alors que le reste du système est OS-aware. Sur
> Linux, `Common.glsl` et `common.glsl` sont deux fichiers distincts. Corrigé en M8-13 — mais **non prouvé sur
> un vrai Linux**. C'est l'objet de cette section.

### 4c. macOS / MoltenVK — machine de dev historique

| Critère | Attendu | OK ? |
|---|---|---|
| Sandbox | 0 validation, 0 leak, rendu correct | ☐ |
| Hot reload live | Fonctionne (FSEvents) | ☐ |
| Labels RenderDoc | **N/A** — RenderDoc ne supporte pas Metal (attendu, pas un échec) | — |

---

## 5. Point de vigilance — crash intermittent au shutdown (M8-14)

Un crash **rare** a été observé **une fois** au shutdown : access violation `0xC0000005` dans
`Silk.NET.Input.Glfw.GlfwEvents.Dispose()`. Il survient **après** le teardown Vulkan complet et après le rendu
— il n'affecte ni les pixels ni les ressources GPU, **mais il masque le rapport ResourceTracker** quand il
frappe (donc pourrait cacher un futur leak).

**Non reproductible à la demande** : 12 runs consécutifs (6 normaux + 6 reload-test) → 0 reproduction.

| Critère | OK ? |
|---|---|
| Au fil des runs de ce protocole, noter si le message `ResourceTracker: no leaks` **manque** à la fermeture | ☐ observé / ☐ jamais vu |

Si jamais observé pendant M8-11 → déférer en phase 2 (ne pas patcher à l'aveugle un défaut non reproductible).
Si observé plusieurs fois → investiguer (hypothèse : delegates de callback GLFW collectés par le GC avant le
dispose natif → les garder vivants côté `EngineWindow`).

---

## Verdict

**PASS with concerns** — revue humaine 2026-07-12 (Windows 11 / RTX 5070 Ti / Vulkan 1.3 core).

**Ce qui est validé** (partie 1, le critère de sortie) : le hot reload fonctionne de bout en bout en session
réelle — édition dans l'éditeur → rechargement à l'écran en **224 ms au pire (cache froid), ~2 ms ensuite**,
soit ≪ 1 s (spec §6). L'échec de compilation se comporte comme exigé par la spec §4 : l'app ne crashe pas, le
rendu est conservé, l'erreur shaderc est précise, la correction recharge normalement. 0 message de validation,
0 leak malgré 4 reloads. Le protocole a en outre **révélé un bug** (log annonçant un reload réussi après un
échec de compile) — corrigé (M8-15).

**Concerns assumés et tracés** (ne bloquent pas la clôture, portés en tête de phase 2) :

1. **Linux non validé** — pas de machine disponible chez l'humain (décision du 2026-07-12 : assumer le trou
   plutôt que bloquer la clôture d'une phase entière). Le rattrapage de la validation M4 sautée reste dû. En
   particulier, le fix du comparateur de chemins OS-aware (finding M2 de l'audit M8-09 — `Common.glsl` vs
   `common.glsl`) est couvert par un **test unitaire qui simule** la sensibilité à la casse, mais **n'a jamais
   tourné sur un vrai Linux**, pas plus que le watcher inotify. Risque estimé faible (le code est
   cross-platform et testé), mais **non nul et non prouvé**.
2. **Labels RenderDoc (partie 2) non déroulés** — l'émission des labels est garantie par construction
   (équilibrage Begin/End via `using`/`DebugLabelScope`, vérifié en audit) et n'affecte pas le rendu (capture
   byte-identique), mais **la hiérarchie n'a pas été observée dans RenderDoc**.
3. **Feel souris (partie 3) non déroulé** — le lissage est prouvé correct par construction (dt-indépendant,
   alloc-free) mais **le ressenti n'a pas été jugé à la main**.

Ces trois points sont des **validations manquantes**, pas des défauts constatés. Ils sont inscrits en dette
d'ouverture de la phase 2.
