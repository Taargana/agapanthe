using System.Numerics;
using Agapanthe.Core;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

public class CameraTests
{
    private const float MaxPitch = 89f * (MathF.PI / 180f);

    [Fact]
    public void ViewMatrix_IsRotationOnly_AndMapsCameraRelativePointsToNegativeViewZ()
    {
        // Camera-relative rendering (spec §3.3): the eye is the origin of the frame, so the view matrix carries
        // NO translation whatever the camera's world position — the eye's position is instead subtracted from
        // every object when the render lists are built. The world origin, seen from (0,0,5), is the
        // camera-relative point (0,0,-5): 5 units ahead, so it lands at view-space z = -5 (right-handed view
        // space looks down its own -Z).
        var camera = new Camera { Position = new Double3(0, 0, 5) };
        var view = camera.ViewMatrix;

        Assert.Equal(0f, view.M41);
        Assert.Equal(0f, view.M42);
        Assert.Equal(0f, view.M43);

        var viewSpace = Vector4.Transform(new Vector4(0f, 0f, -5f, 1f), view);

        Assert.Equal(0f, viewSpace.X, 5);
        Assert.Equal(0f, viewSpace.Y, 5);
        Assert.Equal(-5f, viewSpace.Z, 5);
    }

    [Fact]
    public void CreateView_CarriesThePositionAsTheOrigin()
    {
        // The whole frame — world, lights, shadow fit — subtracts THIS origin, and nothing else (spec §3.3).
        var camera = new Camera { Position = new Double3(1e7, 0, -3e6) };

        var view = camera.CreateView();

        Assert.Equal(camera.Position, view.Origin);
        Assert.Equal(camera.ViewMatrix, view.View);
        Assert.Equal(camera.ProjectionMatrix, view.Projection);
    }

    [Fact]
    public void Forward_DefaultOrientation_PointsDownNegativeZ()
    {
        var camera = new Camera();

        Assert.Equal(0f, camera.Forward.X, 5);
        Assert.Equal(0f, camera.Forward.Y, 5);
        Assert.Equal(-1f, camera.Forward.Z, 5);
    }

    [Fact]
    public void Yaw_90Degrees_RotatesForwardTowardPositiveX()
    {
        // Sign convention: +yaw turns the view to the right (toward +X) when up is +Y.
        // Yaw = +90° takes forward from (0,0,-1) to (1,0,0).
        var camera = new Camera { Yaw = MathF.PI / 2f };

        Assert.Equal(1f, camera.Forward.X, 5);
        Assert.Equal(0f, camera.Forward.Y, 5);
        Assert.Equal(0f, camera.Forward.Z, 5);
    }

    [Fact]
    public void Yaw_Negative90Degrees_RotatesForwardTowardNegativeX()
    {
        var camera = new Camera { Yaw = -MathF.PI / 2f };

        Assert.Equal(-1f, camera.Forward.X, 5);
        Assert.Equal(0f, camera.Forward.Z, 5);
    }

    [Fact]
    public void Pitch_90Degrees_LooksUp()
    {
        var camera = new Camera { Pitch = MathF.PI / 2f };

        Assert.Equal(1f, camera.Forward.Y, 5);
    }

    [Fact]
    public void Controller_ClampsPitchToPlus89Degrees()
    {
        var camera = new Camera();
        var controller = new FreeCameraController();

        // Large upward mouse motion (screen Y negative = look up) must not exceed +89°.
        var lookUp = new CameraInput(false, false, false, false, false, false, new Vector2(0f, -100_000f));
        controller.Update(camera, 1f / 60f, lookUp);

        Assert.Equal(MaxPitch, camera.Pitch, 5);
    }

    [Fact]
    public void Controller_ClampsPitchToMinus89Degrees()
    {
        var camera = new Camera();
        var controller = new FreeCameraController();

        var lookDown = new CameraInput(false, false, false, false, false, false, new Vector2(0f, 100_000f));
        controller.Update(camera, 1f / 60f, lookDown);

        Assert.Equal(-MaxPitch, camera.Pitch, 5);
    }

    [Fact]
    public void Controller_MouseRight_IncreasesYaw()
    {
        var camera = new Camera();
        var controller = new FreeCameraController { LookSensitivity = 0.01f };

        controller.Update(camera, 1f / 60f, new CameraInput(false, false, false, false, false, false, new Vector2(100f, 0f)));

        Assert.True(camera.Yaw > 0f, "mouse-right should increase yaw (turn toward +X)");
    }

    [Fact]
    public void Controller_MoveForward_AdvancesAlongForward()
    {
        var camera = new Camera { Position = Double3.Zero };
        var controller = new FreeCameraController { MoveSpeed = 10f };

        // Default forward is -Z; one second of forward input moves MoveSpeed units.
        controller.Update(camera, 1f, new CameraInput(true, false, false, false, false, false, Vector2.Zero));

        Assert.Equal(0d, camera.Position.X, 5);
        Assert.Equal(0d, camera.Position.Y, 5);
        Assert.Equal(-10d, camera.Position.Z, 5);
    }

    [Fact]
    public void Controller_DiagonalMovement_IsNormalizedToConstantSpeed()
    {
        var camera = new Camera { Position = Double3.Zero };
        var controller = new FreeCameraController { MoveSpeed = 10f };

        // Forward + right pressed together: distance travelled must equal MoveSpeed, not MoveSpeed*sqrt(2).
        controller.Update(camera, 1f, new CameraInput(true, false, false, true, false, false, Vector2.Zero));

        Assert.Equal(10d, camera.Position.Length, 4);
    }

    [Fact]
    public void Controller_NoInput_DoesNotMoveOrProduceNaN()
    {
        var camera = new Camera { Position = new Double3(1, 2, 3) };
        var controller = new FreeCameraController();

        controller.Update(camera, 1f / 60f, default);

        Assert.Equal(new Double3(1, 2, 3), camera.Position);
    }
}
