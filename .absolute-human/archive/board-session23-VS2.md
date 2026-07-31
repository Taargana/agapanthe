# Absolute-Work Board — Agapanthe Session 23 (VS-2 : spawn runtime + gravité newtonienne)

**Status**: 🟢 **EXÉCUTION — Wave 1 & Wave 2 CLOSES. Pause en boundary Wave 2 (avant Wave 3).** Spec **Approved
4.4/5** (`engine-architect`, 4 findings repliés). Rollback point : `3057bb4` (arbre propre, VS-1 poussé).
**Sessions passées** : S1–S22 → `archive/` (S22 = VS-1, clos + poussé `3057bb4`).

### ⏸️ REPRISE (prochaine session) — où repartir

**Fait (non commité — commit sur demande uniquement) :**
- **Wave 1 CLOSE** : VS-2-01 `PhysicsSettings.WithAttractor` · VS-2-02 gravité radiale + sol radial
  (`restSpeed = 2·(μ/R²)·dt`) · VS-2-03 `SpawnBodyDeferred` + `CommandKind.SpawnBody` + `MaterialiseBody`. Tests World
  ajoutés (9). Régression μ=0 préservée (branche `else`).
- **Wave 2 CLOSE** : VS-2-04 probe étendue → **NativeAOT PASS** (`iterated 13`, `IsDynamicCodeSupported=False`, exit 0) ·
  VS-2-05 scène `AGAPANTHE_SCENE=planet-drop` (planète+Soleil ½, attracteur, sol radial, `ProbeDropSystem` en
  `Stage.Input`, keypress `B`, caméra proche surface `FramePlanetDropCamera`).
- **Preuves** : 355/355 tests · 0 warning · 0 validation · 0 leak (217 resources) · **capture headless
  reproductible byte-identique** (MD5 `559bcf6b…`, 2 runs). Capture **regénérable** (déterministe) via la commande
  ci-dessous — le bon cadrage est side-lit (`AGAPANTHE_DROP_SUN_OFF=32`, Soleil dégagé à droite de la colonne).

**Fichiers modifiés (non commités)** : `src/Agapanthe.World/{PhysicsSettings.cs, GameWorld.cs, GameWorld.Physics.cs}` ·
`tests/Agapanthe.Tests/{PhysicsTests.cs, LifecycleTests.cs}` · `samples/Sandbox/Program.cs`.
Spec untracked : `docs/plans/2026-07-25-vs2-spawn-runtime-newtonian-gravity-design.md`.

