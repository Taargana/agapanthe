using System.Numerics;

namespace Agapanthe.Core;

/// <summary>
/// Everything the render side needs to draw one frame camera-relative (spec §3.3): the <see cref="Origin"/> the
/// whole frame is expressed against, plus the view and projection matrices that go with it.
/// <para>
/// <b>Why an aggregate rather than several parameters.</b> Camera-relative rendering is only correct if the world,
/// the lights and the shadow fit subtract <b>exactly the same</b> origin. Passing the origin alongside the
/// matrices as one value makes that impossible to get wrong: there is a single origin per frame, and every
/// consumer reads it from here.
/// </para>
/// <para>
/// <b>Origin = the eye, QUANTIZED to a grid</b> (M4). The origin snaps to a fixed <see cref="CellSize"/> grid
/// rather than tracking the eye exactly. The eye then sits at <see cref="EyeRelative"/> within the frame (bounded
/// by one cell), and <see cref="View"/> carries that small translation. Snapping is what keeps a static object's
/// camera-relative position <b>stable between frames</b> (it changes only when the camera crosses a cell
/// boundary, not every time the camera nudges) — the prerequisite for a persistent GPU instance buffer and for a
/// physics simulation that cannot have the world moved under it each frame (Phase 3). Coordinates handed to the
/// GPU stay bounded by <c>≈ far + CellSize</c>, so float keeps its precision.
/// </para>
/// </summary>
public readonly struct RenderView
{
    /// <summary>
    /// The grid the origin snaps to, in world units. Fixed (not derived from the scene, which would make the
    /// origin jump when the scene changes). 1024 m: large enough that the origin re-snaps rarely (a predictable
    /// rebasing cadence), small enough that GPU coordinates stay tiny. Retunable; not irreversible.
    /// </summary>
    public const double CellSize = 1024d;

    /// <summary>The world-space grid point every position in this frame is expressed relative to.</summary>
    public readonly Double3 Origin;

    /// <summary>
    /// The eye in this frame's space: <c>eye − Origin</c>, bounded by one cell. The GPU camera position (so the
    /// PBR view vector <c>V = normalize(eye − worldPos)</c> and the skybox ray are correct), and the translation
    /// baked into <see cref="View"/>.
    /// </summary>
    public readonly Vector3 EyeRelative;

    /// <summary>View matrix, expressed in the frame's camera-relative space (eye at <see cref="EyeRelative"/>).</summary>
    public readonly Matrix4x4 View;

    /// <summary>Projection matrix (Vulkan clip space: Y flipped, depth [0,1]).</summary>
    public readonly Matrix4x4 Projection;

    /// <summary>Vertical field of view, in radians.</summary>
    public readonly float FovY;

    /// <summary>Viewport aspect ratio (width / height).</summary>
    public readonly float AspectRatio;

    /// <summary>Near plane distance.</summary>
    public readonly float Near;

    /// <summary>Far plane distance.</summary>
    public readonly float Far;

    public RenderView(
        Double3 origin, Vector3 eyeRelative, in Matrix4x4 view, in Matrix4x4 projection,
        float fovY, float aspectRatio, float near, float far)
    {
        Origin = origin;
        EyeRelative = eyeRelative;
        View = view;
        Projection = projection;
        FovY = fovY;
        AspectRatio = aspectRatio;
        Near = near;
        Far = far;
    }

    /// <summary>Snaps a world position down to the <see cref="CellSize"/> grid, per axis, in double.</summary>
    public static Double3 Snap(Double3 position)
        => new(
            Math.Floor(position.X / CellSize) * CellSize,
            Math.Floor(position.Y / CellSize) * CellSize,
            Math.Floor(position.Z / CellSize) * CellSize);
}
