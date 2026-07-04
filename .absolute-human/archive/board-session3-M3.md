# Absolute-Human Board — Agapanthe Session 3 (M3 : Mémoire & Textures)

**Status**: completed
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

### M3-03 — VulkanMemoryBackend + GpuAllocator dans GraphicsDevice [code, M] — done — OWNER GraphicsDevice
**Résultat**: VulkanMemoryBackend (Id = handle u64 du VkDeviceMemory, map persistant du bloc entier si HostVisible, HashSet _liveBlocks pour validation), GpuAllocator public (`Allocate(in MemoryRequirementsInfo, MemoryDomain)` → `GpuAllocation {Offset, Size, MappedPointer nint, Domain}` + internes MemoryTypeIndex/Suballocation/DeviceMemory ; `Free` immédiat ; `GetStats`/`LogStats`), `device.Allocator`, ctor test-seam interne + InternalsVisibleTo. Teardown : WaitIdle → FlushAll → LogStats (#if DEBUG) → allocator.Dispose → ReleaseResources. 66 tests verts (+11), run clean, stats à vide logguées proprement.
`VulkanMemoryBackend : IMemoryBackend` (vkAllocateMemory/vkFreeMemory, ResourceTracker "VkDeviceMemory", map persistant des blocs host-visible). `GpuAllocator` public (Allocate(requirements, MemoryDomain) → `GpuAllocation {DeviceMemory, Offset, Size, MappedPtr?}`, Free différé-compatible). Possédé par GraphicsDevice, stats loggées au shutdown (« stats mémoire visibles », spec §6). AC: build + run M2 inchangé (buffers pas encore migrés).

### M3-04 — GpuBuffer via allocateur + MemoryDomain [code, M] — done — OWNER GpuBuffer.cs
**Résultat**: ctor + `MemoryDomain domain = HostVisible` (call sites M2 intacts), mémoire via Allocator, `Write<T>` = MappedPointer du bloc ; DeviceLocal → Write jette + TransferDst auto, `Domain` exposé. **Free différé zéro-alloc sans registre** : une suballocation est identifiée par (memoryTypeIndex, blockId=VkDeviceMemory handle, offset) — Size non consulté par Free → payload = Handle0 buffer, Handle1 memory, Handle2 memTypeIndex, Offset offset ; GpuAllocation reconstruite dans le destructeur statique. Contrat verrouillé par test (échoue si Free exige la taille un jour). Plus de map/unmap par buffer. 69 tests verts (+3), run clean 33 ressources. Vérif intégrée W3 (état combiné 04+05) : build 0 warning, 69 tests, run 0 validation 0 leak.

### M3-05 — GpuImage refonte [code, M] — done — OWNER GpuImage.cs
**Résultat**: `ImageUsage` [Flags] (bits explicites) remplace ImagePurpose ; ctor (w, h, format, usage, mipLevels=1), view [0, MipLevels), `FullMipChain()`, `Aspect` interne dérivé ; mémoire via Allocator DeviceLocal (dedicated = seuil free-list) ; `DestroyImmediately()` interne pour la depth (toujours post-WaitIdle, FrameRenderer adapté), `Dispose()` différé — **TODO-M3-06** : chemin différé sur surcharge legacy à closure (GpuAllocation ne tient pas dans DeletionPayload, pas de registre partagé) — à migrer zéro-alloc en W4. 66 tests verts, run clean (33 ressources — VkDeviceMemory compté par bloc désormais), stats : depth via allocateur, bloc DeviceLocal 64 MiB rendu (0 alloc au shutdown).

### M3-06 — Staging upload [code, M] — done — OWNER GpuUploader.cs
**Résultat**: `GpuUploader(device)` autonome (command pool transient + cmd buffer + fence réutilisés, zéro modif GraphicsDevice) : `Upload<T>(GpuBuffer DeviceLocal, span)` / `Upload<T>(GpuImage, span mip 0)`. Staging en Vulkan brut (BufferUsage n'a pas TransferSrc — encapsulé), mémoire via Allocator HostVisible, détruit immédiatement post-fence-wait. synchronization2 via helpers fallback existants. Synchrone assumé M3 (async → M8). Migration TODO-M3-05 : GpuImage.Dispose non-capturant — payload Handle0 image, Handle1 view, Handle2 blockId, Offset packé (40 bits offset + 24 bits memTypeIndex, garde-fous + round-trip testé aux bornes).

### M3-07 — Mipmaps par blits [code, M] — done
**Résultat**: vkCmdBlitImage core 1.0 (pas besoin de Blit2), filter Linear. Support format vérifié (BLIT_SRC|BLIT_DST|SAMPLED_IMAGE_FILTER_LINEAR sinon GraphicsException explicite, pas de fallback silencieux). Barriers : tous mips Undefined→TransferDst, copy mip 0, boucle blit i-1→i avec TransferDst→TransferSrc par mip, 2 barriers finaux →ShaderReadOnly. MipLevels==1 : chemin direct sans TransferSrc. `GpuImage.MipSize()` testé. 88 tests verts (+16 W4). Vérif intégrée W4 : 0 warning, 88 tests, run 0 validation/0 warn, 0 leak. Preuve runtime GPU du chemin upload+blits = M3-09.

### M3-08 — Sampler + set 1 per-material [code, M] — done — OWNER FrameContext/descriptors
**Résultat**: Sampler.cs (`SamplerDesc` record — Filter/MipFilter/AddressMode/MaxAnisotropy/MipLodBias, zéro-valeurs = défauts sains ; Dispose différé non-capturant, pattern GpuBuffer), DescriptorAllocator.cs (pools persistants grow-on-demand 64 sets, retry sur OutOfPoolMemory/Fragmented, possédé par l'appelant, Dispose post-WaitIdle ; contient `DescriptorWrites` interne statique partagé), FrameContext factorisé (`WriteUniformBuffer`/`WriteCombinedImageSampler` délèguent à DescriptorWrites), doc DescriptorSetHandle ajustée (validité = pool d'origine). **samplerAnisotropy PAS activée à la création du device** → aniso forcée off (chemin de clamp dormant, `Sampler.AnisotropyFeatureEnabled = false` documenté) — à activer dans GraphicsDevice si besoin M5+. 72 tests verts (+3), run clean.

### M3-09 — Sandbox cube texturé [code, M] — done — OWNER Program.cs + shaders
**Résultat**: checkerboard 256² ambre/teal (32 px), Rgba8Srgb, FullMipChain = 9 mips uploadés (copy mip 0 + blits) ; vertex/index DeviceLocal via GpuUploader (using-block, disposé post-upload) ; sampler défauts ; set 1 baseColor via DescriptorAllocator persistant écrit une fois ; pipeline SetLayouts [frame, material], VertexLayout [0 Position, 1 Color, 3 Uv] (Normal → M5) ; cube.frag module texture × couleur de face. Run 120 frames : EXIT=0, zéro VUID/warning, 43 ressources équilibrées, stats : bloc DeviceLocal 64 MiB (vertex+index+texture) + bloc host-visible (UBO+staging), tout rendu au FlushAll avant LogStats.
Checkerboard procédural RGBA (Rgba8Srgb) → upload staging → mips → sampler ; vertex/index en DeviceLocal via staging ; UV consommé (attribut 3 + cube.vert/frag échantillonne baseColor × couleur). Stats allocateur loggées au shutdown. AC: run macOS — cube texturé net (mips visibles en éloignant la caméra), 0 validation, 0 leak, stats visibles.

### M3-10 — Self code review [test, S] — done (2× PASS)
**csharp-lowlevel PASS (0 critique)** : free-list saine (padding = région libre récupérable, fusion triple correcte, dedicated offset 0 libéré entier, double-free détecté, contrat « Size ignorée » verrouillé par les 3 tests deferred-free) ; packing 40/24 sain (dedicated toujours offset 0 → jamais > 2^40, garde-fous + round-trip aux bornes) ; chemins d'erreur ctor/mid-upload équilibrés (staging en try/finally) ; hot path toujours zéro-alloc (CommandList readonly struct, callback hoisté, Flush sans alloc) ; ordres de vie corrects (flush avant allocator.Dispose, teardown Sandbox tracé). 2 observations MINEURES phase 2 : compteur de génération anti-ABA par région en #if DEBUG ; `entry.Device!` documenté (null impossible en prod).
**Architecte PASS** : section dédiée ci-dessous (décisions M4).

### M3-11 — Requirements validation [test, S] — done (tableau ci-dessous)
Exigences spec §3.5 + §6 M3 cochées vs code. AC: toutes couvertes ou déférées documentées.

### M3-12 — Full verification [test, S] — done
**Sortie (2026-07-04)** :
```
Build succeeded.    0 Warning(s)    0 Error(s)
Passed!  - Failed: 0, Passed: 88, Skipped: 0, Total: 88 - Agapanthe.Tests.dll (net10.0)

AGAPANTHE_MAX_FRAMES=240 DYLD_LIBRARY_PATH=/opt/homebrew/lib dotnet run --project samples/Sandbox :
GpuAllocator memory stats:
  type 0 [DeviceLocal]: 64.00 MiB allocated / 0.00 MiB used, 1 block(s), 0 alloc(s), frag 0.00
  type 1 [DeviceLocal|HostVisible|HostCoherent|HostCached]: 64.00 MiB, 1 block(s), 0 alloc(s)
  total: 128.00 MiB allocated across 2 block(s).
ResourceTracker: no leaks (43 resources created and destroyed).
EXIT=0 — zéro ERROR/WARN/VUID.
```
Vérification visuelle cube texturé + resize : manuelle par l'utilisateur (run non-headless).

## M3-11 — Validation des exigences (spec §3.5 + §6 M3)

| Exigence | État |
|---|---|
| Suballocation gros blocs par memory type, free-list, alignement | ✓ FreeListAllocator, blocs 64 MiB paresseux, first-fit + coalescing |
| Seam IMemoryBackend, tests sans GPU | ✓ mock xUnit, 19 tests free-list + 11 GpuAllocator + 3 deferred-free |
| Trois usages DeviceLocal/HostVisible/Staging | ✓ MemoryDomain {DeviceLocal, HostVisible} ; Staging = HostVisible transient encapsulé dans GpuUploader (enum à 2 domaines suffit — staging n'est pas un domaine mémoire distinct) |
| Dedicated allocation grosses images | ✓ seuil blockSize/2 dans le free-list (taille exacte, libéré entier) |
| Stats debug par heap (octets, blocs, fragmentation) | ✓ AllocationStats + LogStats au shutdown du device |
| Staging uploads (pas de submit caché dans Write<T>) | ✓ GpuUploader explicite, synchrone, fence ; Write<T> jette sur DeviceLocal |
| Images + mipmaps + samplers | ✓ GpuImage MipLevels + blits linéaires (support format vérifié), Sampler/SamplerDesc |
| sRGB vs linéaire | ✓ texture baseColor en Rgba8Srgb ; formats linéaires disponibles dans PixelFormat (exercés M4 avec normal/MR) |
| Mesh texturé (critère de sortie) | ✓ cube checkerboard 9 mips, set 1 per-material |
| Tests allocateur verts | ✓ 88 tests total |
| Stats mémoire visibles | ✓ log shutdown (voir M3-12) |
| Anisotropie sampler | Dormante — feature device non activée, chemin de clamp prêt (M5+ si besoin) |
| Upload async / queue transfert | Déféré M8 (Deferred Work) |

## Revue architecte M3-10 (PASS) — décisions à acter au démarrage M4

1. **GltfLoader/ImageLoader = DTO CPU, zéro dépendance Graphics** (tests unitaires sans GPU, spec §5). Rendering transforme les DTO en ressources GPU. Graphe : Assets → Core ; Rendering → Assets + Graphics.
2. **Créer dans Rendering : Mesh, Material, MeshInstance, Scene, Renderer/ForwardPass** — migrer le câblage manuel du Sandbox (layouts/sets/uploads/record). Sandbox → glue applicative.
3. **Ownership** : Material possède images/samplers/UBO facteurs + set persistant ; Mesh ses buffers ; Renderer le DescriptorAllocator. Teardown : WaitIdle → Dispose ressources (différé) → FlushAll → allocateur descriptors (synchrone).
4. **Figer le layout set 1 en forme PBR finale (6 bindings) dès M4** même si seul baseColor est rempli — stabilise layout + pool sizing jusqu'à M5.
5. **Cache de Sampler** dans Rendering (dedup configs glTF).
6. **Upload per-ressource synchrone conservé** — un seul `using` GpuUploader pour tout le load. Point clé relevé : le staging unique vivant recoalesce avant l'upload suivant → zéro fragmentation host-visible ; le batch la réintroduirait. Batch/async → M8, seulement si mesuré nécessaire.

À anticiper M5 (rien à faire M4) : généralisation FrameRenderer (target HDR off-screen + passe tonemap — plus gros item structurel, concevoir en tête de M5) ; pool sizing DescriptorAllocator proportionnel aux samplers/set (saturation CIS à ~12 sets sinon) ; activer samplerAnisotropy ; vérif format features du RT HDR (Rgba16Sfloat déjà mappé, GpuImage prêt).

Dette acceptable : MemoryDomain à 2 domaines (Staging piggyback HostVisible — assumé, revisiter si async M8) ; DescriptorSetHandle sémantique partagée frame/persistant (tag de génération si bug un jour) ; SamplerDesc AddressMode unique (U/V séparés si une fixture l'exige) ; dedup samplers → M4 (décision 5).

## Deferred Work

- Décodage images fichier (StbImageSharp) + module Assets → M4 (avec glTF).
- Placeholders shadow/IBL 1×1 dans set 0 → M5 (quand le shader PBR les échantillonne).
- Upload async (queue transfert dédiée, sans fence wait synchrone) → si besoin perf, M8.
- Compaction/défragmentation allocateur → phase 2.

## Log

- 2026-07-03: session 3 ouverte. Board S2 archivé. DAG 12 tâches, 6 vagues. Décisions architecte S2 + finding 2 audit actés en W1-W3.
- 2026-07-04: W1-W5 exécutées (commits eb8e37a, 581fb04, 2514bb2, 0b8914f, ed44264). M3-10 : 2× PASS (0 critique). M3-11/12 : 88 tests, run 240 frames 0 validation 0 leak, stats visibles. **Session 3 close — M3 atteint** (cube texturé, chaîne de mips GPU, allocateur testé). M4 : acter les 6 décisions architecte (DTO CPU, Mesh/Material/Scene/Renderer dans Rendering, set 1 figé PBR, cache samplers).
