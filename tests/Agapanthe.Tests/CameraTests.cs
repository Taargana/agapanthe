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
    public void Controller_MovesExactly_EvenTenThousandKilometresOut()
    {
        // The failure this exists to prevent: a float position at 1e7 m sits on a 1 m grid, so a 0.3 m step is
        // rounded straight back to where it started and the camera simply REFUSES TO MOVE. Accumulating in double
        // keeps the step, whole.
        var start = new Double3(1e7, 1e7, 1e7);
        var camera = new Camera { Position = start };
        var controller = new FreeCameraController { MoveSpeed = 1f };

        controller.Update(camera, 0.3f, new CameraInput(true, false, false, false, false, false, Vector2.Zero));

        // Default forward is -Z: the step lands on Z, and it lands exactly.
        var step = 1f * 0.3f; // the float the controller computes
        Assert.Equal(start.Z - step, camera.Position.Z, 6);
        Assert.NotEqual(start.Z, camera.Position.Z);
    }

    [Fact]
    public void Float_CannotEvenRepresentThatStep_WhichIsWhyThePositionIsDouble()
    {
        // The witness for the test above: the same arithmetic in float, where the step vanishes on contact.
        const float farOut = 1e7f;
        Assert.Equal(farOut, farOut + 0.3f);       // the step is gone
        Assert.Equal(farOut, farOut + 0.49f);      // so is half a metre
    }

    [Fact]
    public void Controller_WrapsYaw_SoLongSessionsDoNotLosePrecision()
    {
        // Turning the same way for a while used to grow Yaw without bound; a float's precision is relative, so
        // past a few thousand radians the mouse can no longer express a fine rotation and the look starts to snap.
        var camera = new Camera();
        var controller = new FreeCameraController { LookSensitivity = 0.01f, LookSmoothingTau = 0f };

        for (var i = 0; i < 500; i++)
        {
            controller.Update(camera, 1f / 60f, new CameraInput(false, false, false, false, false, false, new Vector2(200f, 0f)));
        }

        Assert.InRange(camera.Yaw, -MathF.PI, MathF.PI);
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
