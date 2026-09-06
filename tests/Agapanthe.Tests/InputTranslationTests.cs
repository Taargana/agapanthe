using Agapanthe.Engine;

namespace Agapanthe.Tests;

/// <summary>
/// MP-0d W1 — <see cref="InputTranslation.Emit"/> reads one <see cref="InputSnapshot"/> through an
/// <see cref="InputMap"/> and enqueues the right commands: button triggers on the right edge, the axis-vector
/// binding every tick, unbound bits nothing.
/// </summary>
public sealed class InputTranslationTests
{
    private const byte KindPress = 10;
    private const byte KindRelease = 11;
    private const byte KindHeld = 12;
    private const byte KindMove = 20;

    private static List<SimCommand> Drain(SimCommandQueue queue)
    {
        var seen = new List<SimCommand>();
        queue.DrainUpTo(long.MaxValue, (in SimCommand c) => seen.Add(c));
        return seen;
    }

    [Fact]
    public void OnPress_FiresOnceForARisingEdge_NotForHeldOrReleased()
    {
        var map = new InputMap();
        map.BindButton(bit: 3, KindPress, ButtonTrigger.OnPress);
        var queue = new SimCommandQueue();

        InputTranslation.Emit(new InputSnapshot { Pressed = 1UL << 3, Held = 1UL << 3 }, map, queue, tick: 7);
        InputTranslation.Emit(new InputSnapshot { Held = 1UL << 3 }, map, queue, tick: 8);

        var cmds = Drain(queue);
        Assert.Single(cmds);
        Assert.Equal(KindPress, cmds[0].Kind);
        Assert.Equal(7, cmds[0].TargetTick);
    }

    [Fact]
    public void OnRelease_FiresOnTheFallingEdge()
    {
        var map = new InputMap();
        map.BindButton(bit: 1, KindRelease, ButtonTrigger.OnRelease);
        var queue = new SimCommandQueue();

        InputTranslation.Emit(new InputSnapshot { Held = 1UL << 1 }, map, queue, tick: 1);
        InputTranslation.Emit(new InputSnapshot { Released = 1UL << 1 }, map, queue, tick: 2);

        var cmds = Drain(queue);
        Assert.Single(cmds);
        Assert.Equal(KindRelease, cmds[0].Kind);
        Assert.Equal(2, cmds[0].TargetTick);
    }

    [Fact]
    public void WhileHeld_FiresEveryTickTheButtonIsDown()
    {
        var map = new InputMap();
        map.BindButton(bit: 5, KindHeld, ButtonTrigger.WhileHeld);
        var queue = new SimCommandQueue();

        InputTranslation.Emit(new InputSnapshot { Held = 1UL << 5 }, map, queue, tick: 1);
        InputTranslation.Emit(new InputSnapshot { Held = 1UL << 5 }, map, queue, tick: 2);
        InputTranslation.Emit(default, map, queue, tick: 3);

        var cmds = Drain(queue);
        Assert.Equal(2, cmds.Count);
        Assert.All(cmds, c => Assert.Equal(KindHeld, c.Kind));
    }

    [Fact]
    public void AxisBinding_EmitsOneCommandPerCall_WithTheBoundAxesAsVector()
    {
        var map = new InputMap();
        map.BindAxisVector(KindMove, axisX: 0, axisY: 2, axisZ: 1);
        var queue = new SimCommandQueue();

        var snap = default(InputSnapshot);
        snap.Axes[0] = 0.5f;
        snap.Axes[1] = -0.25f;
        snap.Axes[2] = 1f;

        InputTranslation.Emit(snap, map, queue, tick: 4);

        var cmds = Drain(queue);
        Assert.Single(cmds);
        Assert.Equal(KindMove, cmds[0].Kind);
        Assert.Equal(0.5, cmds[0].Vector.X);
        Assert.Equal(1.0, cmds[0].Vector.Y);   // axisY -> axes[2]
        Assert.Equal(-0.25, cmds[0].Vector.Z); // axisZ -> axes[1]
    }

    [Fact]
    public void UnboundBits_ProduceNothing()
    {
        var map = new InputMap();
        map.BindButton(bit: 0, KindPress, ButtonTrigger.OnPress);
        var queue = new SimCommandQueue();

        InputTranslation.Emit(new InputSnapshot { Pressed = 1UL << 9, Held = ulong.MaxValue }, map, queue, tick: 1);

        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void RebindingSameBitAndTrigger_ReplacesRatherThanAdds()
    {
        var map = new InputMap();
        map.BindButton(bit: 2, KindPress, ButtonTrigger.OnPress);
        map.BindButton(bit: 2, KindHeld, ButtonTrigger.OnPress);
        var queue = new SimCommandQueue();

        InputTranslation.Emit(new InputSnapshot { Pressed = 1UL << 2 }, map, queue, tick: 1);

        var cmds = Drain(queue);
        Assert.Single(cmds);
        Assert.Equal(KindHeld, cmds[0].Kind);
    }
}
