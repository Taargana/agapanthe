using Agapanthe.Core;
using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// Owns the GPU resources a loaded model needs, and resolves the opaque handles the world carries back to them
/// (spec §3.2). This is the possession half of the old <c>Scene</c>: the world holds
/// <see cref="MeshHandle"/>/<see cref="MaterialHandle"/> and never a GPU type, and the renderer resolves them at
/// draw time.
/// <para>
/// <b>Ownership.</b> Everything the build created: all meshes, all materials, the texture <see cref="GpuImage"/>s,
/// the three shared 1×1 placeholders, and the <see cref="SamplerCache"/>. It does <b>not</b> own the
/// <see cref="DescriptorAllocator"/> or the set-1 layout — those are the Renderer's and outlive a model.
/// <see cref="Dispose"/> releases in the same reverse creation order the old Scene used (materials → meshes →
/// textures → placeholders → sampler cache), so the leak gate sees exactly the same resources and teardown.
/// </para>
/// </summary>
public sealed class ResourceRegistry : IDisposable
{
    private readonly Mesh[] _meshes;
    private readonly Material[] _materials;
    private readonly GpuImage[] _textures;
    private readonly GpuImage[] _placeholders;
    private readonly SamplerCache _samplerCache;
    private bool _disposed;

    internal ResourceRegistry(
        Mesh[] meshes,
        Material[] materials,
        GpuImage[] textures,
        GpuImage[] placeholders,
        SamplerCache samplerCache,
        string name)
    {
        _meshes = meshes;
        _materials = materials;
        _textures = textures;
        _placeholders = placeholders;
        _samplerCache = samplerCache;
        Name = name;
    }

    /// <summary>Model name, for diagnostics.</summary>
    public string Name { get; }

    /// <summary>Resolves a mesh handle. An invalid handle is a hard error (spec §4), never a silently skipped draw.</summary>
    public Mesh Resolve(MeshHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)handle.Index >= (uint)_meshes.Length)
        {
            throw new GraphicsException(
                $"Mesh handle {handle.Index} is out of range for registry '{Name}' ({_meshes.Length} meshes).");
        }

        return _meshes[handle.Index];
    }

    /// <inheritdoc cref="Resolve(MeshHandle)"/>
    public Material Resolve(MaterialHandle handle)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if ((uint)handle.Index >= (uint)_materials.Length)
        {
            throw new GraphicsException(
                $"Material handle {handle.Index} is out of range for registry '{Name}' ({_materials.Length} materials).");
        }

        return _materials[handle.Index];
    }

    /// <summary>Releases all owned GPU resources in reverse creation order (identical to the old Scene).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var material in _materials)
        {
            material.Dispose();
        }

        foreach (var mesh in _meshes)
        {
            mesh.Dispose();
        }

        foreach (var texture in _textures)
        {
            texture.Dispose();
        }

        foreach (var placeholder in _placeholders)
        {
            placeholder.Dispose();
        }

        _samplerCache.Dispose();
    }
}
