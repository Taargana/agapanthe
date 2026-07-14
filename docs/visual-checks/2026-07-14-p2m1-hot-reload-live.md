# P2-M1 — verdict visuel : hot reload shaders live (Debug)

**Date** : 2026-07-14 · **Machine** : Windows 11, RTX 5070 Ti · **Build** : Debug
**Objet** : re-tester le hot reload live, non re-jugé depuis M8. Voir `docs/AVANCEMENT.md`.

## Preuve headless (automatique) — budget < 1 s

`AGAPANTHE_SHADER_RELOAD_TEST=1` force un reload des 4 passes et logge le wall-time (cache chaud) :

| Passe | Recompile + recréation pipeline |
|---|---|
| ShadowPass | 1,8 ms |
| ScenePass | 2,2 ms |
| SkyboxPass | 1,1 ms |
| TonemapPass | 0,9 ms |

- **≪ 1 s** (cible spec §6). 0 leak (147 ressources), 0 message de validation.
- `ShaderHotReloader: watching 7 shader file(s)` → watcher actif sur `shaders/`.

## Verdict humain (en fenêtre) — À REMPLIR

```powershell
! dotnet run --project samples/Sandbox -c Debug
```
Puis, app tournante, dans un éditeur :
1. Éditer un `.frag` de `shaders/` (p. ex. forcer une teinte dans `mesh.frag`) → sauvegarder.
2. Casser volontairement un shader (erreur de syntaxe) → sauvegarder.
3. Corriger → sauvegarder.

Points à juger :
- [ ] Édition valide → recompile + pipeline recréé **< 1 s**, le rendu change à l'écran.
- [ ] Shader cassé → log d'erreur shaderc précis, **ancien pipeline conservé**, pas de crash.
- [ ] Correction → reload normal, rendu correct.

**Verdict** : **PASS** (humain, 2026-07-14, Windows/RTX 5070 Ti).
**Notes** : reload live confirmé en fenêtre.
