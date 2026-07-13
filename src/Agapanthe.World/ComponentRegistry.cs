namespace Agapanthe.World;

/// <summary>
/// The single source of truth for the set of ECS component types, and the NativeAOT rooting shim (spec §6.1).
/// <para>
/// Under NativeAOT the ILC only emits generic code it can see instantiated. Arch stores each component in a
/// <c>T[]</c> chunk array reached through generic paths the ILC does not pre-generate, so the first
/// <c>World.Create&lt;…&gt;</c> throws <c>'T[]' is missing native code</c> — with NO publish warning, and it can
/// corrupt state mid-operation. P2-M0 proved empirically that a single <c>new T[1]</c> per component type roots
/// that array for every downstream path (Create, Add/Remove structural moves, queries, CommandBuffer). <see
/// cref="RootAll"/> does exactly that and is called from the <see cref="GameWorld"/> constructor before any
/// entity exists.
/// </para>
/// <para>
/// This registry is hand-written rather than source-generated: the rooting is a trivial, proven one-liner per
/// type, and a reflection test (<c>ComponentRegistryTests</c>) asserts every <c>[Component]</c> struct in this
/// assembly is listed here — so forgetting one fails the test gate, not AOT at runtime. A source generator that
/// also drives serialization is deferred to Phase 3 (spec: "un seul générateur").
/// </para>
/// </summary>
public static class ComponentRegistry
{
    /// <summary>Every registered component type. Kept in sync with the <c>[Component]</c> structs by a reflection test.</summary>
    public static IReadOnlyList<Type> All { get; } =
    [
        typeof(GlobalId),
        typeof(LocalTransform),
        typeof(Parent),
        typeof(WorldTransform),
        typeof(MeshRef),
        typeof(Bounds),
        typeof(RenderOrder),
    ];

    /// <summary>
    /// Roots each component's <c>T[]</c> chunk-array type for the ILC (spec §6.1). One <c>new T[1]</c> per type,
    /// kept alive so the allocation is not elided. Must run before any entity is created — see <see cref="GameWorld"/>.
    /// </summary>
    public static void RootAll()
    {
        GC.KeepAlive(new GlobalId[1]);
        GC.KeepAlive(new LocalTransform[1]);
        GC.KeepAlive(new Parent[1]);
        GC.KeepAlive(new WorldTransform[1]);
        GC.KeepAlive(new MeshRef[1]);
        GC.KeepAlive(new Bounds[1]);
        GC.KeepAlive(new RenderOrder[1]);
    }
}
