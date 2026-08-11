# Absolute-Work Board — Agapanthe Session 25 (UI-1 : texte à l'écran)

**Status**: ✅ **CLOS (session 25).** 5 vagues closes · double audit PASS-with-concerns ×2 (aucun 🔴, 7 🟠 + 5 🟡 appliqués) · verdict humain PASS · CONVERGE fait · commit sur demande.
**Spec** : [`docs/plans/2026-08-03-text-ui-design.md`](../docs/plans/2026-08-03-text-ui-design.md) — **APPROVED 4,4/5**
(revue scorée indépendante, 2 itérations). **Ne pas re-litiger ses décisions**, elles ont été vérifiées contre le code.
**Rollback point** : `2c7b2be` (arbre propre, tout poussé).
**Sessions passées** : S1–S24 → `archive/` (S24 = VS-3, clos + poussé `a9ed148`).

**But** : premier des 3 jalons Texte & UI. Afficher du texte à l'écran, livré comme **infrastructure** du moteur
(primitif partagé par les 2 futures couches d'UI), pas comme HUD de démo. **UI-2 (profiler CPU) et UI-3 (timestamps
GPU) sont HORS SCOPE.**

## Décisions verrouillées (spec §Locked + revue)

- **Graphe** : `Ui → Assets → Core` et **`Rendering → Ui`** (forcé : `StorageBufferRing<T>` est `internal` à Rendering).
- **Quad→GPU** : SSBO + `gl_VertexIndex`, `VertexLayout = null` (patron `TonemapPass`), **1 pipeline / 1 atlas / 1 draw**.
- **Descriptor set** : **UN SEUL set PER-FRAME depuis `FrameContext`** (b0 = sampler atlas, b1 = SSBO quads).
  Patron exact `Renderer.cs:1145-1162`. **Jamais** mélanger un binding persistant et un per-frame (1 set = 1 pool).
