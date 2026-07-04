using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// A loaded model as GPU-ready render data: the list of drawable <see cref="MeshInstance"/>s plus every
/// resource they need. Produced by <see cref="SceneBuilder.Build"/> and consumed by the Renderer (M4-10),
/// which walks <see cref="Instances"/> and, per instance, binds the mesh buffers, the material's set 1,
/// and pushes the mesh world transform.
/// <para>
/// <b>Ownership.</b> A scene owns <i>everything</i> the build created: all meshes, all materials, the
/// texture <see cref="GpuImage"/>s (one per source image), the three shared 1×1 placeholders, and the
/// <see cref="SamplerCache"/>. It does <b>not</b> own the <see cref="DescriptorAllocator"/> or the set-1
/// layout — those are the Renderer's and outlive individual scenes. <see cref="Dispose"/> releases owned
/// resources in reverse creation order (materials → meshes → textures → placeholders → sampler cache);
/// GpuBuffer/GpuImage/Sampler destroys are deferred through the device DeletionQueue, so the caller drains
/// it (and waits idle) at shutdown as usual.
/// </para>
/// </summary>
public sealed class Scene : IDisposable
{
    private readonly MeshInstance[] _instances;
    private readonly Mesh[] _meshes;
    private readonly Material[] _materials;
    private readonly GpuImage[] _textures;
    private readonly GpuImage[] _placeholders;
    private readonly SamplerCache _samplerCache;
    private bool _disposed;

    internal Scene(
        MeshInstance[] instances,
        Mesh[] meshes,
        Material[] materials,
        GpuImage[] textures,
        GpuImage[] placeholders,
        SamplerCache samplerCache,
        string name)
    {
        _instances = instances;
        _meshes = meshes;
        _materials = materials;
        _textures = textures;
        _placeholders = placeholders;
        _samplerCache = samplerCache;
        Name = name;
    }

    /// <summary>The drawables, each a (mesh, resolved material) pair. Iteration order is the model's mesh order.</summary>
    public IReadOnlyList<MeshInstance> Instances => _instances;

    /// <summary>Model name for diagnostics.</summary>
    public string Name { get; }

    /// <summary>Releases all owned GPU resources in reverse creation order.</summary>
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