**Reste à faire — Wave 3 (tail) :**
1. **Verdict visuel humain** en attente sur la capture (question posée fin de session ; l'humain lançait la démo interactive `B`).
2. ~~**VS-2-06** double audit~~ ✅ **FAIT** — `csharp-lowlevel` **PASS-with-concerns**, `engine-architect` **PASS-with-concerns 4.5/5**.
   Les deux ont trouvé indépendamment le **même 🟠** (singularité `r2≈0` non gardée dans l'intégration newtonienne). **Findings appliqués** :
   - 🟠 **M1** : garde `if (r2 > 1e-18)` dans `StepPhysics` pass 1 (symétrique au sol radial / narrowphase) — `GameWorld.Physics.cs`
   - 🟡 **m1** : `Debug.Assert(!newtonian || Gravity==0)` (config incohérente attrapée en Debug, compilée away en Release)
   - 🟡 **m3** : probe AOT `surfaceRadius=700` → **exécute** (pas seulement roote) le contact du sol radial sous ILC
   - 🟡 **m4** : spirale golden-angle `(_dropped%8)+0.5` → plus d'overlap XZ exact tous les 8 drops
   - 🟡 **m2** : dette documentée (pas de lifetime/rest-cull des corps → croissance non bornée) — spec §Debts + Deferred Work
   Post-fix : **355 tests · 0 warning · NativeAOT PASS (`iterated 13`) · capture déterministe** (nouveau MD5 `12638edd…`, 2 runs) · 0 leak / 0 validation.
3. **VS-2-07** vérification finale + CONVERGE (maj AVANCEMENT/BACKLOG/CLAUDE, archive board `archive/board-session23-VS2.md`).

**Lancer la démo** : `$env:AGAPANTHE_SCENE="planet-drop"; dotnet run --project samples/Sandbox -c Debug` (souris = clic,
ZQSD/WASD, `B` largue une probe, Échap quitte). Capture : + `AGAPANTHE_MAX_FRAMES=420 AGAPANTHE_CAPTURE=x.ppm`.
⚠️ AOT publish : `vswhere.exe` hors PATH Git Bash → préfixer `export PATH="/c/Program Files (x86)/Microsoft Visual Studio/Installer:$PATH"`.

**But** : solder la dette P3-M3 — `SpawnBodyDeferred` (spawn de corps physique DIFFÉRÉ au runtime, jumeau de
`SpawnDeferred`, appliqué à la barrière structurelle) + **gravité newtonienne** radiale ponctuelle minimale + un
**demi-espace de sol RADIAL** (analogue du sol plat). Preuve : scène `planet-drop` (sonde larguée sur une planète à
échelle réelle ½, vue proche surface), déclenchée par un système **déterministe** ET une **touche clavier**.
**Spec** : [../docs/plans/2026-07-25-vs2-spawn-runtime-newtonian-gravity-design.md](../docs/plans/2026-07-25-vs2-spawn-runtime-newtonian-gravity-design.md)

## Décisions verrouillées (spec §Locked)

- **Immédiat `SpawnBody` conservé + différé `SpawnBodyDeferred` ajouté** (parité `SpawnImported`/`SpawnDeferred`).
- **Attracteur dans `PhysicsSettings`** (champ, pas composant ECS) → n'altère PAS l'ordre gelé `ComponentRegistry.All`
  ni le masque VS-1. `μ = 0` → chemin uniforme inchangé (scène `drop` **byte-identique**).
- **Sol RADIAL**, pas de planète rigid-body géante (broadphase non polluée ; planète reste pur drawable).
- **`restSpeed` radial = `2·(μ/R²)·dt`** (PAS `gravity.Y` — sinon micro-bounce éternel, finding review #1).
- **Échelle réelle ½, vue proche surface** ; démo `planet-drop` = **nouvelle variante** (scène `planet` P3-M8 intacte).
- Byte-identité du gate capture = **intra-binaire** (casts `double→float` → pas de byte-identité pixel cross-JIT/AOT).

## Critère de sortie

Tests unitaires World (spawn différé + gravité radiale + sol radial + settling + déterminisme + régression μ=0
byte-identique) · **probe AOT** (SpawnBodyDeferred + step newtonien) · intégration Sandbox `planet-drop` +
spawner déterministe → **capture headless reproductible** · 0 warning/validation/leak · 0 alloc/frame régime stable ·
NativeAOT PASS · **double audit** (`csharp-lowlevel` + `engine-architect`) + **verdict visuel humain**.

## Project Conventions (rappel)

.NET 10, `dotnet build` / `dotnet test`, `TreatWarningsAsErrors`. Aucun type Arch hors `Agapanthe.World`. Aucun `Vk*`
hors `Agapanthe.Graphics`. System.Numerics row-vector. NativeAOT-pur (dispatch switch concret, pas de réflexion sur
le hot path). Gates bloquants : 0 warning · 0 message de validation · 0 leak ResourceTracker · 0 alloc/frame régime
stable. Env probe : `AGAPANTHE_MAX_FRAMES=N`, `AGAPANTHE_CAPTURE=out.ppm`.

## Tâches (DAG + vagues)

> **Wave 1 ✅ · Wave 2 ✅ · Wave 3 ⏳** (détail statut dans « REPRISE » ci-dessus).

### Wave 1 — Physique + spawn différé (GPU-free, TDD) — séquentielle (fichiers physique partagés) — ✅ CLOSE

**VS-2-01** · `code`+`test` · S · deps: — · fichier: `PhysicsSettings.cs`, `tests/…PhysicsTests.cs`
Étend `PhysicsSettings` : champs `Double3 AttractorCenter`, `double Mu`, `double SurfaceRadius` + méthode
`WithAttractor(C, μ, R)` (retourne une copie ; `readonly struct` → pas d'object-initializer) + ctor overload délégué.
Tests : `Default(...).WithAttractor(...)` pose les 3 champs ; défaut μ=0.

**VS-2-02** · `code`+`test` · M · deps: VS-2-01 · fichier: `GameWorld.Physics.cs`, `PhysicsTests.cs`
`StepPhysics` branche sur `Mu > 0`. Passe 1 : `a = μ·(C−p)/|C−p|³` (double → float velocity) sinon uniforme.
Passe 3 : sol radial `|p−C|−r < R` → repousse à `R+r` sur `n̂`, réfléchit vitesse normale, **clamp
`restSpeed = 2·(μ/R²)·dt`**. Tangentielle intacte. Tests : accel vers C, symétrie angulaire, inverse-carré, sol
radial lift/reflect, **settling radial (verrouille la formule restSpeed)**, déterminisme intra-binaire,
**régression μ=0 byte-identique** (scène type `drop` inchangée).

**VS-2-03** · `code`+`test` · M · deps: VS-2-01 · fichier: `GameWorld.cs`, `GameWorld.Physics.cs`, `GameWorld…Tests.cs`
`CommandKind.SpawnBody` + 4 champs plats sur `StructuralCommand` (`Velocity`, `InverseMass`, `Restitution`,
`Radius`). `SpawnBodyDeferred(spec, v, invMass, e, r)` : `_pendingSpawn.Add` + enqueue + `EntityRef`. Refactor
`MaterialiseBody(...)` partagé (immédiat + flush). `case CommandKind.SpawnBody` en passe 1 du flush. Tests : handle
`IsAlive` avant flush, payload exact après, `InstanceSlot=-1`, `_structuralDirty`, `StepPhysics` ne le voit pas avant
flush / le voit après.
*(partage `GameWorld.Physics.cs` avec VS-2-02 → séquentiel après 02).*

### Wave 2 — AOT + intégration Sandbox — parallèle-safe (fichiers disjoints) — ✅ CLOSE

**VS-2-04** · `code`+`test` · S · deps: VS-2-02, VS-2-03 · fichier: `GameWorld.cs` (AotRootingSmoke interne),
`tools/AotComponentProbe/Program.cs`
Étend `AotRootingSmoke` : `SpawnBodyDeferred` → flush → `StepPhysics` newtonien (racine `CommandKind.SpawnBody` →
`MaterialiseBody` + intégration radiale sous ILC). Test JIT + probe AOT.

**VS-2-05** · `code` · M · deps: VS-2-02, VS-2-03 · fichier: `samples/Sandbox/Program.cs`, `Agapanthe.Engine` (spawner)
Scène `AGAPANTHE_SCENE=planet-drop` : planète ½ drawable + `PhysicsSettings.WithAttractor(C, μ, R)`, caméra proche
surface, Soleil au ciel (réutilise éclairage P3-M8). Spawner déterministe `ISystem` en `Stage.Input` (`AGAPANTHE_DROP_EVERY=N`
→ `SpawnBodyDeferred`). Touche clavier edge-triggered (`Key.B`) = largage à la volée (verdict humain, hors gate
déterministe). μ tunable `AGAPANTHE_PLANET_MU`.
*(fichiers disjoints de VS-2-04 → parallélisable ; défaut sinon : sérialiser).*

### Wave 3 — audits + verdict (tail tasks) — 🔵 VS-2-06 CLOSE · VS-2-07 en attente du verdict humain

**VS-2-06** · double audit `csharp-lowlevel` (alloc cachée, cast double→float, leak, 0-alloc régime stable) +
`engine-architect` (étanchéité Arch, barrière/stage, cohérence physique, ordre `ComponentRegistry` intact). Appliquer
findings 🔴/🟠.

**VS-2-07** · tail — **Self review** (diff) · **Requirements validation** (spec §DoD couverte) · **Full verification** :
`dotnet build` (0 warning) + `dotnet test` (tous verts) + Sandbox `planet-drop` headless (0 validation, 0 leak) +
capture reproductible + probe AOT PASS + **verdict visuel humain**.

## DAG

```
VS-2-01 ─► VS-2-02 ─► VS-2-03 ─┬─► VS-2-04 ─┐
                               └─► VS-2-05 ─┴─► VS-2-06 ─► VS-2-07
Wave 1 (séquentiel) ───────────  Wave 2 (∥ safe) ─────────  Wave 3 (tail)
```

## Rollback Point
Avant que Wave 1 touche un fichier : commit `3057bb4` (arbre propre, VS-1 poussé). Spec non-code déjà présente
(untracked, sans risque).

## Deferred Work
Dettes déclarées hors-scope (spec §Debts) : orbites (Euler+float velocity), n-body/attraction mutuelle, friction,
terrain non-sphérique, multi-attracteur composant, broadphase cellule planétaire.
Ajoutées par l'audit : **m2** pas de lifetime/rest-cull des corps runtime → croissance non bornée (à traiter pour
l'ancre persistante). **Note architect sur M1** : la garde `r2>ε` retourne accel=0 au barycentre (sain) ; pour
l'univers persistant, envisager un échec *bruyant* si un corps mobile atteint le centre (politique « fail loudly »).

## Clôture (CONVERGE)
Double audit findings appliqués · verdict humain · maj AVANCEMENT/BACKLOG/CLAUDE · board archivé
`archive/board-session23-VS2.md` · commit sur demande.
