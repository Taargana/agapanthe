# Agapanthe

Moteur de jeu Vulkan en C# (.NET 10), cross-platform. Voir la spec de la phase graphique 3D :
`docs/plans/2026-07-02-graphics-engine-design.md`.

## Prérequis

Requiert le **.NET 10 SDK** sur toute plateforme, plus le runtime Vulkan :

### macOS (Apple Silicon / Intel)

Vulkan passe par MoltenVK (couche au-dessus de Metal), installé via Homebrew :

```sh
brew install molten-vk vulkan-loader vulkan-validationlayers vulkan-tools
```

### Windows

Installer le **[Vulkan SDK LunarG](https://vulkan.lunarg.com/sdk/home#windows)** (fournit le loader
`vulkan-1.dll` et les validation layers) et vérifier que les **pilotes GPU sont à jour** (ils fournissent
l'ICD Vulkan). Aucune variable d'environnement n'est nécessaire : le loader est trouvé via le PATH système.

## Build & test

```sh
dotnet build
dotnet test
```

## Lancer la Sandbox

### macOS

Le loader Vulkan de Homebrew est dans `/opt/homebrew/lib`, hors des chemins que GLFW sonde par défaut.
Exporte `DYLD_LIBRARY_PATH` pour que GLFW trouve `libvulkan` :

```sh
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox
# grille metallic×roughness (juge de paix IBL) :
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox -- MetalRoughSpheres.glb
```

### Windows

Le loader du SDK est sur le PATH — pas de préfixe :

```powershell
dotnet run --project samples/Sandbox
# grille metallic×roughness (juge de paix IBL) :
dotnet run --project samples/Sandbox -- MetalRoughSpheres.glb
```

### Env vars debug

`AGAPANTHE_MAX_FRAMES=N` (ferme après N frames, sortie 0 si 0 leak) · `AGAPANTHE_CAPTURE=out.ppm`
(dump HDR tonemappé) · `AGAPANTHE_VIEW="x,y,z"` (angle caméra) · `AGAPANTHE_HDRI=<path.hdr>`
(environnement IBL) · `AGAPANTHE_IBL_TEST=<préfixe>` (génère l'IBL headless).

Positionnement selon le shell : `VAR=valeur dotnet run …` (bash/zsh macOS) ·
`$env:VAR="valeur"; dotnet run …` (PowerShell) · `set VAR=valeur` puis `dotnet run …` (cmd).

## État

**Phase 1 : 7/8 jalons.** M0–M7 livrés — fondations, triangle, mesh 3D, allocateur GPU, glTF+textures,
PBR Cook-Torrance + 3 lumières HDR + ACES, shadow mapping directionnel, **IBL compute (cubemap /
irradiance / prefiltered / BRDF LUT) + skybox**. Zéro erreur de validation, zéro leak.
Prochain : M8 (hot reload shaders, debug labels, audit final). Contexte de reprise : `CLAUDE.md`
et `docs/AVANCEMENT.md`. Board : `.absolute-human/board.md`.
