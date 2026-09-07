using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Engine;
using Agapanthe.Rendering;
using Agapanthe.World;

namespace Sandbox;

// Load-bench animator: spins every drawable about world +Y by a fixed step each frame. Incremental and
// deterministic by frame count. Spin preserves the bounding sphere, so the scene extent stays valid without a
// per-frame refold. A struct, so GameWorld.AnimateDrawables dispatches to it without boxing.
internal readonly struct SpinAnimator(float deltaRadians) : IDrawableAnimator
{
    private readonly Matrix4x4 _delta = Matrix4x4.CreateRotationY(deltaRadians);

    public void Animate(ulong globalId, ref Double3 position, ref Matrix4x4 rotationScale)
        => rotationScale = _delta * rotationScale;
}

// The load bench, as a Simulation system (P3-M2 V3): spin every drawable and drift the camera yaw, both by a
// fixed step per frame so headless captures stay deterministic. APP business — it belongs in the sample.
internal sealed class BenchSpinSystem(GameWorld world, Camera camera) : ISystem
{
    public void Execute(in TickContext ctx)
    {
        var spin = new SpinAnimator(0.02f);
        world.AnimateDrawables(ref spin);
        camera.Yaw += 0.01f;
    }
}
