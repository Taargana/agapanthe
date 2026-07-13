using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// Everything the render side needs to draw one frame camera-relative (spec §3.3): the <see cref="Origin"/> the
/// whole frame is expressed against, and the rotation-only view + projection matrices that go with it.
/// <para>
/// <b>Why an aggregate rather than three parameters.</b> Camera-relative rendering is only correct if the world,
/// the lights and the camera subtract <b>exactly the same</b> origin. Passing the origin alongside the matrices
/// as one value makes that impossible to get wrong: there is a single origin per frame, and every consumer reads
/// it from here.
/// </para>
/// <para>
/// <b>Origin = the eye.</b> <see cref="View"/> carries no translation — the camera sits at the origin by
/// construction, so the GPU only ever sees small coordinates (<c>object − camera</c>), where float still has its
/// mantissa. At 10 000 km from the world origin, consecutive floats are ~1 m apart; that is what this avoids.
/// </para>
/// </summary>
public readonly struct RenderView
{
    /// <summary>The world-space point every position in this frame is expressed relative to: the eye.</summary>
    public readonly Double3 Origin;

    /// <summary>Rotation-only view matrix (the eye is at the origin, so it has no translation).</summary>
    public readonly Matrix4x4 View;

    /// <summary>Projection matrix (Vulkan clip space: Y flipped, depth [0,1]).</summary>
    public readonly Matrix4x4 Projection;

    public RenderView(Double3 origin, in Matrix4x4 view, in Matrix4x4 projection)
    {
        Origin = origin;
        View = view;
        Projection = projection;
    }
}
