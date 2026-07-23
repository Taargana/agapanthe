using Arch.Core;

namespace Agapanthe.World;

/// <summary>
/// The single source of truth for the set of ECS component types, and the NativeAOT rooting shim (spec §6.1).
/// <para>
/// Under NativeAOT the ILC only emits generic code it can see instantiated. Arch stores each component in a
/// <c>T[]</c> chunk array reached through generic paths the ILC does not pre-generate, so the first
/// <c>World.Create&lt;…&gt;</c> throws <c>'T[]' is missing native code</c> — with NO publish warning, and it can
/// corrupt state mid-operation. P2-M0 proved empirically that a single <c>new T[]</c> per component type roots
/// that array for every downstream path (Create, Add/Remove structural moves, queries, CommandBuffer).
/// </para>
/// <para>
/// Rooting and registration are ONE operation (<see cref="Root{T}"/>): a type is added to <see cref="All"/>
/// only by the same call that roots its array, so the list of components and the set of rooted arrays can never
/// desync (audit W1 M1). A reflection test additionally asserts every <c>[Component]</c> struct is present here,
/// so forgetting a component fails the test gate rather than AOT at runtime. This is hand-written rather than
/// source-generated on purpose; a source generator that also drives serialization is deferred to Phase 3
/// (spec: "un seul générateur").
/// </para>
/// </summary>
public static class ComponentRegistry
{
    private static readonly Lock InitLock = new();
    private static readonly List<Type> Components = new(8);
    private static readonly IReadOnlyList<Type> ReadOnlyComponents = Components.AsReadOnly();
    private static volatile bool _initialized;

    /// <summary>Every registered component type. Accessing it guarantees <see cref="RootAll"/> has run.</summary>
    /// <remarks>Wrapped, not the backing list: an <c>IReadOnlyList</c> returning the live <c>List</c> could be
    /// cast back and mutated by a caller (audit M2, minor).</remarks>
    public static IReadOnlyList<Type> All
    {
        get
        {
            EnsureInitialized();
            return ReadOnlyComponents;
        }
    }

    /// <summary>
    /// Roots every component's <c>T[]</c> chunk-array type for the ILC (spec §6.1). Idempotent. Called from the
    /// <see cref="GameWorld"/> constructor before any entity exists. Adding a component here (via one
    /// <see cref="Root{T}"/> line) is the only way to register it — so it cannot be listed without being rooted.
    /// </summary>
    public static void RootAll() => EnsureInitialized();

    private static void EnsureInitialized()
    {
        if (_initialized)
        {
            return;
        }

        // Double-checked under a lock: two threads constructing a GameWorld concurrently would otherwise both
        // run the body and Add to the same List (duplicated/torn entries). _initialized is volatile so the fully
        // populated list is published before the flag is observed.
        lock (InitLock)
        {
            if (_initialized)
            {
                return;
            }

            // The one and only place the component set is enumerated. Each Root<T> both roots T[] and registers T.
            Root<GlobalId>();
            Root<LocalTransform>();
            Root<Parent>();
            Root<WorldTransform>();
            Root<WorldPosition>();
            Root<MeshRef>();
            Root<Bounds>();
            Root<RenderOrder>();
            Root<Velocity>();
            Root<RigidBody>();
            Root<NoShadowCast>();
            Root<InstanceSlot>();

            _initialized = true;
        }
    }

    private static void Root<T>()
        where T : struct
    {
        // `new T[]` emits a `newarr T[]` opcode with a concrete (struct = exact, non-shared) instantiation, which
        // is what makes the ILC keep the T[] type — it is the reachability of the opcode, not the survival of the
        // instance, that roots the array. GC.KeepAlive only stops the (harmless) allocation being collected early.
        GC.KeepAlive(new T[1]);

        // Force Arch to assign this component's type id HERE, inside our lock (audit M2: both auditors converged
        // on this). Arch assigns those ids in process-global state on first touch, WITHOUT a lock: left lazy, the
        // first touch happened later — at World.Create, off this lock — so two worlds built concurrently could
        // race and end up with mismatched chunk arrays (a component read back as all-zero: observed,
        // reproducible). Touching Component<T>.ComponentType here means every id is assigned exactly once,
        // serialized, before any world exists — the race is closed by construction rather than by convention.
        // (Creating the worlds themselves is still not thread-safe on Arch's side: see GameWorld's contract.)
        _ = Component<T>.ComponentType;

        Components.Add(typeof(T));
    }
}
