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
/// <see cref="Update"/> performs no managed allocations (all state is a single
/// <see cref="Vector2"/> field; the smoothing is pure float math).
/// <para>
/// The raw mouse delta is exponentially smoothed toward before it drives yaw/pitch, which
/// removes the per-pixel jitter of high-DPI sensors without the mushy lag of a naive
/// per-frame lerp. The smoothing is <b>frame-rate independent</b>: see <see cref="Update"/>.
/// </para>
/// </remarks>
public sealed class FreeCameraController
{
    /// <summary>Hard pitch limit in radians (±89°), keeping the view off the poles.</summary>
    private const float MaxPitch = 89f * (MathF.PI / 180f);

    /// <summary>
    /// Exponentially-smoothed mouse delta carried between frames (pixels). Applying this instead
    /// of the raw per-frame delta debounces the look; it decays to zero on its own when the mouse
    /// is still, and is force-cleared by <see cref="ResetLook"/> across a capture gap.
    /// </summary>
    private Vector2 _smoothedLookDelta;

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
    /// Time constant (seconds) of the exponential look smoothing. Smaller = snappier and closer to
    /// the raw mouse (less debouncing); larger = smoother but with more perceptible lag. ~0.02–0.05 s
    /// is a reactive-but-clean range; the default keeps the pointer feeling direct. Set to 0 to
    /// disable smoothing entirely (apply the raw delta). See <see cref="Update"/> for why this is
    /// frame-rate independent.
    /// </summary>
    public float LookSmoothingTau { get; set; } = 0.03f;

    /// <summary>
    /// Reference vertical FOV (radians) at which <see cref="LookSensitivityX"/>/<see cref="LookSensitivityY"/>
    /// are the effective sensitivities. The effective look sensitivity scales with the camera's current
    /// <see cref="Camera.FovY"/> divided by this reference, so zooming in (a narrower FOV) makes the same
    /// screen motion turn the view less — the pointer gets finer exactly when the world magnifies. Defaults
    /// to 60° to match <see cref="Camera.FovY"/>'s default, i.e. a scale of 1 until a zoom changes the FOV.
    /// </summary>
    /// <remarks>
    /// The engine has no dynamic FOV zoom yet, so today <see cref="Camera.FovY"/> stays at its nominal
    /// value and this scale is 1 (no behavior change). The dependency is wired now so a future zoom gets
    /// correct feel for free — it is deliberately just the sensitivity link, not a zoom system.
    /// </remarks>
    public float FovYReference { get; set; } = MathF.PI / 3f;

    /// <summary>
    /// Clears the smoothed look delta. Call this across a gap where <see cref="Update"/> is not driven
    /// (e.g. while mouse capture is released) so the next captured frame starts from rest instead of
    /// gliding out a stale delta — otherwise the residual would produce a small unwanted rotation kick.
    /// </summary>
    public void ResetLook() => _smoothedLookDelta = Vector2.Zero;

    /// <summary>
    /// Applies one frame of input to <paramref name="camera"/>. Orientation updates are
    /// frame-rate independent (radians per pixel); movement is scaled by
    /// <paramref name="deltaSeconds"/>.
    /// </summary>
    public void Update(Camera camera, float deltaSeconds, in CameraInput input)
    {
        ArgumentNullException.ThrowIfNull(camera);

        // --- Look: mouse delta → yaw/pitch. Screen Y is down, so a negative dy looks up. ---
        //
        // Frame-rate-independent exponential smoothing. alpha = 1 - exp(-dt/tau): the fraction of the
        // way we move the smoothed delta toward the raw delta this frame. The point of exp(-dt/tau)
        // (vs a fixed per-frame lerp factor like 0.5) is that the residual after a wall-clock duration
        // T is exp(-T/tau) *regardless of how T is split into frames* — sum of dt over the frames is T,
        // so the smoothing decays identically at 60 fps and 144 fps. A fixed factor instead decays per
        // *frame*, so it would feel snappier the higher the frame rate. In steady motion the smoothed
        // delta converges to the raw delta, so the overall look gain is 1 (total rotation == raw input);
        // smoothing only shapes the transient. tau == 0 disables it (apply the raw delta verbatim).
        float alpha = LookSmoothingTau > 0f
            ? 1f - MathF.Exp(-deltaSeconds / LookSmoothingTau)
            : 1f;
        _smoothedLookDelta += (input.LookDelta - _smoothedLookDelta) * alpha;

        // Effective sensitivity scales with the current FOV vs the reference: a narrower FOV (zoom in)
        // → finer look. Ratio is 1 at the nominal FOV, so this is a no-op until a zoom changes FovY.
        float fovScale = camera.FovY / FovYReference;

        camera.Yaw += _smoothedLookDelta.X * LookSensitivityX * fovScale;
        camera.Pitch -= _smoothedLookDelta.Y * LookSensitivityY * fovScale;
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
