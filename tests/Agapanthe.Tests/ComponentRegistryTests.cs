using System.Reflection;
using Agapanthe.World;

namespace Agapanthe.Tests;

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
    public void GameWorld_AotRootingSmoke_RunsUnderJit()
    {
        // JIT counterpart of the AotComponentProbe: proves the exercise logic is correct. The AOT proof itself
        // is the probe published with PublishAot (a JIT run cannot expose a missing-native-code array).
        using var world = new GameWorld();
        var iterated = world.AotRootingSmoke();
        Assert.True(iterated >= 9, $"expected >= 9 iterated entities, got {iterated}");
    }
}
