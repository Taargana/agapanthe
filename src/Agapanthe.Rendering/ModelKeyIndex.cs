using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// The GPU-free half of stable asset identity (Contenu-1): a two-way map between an <see cref="AssetKey"/> and the
/// process-local <see cref="MeshHandle"/>/<see cref="MaterialHandle"/> slots a loaded model occupies.
/// <see cref="ResourceRegistry"/> composes one and keeps it in step with <c>Load</c>/<c>Unload</c>.
/// <para>
/// Split out of the registry so it is testable without a <c>GraphicsDevice</c>: it only ever touches handle
/// arrays, never a GPU object.
/// </para>
/// </summary>
internal sealed class ModelKeyIndex
{
    private readonly record struct Entry(MeshHandle[] Meshes, MaterialHandle[] Materials);

    private readonly Dictionary<AssetKey, Entry> _byKey = [];
    // Contenu-3a: after Identify() was deleted these hold no per-handle payload any more — Add() only asks
    // "is this handle already mapped?" (the MP-0b W4 atomic-collision guard). Sets, not maps.
    private readonly HashSet<MeshHandle> _mappedMeshes = [];
    private readonly HashSet<MaterialHandle> _mappedMaterials = [];

    /// <summary>True if <paramref name="key"/> is currently registered.</summary>
    public bool Contains(AssetKey key) => _byKey.ContainsKey(key);

    /// <summary>Registers a loaded model's handles under <paramref name="key"/>.</summary>
    /// <exception cref="ArgumentException"><paramref name="key"/> is <see cref="AssetKey.None"/>.</exception>
    /// <exception cref="GraphicsException"><paramref name="key"/> is already registered.</exception>
    public void Add(AssetKey key, MeshHandle[] meshHandles, MaterialHandle[] materialHandles)
    {
        if (key.IsNone)
        {
            throw new ArgumentException("An asset must be loaded under a non-None AssetKey.", nameof(key));
        }

        if (_byKey.ContainsKey(key))
        {
            throw new GraphicsException($"asset key '{key}' is already loaded.");
        }

        // Check every handle for a collision BEFORE mutating anything (audit — the class of the MP-0b W4 bug: a
        // silent indexer overwrite). A live handle belongs to exactly one loaded model — SlotTable bumps the
        // generation before recycling an index — so a collision is a real invariant break, and Add stays atomic:
        // it either registers the whole model or throws having changed nothing.
        foreach (var h in meshHandles)
        {
            if (_mappedMeshes.Contains(h))
            {
                throw new GraphicsException($"mesh handle {h} is already mapped — asset '{key}' collides with a loaded model.");
            }
        }

        foreach (var h in materialHandles)
        {
            if (_mappedMaterials.Contains(h))
            {
                throw new GraphicsException($"material handle {h} is already mapped — asset '{key}' collides with a loaded model.");
            }
        }

        _byKey.Add(key, new Entry(meshHandles, materialHandles));
        foreach (var h in meshHandles)
        {
            _mappedMeshes.Add(h);
        }

        foreach (var h in materialHandles)
        {
            _mappedMaterials.Add(h);
        }
    }

    /// <summary>Forgets <paramref name="key"/> and every handle it owned. Silent if it was never registered.</summary>
    public void Remove(AssetKey key)
    {
        if (!_byKey.Remove(key, out var entry))
        {
            return;
        }

        foreach (var handle in entry.Meshes)
        {
            _mappedMeshes.Remove(handle);
        }

        foreach (var handle in entry.Materials)
        {
            _mappedMaterials.Remove(handle);
        }
    }

    /// <summary>Drops everything (registry disposal).</summary>
    public void Clear()
    {
        _byKey.Clear();
        _mappedMeshes.Clear();
        _mappedMaterials.Clear();
    }

    // Contenu-3a: Identify() is gone — the entity carries its AssetRef from spawn, so Save no longer does a
    // handle→key lookup. _mappedMeshes / _mappedMaterials stay: Add() consults them for its atomic per-handle
    // collision check (the MP-0b W4 bug class), and Remove/Clear keep them in step.

    /// <summary>The current handles for an asset's local mesh/material — how a snapshot's MeshRef is restored.</summary>
    /// <exception cref="GraphicsException"><paramref name="key"/> is not loaded, or an index is out of range.</exception>
    public (MeshHandle Mesh, MaterialHandle Material) Resolve(AssetKey key, int localMesh, int localMat)
    {
        if (!_byKey.TryGetValue(key, out var entry))
        {
            throw new GraphicsException(
                $"snapshot references asset '{key}' which is not loaded — load it before restoring the world.");
        }

        if ((uint)localMesh >= (uint)entry.Meshes.Length || (uint)localMat >= (uint)entry.Materials.Length)
        {
            throw new GraphicsException(
                $"snapshot references asset '{key}' mesh {localMesh} / material {localMat}, but it has "
                + $"{entry.Meshes.Length} mesh(es) / {entry.Materials.Length} material(s).");
        }

        return (entry.Meshes[localMesh], entry.Materials[localMat]);
    }
}
