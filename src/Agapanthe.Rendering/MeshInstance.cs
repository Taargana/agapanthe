namespace Agapanthe.Rendering;

/// <summary>
/// A drawable: a <see cref="Mesh"/> paired with the <see cref="Material"/> it is rendered with. The world
/// transform is <b>not</b> stored here — it lives on <see cref="Mesh.WorldTransform"/>, because in M4 the
/// glTF node hierarchy is flattened one transform per mesh and geometry is not yet instanced. When true
/// instancing arrives (the same mesh drawn at several transforms), the transform moves onto this struct.
/// <para>
/// Both members are references into the owning <see cref="Scene"/>; an instance owns nothing.
/// </para>
/// </summary>
public readonly record struct MeshInstance(Mesh Mesh, Material Material);
