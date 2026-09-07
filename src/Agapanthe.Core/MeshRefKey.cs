namespace Agapanthe.Core;

/// <summary>
/// The serialised identity of one drawable (Contenu-1): which asset (<see cref="Key"/>) and which local mesh /
/// material index within it. A world snapshot stores this instead of the raw process-local
/// <see cref="MeshHandle"/>/<see cref="MaterialHandle"/>, so a reload can re-resolve the handles through the
/// registry regardless of asset load order.
/// </summary>
/// <param name="Key">The asset the drawable's mesh and material come from. <see cref="AssetKey.None"/> for a
/// drawable that could not be identified (a headless save, or a world with no real assets).</param>
/// <param name="LocalMesh">Index of the mesh within that asset's meshes.</param>
/// <param name="LocalMat">Index of the material within that asset's materials.</param>
public readonly record struct MeshRefKey(AssetKey Key, int LocalMesh, int LocalMat)
{
    /// <summary><c>(AssetKey.None, 0, 0)</c> — equals <c>default(MeshRefKey)</c>.</summary>
    public static MeshRefKey None => default;

    /// <summary>True when <see cref="Key"/> is <see cref="AssetKey.None"/>.</summary>
    public bool IsNone => Key.IsNone;
}
