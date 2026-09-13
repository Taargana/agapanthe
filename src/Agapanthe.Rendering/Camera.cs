using System.Numerics;
using Agapanthe.Core;

namespace Agapanthe.Rendering;

/// <summary>Slice-2 — which projection <see cref="Camera.ProjectionMatrix"/> builds. <see cref="Perspective"/>
/// is the default, byte-identical to every scene before Slice-2. <see cref="Orthographic"/> is the top-down
/// app's generality test — parallel rays, no foreshortening.</summary>
public enum CameraProjection { Perspective, Orthographic }

/// <summary>
/// Free 3D camera. Orientation is stored as yaw/pitch (radians) and turned into a
/// view matrix on demand.
/// </summary>
/// <remarks>
/// Conventions (matching <see cref="MathHelpers"/> / session 1 tests):
/// <list type="bullet">
/// <item>World space is <b>right-handed</b>, world up is <c>+Y</c>.</item>
/// <item>At <c>Yaw == Pitch == 0</c> the camera looks down <b>-Z</b>
///   (forward = <c>(0,0,-1)</c>), matching <see cref="MathHelpers.LookAt"/> where
///   view space looks down its own -Z.</item>
/// <item><b>Yaw</b> rotates around world up. It <i>increases</i> as the view turns
///   toward <c>+X</c>: <c>Yaw = +90°</c> gives forward = <c>(1,0,0)</c> (turning right
///   when up is +Y). This is a compass/heading style yaw, not the right-hand rotation
///   about +Y (which would send -Z toward -X); the sign is chosen so mouse-right = turn-right.</item>
/// <item><b>Pitch</b> tilts up/down: <c>Pitch = +90°</c> looks straight up (<c>+Y</c>).
///   Callers must keep pitch away from ±90° to avoid gimbal degeneracy at the poles
///   (<see cref="FreeCameraController"/> clamps to ±89°).</item>
/// </list>
/// The Vulkan clip-space quirks (Y flip, depth [0,1]) live in
/// <see cref="MathHelpers.PerspectiveVulkan"/>, not here.
/// </remarks>
public sealed class Camera
{
    /// <summary>
    /// Eye position in world space, in <see cref="Double3"/> (spec §3.3). It is never handed to the GPU as-is:
    /// it IS the camera-relative origin, so the GPU sees the eye at zero and every object relative to it.
    /// </summary>
    public Double3 Position { get; set; }

    /// <summary>Heading around world up, in radians. See remarks for sign convention.</summary>
    public float Yaw { get; set; }

    /// <summary>Elevation, in radians. Positive looks up. Keep within (-90°, +90°).</summary>
    public float Pitch { get; set; }

    /// <summary>Vertical field of view, in radians.</summary>
    public float FovY { get; set; } = MathF.PI / 3f; // 60°

    /// <summary>Near plane distance (positive, in front of the camera).</summary>
    public float Near { get; set; } = 0.1f;

    /// <summary>Far plane distance.</summary>
    public float Far { get; set; } = 1000f;

    /// <summary>Viewport aspect ratio (width / height).</summary>
    public float AspectRatio { get; set; } = 16f / 9f;

    /// <summary>Slice-2 — which projection <see cref="ProjectionMatrix"/> builds. Default <see cref="CameraProjection.Perspective"/>
    /// keeps every pre-Slice-2 scene byte-identical.</summary>
    public CameraProjection Projection { get; set; } = CameraProjection.Perspective;

    /// <summary>Orthographic view width, in world units. Meaningless when <see cref="Projection"/> is <see cref="CameraProjection.Perspective"/>.</summary>
    public float OrthoWidth { get; set; }

    /// <summary>Orthographic view height, in world units, or <c>0</c> to derive it from <see cref="OrthoWidth"/>
    /// and <see cref="AspectRatio"/> (audit finding, Slice-2: unlike perspective's FovY+AspectRatio, a fixed
    /// orthographic height does not auto-correct on window resize — this sentinel, matching the
    /// <c>MoveSpeed</c>/<c>ShadowDistance</c> "0 = dynamic" convention, is the fix). Meaningless when
    /// <see cref="Projection"/> is <see cref="CameraProjection.Perspective"/>.</summary>
    public float OrthoHeight { get; set; }

