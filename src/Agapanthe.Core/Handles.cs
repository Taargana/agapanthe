namespace Agapanthe.Core;

/// <summary>
/// Opaque handle to a mesh owned by the render-side resource registry (spec §3.2). The world and the render
/// lists carry handles, never GPU objects, so no GPU type ever crosses into <c>Agapanthe.World</c>. The handle
/// is a plain index into the registry's mesh table; it is resolved back to a GPU mesh only at draw time.
/// </summary>
public readonly record struct MeshHandle(int Index)
{
    /// <summary>The sentinel for "no mesh". A registry lookup on it is a hard error (spec §4), not a silent skip.</summary>
    public static MeshHandle Invalid => new(-1);

    public bool IsValid => Index >= 0;
}

/// <summary>Opaque handle to a material owned by the render-side resource registry (spec §3.2). See <see cref="MeshHandle"/>.</summary>
public readonly record struct MaterialHandle(int Index)
{
    public static MaterialHandle Invalid => new(-1);

    public bool IsValid => Index >= 0;
}
