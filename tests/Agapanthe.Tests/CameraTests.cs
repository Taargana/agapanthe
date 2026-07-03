using System.Numerics;
using Agapanthe.Rendering;

namespace Agapanthe.Tests;

public class CameraTests
{
    private const float MaxPitch = 89f * (MathF.PI / 180f);

    [Fact]
    public void ViewMatrix_DefaultOrientation_ProjectsWorldOriginToNegativeViewZ()
    {
        // Camera at (0,0,5), yaw=pitch=0 → forward = -Z, looking at the world origin.
        // Consistent with MathHelpersTests.LookAt_EyeLookingDownNegativeZ_TargetIsInFront:
        // right-handed view space looks down its own -Z, so a point 5 units ahead lands at z=-5.
        var camera = new Camera { Position = new Vector3(0f, 0f, 5f) };

        var viewSpace = Vector4.Transform(new Vector4(0f, 0f, 0f, 1f), camera.ViewMatrix);

        Assert.Equal(0f, viewSpace.X, 5);
        Assert.Equal(0f, viewSpace.Y, 5);
        Assert.Equal(-5f, viewSpace.Z, 5);
        Assert.True(viewSpace.Z < 0f, "world point in front of the camera must map to negative view-space Z");
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
        var camera = new Camera { Position = Vector3.Zero };
        var controller = new FreeCameraController { MoveSpeed = 10f };

        // Default forward is -Z; one second of forward input moves MoveSpeed units.
        controller.Update(camera, 1f, new CameraInput(true, false, false, false, false, false, Vector2.Zero));

        Assert.Equal(0f, camera.Position.X, 5);
        Assert.Equal(0f, camera.Position.Y, 5);
        Assert.Equal(-10f, camera.Position.Z, 5);
    }

    [Fact]
    public void Controller_DiagonalMovement_IsNormalizedToConstantSpeed()
    {
        var camera = new Camera { Position = Vector3.Zero };
        var controller = new FreeCameraController { MoveSpeed = 10f };

        // Forward + right pressed together: distance travelled must equal MoveSpeed, not MoveSpeed*sqrt(2).
        controller.Update(camera, 1f, new CameraInput(true, false, false, true, false, false, Vector2.Zero));

        Assert.Equal(10f, camera.Position.Length(), 4);
    }

    [Fact]
    public void Controller_NoInput_DoesNotMoveOrProduceNaN()
    {
        var camera = new Camera { Position = new Vector3(1f, 2f, 3f) };
        var controller = new FreeCameraController();

        controller.Update(camera, 1f / 60f, default);

        Assert.Equal(new Vector3(1f, 2f, 3f), camera.Position);
    }
}
