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
        Vector2 lookDelta,
        bool sprint = false)
    {
        MoveForward = moveForward;
        MoveBackward = moveBackward;
        MoveLeft = moveLeft;
        MoveRight = moveRight;
        MoveUp = moveUp;
        MoveDown = moveDown;
        LookDelta = lookDelta;
        Sprint = sprint;
    }

    /// <summary>W — move along the camera's look direction (pitch included).</summary>
    public bool MoveForward { get; }

    /// <summary>S — move opposite the look direction.</summary>
    public bool MoveBackward { get; }

    /// <summary>A — strafe along the camera's -Right.</summary>
    public bool MoveLeft { get; }

    /// <summary>D — strafe along the camera's +Right.</summary>
    public bool MoveRight { get; }

    /// <summary>Space — move along the camera's +Up (always orthogonal to Forward).</summary>
    public bool MoveUp { get; }

    /// <summary>Ctrl / C — move along the camera's -Up.</summary>
    public bool MoveDown { get; }

    /// <summary>Shift — multiply speed by <see cref="FreeCameraController.SprintMultiplier"/>.</summary>
    public bool Sprint { get; }

    /// <summary>
    /// Mouse motion since the previous frame, in <b>pixels</b>. X is screen-right,
    /// Y is screen-down (native mouse convention). Scaled by
    /// <see cref="FreeCameraController.LookSensitivityX"/>/<see cref="FreeCameraController.LookSensitivityY"/> into radians.
    /// </summary>
    public Vector2 LookDelta { get; }
}

/// <summary>
/// Fly controller in the camera's own referential: the mouse drives yaw/pitch, and W/S, A/D,
/// Space/Ctrl travel along the camera's Forward, Right and Up axes respectively — the whole
/// basis rotates with the view, so up/down is always orthogonal to the look direction (no
/// world-Y bias). Shift sprints. Pitch is clamped to ±89° to keep the view matrix away from
/// the gimbal poles.
/// </summary>
/// <remarks>
/// Sign conventions (see also <see cref="Camera"/>):
/// mouse-right (LookDelta.X &gt; 0) increases <see cref="Camera.Yaw"/> → turns right;
/// mouse-up (LookDelta.Y &lt; 0) increases <see cref="Camera.Pitch"/> → looks up.
/// <see cref="Update"/> performs no managed allocations.
/// </remarks>
public sealed class FreeCameraController
{
    /// <summary>Hard pitch limit in radians (±89°), keeping the view off the poles.</summary>
    private const float MaxPitch = 89f * (MathF.PI / 180f);

    /// <summary>
    /// Movement speed in world units per second. Scale this to the scene (e.g. a fraction of the
    /// model's bounding-box diagonal) — a fixed value feels absurdly fast on small models and
    /// glacial on large ones.
    /// </summary>
    public float MoveSpeed { get; set; } = 5f;

    /// <summary>Speed factor applied while <see cref="CameraInput.Sprint"/> is held.</summary>
    public float SprintMultiplier { get; set; } = 3f;

    /// <summary>Horizontal (yaw) look sensitivity in radians per pixel of mouse motion
    /// (default ≈ 0.057°/px, a mid-range FPS feel: a full turn ≈ 6300 px of travel).</summary>
    public float LookSensitivityX { get; set; } = 0.001f;

    /// <summary>Vertical (pitch) look sensitivity in radians per pixel of mouse motion.
    /// Same default as <see cref="LookSensitivityX"/>; tune independently to taste.</summary>
    public float LookSensitivityY { get; set; } = 0.001f;

    /// <summary>Sets both axis sensitivities at once (radians per pixel).</summary>
    public float LookSensitivity
    {
        set => LookSensitivityX = LookSensitivityY = value;
    }

    /// <summary>
    /// Applies one frame of input to <paramref name="camera"/>. Orientation updates are
    /// frame-rate independent (radians per pixel); movement is scaled by
    /// <paramref name="deltaSeconds"/>.
    /// </summary>
    public void Update(Camera camera, float deltaSeconds, in CameraInput input)
    {
        ArgumentNullException.ThrowIfNull(camera);

        // --- Look: mouse delta → yaw/pitch. Screen Y is down, so a negative dy looks up. ---
        camera.Yaw += input.LookDelta.X * LookSensitivityX;
        camera.Pitch -= input.LookDelta.Y * LookSensitivityY;
        camera.Pitch = Math.Clamp(camera.Pitch, -MaxPitch, MaxPitch);

        // --- Move: the whole camera basis is the travel referential. Forward = look axis,
        // Right/Up = the camera's own orthonormal axes, so up/down stays perpendicular to the
        // look direction whatever the rotation (true 6DOF fly referential, no world-Y bias). ---
        float strafe = (input.MoveRight ? 1f : 0f) - (input.MoveLeft ? 1f : 0f);
        float lift = (input.MoveUp ? 1f : 0f) - (input.MoveDown ? 1f : 0f);
        float advance = (input.MoveForward ? 1f : 0f) - (input.MoveBackward ? 1f : 0f);

        if (strafe == 0f && lift == 0f && advance == 0f)
        {
            return;
        }

        var direction = (camera.Forward * advance) + (camera.Right * strafe) + (camera.Up * lift);
        if (direction == Vector3.Zero)
        {
            return; // Opposing keys cancelled out.
        }

        var speed = MoveSpeed * (input.Sprint ? SprintMultiplier : 1f);
        camera.Position += Vector3.Normalize(direction) * (speed * deltaSeconds);
    }
}
