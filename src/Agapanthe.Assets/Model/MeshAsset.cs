using System.Numerics;

namespace Agapanthe.Assets.Model;

/// <summary>
/// One drawable primitive with its geometry laid out as parallel arrays (structure-of-arrays).
/// SoA is used deliberately rather than reusing <c>Rendering.Vertex</c>: the DTO layer must not
/// depend on Rendering/Graphics, and per-attribute arrays let the loader fill only the streams a
/// primitive actually provides and let <c>TangentGenerator</c> (M4-07) produce
/// <see cref="Tangents"/> in isolation. Rendering interleaves these into its GPU vertex.
/// <para>
/// Conventions (glTF 2.0): right-handed, Y-up, counter-clockwise front faces. Positions/normals
/// are in the mesh's local space; <see cref="WorldTransform"/> maps them to world space.
/// </para>
/// </summary>
public sealed record MeshAsset
{
    /// <summary>Vertex positions in local space. Always present (POSITION is required). Defines vertex count.</summary>
    public required Vector3[] Positions { get; init; }

    /// <summary>
    /// Per-vertex normals in local space, or empty if the primitive supplied none.
    /// When present, length equals <see cref="Positions"/>.
    /// </summary>
    public Vector3[] Normals { get; init; } = [];

    /// <summary>
    /// Per-vertex tangents. <c>xyz</c> = tangent direction, <c>w</c> = glTF handedness (±1) such that
    /// <c>bitangent = w · cross(normal, tangent.xyz)</c>. Empty means "not supplied" — M4-07 generates
    /// them when a normal map is present. When present, length equals <see cref="Positions"/>.
    /// </summary>
    public Vector4[] Tangents { get; init; } = [];

    /// <summary>Texture coordinates (TEXCOORD_0), or empty if absent. When present, length equals <see cref="Positions"/>.</summary>
    public Vector2[] Uvs { get; init; } = [];

    /// <summary>
    /// Triangle-list indices into the vertex arrays. Always widened to u32 in the DTO regardless of the
    /// source accessor width (u16 or u32) — a board decision keeping the CPU contract uniform; Rendering
    /// may narrow to u16 at upload time if it chooses.
    /// </summary>
    public required uint[] Indices { get; init; }

    /// <summary>Index into <see cref="ModelAsset.Materials"/>, or <c>-1</c> to use the engine default material.</summary>
    public int MaterialIndex { get; init; } = -1;

    /// <summary>
    /// Local-to-world transform, with the node hierarchy already flattened. Row-vector convention
    /// (<c>System.Numerics</c>): a point is transformed as <c>Vector3.Transform(p, WorldTransform)</c>.
    /// </summary>
    public Matrix4x4 WorldTransform { get; init; } = Matrix4x4.Identity;

    /// <summary>Mesh/primitive name for diagnostics; not required to be unique.</summary>
    public string Name { get; init; } = string.Empty;
}
