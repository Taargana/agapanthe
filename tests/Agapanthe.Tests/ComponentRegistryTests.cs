using System.Reflection;
using Agapanthe.World;

namespace Agapanthe.Tests;

[Collection("World")]
public sealed class ComponentRegistryTests
{
    [Fact]
    public void Registry_ListsEveryComponentStruct_AndNothingExtra()
    {
        // The single-source-of-truth guarantee (spec §6.1): every [Component] struct in the World assembly must
        // be in ComponentRegistry.All, so RootAll roots its T[] under AOT. This reflection check turns "someone
        // added a component but forgot to register it" from a silent AOT-runtime crash into a failing test.
        var declared = typeof(GameWorld).Assembly
            .GetTypes()
            .Where(t => t.IsValueType && t.GetCustomAttribute<ComponentAttribute>() is not null)
            .ToHashSet();

        var registered = ComponentRegistry.All.ToHashSet();

        var missing = declared.Except(registered).ToList();
        var extra = registered.Except(declared).ToList();

        Assert.True(missing.Count == 0, $"[Component] structs not in ComponentRegistry.All: {string.Join(", ", missing.Select(t => t.Name))}");
        Assert.True(extra.Count == 0, $"ComponentRegistry.All entries that are not [Component] structs: {string.Join(", ", extra.Select(t => t.Name))}");
    }

    [Fact]
    public void ComponentRegistry_All_HasNoDuplicates()
    {
        Assert.Equal(ComponentRegistry.All.Count, ComponentRegistry.All.Distinct().Count());
    }

    [Fact]
    public void ComponentRegistry_All_MatchesTheFrozenOrder()
    {
        // VS-1 (R1): the world snapshot's presence mask is POSITIONAL — bit i means "component at index i of
        // ComponentRegistry.All". The exhaustiveness test above (a HashSet) is order-insensitive, so it would NOT
        // catch a reorder that silently reinterprets every existing save. This pins the order: ComponentRegistry.All
        // is append-only. If you must reorder or remove, bump GameWorld's SerializationVersion in the SAME change —
        // and update this frozen list deliberately.
        var expected = new[]
        {
            typeof(GlobalId), typeof(LocalTransform), typeof(Parent), typeof(WorldTransform), typeof(WorldPosition),
            typeof(MeshRef), typeof(Bounds), typeof(RenderOrder), typeof(Velocity), typeof(RigidBody),
            typeof(NoShadowCast), typeof(InstanceSlot),
        };

        Assert.True(
            ComponentRegistry.All.SequenceEqual(expected),
            "ComponentRegistry.All order changed. The snapshot format is positional — bump SerializationVersion and " +
            $"update this frozen list. Actual: [{string.Join(", ", ComponentRegistry.All.Select(t => t.Name))}]");
    }

    [Fact]
    public void ComponentRegistry_All_FitsInThePresenceMask()
    {
        // VS-1 (audit 🟠, spec §4): the snapshot's presence mask is a u32, so it holds at most 32 components. At the
        // 33rd, `1u << 32` wraps to `1u << 0` (C# masks the shift count) and silently corrupts GlobalId's presence
        // bit. This gate turns that far-off overflow into a failing test instead of silent save corruption.
        Assert.True(
            ComponentRegistry.All.Count <= 32,
            $"ComponentRegistry.All has {ComponentRegistry.All.Count} components; the u32 presence mask holds 32. " +
            "Widen the mask (and bump SerializationVersion) before adding more.");
    }

    [Fact]
    public void GameWorld_AotRootingSmoke_RunsUnderJit()
    {
        // JIT counterpart of the AotComponentProbe: proves the exercise logic is correct. The AOT proof itself
        // is the probe published with PublishAot (a JIT run cannot expose a missing-native-code array).
        using var world = new GameWorld();
        var iterated = world.AotRootingSmoke();
        Assert.True(iterated >= 9, $"expected >= 9 iterated entities, got {iterated}");
    }

    [Fact]
    public void GameWorld_AotSerializationSmoke_RunsUnderJit()
    {
        // JIT counterpart of the probe's serialization round-trip (VS-1): proves the save/load exercise (every
        // archetype, byte-identical) is correct. The AOT proof is the probe published with PublishAot.
        using var world = new GameWorld();
        var restored = world.AotSerializationSmoke();
        Assert.True(restored >= 5, $"expected >= 5 restored entities, got {restored}");
    }
}
