namespace Agapanthe.Core;

/// <summary>
/// Opaque handle to a mesh owned by the render-side resource registry (spec §3.2). The world and the render
/// lists carry handles, never GPU objects, so no GPU type ever crosses into <c>Agapanthe.World</c>.
/// <para>
/// A handle is a slot index PLUS a generation. The generation is what makes a stale handle detectable: when a
/// model is unloaded its slots are recycled with a bumped generation, so a handle kept by an entity that was not
/// cleaned up resolves to a <b>mismatch</b> — an explicit error (spec §4) — instead of silently resolving to
/// whatever resource now occupies that slot. Without it, an index-only handle would quietly draw the wrong mesh
/// after a reload, which is exactly the failure streaming would produce (audit P2-M2).
/// </para>
/// </summary>
public readonly record struct MeshHandle(int Index, uint Generation)
{
    /// <summary>The sentinel for "no mesh". Resolving it is a hard error (spec §4), not a silent skip.</summary>
    public static MeshHandle Invalid => new(-1, 0);

    public bool IsValid => Index >= 0;
}

/// <summary>Opaque handle to a material owned by the render-side resource registry. See <see cref="MeshHandle"/>.</summary>
public readonly record struct MaterialHandle(int Index, uint Generation)
{
    public static MaterialHandle Invalid => new(-1, 0);

    public bool IsValid => Index >= 0;
}
