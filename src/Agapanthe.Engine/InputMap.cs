namespace Agapanthe.Engine;

/// <summary>When a button binding fires a command.</summary>
public enum ButtonTrigger : byte
{
    /// <summary>On the rising edge — one command per physical press (<see cref="InputSnapshot.Pressed"/>).</summary>
    OnPress,

    /// <summary>On the falling edge — one command per physical release (<see cref="InputSnapshot.Released"/>).</summary>
    OnRelease,

    /// <summary>Every tick the button is down (<see cref="InputSnapshot.Held"/>).</summary>
    WhileHeld,
}

/// <summary>
/// Declarative <see cref="InputSnapshot"/> → <see cref="SimCommand"/> mapping (MP-0d, locked decision 6). Fixed
/// capacity (<see cref="MaxButtonBindings"/> button bindings + one axis-vector binding), 0-alloc to read.
/// <see cref="InputTranslation.Emit"/> applies it once per tick.
/// </summary>
public sealed class InputMap
{
    /// <summary>Maximum number of button bindings. Exceeding it throws — bindings are authored, not runtime data.</summary>
    public const int MaxButtonBindings = 32;

    internal readonly record struct ButtonBinding(int Bit, byte Kind, ButtonTrigger Trigger);

    private readonly ButtonBinding[] _buttons = new ButtonBinding[MaxButtonBindings];
    private int _buttonCount;

    private bool _hasAxis;
    private byte _axisKind;
    private int _axisX;
    private int _axisY;
    private int _axisZ;

    /// <summary>
    /// Binds button <paramref name="bit"/> (0..63, indexing <see cref="InputSnapshot"/>'s masks) to a command of
    /// <paramref name="kind"/>, fired per <paramref name="trigger"/>. Re-binding the same (bit, trigger) pair
    /// replaces the kind rather than adding a second binding.
    /// </summary>
    public void BindButton(int bit, byte kind, ButtonTrigger trigger)
    {
        if ((uint)bit >= 64)
        {
            throw new ArgumentOutOfRangeException(nameof(bit), bit, "Button bit must be 0..63.");
        }

        for (var i = 0; i < _buttonCount; i++)
        {
            if (_buttons[i].Bit == bit && _buttons[i].Trigger == trigger)
            {
                _buttons[i] = new ButtonBinding(bit, kind, trigger);
                return;
            }
        }

        if (_buttonCount == MaxButtonBindings)
        {
            throw new InvalidOperationException(
                $"InputMap holds at most {MaxButtonBindings} button bindings — bindings are authored, not runtime data.");
        }

        _buttons[_buttonCount++] = new ButtonBinding(bit, kind, trigger);
    }

    /// <summary>
    /// Binds a command of <paramref name="kind"/> emitted <b>every tick</b>, whose <see cref="SimCommand.Vector"/>
    /// is <c>(axes[axisX], axes[axisY], axes[axisZ])</c> from the tick's <see cref="InputSnapshot.Axes"/>. Each
    /// axis index is 0..3. A second call replaces the binding.
    /// <para>The emitted command's <see cref="SimCommand.Target"/> is <c>default</c> — the declarative path never
    /// names an entity; the apply point resolves the target from the emitter's identity (see
    /// <see cref="SimCommand"/>).</para>
    /// </summary>
    public void BindAxisVector(byte kind, int axisX, int axisY, int axisZ)
    {
        Validate(axisX, nameof(axisX));
        Validate(axisY, nameof(axisY));
        Validate(axisZ, nameof(axisZ));

        _hasAxis = true;
        _axisKind = kind;
        _axisX = axisX;
        _axisY = axisY;
        _axisZ = axisZ;

        static void Validate(int axis, string name)
        {
            if ((uint)axis >= 4)
            {
                throw new ArgumentOutOfRangeException(name, axis, "Axis index must be 0..3.");
            }
        }
    }

    internal ReadOnlySpan<ButtonBinding> Buttons => _buttons.AsSpan(0, _buttonCount);

    internal bool HasAxis => _hasAxis;

    internal byte AxisKind => _axisKind;

    internal int AxisX => _axisX;

    internal int AxisY => _axisY;

    internal int AxisZ => _axisZ;
}
