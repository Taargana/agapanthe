using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Agapanthe.Engine;

/// <summary>
/// One tick of input, produced by the application's <see cref="SimulationHost.SampleInput"/> callback. Blittable,
/// fixed size (<b>40 bytes</b> = 8 + 8 + 8 + 16), app-indexed — the engine never assigns meaning to a bit or an
/// axis, it only translates them through an <see cref="InputMap"/>.
/// <para>
/// <b><see cref="Pressed"/> / <see cref="Released"/> are edges.</b> The callback merges
/// <c>EngineWindow.KeyPressed</c> / released events into a pending mask between calls, returns it, then clears it —
/// so one physical press yields exactly one command even on a catch-up frame that runs N ticks (the callback is
/// invoked once per tick). The engine cannot enforce this; the edge-bit test pins it.
/// </para>
/// The mouse is a camera/view concern (client-side, <c>Agapanthe.Rendering</c>) and is not in the snapshot.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct InputSnapshot
{
    /// <summary>Bit N set = button N is down this tick (level).</summary>
    public ulong Held;

    /// <summary>Bit N set = button N went down since the last snapshot (rising edge).</summary>
    public ulong Pressed;

    /// <summary>Bit N set = button N went up since the last snapshot (falling edge).</summary>
    public ulong Released;

    /// <summary>Four analog axes, app-indexed. −1..+1 by convention, but the engine does not clamp.</summary>
    public InputAxes Axes;
}

/// <summary>Four <c>float</c> analog axes, inline (no heap). First use of <see cref="InlineArrayAttribute"/> in the
/// project — a pure layout feature, no reflection, NativeAOT-safe by construction (<c>AotComponentProbe</c> runs
/// it). Fallback if ILC ever objects: four named fields + a <c>switch</c> indexer.</summary>
[InlineArray(4)]
public struct InputAxes
{
    private float _element0;
}
