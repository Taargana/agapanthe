using System.Runtime.CompilerServices;
using Agapanthe.World;

// AOT component-rooting probe (spec §6.1). Published as NativeAOT and run: it constructs a GameWorld (which
// roots every component's T[] chunk array) and exercises every component through the paths that trigger the
// "'T[]' is missing native code" failure — Create, structural Add/Remove, a query, and a deferred CommandBuffer
// change. If rooting is incomplete, Arch throws under AOT and this exits non-zero. Under NativeAOT
// IsDynamicCodeSupported must be False — that is the whole point (no JIT fallback to paper over a missing array).

Console.WriteLine($"AotComponentProbe — IsDynamicCodeSupported = {RuntimeFeature.IsDynamicCodeSupported}");
Console.WriteLine($"Registered components: {ComponentRegistry.All.Count}");
foreach (var t in ComponentRegistry.All)
{
    Console.WriteLine($"  - {t.Name}");
}

try
{
    using var world = new GameWorld();
    var iterated = world.AotRootingSmoke();
    Console.WriteLine($"AotRootingSmoke iterated {iterated} entities.");

    if (iterated < 9) // 8 imported + 1 deferred via CommandBuffer
    {
        Console.Error.WriteLine($"AotComponentProbe: FAIL — query iterated {iterated}, expected >= 9.");
        return 1;
    }
}
catch (Exception ex)
{
    // Under AOT a missing-native-code array surfaces here (e.g. MissingRuntimeArtifactException / TypeLoad).
    Console.Error.WriteLine($"AotComponentProbe: FAIL — {ex.GetType().FullName}: {ex.Message}");
    return 1;
}

Console.WriteLine("AotComponentProbe: PASS — component rooting sufficient under this configuration.");
return 0;
