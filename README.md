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

## Lancer la Sandbox (démo triangle — M1)

Le loader Vulkan de Homebrew est dans `/opt/homebrew/lib`, hors des chemins que GLFW
sonde par défaut. Exporte `DYLD_LIBRARY_PATH` pour que GLFW trouve `libvulkan` :

```sh
DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox
```

Variable optionnelle `AGAPANTHE_MAX_FRAMES=N` : ferme la fenêtre après N frames
(validation automatisée / headless). Sortie 0 si aucun leak de ressource GPU.

## État

Session 1 (jalons M0 + M1) : fondations + triangle rendu via dynamic rendering +
synchronization2, zéro erreur de validation, zéro leak. Board : `.absolute-human/board.md`.
