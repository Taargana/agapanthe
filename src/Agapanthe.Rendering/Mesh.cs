using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Assets.Model;
using Agapanthe.Graphics;
using Agapanthe.Graphics.Memory;

namespace Agapanthe.Rendering;

/// <summary>
/// One drawable primitive's geometry on the GPU: a device-local vertex buffer and index buffer, filled
/// once at load through the staging <see cref="GpuUploader"/>. The CPU-side structure-of-arrays of a
/// <see cref="MeshAsset"/> is interleaved into <see cref="Vertex"/> here (the Assets layer must not depend
/// on Rendering's vertex format, so the interleave lives on this side).
/// <para>
/// <b>Ownership.</b> Owns its two <see cref="GpuBuffer"/>s and nothing else; the world transform and
/// material index are plain values copied from the asset. <see cref="Dispose"/> releases both buffers
/// (deferred through the device DeletionQueue by <see cref="GpuBuffer"/> itself).
/// </para>
/// <para>
/// <b>Conventions.</b> Positions/normals are in the mesh's local space; the local-to-world matrix belongs to the
/// entity that references this mesh (the ECS <c>WorldTransform</c> component), not to the mesh itself.
/// Tangent <c>w</c> is glTF handedness: <c>bitangent = w · cross(normal, tangent.xyz)</c>.
/// </para>
/// </summary>
public sealed class Mesh : IDisposable
{
    private GpuBuffer _vertexBuffer;
    private GpuBuffer _indexBuffer;
    private bool _disposed;

    private Mesh(
        GpuBuffer vertexBuffer, GpuBuffer indexBuffer, IndexFormat indexFormat, uint indexCount,
        int materialIndex, string name)
    {
        _vertexBuffer = vertexBuffer;
        _indexBuffer = indexBuffer;
        IndexFormat = indexFormat;
        IndexCount = indexCount;
        MaterialIndex = materialIndex;
        Name = name;
    }

    /// <summary>Device-local vertex buffer (interleaved <see cref="Vertex"/>, 60-byte stride).</summary>
    public GpuBuffer VertexBuffer => _vertexBuffer;

    /// <summary>Device-local index buffer, element width given by <see cref="IndexFormat"/>.</summary>
    public GpuBuffer IndexBuffer => _indexBuffer;

    /// <summary>Index element width, narrowed to <see cref="IndexFormat.UInt16"/> when every index fits (see <see cref="ChooseIndexFormat"/>).</summary>
    public IndexFormat IndexFormat { get; }

    /// <summary>Number of indices to draw.</summary>
    public uint IndexCount { get; }

    // The local-to-world matrix used to live here. It is now carried by the ECS (the entity's WorldTransform
    // component, baked into RenderItem by the render-list builder), and the draw loop reads it from there.
    // Keeping a copy on the GPU mesh would be a second source of truth for the same matrix, free to diverge from
    // the world the day anything moves (audit M2, minor 3).

    /// <summary>Material index into <see cref="ModelAsset.Materials"/>, or <c>-1</c> for the engine default material.</summary>
    public int MaterialIndex { get; }

    /// <summary>Mesh/primitive name for diagnostics.</summary>
    public string Name { get; }

    /// <summary>
    /// Picks the narrowest index element width that fits: <see cref="IndexFormat.UInt16"/> when every index
    /// is ≤ <see cref="ushort.MaxValue"/> (65535), otherwise <see cref="IndexFormat.UInt32"/>. Halving the
    /// index buffer for typical meshes is a free memory/bandwidth win. Pure and GPU-free (unit-testable).
    /// </summary>
    public static IndexFormat ChooseIndexFormat(ReadOnlySpan<uint> indices)
    {
        foreach (var index in indices)
        {
            if (index > ushort.MaxValue)
            {
                return IndexFormat.UInt32;
            }
        }

        return IndexFormat.UInt16;
    }

