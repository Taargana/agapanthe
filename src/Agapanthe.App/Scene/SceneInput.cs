using Agapanthe.Rendering;
using Silk.NET.Input;

namespace Agapanthe.App;

/// <summary>
/// Contenu-3b — free-fly camera plumbing, lifted from the Sandbox's <c>RecipeInput.WireFreeFly</c> (S30 debt,
/// scoped): drives a <see cref="FreeCameraController"/> from the window's per-frame <c>Updated</c> event, the one
/// place <see cref="IWindow.MouseDelta"/> is valid. A scene whose cooked <c>camera.free_fly</c> is set gets this.
/// </summary>
public static class SceneInput
{
    public static void EnableFreeFly(IWindow window, Camera camera, FreeCameraController controller)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(controller);

        window.Updated += dt =>
        {
            if (!window.MouseCaptured)
            {
                controller.ResetLook();
                return;
            }

            var input = new CameraInput(
                moveForward: window.IsKeyDown(Key.W) || window.IsKeyDown(Key.Z) || window.IsKeyDown(Key.Up),
                moveBackward: window.IsKeyDown(Key.S) || window.IsKeyDown(Key.Down),
                moveLeft: window.IsKeyDown(Key.A) || window.IsKeyDown(Key.Q) || window.IsKeyDown(Key.Left),
                moveRight: window.IsKeyDown(Key.D) || window.IsKeyDown(Key.Right),
                moveUp: window.IsKeyDown(Key.Space),
                moveDown: window.IsKeyDown(Key.ControlLeft) || window.IsKeyDown(Key.C),
                lookDelta: window.MouseDelta,
                sprint: window.IsKeyDown(Key.ShiftLeft));

            controller.Update(camera, (float)dt, in input);
        };
    }
}
