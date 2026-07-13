namespace Agapanthe.World;

/// <summary>
/// Marks a struct as an ECS component. It is the single source of truth for the set of component types: every
/// <c>[Component]</c> struct must be listed in <see cref="ComponentRegistry"/> so its <c>T[]</c> chunk array is
/// rooted for NativeAOT (spec §6.1). A test reflects over this attribute to prove the registry is exhaustive —
/// so adding a component without registering it fails the build's test gate, not silently at runtime under AOT.
/// </summary>
[AttributeUsage(AttributeTargets.Struct, Inherited = false)]
public sealed class ComponentAttribute : Attribute;