    /// <summary>
    /// Builds a GPU mesh from <paramref name="asset"/>: interleaves the SoA streams into
    /// <see cref="Vertex"/> (missing streams get sensible defaults — color white <c>(1,1,1)</c>, normal
    /// <c>+Y</c>, uv <c>(0,0)</c>, tangent <c>(1,0,0,1)</c>; glTF in our subset carries no vertex color, so
    /// color is always white), narrows the indices to u16 when possible, creates device-local buffers and
    /// uploads both through <paramref name="uploader"/> (synchronous staging copy).
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="asset"/> has no positions or no indices.</exception>
    public static Mesh Create(GraphicsDevice device, GpuUploader uploader, MeshAsset asset)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(uploader);
        ArgumentNullException.ThrowIfNull(asset);

        var positions = asset.Positions;
        if (positions.Length == 0)
        {
            throw new ArgumentException($"MeshAsset '{asset.Name}' has no positions.", nameof(asset));
        }

        if (asset.Indices.Length == 0)
        {
            throw new ArgumentException($"MeshAsset '{asset.Name}' has no indices.", nameof(asset));
        }

        var vertices = Interleave(asset);
        var indexFormat = ChooseIndexFormat(asset.Indices);
        var indexCount = (uint)asset.Indices.Length;

        GpuBuffer? vertexBuffer = null;
        GpuBuffer? indexBuffer = null;
        try
        {
            var vertexBytes = (ulong)(vertices.Length * Unsafe.SizeOf<Vertex>());
            vertexBuffer = new GpuBuffer(device, vertexBytes, BufferUsage.Vertex, MemoryDomain.DeviceLocal);
            uploader.Upload<Vertex>(vertexBuffer, vertices);

            if (indexFormat == IndexFormat.UInt16)
            {
                var narrow = new ushort[asset.Indices.Length];
                for (var i = 0; i < narrow.Length; i++)
                {
                    narrow[i] = (ushort)asset.Indices[i];
                }

                indexBuffer = new GpuBuffer(device, (ulong)(narrow.Length * sizeof(ushort)), BufferUsage.Index, MemoryDomain.DeviceLocal);
                uploader.Upload<ushort>(indexBuffer, narrow);
            }
            else
            {
                indexBuffer = new GpuBuffer(device, (ulong)(asset.Indices.Length * sizeof(uint)), BufferUsage.Index, MemoryDomain.DeviceLocal);
                uploader.Upload<uint>(indexBuffer, asset.Indices);
            }

            return new Mesh(
                vertexBuffer, indexBuffer, indexFormat, indexCount,
                asset.MaterialIndex, asset.Name);
        }
        catch
        {
            // A buffer created before the failure must not leak: dispose whatever was built.
            indexBuffer?.Dispose();
            vertexBuffer?.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Interleaves the parallel per-attribute arrays into a flat <see cref="Vertex"/> array. Present streams
    /// are copied element-wise; absent streams (length 0) are filled with the documented defaults.
    /// </summary>
    private static Vertex[] Interleave(MeshAsset asset)
    {
        var positions = asset.Positions;
        var normals = asset.Normals;
        var tangents = asset.Tangents;
        var uvs = asset.Uvs;

        var hasNormals = normals.Length == positions.Length;
        var hasTangents = tangents.Length == positions.Length;
        var hasUvs = uvs.Length == positions.Length;

        var color = Vector3.One; // glTF subset has no vertex color; white is the neutral tint.
        var defaultNormal = Vector3.UnitY;
        var defaultTangent = new Vector4(1f, 0f, 0f, 1f);

        var vertices = new Vertex[positions.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            vertices[i] = new Vertex(
                positions[i],
                color,
                hasNormals ? normals[i] : defaultNormal,
                hasUvs ? uvs[i] : Vector2.Zero,
                hasTangents ? tangents[i] : defaultTangent);
        }

        return vertices;
    }

    /// <summary>Releases both GPU buffers (deferred through the device DeletionQueue).</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _indexBuffer?.Dispose();
        _vertexBuffer?.Dispose();
        _indexBuffer = null!;
        _vertexBuffer = null!;
    }
}
