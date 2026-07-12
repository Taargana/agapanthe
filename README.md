# Agapanthe

Moteur de jeu Vulkan en C# (.NET 10), cross-platform. Voir la spec de la phase graphique 3D :
`docs/plans/2026-07-02-graphics-engine-design.md`.

## Prérequis (machine de dev macOS)

Runtime Vulkan via Homebrew :

```sh
brew install molten-vk vulkan-loader vulkan-validationlayers vulkan-tools
```

## Build & test

```sh
dotnet build
dotnet test
```

## Lancer la Sandbox

Le loader Vulkan de Homebrew est dans `/opt/homebrew/lib`, hors des chemins que GLFW
sonde par défaut. Exporte `DYLD_LIBRARY_PATH` pour que GLFW trouve `libvulkan` :

```sh
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox
# grille metallic×roughness (juge de paix IBL) :
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox -- MetalRoughSpheres.glb
```

Env vars debug : `AGAPANTHE_MAX_FRAMES=N` (ferme après N frames, sortie 0 si 0 leak) ·
`AGAPANTHE_CAPTURE=out.ppm` (dump HDR tonemappé) · `AGAPANTHE_VIEW="x,y,z"` (angle caméra) ·
`AGAPANTHE_HDRI=<path.hdr>` (environnement IBL) · `AGAPANTHE_IBL_TEST=<préfixe>` (génère l'IBL headless).

## État

**Phase 1 : 7/8 jalons.** M0–M7 livrés — fondations, triangle, mesh 3D, allocateur GPU, glTF+textures,
PBR Cook-Torrance + 3 lumières HDR + ACES, shadow mapping directionnel, **IBL compute (cubemap /
irradiance / prefiltered / BRDF LUT) + skybox**. Zéro erreur de validation, zéro leak.
Prochain : M8 (hot reload shaders, debug labels, audit final). Contexte de reprise : `CLAUDE.md`
et `docs/AVANCEMENT.md`. Board : `.absolute-human/board.md`.
