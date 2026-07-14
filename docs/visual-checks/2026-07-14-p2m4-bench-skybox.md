# P2-M4 — verdict visuel : banc `grid:100x100` + skybox (W1)

**Date** : 2026-07-14 · **Machine** : Windows 11, RTX 5070 Ti (Vulkan 1.3 core) · **Build** : Debug (validation layers actives)
**Objet** : solder la vérif humaine due de P2-M4 (le banc 10k et le skybox W1 n'avaient été jugés que headless). Voir `docs/AVANCEMENT.md` § « Vérifs humaines encore dues ».

## Preuves headless (automatiques)

Banc `AGAPANTHE_SCENE=grid:100x100` + `AGAPANTHE_CULL_STATS=1`, 120 frames, capture de la dernière frame.

| Run | Visibles / total | Casters d'ombre | Alloc/frame | Leak | Validation |
|---|---|---|---|---|---|
| Origine | 2556 → 2559 / 10000 | 10000 | **0 B** | 0 (135) | 0 |
| Origine + 10 000 km (`WORLD_ORIGIN=10000000,0,0`) | 2556 → 2559 / 10000 | 10000 | **0 B** | 0 (135) | 0 |

- Captures : `2026-07-14-m4-bench-grid100-origin.png`, `2026-07-14-m4-bench-grid100-10Mkm.png`.
- Les deux captures diffèrent de **1,34 % des canaux (max Δ 237)** — attendu : l'offset `10 000 000` n'est **pas un multiple de la maille de 1024 m** → cas « indiscernable mais pas bit-exact » (D3). Le contenu (grille, cull, reflets) est identique ; seuls les bords/spéculaires sous-pixel bougent.
- Rendu vérifié à l'œil sur capture : rangées de casques en grille fuyant vers l'horizon, PBR/reflets nets, **skybox visible et cohérent**. Caméra dans la scène.

> Note perf : cull+collect ~78–92 ms/frame **en Debug + validation layers** — la cible perf est mesurée en Release (dette perf de M4 documentée, ~80 % = liste d'ombres à 10 000 casters).

## Verdict humain (en fenêtre) — À REMPLIR

Protocole : lancer sans `MAX_FRAMES`, se déplacer (WASD + souris).
```powershell
! $env:AGAPANTHE_SCENE="grid:100x100"; $env:AGAPANTHE_CULL_STATS="1"; dotnet run --project samples/Sandbox -c Debug
```
Points à juger :
- [ ] La grille se peuple/culle proprement quand la caméra tourne (pas de pop brutal, pas de trou).
- [ ] Le **skybox** (nouveau shader W1) est correct et **stable** (pas de tremblement/scintillement de l'environnement).
- [ ] Aucun tremblement de la géométrie au loin.

**Verdict** : **PASS with concerns** (humain, 2026-07-14, Windows/RTX 5070 Ti).
**Notes** : cull + skybox jugés corrects et stables en fenêtre. Concern = **FPS bas sur la grande grille** (manque d'optimisation ressenti). Attendu : (1) run **Debug + validation layers** → 74–92 ms/frame de cull+collect vs **3,7 ms JIT-Release / ~6 ms AOT** mesurés à la clôture M4 ; (2) dette perf **assumée** de M4 (pas de buffer d'instances persistant, 10 000 casters d'ombre non cullés, cull lumière conservateur). **Remboursement planifié : P3-M1** (buffer d'instances persistant + 2 dettes de culling). Aucun défaut de rendu.