    /// <summary>Unit forward vector derived from yaw/pitch. <c>(0,0,-1)</c> when both are 0.</summary>
    public Vector3 Forward
    {
        get
        {
            float cp = MathF.Cos(Pitch);
            float sp = MathF.Sin(Pitch);
            float cy = MathF.Cos(Yaw);
            float sy = MathF.Sin(Yaw);
            return new Vector3(sy * cp, sp, -cy * cp);
        }
    }

    /// <summary>Unit right vector (in the horizontal plane), <c>cross(forward, worldUp)</c>.</summary>
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    /// <summary>Unit up vector of the camera basis, <c>cross(right, forward)</c>.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    /// <summary>Vulkan reversed-Z projection (P3-M8, Slice-2): Y flipped, near→1 / far→0 regardless of
    /// <see cref="Projection"/>. Paired with the D32 float depth target cleared to 0 and the camera passes'
    /// <c>GreaterOrEqual</c> test, it spreads depth precision across a planetary near/far range without
    /// z-fighting (perspective) or simply keeps near/far ordering correct under that fixed compare op
    /// (orthographic). The shadow pass keeps standard depth (see ShadowFit).</summary>
    public Matrix4x4 ProjectionMatrix => Projection switch
    {
        CameraProjection.Perspective => MathHelpers.PerspectiveVulkanReversed(FovY, AspectRatio, Near, Far),
        CameraProjection.Orthographic => MathHelpers.OrthographicVulkanReversed(
            OrthoWidth, ResolveOrthoHeight(), Near, Far),
        _ => throw new InvalidOperationException($"unknown projection {Projection}"),
    };

    // Audit finding (Slice-2, csharp-lowlevel): OrthoWidth/OrthoHeight are cook-time validated (SceneCompiler)
    // but Camera is public Rendering surface usable outside a cooked scene — a caller leaving OrthoWidth/
    // OrthoHeight at their 0 default would otherwise reach CreateOrthographic(0, …) and upload a NaN/Inf
    // matrix with no validation-layer message. Guard here, once, on the same footing as the cook-time reject.
    private float ResolveOrthoHeight()
    {
        if (!float.IsFinite(OrthoWidth) || OrthoWidth <= 0f)
        {
            throw new InvalidOperationException(
                $"orthographic projection requires a positive finite OrthoWidth, got {OrthoWidth}.");
        }

        if (OrthoHeight == 0f)
        {
            if (!float.IsFinite(AspectRatio) || AspectRatio <= 0f)
            {
                throw new InvalidOperationException(
                    $"orthographic projection with OrthoHeight=0 (derive-from-aspect) requires a positive "
                    + $"finite AspectRatio, got {AspectRatio}.");
            }

            return OrthoWidth / AspectRatio;
        }

        if (!float.IsFinite(OrthoHeight) || OrthoHeight < 0f)
        {
            throw new InvalidOperationException(
                $"orthographic projection requires a non-negative finite OrthoHeight, got {OrthoHeight}.");
        }

        return OrthoHeight;
    }

    /// <summary>
    /// The frame's <see cref="RenderView"/> (M4): the camera-relative origin is this camera's position
    /// <b>snapped to the <see cref="RenderView.CellSize"/> grid</b>, the eye sits at <c>Position − Origin</c>
    /// within the frame, and the view matrix carries that sub-cell translation. Build it ONCE per frame and pass
    /// it to both the world (which narrows every object against <see cref="RenderView.Origin"/>) and the renderer
    /// (lights + shadow fit against the same origin) — one origin per frame, by construction.
    /// <para>
    /// Snapping the origin (rather than tracking the eye exactly, as M3 did) keeps a static object's
    /// camera-relative position stable frame to frame: it only shifts when the camera crosses a cell boundary.
    /// The precision cost is that the same scene, seen from a position that is NOT a whole number of cells away,
    /// no longer round-trips bit-for-bit (the sub-cell eye offset differs) — the on-screen result is identical,
    /// but the intermediate floats are not. See <see cref="RenderView"/>.
    /// </para>
    /// </summary>
    public RenderView CreateView()
    {
        var origin = RenderView.Snap(Position);
        var eyeRelative = (Position - origin).ToVector3(Double3.Zero); // bounded by one cell
        var view = MathHelpers.LookAt(eyeRelative, eyeRelative + Forward, Vector3.UnitY);
        return new RenderView(origin, eyeRelative, in view, ProjectionMatrix, FovY, AspectRatio, Near, Far);
    }

