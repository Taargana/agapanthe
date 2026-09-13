using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

/// <summary>
/// Physics queries (spec §3.3): <see cref="Camera.ScreenPointToRay"/> is the screen→world helper that makes
/// "what's under the cursor" demonstrable. These pin its projection-basis (FovY/AspectRatio, not the passed
/// viewport dims) and its camera-relative-origin round-trip against <see cref="RenderView.Origin"/>.
/// </summary>
public sealed class CameraScreenPointToRayTests
{
    private const uint ViewportWidth = 1920;
    private const uint ViewportHeight = 1080;

    private static Camera MakeCamera(Double3 position)
        => new()
        {
            Position = position,
            Yaw = 0.4f,
            Pitch = 0.2f,
            FovY = MathF.PI / 3f, // 60°
            AspectRatio = (float)ViewportWidth / ViewportHeight,
        };

    [Fact]
    public void ViewportCenter_PointsAlongCameraForward()
    {
        var camera = MakeCamera(new Double3(123.0, 45.0, -678.0));
        var center = new Vector2(ViewportWidth / 2f, ViewportHeight / 2f);

        var ray = camera.ScreenPointToRay(center, ViewportWidth, ViewportHeight);

        var dot = Vector3.Dot(Vector3.Normalize(ray.Direction), camera.Forward);
        Assert.True(dot > 0.9999f, $"expected ~1.0 (parallel to Forward), got dot={dot}");
    }

    [Fact]
    public void ViewportCorner_DivergesByRoughlyHalfFov()
    {
        var camera = MakeCamera(new Double3(0, 0, 0));
        var corner = new Vector2(0f, 0f); // top-left pixel

        var ray = camera.ScreenPointToRay(corner, ViewportWidth, ViewportHeight);

        var dot = Vector3.Dot(Vector3.Normalize(ray.Direction), camera.Forward);
        var angle = MathF.Acos(Math.Clamp(dot, -1f, 1f));

        // The corner ray's divergence is the diagonal half-angle: greater than the vertical half-FOV (since it
        // also carries the horizontal component) but bounded well under 90°.
        var halfFovY = camera.FovY / 2f;
        Assert.True(angle > halfFovY * 0.9f, $"expected divergence >~ halfFovY ({halfFovY} rad), got {angle}");
        Assert.True(angle < halfFovY * 2.5f, $"expected divergence bounded, got {angle}");
    }

    [Fact]
    public void RayOrigin_RecoversTheCamerasWorldPosition()
    {
        var position = new Double3(1_000_000.25, -500.5, 3.75); // far enough to exercise camera-relative origin
        var camera = MakeCamera(position);
        var center = new Vector2(ViewportWidth / 2f, ViewportHeight / 2f);

        var ray = camera.ScreenPointToRay(center, ViewportWidth, ViewportHeight);

        // Narrow both sides against the same origin (RenderView's own snap) so the comparison isn't dominated by
        // the large absolute magnitude of position — this is the "narrow, then widen" round-trip check.
        var origin = RenderView.Snap(position);
        var expectedRelative = position.ToVector3(origin);
        var actualRelative = ray.Origin.ToVector3(origin);

        Assert.True(
            Vector3.Distance(expectedRelative, actualRelative) < 0.01f,
            $"expected ray origin ~= camera position, delta = {Vector3.Distance(expectedRelative, actualRelative)}");
    }

    // Audit finding (physics-queries): the perspective-basis construction above is nonsensical for an
    // orthographic camera (a converging cone instead of parallel rays) — TopDown, this engine's orthographic
    // app, would have silently gotten wrong rays from the `Key.F` demo hook. These pin the fix: parallel
    // direction, per-pixel origin offset.
    private static Camera MakeOrthoCamera(Double3 position)
        => new()
        {
            Position = position,
            Yaw = 0.4f,
            Pitch = 0.2f,
            Projection = CameraProjection.Orthographic,
            OrthoWidth = 40f,
            OrthoHeight = 22.5f,
            AspectRatio = (float)ViewportWidth / ViewportHeight,
        };

    [Fact]
    public void Orthographic_TwoScreenPoints_ProduceParallelDirectionsButDifferentOrigins()
    {
        var camera = MakeOrthoCamera(new Double3(10, 20, 30));
        var center = new Vector2(ViewportWidth / 2f, ViewportHeight / 2f);
        var corner = new Vector2(0f, 0f);

        var centerRay = camera.ScreenPointToRay(center, ViewportWidth, ViewportHeight);
        var cornerRay = camera.ScreenPointToRay(corner, ViewportWidth, ViewportHeight);

        // Parallel rays: both directions equal Forward exactly (no per-pixel divergence, unlike perspective).
        Assert.Equal(camera.Forward, centerRay.Direction);
        Assert.Equal(camera.Forward, cornerRay.Direction);

        // Different origins: the corner ray is offset from the center ray by the per-pixel ortho extent.
        var origin = RenderView.Snap(camera.Position);
        var centerRelative = centerRay.Origin.ToVector3(origin);
        var cornerRelative = cornerRay.Origin.ToVector3(origin);
        Assert.True(
            Vector3.Distance(centerRelative, cornerRelative) > 1f,
            $"expected the corner ray's origin to differ from the center ray's, got delta = "
            + $"{Vector3.Distance(centerRelative, cornerRelative)}");
    }

    [Fact]
    public void Orthographic_CenterScreenPoint_OriginMatchesCameraPosition()
    {
        var position = new Double3(10, 20, 30);
        var camera = MakeOrthoCamera(position);
        var center = new Vector2(ViewportWidth / 2f, ViewportHeight / 2f);

        var ray = camera.ScreenPointToRay(center, ViewportWidth, ViewportHeight);

        var origin = RenderView.Snap(position);
        var expectedRelative = position.ToVector3(origin);
        var actualRelative = ray.Origin.ToVector3(origin);
        Assert.True(
            Vector3.Distance(expectedRelative, actualRelative) < 0.01f,
            $"expected center-screen ray origin ~= camera position, delta = "
            + $"{Vector3.Distance(expectedRelative, actualRelative)}");
    }
}
