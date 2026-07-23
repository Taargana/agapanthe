# Absolute-Work Board — Agapanthe Session 20 (P3-M7 : device-local + raster ombre 4×)

**Status**: ✅ **CLOS (2026-07-23)** — A+B livrés, **double audit PASS** (findings appliqués), **verdict visuel humain PASS** (incl. cas soleil bas).
Protocole visuel : [../docs/visual-checks/2026-07-23-p3m7-device-local-shadow-raster.md](../docs/visual-checks/2026-07-23-p3m7-device-local-shadow-raster.md).
Double audit : `csharp-lowlevel` **PASS** · `graphics-3d` **PASS with concerns** (0 🔴/🟠 code ; findings appliqués :
commentaire shader `c*7`, scratch `[28]`, terminologie near-side, gate soleil-bas inscrit + env var `AGAPANTHE_SUN` ajoutée).
**Sessions passées** : S1–S19 → `archive/` (S19 = P3-M6 slots persistants + cull d'ombre GPU, clos).

**Gate final (mesuré, AOT `grid:100x100` sauf mention) :**
- **A (device-local)** : mono bit-identical `4848F93F` · GPU==CPU `2576 MATCH` · 0 alloc/frame · 0 leak · **~15,3 → ~11,2 ms** (banc animé, pire cas).
- **B (raster ombre 4×)** : **shadow-verify total 4944 ≈ 1× par caster** (par cascade `[28,123,701,4092]`) — le 4×→~1× prouvé numériquement · **~11,2 → ~8,0 ms** · mono reste `4848F93F` (caster centré en cascade 0 exemptée).
- **A+B cumulé : ~15,3 → ~8,0 ms (≈ ×2)** · draws 2+4 · 0 alloc/leak/validation · NativeAOT PASS · **325 tests** · 0 warning.
- **Bonus livré** : `ReadBackShadowVisible` (readback de comptage d'ombre par cascade) ferme la dette de gate GPU==CPU de l'ombre (P3-M6).
- **3 failles attrapées en route** : `CmdCopyBuffer2` (KHR_copy_commands2, risque MoltenVK) → core `vkCmdCopyBuffer` · alloc `>32` régions → batch stackalloc · `TransferSrc` absent de `BufferUsage` → ajouté.

**But** : refermer les **deux dettes perf déférées de P3-M6** — buffers GPU host-visible → **device-local** ([backlog §1](../docs/BACKLOG.md))
et le **raster ombre ~4×** ([§2.0bis](../docs/BACKLOG.md)). Deux vagues, **feu vert humain entre A et B**.
**Spec** : [docs/plans/2026-07-23-p3m7-device-local-shadow-raster-design.md](../docs/plans/2026-07-23-p3m7-device-local-shadow-raster-design.md)

## Décisions verrouillées (détail : spec §1/§6 + journal)

- **A d'abord (device-local, sûr), puis B (raster 4×, risqué)** ; feu vert entre les deux.
- **A** : nouveau `CommandList.CopyBuffer` **async** intra-frame ; **gros** buffers → device-local (candidats via
  staging persistant = miroir + copie des ranges dirty ; instances scène/ombre directs, GPU write+read). **Args +
  petits uploads restent host-visible** (host-lus par `ReadBackSceneVisible`).
- **A — honnêteté** : le gain candidats se voit sur scène **statique/typique**, peu/pas sur le banc tout-animé
  (pire cas) → **mesurer les deux** ; fallback copie pleine si dirty count élevé. Les instances gagnent chaque frame.
- **B** : **7ᵉ plan de coupe near-side (profondeur-vue)** par cascade (tuilage en profondeur) ; marge amont κ **conservée** → caméra-seul,
  pas de circularité. **`UpstreamExtent` par cascade complet déféré** (cas rare tour/falaise).
- **B — gate NON bit-identical** (leçon P3-M6) : « visuellement identique + comptage par cascade justifié ».
- **Bonus** : readback de comptage d'ombre par cascade → mesure B **et** ferme la dette de gate GPU==CPU de l'ombre.

## Critère de sortie

Banc `grid:100x100` JIT **et** AOT : 0 alloc/frame · GPU scène == CPU · draws 2+4 · **baisse ms mesurée** (A: PCIe ;
B: raster, comptage d'ombre ~4× → ~1×) · mono bit-identical `4848F93F` **après A** (B relâché : visuel + comptage) ·
0 warning · 0 validation · 0 leak · NativeAOT PASS · **double audit** + **verdict visuel humain** (surtout B).

## Project Conventions (détectées)

- .NET 10, `TreatWarningsAsErrors`, NativeAOT (`AotComponentProbe` + Sandbox). Tests **xUnit** (`dotnet test`).
- Gates bloquants **0 warning / 0 validation / 0 leak ResourceTracker**. Aucun type `Vk*` hors `Agapanthe.Graphics`.
- MoltenVK : mémoire unifiée → device-local == host-visible (copie redondante inoffensive) ; vérifier chaque flag/stage au 1er VUID.
- **Commits sur demande uniquement.** Conversation FR / code+docs EN.

## DAG des tâches

```
Wave 1 (Graphics — socle async copy)     Wave 2 (A device-local)           Wave 3 (A — cull lit device-local)
  AW-001 CommandList.CopyBuffer +           AW-003 InstanceBufferRing →        AW-004 CullSceneOnGpu + RecordShadowPass
         BufferCopyRegion + BufferSync              device-local                       bindent les buffers device-local
         transfer scope                     AW-002 PersistentInstanceBuffer          + barrières transfer→compute
         ▲ dep: —                                  staging persistant + device-       ▲ dep: 001,002,003
                                                    local + CopyBuffer ranges dirty
                                                    ▲ dep: 001
                            ── FEU VERT HUMAIN (fin vague A) ──▶
Wave 4 (B — plan de coupe near-side (profondeur-vue))           Wave 5 (B — diagnostic + gate ombre)
  AW-005 ShadowFit : borne near-side par         AW-007 readback comptage d'ombre par cascade
         cascade (7ᵉ plan camera-relatif)          (Debug/verify, ferme dette gate GPU==CPU ombre)
         ▲ dep: —                                  ▲ dep: 006
  AW-006 shadow_cull.comp + DispatchShadowCull : 7ᵉ plan appliqué
         ▲ dep: 005,004
Wave 6 (tail obligatoire)
  AW-008 mesures (A: static+animé PCIe ; B: comptage ombre 4×→1×)  ▲ dep: 006,007
  AW-009 self code review (diff)                                    ▲ dep: 008
  AW-010 requirements validation (vs spec)                          ▲ dep: 009
  AW-011 full verification + AOT                                    ▲ dep: 010
```

## Tâches

### Wave 1 — Graphics : socle de copie async

**AW-001** · code · **M** · deps: — · ✅ `done`
`CommandList.CopyBuffer(src, dst, ReadOnlySpan<BufferCopyRegion>)` (sync2 transfer, enregistré sur le cmd buffer
de frame — distinct de `GpuUploader` synchrone) ; `readonly record struct BufferCopyRegion(ulong Src, ulong Dst,
ulong Size)` ; `BufferSync` gagne un scope **transfer** (stage COPY, access TRANSFER_READ/WRITE). Device-local cible
gagne `TransferDst`. **Tests** : math des régions (offsets/tailles) ; VUID MoltenVK vérifié au 1er run.

### Wave 2 — A : buffers device-local

**AW-002** · code · **M** · deps: 001 · ✅ `done`
`PersistentInstanceBuffer` : par copie, **staging host-visible persistant (= miroir)** + **device-local**
(`TransferDst|Storage`). `Sync` écrit le staging comme aujourd'hui, puis `CopyBuffer` des **ranges dirty seulement**
(1 région/slot ; **fallback copie pleine si dirty > count/2**). Barrière transfer→compute avant les culls. **Tests** :
l'invariant §5 tient (device-local reste miroir fidèle après replay) ; 0-alloc du tableau de régions (fallback inclus).

**AW-003** · code · **S** · deps: — · ✅ `done`
`InstanceBufferRing` (scène + ombre) → **device-local** sans staging (GPU write+read). Confirmer que `Compact`
(ancien write CPU) est mort post-P3-M6 et le retirer. **Tests** : allocation device-local ; pas de régression capacité.

### Wave 3 — A : le cull lit device-local

**AW-004** · code · **M** · deps: 001, 002, 003 · ✅ `done`
`Renderer.CullSceneOnGpu` + `RecordShadowPass`/`DispatchShadowCull` : bindent le buffer de candidats **device-local**,
instances device-local ; barrière **transfer→compute** partagée (une avant les 2 culls, candidats read/read).
**Gate A (W1-3)** : bit-identical `4848F93F` · GPU==CPU MATCH · 0 alloc/frame · 0 leak · **baisse ms mesurée** (static+animé).
**⛔ FEU VERT HUMAIN avant Wave 4.**

### Wave 4 — B : plan de coupe near-side (profondeur-vue) par cascade

**AW-005** · code · **S** · deps: — · ✅ `done`
`ShadowFit` : calcule pour chaque cascade la **borne near-side** (profondeur vue `sliceFar[c]` + marge) → un plan
`(lightDir, -(d_backLimit))` en espace camera-relatif. Exposé à côté des frusta de cascade. **Tests** : un caster à
profondeur d tombe dans la/les cascade(s) attendue(s) (assignation depth-bounded).

**AW-006** · code · **M** · deps: 005, 004 · ✅ `done`
`shadow_cull.comp` : applique le **7ᵉ plan** (rejet near-side) en plus des 6 du frustum de cascade ; `DispatchShadowCull`
pousse le plan supplémentaire. **Gate B** : visuel « identique + comptage par cascade justifié » (**pas** bit-identical) ;
comptage d'ombre par cascade **~4× → ~1×** ; 0 alloc/leak/validation.

### Wave 5 — B : diagnostic + fermeture dette gate ombre

**AW-007** · code · **S** · deps: 006 · ✅ `done`
Readback de comptage d'ombre par cascade (Debug/verify, symétrique à `ReadBackSceneVisible` — somme des
`instanceCount` d'args d'ombre par cascade). Env var / log. Ferme la **dette de gate GPU==CPU de l'ombre** (P3-M6).
**Tests** : le readback somme correctement.

### Wave 6 — tail obligatoire

**AW-008** · test · **S** · deps: 006, 007 · ✅ `done`
Mesures : banc `grid:100x100` JIT+AOT — **A** : ms static (drop:N posé/grid figé) vs animé (PCIe, avec honnêteté) ;
**B** : comptage d'ombre par cascade 4×→1×, ms raster. Draws 2+4, 0 alloc, GPU==CPU.

**AW-009** · test · **S** · deps: 008 · ✅ `done` — Self code review du diff (reuse, altitude, 0-alloc, barrières).

**AW-010** · test · **S** · deps: 009 · ✅ `done` — Requirements validation vs spec §2-§5 (chaque gate coché).

**AW-011** · test · **S** · deps: 010 · ✅ `done`
Full verification : `dotnet build` + `dotnet test` verts, 0 warning/validation/leak, `AotComponentProbe` + Sandbox
NativeAOT PASS, publish AOT + banc.

## Deferred Work (hors périmètre → backlog)

- `UpstreamExtent` par cascade complet (cas rare tour/falaise dont l'ombre disparaît de près) — [backlog §2.0bis](../docs/BACKLOG.md).
- Args buffer device-local (host-lu par le gate) · coalescing avancé des régions dirty · MultiDrawIndirect.

## Rollback Point

À capturer **avant** que Wave 1 touche un fichier (arbre de code propre requis). Commit courant : `fc78145`.

## Clôture (CONVERGE — méthode projet)

Double audit (`csharp-lowlevel` + `graphics-3d`) findings appliqués · **verdict visuel humain** (surtout B —
protocole `docs/visual-checks/`) · maj `docs/AVANCEMENT.md` + `docs/BACKLOG.md` · suggestion de commit (jamais auto).
Board archivé `archive/board-session20-P3M7.md`.