    /// <summary>
    /// Physics queries (spec §3.3): builds a world-space <see cref="Ray"/> through a screen-space pixel, using
    /// this camera's own projection basis (<see cref="FovY"/>/<see cref="AspectRatio"/> for <see
    /// cref="CameraProjection.Perspective"/>, <see cref="OrthoWidth"/>/<see cref="OrthoHeight"/> for <see
    /// cref="CameraProjection.Orthographic"/>) — <paramref name="viewportWidth"/>/<paramref name="viewportHeight"/>
    /// are used ONLY to map the pixel into normalized device coordinates, never to re-derive the projection.
    /// <b>Caller contract</b> (documented, not validated — matches this project's precedent for caller contracts
    /// elsewhere): pass the same viewport dimensions this camera's <see cref="AspectRatio"/> was computed from, or
    /// the ray will be skewed.
    /// <para>
    /// <b>Perspective</b>: the ray's direction is built directly from the camera's own basis (<see cref="Right"/>/
    /// <see cref="Up"/>/<see cref="Forward"/>) rather than by inverting <see cref="ProjectionMatrix"/> —
    /// algebraically equivalent for a symmetric perspective frustum, and it sidesteps a matrix inversion entirely.
    /// One origin (the eye) for every screen point; direction varies (converging cone).
    /// </para>
    /// <para>
    /// <b>Orthographic</b> (audit finding, physics-queries: the perspective-only basis construction above produces
    /// a nonsensical converging cone for an orthographic camera — TopDown, this engine's orthographic app, would
    /// have silently gotten wrong rays): direction is <see cref="Forward"/> for every screen point (parallel rays,
    /// no foreshortening — the entire point of Slice-2's orthographic projection); the per-pixel offset instead
    /// moves the ORIGIN, along <see cref="Right"/>/<see cref="Up"/> scaled by half the ortho view extents.
    /// </para>
    /// <para>
    /// Either way, the origin is widened back to <see cref="Double3"/> world space via <see cref="CreateView"/>'s
    /// <see cref="RenderView.Origin"/> (the same "narrow, then widen" pattern every other camera-relative
    /// computation in this engine uses).
    /// </para>
    /// </summary>
    public Ray ScreenPointToRay(Vector2 screenPoint, uint viewportWidth, uint viewportHeight)
    {
        var ndcXRaw = (screenPoint.X / viewportWidth * 2f) - 1f;
        var ndcYRaw = 1f - (screenPoint.Y / viewportHeight * 2f); // screen Y is down, world Y is up

        var view = CreateView();
        var eye = view.Origin + new Double3(view.EyeRelative);

        if (Projection == CameraProjection.Orthographic)
        {
            var halfWidth = OrthoWidth / 2f;
            var halfHeight = ResolveOrthoHeight() / 2f;
            var offset = (ndcXRaw * halfWidth * Right) + (ndcYRaw * halfHeight * Up);
            return new Ray(eye + new Double3(offset), Forward);
        }

        var halfFovY = MathF.Tan(FovY / 2f);
        var ndcX = ndcXRaw * AspectRatio * halfFovY;
        var ndcY = ndcYRaw * halfFovY;
        var direction = Vector3.Normalize((ndcX * Right) + (ndcY * Up) + Forward);
        return new Ray(eye, direction);
    }
}