- **Passe** : après tonemap, `LoadOp = Load`, `DepthTest = false`, `Cull = None`, blend **premultiplied**.
- **Ordre shader** : unpack → **sRGB→linéaire** → **puis** RGB × alpha (l'inverse = bug de halo). Alpha reste linéaire.
- **Atlas** : `MipLevels = 1` (les mips lisseraient le SDF) · sampler linéaire + ClampToEdge + sans mips + sans aniso.
- **Cooker** : em 64 px, spread 4 px, padding spread+1, atlas PO2 ≤ 2048 (échec bruyant si dépassement), packing shelf
  (tri hauteur décroissante), **ordre codepoint croissant** (byte-identité), texel blanc 2×2 en (0,0).
- **Kerning v1 = no-op assumé** (monospace + `StbTrueTypeSharp` ne lit que la table `kern` legacy, pas GPOS) → le test
  asserte la lookup sur un `FontAsset` **synthétique**.
- **Policies** : glyphe manquant = tofu sinon skip+advance (**jamais de throw au draw**) · z = ordre de soumission ·
  **clipping hors scope v1** · une seule fonte v1 · resize via push constant `invScreenSize` · `UiDrawList` détenue par
  `UiRenderSystem`, clear au début du Tick, thread propriétaire unique.

## Critère de sortie (DoD)

Hash de capture reproductible (scène + chaîne figées) + **verdict humain de lisibilité** · **capture bit-identique
AVANT/APRÈS `BlendMode`** (gate bloquant : il touche TOUS les pipelines) · 0 alloc/frame · tests layout GPU-free ·
cooker byte-identique + round-trip + robustesse `.agfont` · **NativeAOT PASS** + `StbTrueTypeSharp` **hors** de la
fermeture AOT · **double audit** `csharp-lowlevel` + **`graphics-3d`** (déviation assumée, cf. spec §Testing).

## Project Conventions

.NET 10 · `dotnet build` / `dotnet test` · `TreatWarningsAsErrors` · Aucun type `Vk*` hors `Agapanthe.Graphics` ·
aucun type Arch hors `Agapanthe.World` · NativeAOT-pur (dispatch concret, zéro réflexion hot path) · System.Numerics
row-vector. **Gates bloquants** : 0 warning · 0 message de validation · 0 leak ResourceTracker · 0 alloc/frame régime
stable. Env : `AGAPANTHE_MAX_FRAMES=N`, `AGAPANTHE_CAPTURE=out.ppm`. AOT publish : préfixer le PATH avec le VS
Installer (`vswhere.exe`). Solution : `Agapanthe.slnx` (ajouter `Agapanthe.Ui` et `FontCooker`).
**Commits/push sur demande explicite UNIQUEMENT.**

## Pré-requis déjà satisfait

✅ **Fonte vendorée** : `fonts/JetBrainsMono-Regular.ttf` (274 Ko, magic TTF valide) + `fonts/OFL.txt` (OFL 1.1,
redistribution autorisée). ✅ `StbTrueTypeSharp 1.26.12` disponible (nuget, pur managé, même auteur que
`StbImageSharp` déjà utilisé).

## Tâches (DAG + vagues)

### Wave 1 — Baseline + fondations bas niveau (fichiers disjoints) — ✅ CLOSE

**UI-1-01** ✅ **DONE** · `infra` · S · deps: — · **doit précéder UI-1-03**
> **BASELINE BLENDING = `12638eddd7f3f67ab161b298ffbcd15e`** (`planet-drop`, DROP_EVERY=12, MAX_FRAMES=420, 1280×720,
> 0 leak / 217 resources). Identique au hash VS-2 post-audit → scène confirmée déterministe. UI-1-13 doit retrouver
> ce hash **exact** après l'ajout de `BlendMode`.
Capturer la **baseline de non-régression blending** AVANT toute modification : run headless d'une scène existante
figée (`AGAPANTHE_SCENE=planet-drop`, `AGAPANTHE_DROP_EVERY=12`, `AGAPANTHE_MAX_FRAMES=420`) → enregistrer le **hash
MD5** de la capture sur ce board. C'est la preuve que `BlendMode.Opaque` par défaut n'affecte rien.

**UI-1-02** ✅ **DONE** · `code`+`test` · S · deps: — · fichiers: `PixelFormat.cs`, `GpuUploader.cs`
`PixelFormat.R8Unorm` + les **3 switches exhaustifs** : `ToVk` (`PixelFormat.cs:48`, throw), `FromVk` (`:62`),
`GpuUploader.BytesPerTexel` (`GpuUploader.cs:471-477`, throw) → `R8Unorm => 1`. Test : round-trip `ToVk`/`FromVk`.

**UI-1-03** ✅ **DONE** · `code`+`test` · S · deps: UI-1-01 · fichiers: `BlendMode.cs` (nouveau), `GraphicsPipelineDesc.cs`, `GraphicsPipeline.cs`
`enum BlendMode { Opaque, AlphaBlend, PremultipliedAlpha }` + `GraphicsPipelineDesc.Blend` (défaut **`Opaque`**) →
brancher `PipelineColorBlendAttachmentState` (`GraphicsPipeline.cs:208-219`, aujourd'hui `BlendEnable = false` en dur).
**Aucun pipeline existant ne doit changer** (défaut = comportement actuel).

**UI-1-04** ✅ **DONE** · `code`+`test` · M · deps: — · fichiers: `src/Agapanthe.Assets/Font/*` (nouveaux)
`FontAsset` (record : pixels R8, métriques globales, `GlyphMetrics[]` plat, kern pairs triés) + lecteur `.agfont`
(blittable, `MemoryMarshal`, patron VS-1) + **écrivain `internal`** (`InternalsVisibleTo` pour **`FontCooker` ET
`Agapanthe.Tests`**) + `FontAssetException`. Header : magic + version + counts. **Aucune nouvelle dépendance.**
Tests : round-trip, byte-identité, robustesse (magic/version/tronqué/counts incohérents).

### Wave 2 — Cooker + logique de texte (fichiers disjoints, parallèle-safe) — ✅ CLOSE

> **COOK RÉEL** : JetBrains Mono → **192 glyphes, 0 kern pair, atlas 1024² SDF, em 64, spread 4**.
> **`.agfont` déterministe = `d19195a387d0001db0b905aafbbc9bd1`** (2 runs identiques).
> **0 kern pair confirme la décision de la spec** : JetBrains Mono ne ship pas de table `kern` legacy (GPOS
> uniquement) → le chemin kerning v1 est bien un no-op, et c'est pourquoi son test tourne sur un `FontAsset`
> synthétique.

**UI-1-05** ✅ **DONE** · `code`+`test` · M · deps: UI-1-04 · fichiers: `tools/FontCooker/*` (nouveau)
Console **non-AOT**, **jamais référencée par un projet shippé** (patron `tools/ShaderPrecompiler`). `StbTrueTypeSharp`
→ SDF em 64 px, spread 4, padding spread+1 ; packing **shelf** (tri hauteur décroissante) ; atlas PO2 ≤ 2048, **échec
bruyant** si dépassement ; **ordre codepoint croissant** ; texel blanc 2×2 en (0,0). CLI
`FontCooker <font.ttf> <charset.txt> <out.agfont> [--dump-atlas <png>]`, exit 0/1/2. Test : sortie **byte-identique**
pour une même entrée.

**UI-1-06** ✅ **DONE** · `code`+`test` · M · deps: UI-1-04 · fichiers: `src/Agapanthe.Ui/*` (nouveau projet)
Projet **GPU-free** (référence **Core + Assets uniquement**). `UiQuad` (rect, uvRect, rgba, flags), `UiDrawList`
(tableau poolé, croissance par doublement, `ReadOnlySpan<UiQuad>`), shaping (`Rune`, **seam explicite**
`text → PositionedGlyph[]`), layout, `Measure`, alignement, multi-ligne `\n`, glyphe manquant.
Tests GPU-free : `Measure`, multi-ligne, alignements, glyphe manquant, génération de quads, **kerning synthétique**,
**0-alloc-after-warmup** (patron `tests/Agapanthe.Tests/SurfaceContactsTests.cs:97`).

### Wave 3 — Intégration GPU — ✅ CLOSE

> **Piège d'alignement attrapé** : `UiQuad` faisait 40 octets côté C#, mais std430 arrondit le stride d'array à
> **48** (alignement `vec4`). Non corrigé, les quads se désynchronisaient dès le second et chaque glyphe lisait les
> données de son voisin — silencieux. Structure figée à 48 des deux côtés (2 mots réservés), **verrouillé par test**.
> Target MSBuild `CookFonts` **incrémentale vérifiée** (2ᵉ build = 0 cook), `.agfont` livré dans `bin/.../fonts/`.

**UI-1-07** ✅ **DONE** · `infra` · M · deps: UI-1-05 · fichiers: `samples/Sandbox/Sandbox.csproj`, `fonts/charset.txt`
Target MSBuild `CookFonts` incrémentale, jumelle de `PrecompileShaders` (`Sandbox.csproj:78-131`) — **reproduire les 2
pièges déjà payés** : `RemoveProperties="RuntimeIdentifier;SelfContained;PublishAot;PublishTrimmed;PublishSingleFile"`
et **pré-expansion du glob** avant `<Content>`. `charset.txt` = ASCII + Latin-1 Supplement (~220 glyphes).

**UI-1-08** ✅ **DONE** · `code` · S · deps: UI-1-06 · fichiers: `shaders/ui.vert`, `shaders/ui.frag`
Sommets depuis `gl_VertexIndex` (`% 6` = coin, `/ 6` = quad, lecture SSBO). Frag : SDF + AA `fwidth`, **ordre
obligatoire** unpack → sRGB→linéaire → RGB × alpha, branche solide via `flags` bit 0 (texel blanc).
Globés **automatiquement** par le précompilateur → aucune modif du build shader.

**UI-1-09** ✅ **DONE** · `code` · M · deps: UI-1-02, UI-1-03, UI-1-06, UI-1-08 · fichiers: `src/Agapanthe.Rendering/*`
`UiPass : ReloadablePass` + `FontResources` (**tous deux `internal`**, possédés par `Renderer`, ajoutés à
`_reloadablePasses` `Renderer.cs:445` → hot reload gratuit). Atlas : `MipLevels = 1`, sampler linéaire/ClampToEdge/
sans mips/sans aniso (helper d'upload extrait de `SceneBuilder.UploadTexture:140-163` avec **mip count + usage
explicites**). Un set per-frame `FrameContext`. Surface publique : `Renderer.LoadFont(FontAsset)` +
`Renderer.DrawUi(cmd, frame, target, ReadOnlySpan<UiQuad>)`, appelée **après** `RecordTonemapPass` (`Renderer.cs:863`).
`Rendering.csproj` référence `Ui`.

### Wave 4 — Câblage applicatif + AOT — ✅ CLOSE

> **UI-1-14 (NOUVELLE TÂCHE, découverte en vague 4, décision humaine) — capture swapchain.**
> `AGAPANTHE_CAPTURE` lit la cible **HDR**, donc l'UI (dessinée sur la swapchain APRÈS le tonemap) lui est
> **structurellement invisible** → la DoD « hash de capture du texte » était insatisfiable. Ajouté :
> `TransferSrcBit` sur la swapchain (capability-checké via `Swapchain.CanCapture`), `CommandList.CopyColorImage`,
> `FrameRenderer.RequestCapture()/ReadCapture()`, `AGAPANTHE_CAPTURE_UI=<path>`.
> **Deux erreurs Vulkan attrapées par la validation layer au passage** (le gate a fait son travail) :
> (1) lire une image de swapchain APRÈS `vkQueuePresentKHR` est illégal → la copie doit être enregistrée
> **dans** la frame, tant que l'image est acquise ; (2) une `ImageView` exige un usage autre que transfert seul
> (VUID-…-04441) → `Sampled` ajouté à l'image de capture.
>
> **HASH DE CAPTURE UI DE RÉFÉRENCE = `c4adc5d244e8e4f253d0dbea4277fd1c`** (`planet-drop`, DROP_EVERY=12,
> MAX_FRAMES=420, 1280×720), reproductible sur 2 runs.
> **AOT** : `.agfont` chargé (192 glyphes), layout exécuté (23 quads) ; **`StbTrueTypeSharp` absent du publish**.

**UI-1-10** ✅ **DONE** · `code` · M · deps: UI-1-07, UI-1-09 · fichiers: `src/Agapanthe.Engine/UiRenderSystem.cs`, `samples/Sandbox/Program.cs`
`UiRenderSystem : IRenderSystem` détenant la `UiDrawList` (clear en début de Tick). Sandbox : charger le `.agfont`,
enregistrer le système, afficher une **chaîne figée** (la ligne HUD reste sur `window.Title` — son remplacement est
**UI-2**, hors scope).

**UI-1-11** ✅ **DONE** · `code` · S · deps: UI-1-07 · fichiers: `tools/AotComponentProbe/*` ou équivalent
Prouver sous **NativeAOT** que le `.agfont` est copié en `Content` et **chargé** (Release = cache-only, c'est là que
les shaders ont mordu), et vérifier que **`StbTrueTypeSharp` n'entre PAS dans la fermeture AOT**.

### Wave 5 — Tail (obligatoire) — audits ✅, verdict humain ⏳

> **DOUBLE AUDIT : `csharp-lowlevel` PASS-with-concerns · `graphics-3d` PASS-with-concerns. Aucun 🔴.**
> Les deux ont **convergé** sur 2 findings (course de resize à la capture · `LastPresentedTarget` pendant).
>
> **🟠 corrigés (7)** :
> - **Barrière manquante** entre le tonemap et la passe UI : deux render pass instances consécutives sur la même
>   image **sans changement de layout**, donc aucune dépendance émise (partout ailleurs la transition la fournit
>   incidemment). → `CommandList.ColorAttachmentBarrier` avec `READ|WRITE` côté destination (un
>   `TransitionImage(from==to)` n'aurait posé que `WRITE`, ratant la moitié du hazard).
> - **Spread SDF sous-dimensionné** (4 → **8**) : à 11 px `fwidth ≈ 0,73` contre une amplitude stockée de `±0,502`
>   → couverture bornée à [0,066 ; 0,934], texte délavé **et voile gris sur toute la boîte paddée**. Aucun réglage
>   shader ne le rattrapait : l'information n'était pas dans l'atlas. Atlas toujours 1024².
> - **Course de resize à la capture** : l'image de capture est désormais dimensionnée **dans** la frame, où l'extent
>   est certain (sinon `CmdCopyImage` hors bornes → message de validation).
> - **`stackalloc` 48 Kio/appel** (1024 × 48 o, zéro-initialisé par `.locals init`) → **256 glyphes (12 Kio)**.
>   Invisible au gate 0-alloc, qui ne mesure que le tas — ~240 Kio de memset/frame sur le HUD de démo.
> - **`LineWidth` en O(n²)** sur le chemin par frame → largeurs calculées **une fois**, court-circuit sur `Left`.
> - **`LastPresentedTarget`** publiait des handles Vulkan pendants après resize → **`LastPresentedExtent`**.
> - **Snap au pixel** des origines de glyphes (mitigation prévue par la spec, non faite).
>
> **🟡 corrigés** : validation du tri des kern pairs + finitude des métriques au load (asymétrie avec les glyphes) ·
> garde fonte dégénérée (`scale` Inf/NaN) dans le cooker · fuite de bitmaps stb si `RasterizeGlyphs` lève en cours
> de boucle · résidus `Sampler { get; } = null!` · message d'erreur de capture trompeur.
>
> **Défaut de build trouvé en re-vérifiant** : les `Inputs` de `CookFonts` ne couvraient que `fonts/**` → **modifier
> le cooker ne recuisait pas** (contrairement au cache shader, dont la clé est un hash et qui s'auto-répare).
> Sources du cooker ajoutées aux inputs.
>
> **Dette laissée explicite** (non bloquante, recommandée pour UI-2) : activer
> `VK_VALIDATION_FEATURE_ENABLE_SYNCHRONIZATION_VALIDATION` — sans elle le gate « 0 validation » **ne peut
> structurellement pas voir** les hazards de synchro, ce qui est précisément la classe du finding corrigé ci-dessus ·
> `MaxStorageBuffers` 12/16 utilisés (UI-2/UI-3 déborderont) · échec bruyant si aucun format sRGB de swapchain ·
> test d'alignement `UiQuad` qui verrouille la taille mais pas les offsets · troncature silencieuse > 256 glyphes.
>
> **HASHES FINAUX** : non-régression blending **`12638edd`** (inchangé) · capture UI **`6e14b23e`** (nouveau
> baseline après le correctif de spread), reproductible sur 2 runs.

**UI-1-12** ✅ **DONE** · Self code review du diff + **double audit** `csharp-lowlevel` (alloc cachée, 0-alloc régime stable,
casts, leak atlas, fermeture AOT) + **`graphics-3d`** (pipeline blend, ordre sRGB/premultiply, LoadOp, descriptor set,
barrières, validation). Appliquer les findings 🔴/🟠.

**UI-1-13** ✅ **DONE** · Requirements validation (DoD spec couverte) + **Full verification** : `dotnet build` (0 warning) +
`dotnet test` (verts) + Sandbox headless (0 validation, 0 leak) + **capture bit-identique vs baseline UI-1-01** +
hash de capture texte reproductible + probe AOT PASS + **verdict visuel humain**.

## DAG

```
UI-1-01 ─► UI-1-03 ─┐
UI-1-02 ────────────┤
                    ├─► UI-1-09 ─┐
UI-1-04 ─┬─► UI-1-06 ─► UI-1-08 ─┘         ├─► UI-1-10 ─┐
         └─► UI-1-05 ─► UI-1-07 ───────────┴─► UI-1-11 ─┴─► UI-1-12 ─► UI-1-13
Wave 1              Wave 2         Wave 3        Wave 4        Wave 5 (tail)
```

**Parallélisme sûr** : W1 → `01`/`02`/`04` disjoints (`03` attend `01` = baseline avant modif) · W2 → `05`/`06`
disjoints · W3 → `07`/`08` disjoints, `09` après · W4 → `10`/`11` disjoints.

## Rollback Point

`2c7b2be` (arbre propre, tout poussé). Seul ajout déjà présent : `fonts/JetBrainsMono-Regular.ttf` + `fonts/OFL.txt`
(vendorés, non commités).

## Deferred Work

Hors scope assumé (spec §Debts) : **UI-2** (DebugOverlay + profiler CPU, remplacement du HUD `window.Title`) ·
**UI-3** (timestamps GPU `QueryPool`) · clipping/scissor · multi-fontes · rich text · labels world-space · CJK et
écritures complexes (le seam de shaping existe) · interactivité (→ **MP-0**, cahier des charges d'entrée en annexe de
la spec) · GPOS/HarfBuzz.

## Clôture (CONVERGE)

Findings du double audit appliqués · verdict humain · maj `AVANCEMENT`/`BACKLOG`/`CLAUDE` · board archivé
`archive/board-session25-UI1.md` · **commit sur demande**.
