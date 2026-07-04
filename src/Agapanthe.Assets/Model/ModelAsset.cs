namespace Agapanthe.Assets.Model;

/// <summary>
/// A fully decoded glTF model as CPU-side data — the hand-off contract between the loader
/// (M4-04/05/06) and Rendering's SceneBuilder (M4-09). Pure DTO: depends only on
/// <c>System.Numerics</c> and <c>Agapanthe.Core</c>, never on Graphics or Silk.NET, so parsing
/// is unit-testable without a GPU (spec §5).
/// <para>
/// Cross-references are resolved as flat integer indices, not object references, so the graph
/// stays acyclic and cheap to construct: <see cref="MeshAsset.MaterialIndex"/> points into
/// <see cref="Materials"/>, and each material's image slots point into <see cref="Images"/>.
/// </para>
/// </summary>
public sealed record ModelAsset
{
    /// <summary>Meshes with hierarchy already flattened into world transforms (one per node instance).</summary>
    public required IReadOnlyList<MeshAsset> Meshes { get; init; }

    /// <summary>Materials referenced by <see cref="MeshAsset.MaterialIndex"/>.</summary>
    public required IReadOnlyList<MaterialAsset> Materials { get; init; }

    /// <summary>Decoded RGBA8 images referenced by <see cref="MaterialAsset"/> image slots.</summary>
    public required IReadOnlyList<ImageAsset> Images { get; init; }

    /// <summary>Model name (glTF <c>asset</c>/scene name or the source file stem); diagnostics only.</summary>
    public string Name { get; init; } = string.Empty;
}
