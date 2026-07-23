# Protocole de vérification visuelle — P3-M7 (device-local + réduction du raster d'ombre 4×)

**Jalon** : P3-M7 — buffers GPU device-local (vague A) + réduction du raster d'ombre ~4× par plan de coupe
near-side en profondeur-vue (vague B).
**Spec** : [../plans/2026-07-23-p3m7-device-local-shadow-raster-design.md](../plans/2026-07-23-p3m7-device-local-shadow-raster-design.md)
**Double audit** : `csharp-lowlevel` **PASS**, `graphics-3d` **PASS with concerns** (findings appliqués).
**Machine de référence** : Windows 11 / RTX 5070 Ti (Vulkan 1.3 core). *(macOS/MoltenVK non validé — dette P3-M0.)*

---

## Pourquoi ce protocole (ce que l'audit demande de prouver à l'œil)

La vague A ne change **aucune** géométrie (mono bit-identical `4848F93F`, gates numériques verts) — rien à juger
visuellement. **Tout le risque visuel est dans la vague B.**

Le plan de coupe near-side rejette, d'une cascade lointaine, les casters dont la **profondeur-vue** est bien avant
sa tranche. Sa marge est indexée sur l'**épaisseur de tranche**, **pas** sur la **longueur d'ombre**. Conséquence
(finding 🟠 de l'audit graphique) : avec un **soleil bas/rasant**, les ombres s'allongent et le décalage
caster↔ombre en profondeur-vue grandit pour *tout* caster — une ombre légitime pourrait alors être **coupée**
(light leak sur les receveurs d'une cascade lointaine). Le banc par défaut a un soleil raide (cas favorable), donc
**le juge doit explicitement éprouver le cas soleil bas.**

**Ce qui est déjà prouvé sans l'œil** (rappel, ne pas re-tester) : mono bit-identical, GPU==CPU MATCH,
0 alloc/frame AOT, 0 leak, 0 validation, comptage d'ombre `≈ 1×/caster` (4×→1×), 325 tests, NativeAOT PASS,
A+B ~15,3 → ~8,0 ms.

---

## Préparation

```powershell
dotnet build -c Release
```

Contrôles utiles en session : **clic** capture la souris, **WASD + souris** vole, **Échap** libère/quitte,
**N** cycle les vues debug (dont `DEBUG_CASCADE` — teinte chaque cascade d'une couleur, pour *voir* où passe la
frontière), **+/−** exposition, **L** fait pivoter le soleil autour de l'axe **vertical** (azimut — ne baisse
**pas** l'élévation ; pour ça, utiliser `AGAPANTHE_SUN` ci-dessous).

---

## Prises à réaliser (verdict par prise : PASS / FAIL + note)

### Prise 1 — Grille, soleil par défaut (raide) — *régression générale*
```powershell
$env:AGAPANTHE_SCENE="grid:100x100"; dotnet run --project samples/Sandbox -c Release
```
Voler dans la foule de casques. **Attendu** : ombres nettes près **et** loin, cohérentes avec P3-M5/P3-M6 ;
**aucune ombre qui manque, poppe ou clignote** quand la caméra avance/recule (traversée de cascades). Le sol reçoit
mais ne projette pas (pas d'auto-ombrage). C'est le contrôle de non-régression : B ne doit rien casser au cas raide.
- [ ] **Verdict** : ______  Note : ______________________________________________

### Prise 2 — Grille, **soleil bas / rasant** — *le test décisif (finding 🟠)*
```powershell
$env:AGAPANTHE_SCENE="grid:100x100"; $env:AGAPANTHE_SUN="0.9,-0.15,-0.4"; dotnet run --project samples/Sandbox -c Release
```
(`AGAPANTHE_SUN="x,y,z"` = direction de propagation du soleil ; petit `|y|` = soleil proche de l'horizon → ombres
longues. Essayer aussi `"0.95,-0.1,-0.3"` encore plus rasant.)
**Ce qu'on cherche** : les longues ombres portées **arrivent-elles entières** sur les receveurs lointains, ou
sont-elles **tronquées / disparaissent-elles** à une distance donnée (une ligne où l'ombre se coupe = light leak du
near-cut) ? Avancer/reculer lentement : une ombre qui **apparaît/disparaît** au passage d'une frontière de cascade
est le mode de défaillance à épingler.
- [ ] **Verdict** : ______  Note : ______________________________________________
- Si FAIL : la marge du near-cut (`0.25 × épaisseur de tranche`, `ShadowFit.cs`) est trop serrée pour ce soleil →
  soit l'élargir, soit implémenter l'`UpstreamExtent` par cascade (déféré, [backlog §2.0bis](../BACKLOG.md)).

### Prise 3 — `DEBUG_CASCADE` sous soleil bas — *voir la frontière*
Même commande que la prise 2, puis presser **N** jusqu'à la vue `DEBUG_CASCADE` (le log annonce le nom de la vue).
Chaque cascade prend une teinte. **Attendu** : les bandes de cascade **se recouvrent** légèrement (fondu 10 %) sans
trou ; une ombre ne doit pas s'éteindre juste avant un changement de teinte. Confirme visuellement que le tuilage en
profondeur-vue est propre.
- [ ] **Verdict** : ______  Note : ______________________________________________

### Prise 4 — Casque seul (mono) — *sanity*
```powershell
dotnet run --project samples/Sandbox -c Release
```
**Attendu** : identique à P3-M6 (le caster est en cascade 0, exemptée du near-cut). Contrôle rapide que le cas
nominal n'a pas bougé.
- [ ] **Verdict** : ______  Note : ______________________________________________

---

## Décision

- [ ] **PASS** — B est signé ; nettoyer les env vars (`Remove-Item Env:AGAPANTHE_SCENE, Env:AGAPANTHE_SUN`).
- [ ] **PASS with concerns** — préciser ce qui est toléré et pourquoi : ____________________________
- [ ] **FAIL** — B rouvert ; détailler la prise et le symptôme : ____________________________

**Verdict humain** : __________  **Date** : __________  **Machine** : __________

> Rappel méthode : ce verdict est le dernier gate avant de marquer P3-M7 `done` (commits sur demande uniquement).
> Le cas soleil bas (prise 2) est **obligatoire** — c'est précisément le trou de couverture que l'audit a signalé.
