using Agapanthe.Assets.Model;
using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// The engine-wide owner of GPU render resources, and the resolver of the opaque handles the world carries
/// (spec §3.2). This is the possession half of the old <c>Scene</c>: the world holds
/// <see cref="MeshHandle"/>/<see cref="MaterialHandle"/> and never a GPU type; the renderer resolves them at draw
/// time.
/// <para>
/// <b>Global, not per-model</b> (audit P2-M2): handles are slots in ONE table, so several models can be loaded at
/// once without their handles colliding — <c>MeshHandle(0)</c> of model A and of model B used to be the same
/// handle. That collision would have made M4's thousands of entities (drawn from a single render list)
/// unresolvable.
/// </para>
/// <para>
/// <b>Slots carry a generation.</b> <see cref="Unload"/> disposes a model's resources, frees its slots and bumps
/// their generation, so a handle held by an entity that outlived the unload resolves to a MISMATCH — an explicit
/// <see cref="GraphicsException"/> (spec §4) — rather than silently resolving to whatever now occupies the slot.
/// </para>
/// <para>
/// <b>Ownership.</b> Everything each load created: meshes, materials, textures, the three 1×1 placeholders and
/// the sampler cache. It does NOT own the <see cref="DescriptorAllocator"/> or the set-1 layout — those are the
/// Renderer's and outlive any model. Disposal releases models in reverse load order, each in the same reverse
/// creation order the old Scene used (materials → meshes → textures → placeholders → sampler cache), so the leak
/// gate sees exactly the same resources and teardown.
/// </para>
/// </summary>
public sealed class ResourceRegistry : IDisposable
{
    // The generational slot maps behind the handles (see SlotTable): a slot freed by Unload bumps its generation,
    // so handles minted before that free are permanently stale rather than silently resolving to a new resource.
    private readonly SlotTable<Mesh> _meshes = new("Mesh");
    private readonly SlotTable<Material> _materials = new("Material");

    // Loaded models, by id — the unit of ownership and of unloading. A null entry is an unloaded slot.
    private readonly List<LoadedModel?> _models = [];
    private bool _disposed;

    private sealed record LoadedModel(
        ModelResources Resources,
        MeshHandle[] MeshHandles,
        MaterialHandle[] MaterialHandles);

    /// <summary>
    /// Uploads <paramref name="model"/> to the GPU, registers its resources in the global slot tables, and
    /// returns the model's id (for <see cref="Unload"/>) plus one <see cref="ImportedEntitySpec"/> per drawable —
    /// the GPU-free description the world spawns entities from.
    /// <para>
    /// <paramref name="worldOrigin"/> places the model in the world, in double (spec §3.3): every drawable's
    /// position and bounds are offset by it. A model at 10 000 km is exact, because that offset is never narrowed
    /// to float — the camera origin is subtracted from it first, at draw time.
    /// </para>
    /// </summary>
    public (int ModelId, ImportedEntitySpec[] Specs) Load(
        GraphicsDevice device,
        ModelAsset model,
        DescriptorAllocator materialAllocator,
        DescriptorSetLayout materialSetLayout,
        Double3 worldOrigin = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The builder creates the GPU objects (and disposes them itself if it throws part-way); the registry
        // assigns the handles, so handle minting lives in exactly one place.
        var resources = SceneBuilder.Build(device, model, materialAllocator, materialSetLayout);

        var meshHandles = new MeshHandle[resources.Meshes.Length];
        for (var i = 0; i < resources.Meshes.Length; i++)
        {
            var (index, generation) = _meshes.Add(resources.Meshes[i]);
            meshHandles[i] = new MeshHandle(index, generation);
        }

        var materialHandles = new MaterialHandle[resources.Materials.Length];
        for (var i = 0; i < resources.Materials.Length; i++)
        {
            var (index, generation) = _materials.Add(resources.Materials[i]);
            materialHandles[i] = new MaterialHandle(index, generation);
        }

        var specs = new ImportedEntitySpec[resources.Entries.Length];
        for (var i = 0; i < specs.Length; i++)
        {
            var entry = resources.Entries[i];
            specs[i] = new ImportedEntitySpec(
                meshHandles[i],
                materialHandles[entry.LocalMaterialIndex],
                entry.Position + worldOrigin,
                entry.RotationScale,
                entry.BoundsMin + worldOrigin,
                entry.BoundsMax + worldOrigin,
                (uint)i);
        }

        _models.Add(new LoadedModel(resources, meshHandles, materialHandles));
        return (_models.Count - 1, specs);
    }

    /// <summary>
    /// Disposes a loaded model's GPU resources and frees its slots, bumping their generation so every handle it
    /// minted becomes permanently stale (resolving one is then an explicit error, not a wrong draw). Entities
    /// still referencing the model must be despawned by the caller — the registry cannot see the world.
    /// </summary>
    public void Unload(int modelId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)modelId >= (uint)_models.Count || _models[modelId] is not { } loaded)
        {
            throw new GraphicsException($"Model id {modelId} is not loaded.");
        }

        foreach (var handle in loaded.MaterialHandles)
        {
            _materials.Free(handle.Index);
        }

        foreach (var handle in loaded.MeshHandles)
        {
            _meshes.Free(handle.Index);
        }

        DisposeResources(loaded.Resources);
        _models[modelId] = null;
    }

    /// <summary>The name of a loaded model, for diagnostics.</summary>
    public string NameOf(int modelId)
        => (uint)modelId < (uint)_models.Count && _models[modelId] is { } loaded
            ? loaded.Resources.Name
            : throw new GraphicsException($"Model id {modelId} is not loaded.");

    /// <summary>
    /// Resolves a mesh handle. An out-of-range index, a freed slot, or a generation mismatch (a stale handle from
    /// an unloaded model) is a hard error (spec §4) — never a silently wrong draw.
    /// </summary>
    public Mesh Resolve(MeshHandle handle) => _meshes.Resolve(handle.Index, handle.Generation);

    /// <inheritdoc cref="Resolve(MeshHandle)"/>
    public Material Resolve(MaterialHandle handle) => _materials.Resolve(handle.Index, handle.Generation);

    /// <summary>Releases every loaded model, in reverse load order.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        for (var i = _models.Count - 1; i >= 0; i--)
        {
            if (_models[i] is { } loaded)
            {
                DisposeResources(loaded.Resources);
                _models[i] = null;
            }
        }
    }

    // Reverse creation order, identical to the old Scene's teardown: materials → meshes → textures →
    // placeholders → sampler cache. The leak gate depends on this being unchanged.
    private static void DisposeResources(ModelResources resources)
    {
        foreach (var material in resources.Materials)
        {
            material.Dispose();
        }

        foreach (var mesh in resources.Meshes)
        {
            mesh.Dispose();
        }

        foreach (var texture in resources.Textures)
        {
            texture.Dispose();
        }

        foreach (var placeholder in resources.Placeholders)
        {
            placeholder.Dispose();
        }

        resources.SamplerCache.Dispose();
    }
}
