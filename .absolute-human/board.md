# Absolute-Human Board — Agapanthe Session 3 (M3 : Mémoire & Textures)

**Status**: in-progress
**Créé**: 2026-07-03
**Spec**: docs/plans/2026-07-02-graphics-engine-design.md §3.2, §3.4, §3.5, §6 (M3)
**Board persistence**: git-tracked
**Sessions passées**: S1 (M0+M1), S2 (M2) → .absolute-human/archive/

## Intake (design déjà fixé — pas de re-brainstorm)

- **Problème**: M2 alloue 1 VkDeviceMemory host-visible par buffer et la depth image en direct — pas viable au-delà (limite d'allocations, pas de device-local, pas de textures). M3 pose l'infrastructure mémoire définitive + le chemin texture complet.
- **Cible de sortie (spec §6 M3)**: mesh texturé (cube avec texture checkerboard sRGB + mipmaps), tests allocateur verts, stats mémoire visibles.
- **Décisions architecte S2 à acter en tête de session**:
  1. Upload device-local via API séparée (staging + copy), `Write<T>` reste host-visible only, jamais de submit caché.
  2. `MemoryDomain {DeviceLocal, HostVisible}` à la création de GpuBuffer.
  3. GpuImage : usage flags composables + `MipLevels` + disposal différé via DeletionQueue.
- **Audit S2 finding 2 à acter**: DeletionQueue sans closure capturante (struct de handles + destructeur statique) — libérations per-frame arrivent avec les textures.
- **Décision session**: texture M3 = checkerboard **procédural** (RGBA généré en code, format Rgba8Srgb). Le décodage fichier StbImageSharp appartient au module Assets (M4, avec glTF) — le chemin GPU (staging → device-local → mips → sampler) est identique et entièrement exercé.

## Project Conventions

- .NET 10, xUnit, TreatWarningsAsErrors, Nullable/ImplicitUsings enable, unsafe dans Graphics/Platform.
- Aucun type Vk*/Silk.NET.Vulkan hors module Graphics.
- Ownership déterministe, ResourceTracker Register/Unregister par handle, finalizer conditionnel au handle acquis.
- Run macOS: `DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox`, `AGAPANTHE_MAX_FRAMES=N` pour headless.
- Seam de test allocateur (spec §3.5): logique free-list contre `IMemoryBackend`, mock xUnit sans GPU.

## Rollback Point

`b6c9615` (fin session 2)

## Task Graph

```
W1: M3-01 Free-list allocator + IMemoryBackend + tests    M3-02 DeletionQueue non-capturante
              │                                                  │
W2: M3-03 VulkanMemoryBackend + GpuAllocator dans device [dep 01]
              │
W3: M3-04 GpuBuffer MemoryDomain via allocateur [dep 02,03]   M3-05 GpuImage refonte (usage flags, mips, allocateur, disposal différé) [dep 02,03]
              │                                                  │
W4: M3-06 Staging upload (buffers + images) [dep 04,05]
    M3-07 Mipmaps par blits [dep 05,06]
    M3-08 Sampler + set 1 per-material (pool persistant) + WriteCombinedImageSampler [dep 05]
              │
W5: M3-09 Sandbox cube texturé [dep 06,07,08]
W6 (tail): M3-10 self review · M3-11 requirements · M3-12 full verification
```

Fichiers partagés (ownership séquentiel) :
- DeletionQueue.cs / GraphicsDevice.cs → M3-02 puis M3-03 (séquentiel).
- GpuBuffer.cs → M3-04 seul. GpuImage.cs → M3-05 seul.
- GraphicsDevice.Commands.cs / upload → M3-06. FrameContext.cs / descriptors → M3-08.
- Program.cs + shaders → M3-09 seul.

## Waves

| Wave | Tâches | Exécution |
|---|---|---|
| 1 | M3-01, M3-02 | parallèle (fichiers disjoints) |
| 2 | M3-03 | seul (GraphicsDevice) |
| 3 | M3-04, M3-05 | parallèle (fichiers disjoints) |
| 4 | M3-06, M3-07, M3-08 | 06→07 séquentiel ; 08 parallèle |
| 5 | M3-09 | seul |
| 6 | M3-10, M3-11, M3-12 | tail |

## Tâches

### M3-01 — Free-list allocator + IMemoryBackend + tests [code+test, M] — done
**Résultat**: Memory/MemoryDomain.cs, Memory/IMemoryBackend.cs (`MemoryBlock(Id, MappedPointer nint)`, AllocateBlock/FreeBlock), Memory/FreeListAllocator.cs (`Suballocation(Block, Offset, Size, MappedPointer calculé)`, `AllocationStats`, first-fit + coalescing bidirectionnel, régions pavant [0,blockSize), bloc paresseux, dedicated > blockSize/2 taille exacte, stats à la demande, frag = 1 − plusGrosTrou/totalLibre). 19 tests verts (50 total), 0 warning. Blocs non-dédiés conservés jusqu'à Dispose (récup/défrag → phase 2).
Logique pure de suballocation (spec §3.5) : `IMemoryBackend` (AllocateBlock/FreeBlock opaques), blocs 64-256 Mo par memory type, free-list par bloc avec alignement, coalescing des voisins libres, fallback nouveau bloc, dedicated allocation au-delà d'un seuil (grosses images). Stats : octets alloués/utilisés par heap, nb blocs, fragmentation. Aucun appel Vulkan. Fichiers: Graphics/Memory/IMemoryBackend.cs, Graphics/Memory/FreeListAllocator.cs (new). Tests xUnit avec mock backend : alignement, coalescing, out-of-block → nouveau bloc, dedicated, stats. AC: tests verts sans GPU.

### M3-02 — DeletionQueue non-capturante [code, S] — done
**Résultat**: option (b) delegate statique caché (`Action<GraphicsDevice, DeletionPayload>` alloué une fois par type de ressource) — évite unsafe dans la file et dans les tests. `DeletionPayload` = 4× ulong (Handle0/1/2 + Offset réservé M3-04/05). Device stocké dans l'Entry struct → `Flush(long)`/`FlushAll()` signatures intactes. `Enqueue(Action)` conservée, documentée shutdown-only. GpuBuffer.Dispose migré (ordre/gardes identiques). Test zéro-alloc strict (GC.GetAllocatedBytesForCurrentThread, 0 octet/1024 enqueues après warmup). 55 tests verts, run 120 frames clean.

### M3-03 — VulkanMemoryBackend + GpuAllocator dans GraphicsDevice [code, M] — pending — OWNER GraphicsDevice
`VulkanMemoryBackend : IMemoryBackend` (vkAllocateMemory/vkFreeMemory, ResourceTracker "VkDeviceMemory", map persistant des blocs host-visible). `GpuAllocator` public (Allocate(requirements, MemoryDomain) → `GpuAllocation {DeviceMemory, Offset, Size, MappedPtr?}`, Free différé-compatible). Possédé par GraphicsDevice, stats loggées au shutdown (« stats mémoire visibles », spec §6). AC: build + run M2 inchangé (buffers pas encore migrés).

### M3-04 — GpuBuffer via allocateur + MemoryDomain [code, M] — pending — OWNER GpuBuffer.cs
`GpuBuffer(device, size, usage, MemoryDomain)`. HostVisible → mappé persistant via allocation, `Write<T>` autorisé. DeviceLocal → `Write<T>` jette, usage |= TransferDst. Dispose → DeletionQueue non-capturante (M3-02), libère la suballocation. AC: build, run M2 clean (UBO/vertex host-visible passent par l'allocateur).

### M3-05 — GpuImage refonte [code, M] — pending — OWNER GpuImage.cs
Remplace `ImagePurpose` par `ImageUsage` flags composables (Sampled|ColorAttachment|DepthAttachment|TransferSrc|TransferDst), ajoute `MipLevels` (param, view couvre tous les mips), mémoire via allocateur (dedicated pour render targets), disposal différé via DeletionQueue par défaut (+ `DestroyImmediately` interne pour depth swapchain-sized après WaitIdle). AC: build, depth M2 fonctionne.

### M3-06 — Staging upload [code, M] — pending — OWNER GraphicsDevice.Commands/Upload
Chemin d'upload explicite (décision architecte 1) : `GraphicsDevice.Upload` ou `UploadContext` — staging buffer HostVisible transient, command buffer one-shot (pool transfert ou graphics queue), `CopyBuffer`, `CopyBufferToImage` (+ transitions Undefined→TransferDst→ShaderReadOnly quand pas de mips), submit + fence wait (synchrone en M3, async plus tard), staging en DeletionQueue. AC: vertex/index cube uploadés en DeviceLocal, run clean.

### M3-07 — Mipmaps par blits [code, M] — pending
Génération de la chaîne de mips : boucle vkCmdBlitImage2 mip N→N+1 (linear filter), barriers par mip (TransferDst→TransferSrc), transition finale ShaderReadOnly. Intégré au chemin upload image (M3-06). Vérif support blit linéaire du format (fallback: pas de mips + warning). AC: image checkerboard avec chaîne complète, 0 validation.

### M3-08 — Sampler + set 1 per-material [code, M] — pending — OWNER FrameContext/descriptors
`Sampler` (filtres, mips, anisotropie si dispo, wrap), `DescriptorAllocator` persistant (pools grow-on-demand, spec §3.4) pour les sets per-material, `WriteCombinedImageSampler` (helper partagé avec FrameContext). Layout set 1 M3 minimal : binding 0 = baseColor combined sampler. AC: build, set allouable+écrivable.

### M3-09 — Sandbox cube texturé [code, M] — pending — OWNER Program.cs + shaders
Checkerboard procédural RGBA (Rgba8Srgb) → upload staging → mips → sampler ; vertex/index en DeviceLocal via staging ; UV consommé (attribut 3 + cube.vert/frag échantillonne baseColor × couleur). Stats allocateur loggées au shutdown. AC: run macOS — cube texturé net (mips visibles en éloignant la caméra), 0 validation, 0 leak, stats visibles.

### M3-10 — Self code review [test, S] — pending
Audit csharp-lowlevel (allocateur : races/alignement/fuites, hot path, DeletionQueue non-capturante prouvée) + graphics-3d (barriers mips/upload, layouts) + architecte (API allocateur/upload prête pour M4 glTF). AC: 0 finding critique.

### M3-11 — Requirements validation [test, S] — pending
Exigences spec §3.5 + §6 M3 cochées vs code. AC: toutes couvertes ou déférées documentées.

### M3-12 — Full verification [test, S] — pending
build 0 warning, dotnet test (dont tests allocateur), run Sandbox texturé (validation clean + no leaks + stats mémoire). Sortie collée au board. AC: tout vert.

## Deferred Work

- Décodage images fichier (StbImageSharp) + module Assets → M4 (avec glTF).
- Placeholders shadow/IBL 1×1 dans set 0 → M5 (quand le shader PBR les échantillonne).
- Upload async (queue transfert dédiée, sans fence wait synchrone) → si besoin perf, M8.
- Compaction/défragmentation allocateur → phase 2.

## Log

- 2026-07-03: session 3 ouverte. Board S2 archivé. DAG 12 tâches, 6 vagues. Décisions architecte S2 + finding 2 audit actés en W1-W3.
