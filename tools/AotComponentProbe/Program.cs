using System.Numerics;
using System.Runtime.CompilerServices;
using Agapanthe.Assets.Font;
using Agapanthe.Engine;
using Agapanthe.Ui;
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

    // VS-1: prove the save/load round-trip (per-component Add<T> dispatch + MemoryMarshal blittable paths) survives
    // NativeAOT and is byte-identical. A fresh world, so its identity counter is independent of the smoke above.
    using var serWorld = new GameWorld();
    var restored = serWorld.AotSerializationSmoke();
    Console.WriteLine($"AotSerializationSmoke restored {restored} entities (byte-identical round-trip).");
    if (restored < 5) // 3 drawables/body + 2 hierarchy nodes
    {
        Console.Error.WriteLine($"AotComponentProbe: FAIL — serialization restored {restored}, expected >= 5.");
        return 1;
    }

    // MP-0c: the fixed-step accumulator (audit LL F-4 — the milestone's AOT gate line must actually exercise the
    // type, not assume it). A plain sealed non-generic class driving SimulationHost.Tick; nothing exotic, but say
    // so by running it. Two profiles: one whole step (1 tick), one catch-up (3 ticks).
    using var accWorld = new GameWorld();
    var accHost = SimulationHost.CreateDefault(accWorld);
    var accumulator = new FixedTimestepAccumulator(1f / 60f);
    var oneStep = accumulator.Advance(accHost, accumulator.FixedDeltaSeconds);
    var catchUp = accumulator.Advance(accHost, 3f * accumulator.FixedDeltaSeconds);
    Console.WriteLine($"AotAccumulatorSmoke: {oneStep} + {catchUp} ticks, host at TickIndex {accHost.TickIndex}.");
    if (oneStep != 1 || catchUp != 3 || accHost.TickIndex != 4)
    {
        Console.Error.WriteLine(
            $"AotComponentProbe: FAIL — accumulator ran {oneStep}+{catchUp} ticks (TickIndex {accHost.TickIndex}), expected 1+3 (4).");
        return 1;
    }
}
catch (Exception ex)
{
    // Under AOT a missing-native-code array surfaces here (e.g. MissingRuntimeArtifactException / TypeLoad).
    Console.Error.WriteLine($"AotComponentProbe: FAIL — {ex.GetType().FullName}: {ex.Message}");
    return 1;
}

// UI-1: the text path under NativeAOT. Two things are being proven here.
//
// 1. The .agfont reader and the layout work: the format is blittable and read through MemoryMarshal, and the
//    layout is plain struct maths, so both SHOULD be AOT-safe by construction — but "should" is exactly what the
//    probe exists to replace. This is also the Release shape (cache-only assets), which is where the shader
//    pipeline has bitten before.
// 2. StbTrueTypeSharp is ABSENT. The font is rasterised offline by tools/FontCooker, which no shipping project
//    references, so the runtime links no font library at all. A stray ProjectReference would silently drag it
//    into the AOT closure — the same mistake the shaderc comments warn about.
try
{
    // The probe does not cook fonts (it has no CookFonts target and must not gain one — that would be build
    // duplication). The path is passed in, typically the Sandbox's cooked output; falling back to a local copy.
    var fontPath = args.Length > 0
        ? args[0]
        : Path.Combine(AppContext.BaseDirectory, "fonts", "JetBrainsMono-Regular.agfont");
    if (File.Exists(fontPath))
    {
        var font = FontAssetFormat.Read(File.ReadAllBytes(fontPath));
        var extent = TextLayout.Measure("Agapanthe 0123", font, pixelSize: 16f);
        var list = new UiDrawList();
        TextLayout.DrawText(list, "Agapanthe 0123\nsecond line", font, Vector2.Zero, 16f, 0xFFFFFFFFu);

        Console.WriteLine(
            $"AotTextSmoke: {font.Glyphs.Length} glyph(s), atlas {font.AtlasWidth}×{font.AtlasHeight}, "
            + $"measured {extent.Width:F1}×{extent.Height:F1} px, {list.Count} quad(s).");

        // UI-2: the profiler's pure types travel the same AOT path. No reflection, no dynamic generics — but the
        // probe IS the declared AOT gate, so they are exercised rather than assumed.
        var series = new FrameSeries(8);
        for (var i = 0; i < 12; i++)
        {
            series.Record(i);
        }

        Span<float> graph = stackalloc float[8];
        var samples = series.CopyChronological(graph);
        Sparkline.Draw(list, samples, new System.Numerics.Vector4(0f, 0f, 8f, 8f), font.WhiteTexelUv, 0xFFFFFFFFu, 16f);
        Console.WriteLine($"AotProfilerSmoke: {samples.Length} sample(s), {list.Count} quad(s) after graph.");

        if (list.Count == 0 || extent.Width <= 0f)
        {
            Console.Error.WriteLine("AotComponentProbe: FAIL — text layout produced nothing under AOT.");
            return 1;
        }
    }
    else
    {
        // Not fatal: the probe is not the Sandbox and does not cook fonts. Say so rather than pass silently.
        Console.WriteLine($"AotTextSmoke: SKIPPED — no cooked font at '{fontPath}'.");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"AotComponentProbe: FAIL (text) — {ex.GetType().FullName}: {ex.Message}");
    return 1;
}


Console.WriteLine("AotComponentProbe: PASS — component rooting sufficient under this configuration.");
return 0;
