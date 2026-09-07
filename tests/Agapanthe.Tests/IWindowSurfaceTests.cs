using System.Reflection;
using Agapanthe.App;
using Agapanthe.Platform;

namespace Agapanthe.Tests;

/// <summary>
/// Agapanthe.App milestone — the <see cref="IWindow"/> abstraction must stay a 1:1 projection of
/// <see cref="EngineWindow"/>'s public surface, so <c>samples/Sandbox/EngineWindowAdapter</c> can forward it
/// member-for-member. (The spec's "test 5" targeted the adapter directly, but it is <c>internal</c> in an exe the
/// test project does not reference; asserting the drift-free relationship between the two public types it bridges
/// is the reachable equivalent.)
/// </summary>
public sealed class IWindowSurfaceTests
{
    [Fact]
    public void EveryIWindowMember_ExistsOnEngineWindow_ByNameAndShape()
    {
        var missing = new List<string>();

        foreach (var member in typeof(IWindow).GetMembers(BindingFlags.Public | BindingFlags.Instance))
        {
            switch (member)
            {
                case MethodInfo m when m.IsSpecialName: // property/event accessors — covered via the property/event
                    continue;

                case MethodInfo m:
                    var match = typeof(EngineWindow).GetMethod(
                        m.Name,
                        BindingFlags.Public | BindingFlags.Instance,
                        binder: null,
                        types: [.. m.GetParameters().Select(p => p.ParameterType)],
                        modifiers: null);
                    if (match is null || match.ReturnType != m.ReturnType)
                    {
                        missing.Add($"method {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name))})");
                    }

                    break;

                case PropertyInfo p:
                    var pp = typeof(EngineWindow).GetProperty(p.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (pp is null || pp.PropertyType != p.PropertyType
                        || (p.CanWrite && !pp.CanWrite))
                    {
                        missing.Add($"property {p.PropertyType.Name} {p.Name}{(p.CanWrite ? " { get; set; }" : " { get; }")}");
                    }

                    break;

                case EventInfo e:
                    var ee = typeof(EngineWindow).GetEvent(e.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (ee is null || ee.EventHandlerType != e.EventHandlerType)
                    {
                        missing.Add($"event {e.EventHandlerType?.Name} {e.Name}");
                    }

                    break;
            }
        }

        Assert.True(
            missing.Count == 0,
            $"IWindow has member(s) that EngineWindow cannot back (the adapter would not compile / would drift): "
            + string.Join("; ", missing));
    }

    [Fact]
    public void EngineWindow_IsNotItselfAnIWindow()
        // The whole point of the adapter: Agapanthe.Platform never references Agapanthe.App, so EngineWindow
        // cannot implement IWindow directly. If this ever becomes true, the acyclic-graph decision was undone.
        => Assert.False(typeof(IWindow).IsAssignableFrom(typeof(EngineWindow)));
}
