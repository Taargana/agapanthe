using System.Numerics;

namespace Agapanthe.Rendering;

/// <summary>
/// Per-frame input snapshot consumed by <see cref="FreeCameraController"/>. This is a
/// value type with no engine dependencies, so <see cref="Agapanthe.Rendering"/> stays
/// free of any windowing/input backend (Silk.NET.Input types live only at the Platform
/// boundary). The host builds one of these each frame from whatever input source it has.
/// </summary>
public readonly struct CameraInput
{
    public CameraInput(
        bool moveForward,
        bool moveBackward,
        bool moveLeft,
        bool moveRight,
        bool moveUp,
        bool moveDown,
        Vector2 lookDelta)
    {
        MoveForward = moveForward;
        MoveBackward = moveBackward;
        MoveLeft = moveLeft;
        MoveRight = moveRight;
        MoveUp = moveUp;
        MoveDown = moveDown;
        LookDelta = lookDelta;
    }

    /// <summary>W — move along the camera's forward vector.</summary>
    public bool MoveForward { get; }

    /// <summary>S — move opposite the forward vector.</summary>
    public bool MoveBackward { get; }

    /// <summary>A — move along -right.</summary>
    public bool MoveLeft { get; }

    /// <summary>D — move along +right.</summary>
    public bool MoveRight { get; }

    /// <summary>Space / E — move along world +Y.</summary>
    public bool MoveUp { get; }

    /// <summary>Ctrl / Q — move along world -Y.</summary>
    public bool MoveDown { get; }

    /// <summary>
    /// Mouse motion since the previous frame, in <b>pixels</b>. X is screen-right,
    /// Y is screen-down (native mouse convention). Scaled by
    /// <see cref="FreeCameraController.LookSensitivity"/> into radians.
    /// </summary>
    public Vector2 LookDelta { get; }
}

/// <summary>
/// Fly-style controller: WASD moves in the view plane, Space/Ctrl (or E/Q) move
/// vertically along world up, and mouse motion drives yaw/pitch. Pitch is clamped to
/// ±89° to keep the view matrix away from the gimbal poles.
/// </summary>
/// <remarks>
/// Sign conventions (see also <see cref="Camera"/>):
/// mouse-right (LookDelta.X &gt; 0) increases <see cref="Camera.Yaw"/> → turns toward +X;
/// mouse-up (LookDelta.Y &lt; 0) increases <see cref="Camera.Pitch"/> → looks up.
/// <see cref="Update"/> performs no managed allocations.
/// </remarks>
public sealed class FreeCameraController
{
    /// <summary>Hard pitch limit in radians (±89°), keeping the view off the poles.</summary>
    private const float MaxPitch = 89f * (MathF.PI / 180f);

    /// <summary>Movement speed in world units per second.</summary>
    public float MoveSpeed { get; set; } = 5f;

    /// <summary>Look sensitivity in radians per pixel of mouse motion.</summary>
    public float LookSensitivity { get; set; } = 0.0025f;

    /// <summary>
    /// Applies one frame of input to <paramref name="camera"/>. Orientation updates are
    /// frame-rate independent (radians per pixel); movement is scaled by
    /// <paramref name="deltaSeconds"/>.
    /// </summary>
    public void Update(Camera camera, float deltaSeconds, in CameraInput input)
    {
        ArgumentNullException.ThrowIfNull(camera);

        // --- Look: mouse delta → yaw/pitch. Screen Y is down, so a negative dy looks up. ---
        camera.Yaw += input.LookDelta.X * LookSensitivity;
        camera.Pitch -= input.LookDelta.Y * LookSensitivity;
        camera.Pitch = Math.Clamp(camera.Pitch, -MaxPitch, MaxPitch);

        // --- Move: build a direction in the camera basis, normalize so diagonals aren't faster. ---
        float strafe = (input.MoveRight ? 1f : 0f) - (input.MoveLeft ? 1f : 0f);
        float lift = (input.MoveUp ? 1f : 0f) - (input.MoveDown ? 1f : 0f);
        float advance = (input.MoveForward ? 1f : 0f) - (input.MoveBackward ? 1f : 0f);

        Vector3 direction = (camera.Forward * advance) + (camera.Right * strafe) + (Vector3.UnitY * lift);
        if (direction != Vector3.Zero)
        {
            camera.Position += Vector3.Normalize(direction) * (MoveSpeed * deltaSeconds);
        }
    }
}
