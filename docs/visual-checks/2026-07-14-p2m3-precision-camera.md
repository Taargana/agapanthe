# P2-M3 — verdict visuel : précision grande distance + feel caméra

**Date** : 2026-07-14 · **Machine** : Windows 11, RTX 5070 Ti · **Build** : Debug
**Objet** : solder la vérif humaine due de P2-M3 (démo précision `WORLD_ORIGIN` + feel caméra / wrap du yaw). Voir `docs/AVANCEMENT.md`.

## Preuves headless (automatiques) — casque seul, 3 frames, capture vs origine

| Offset monde | Canaux différents / 2 764 804 | Max Δ | Leak | Validation |
|---|---|---|---|---|
| `10000000,10000000,10000000` (10 000 km, 3 axes — **non aligné** maille) | 25 569 (0,925 %) | 200 | 0 | 0 |
| `10000000000,0,0` (10 M km — **aligné** maille 1024) | **815 (0,029 %)** | 34 | 0 | 0 |
| `1000000000000000,0,0` (1e15) | 55 877 (2,021 %) | 248 | 0 | 0 |

Captures : `2026-07-14-m3-origin.png`, `2026-07-14-m3-1e7.png`, `2026-07-14-m3-1e15.png`.

**Découverte (confirme et précise D3)** : c'est **l'alignement sur la maille (snap 1024 m) qui gouverne le bit-exact, pas la magnitude**. Un offset aligné à **10 millions de km** (0,029 %) est plus proche du bit-exact qu'un offset non aligné à **10 000 km** (0,925 %). Tous deux restent sous-pixel/edge sur le casque spéculaire (indiscernables à l'œil).

**Anti-faux-positif** : le log imprime `eye at 9999999.99751842` — valeur qu'un `float` ne peut pas représenter (ULP ≈ 1 m à 1e7) → l'origine est réellement appliquée. À `1e15`, l'offset de 2,5 mm est **avalé** (`eye at 1000000000000000`, ULP ≈ 0,125 m) et la dérive devient **visible** (2 %, cadrage/orientation décalés) — la panne du `float` à 10 000 km, repoussée de 8 ordres de grandeur.

## Verdict humain (en fenêtre) — À REMPLIR

```powershell
# (a) feel caméra à l'origine
! dotnet run --project samples/Sandbox -c Debug
# (b) précision : indiscernable de l'origine
! $env:AGAPANTHE_WORLD_ORIGIN="10000000,10000000,10000000"; dotnet run --project samples/Sandbox -c Debug
# (c) rupture : 1e15 doit visiblement claquer
! $env:AGAPANTHE_WORLD_ORIGIN="1000000000000000,0,0"; dotnet run --project samples/Sandbox -c Debug
```
Points à juger :
- [ ] Feel caméra : lissage correct, **wrap du yaw** sans à-coup, sensibilité +/− cohérente.
- [ ] À 1e7 (3 axes) : image **indiscernable** de l'origine.
- [ ] À 1e15 : dégradation **visible** (précision qui claque) — comportement attendu.

**Verdict** : **PASS** (humain, 2026-07-14, Windows/RTX 5070 Ti).
**Notes** : feel caméra bon, image à 1e7 indiscernable de l'origine, dégradation visible à 1e15 — conforme aux attentes.
