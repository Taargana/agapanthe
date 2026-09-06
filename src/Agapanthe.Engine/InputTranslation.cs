using Agapanthe.Core;

namespace Agapanthe.Engine;

/// <summary>
/// The per-tick declarative translation (MP-0d, locked decision 3/6): reads one <see cref="InputSnapshot"/>
/// through an <see cref="InputMap"/> and enqueues the resulting <see cref="SimCommand"/>s. Static, stateless,
/// 0-alloc.
/// <para>
/// No previous-snapshot parameter: the edges the translation needs are already carried by
/// <see cref="InputSnapshot.Pressed"/> / <see cref="InputSnapshot.Released"/>, and only those survive a
/// press-and-release contained within a single frame.
/// </para>
/// </summary>
public static class InputTranslation
{
    /// <summary>
    /// Applies <paramref name="map"/> to <paramref name="current"/> and enqueues commands stamped for
    /// <paramref name="tick"/>: one per matching button binding, plus one for the axis-vector binding (every tick,
    /// carrying the bound axes as <see cref="SimCommand.Vector"/>).
    /// </summary>
    public static void Emit(in InputSnapshot current, InputMap map, SimCommandQueue queue, long tick)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(queue);

        foreach (var binding in map.Buttons)
        {
            var mask = 1UL << binding.Bit;
            var fired = binding.Trigger switch
            {
                ButtonTrigger.OnPress => (current.Pressed & mask) != 0,
                ButtonTrigger.OnRelease => (current.Released & mask) != 0,
                ButtonTrigger.WhileHeld => (current.Held & mask) != 0,
                _ => false,
            };

            if (fired)
            {
                queue.Enqueue(new SimCommand(tick, binding.Kind, default, Double3.Zero, 0f, 0u));
            }
        }

        if (map.HasAxis)
        {
            var vector = new Double3(current.Axes[map.AxisX], current.Axes[map.AxisY], current.Axes[map.AxisZ]);
            queue.Enqueue(new SimCommand(tick, map.AxisKind, default, vector, 0f, 0u));
        }
    }
}
