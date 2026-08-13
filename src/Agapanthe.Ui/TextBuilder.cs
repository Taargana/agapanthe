using System.Globalization;

namespace Agapanthe.Ui;

/// <summary>
/// Builds a <see cref="ReadOnlySpan{T}"/> of characters in a caller-owned buffer, without ever allocating (UI-2).
/// <para>
/// This is what lets a HUD refresh <b>every frame</b>. An interpolated string would allocate per frame, which
/// is exactly the metric this overlay reports — the profiler would be measuring itself. It is also why the previous
/// title-bar HUD had to be throttled to 4 Hz and why the landing challenge only rebuilt its title on change: with
/// span formatting, neither workaround is needed.
/// </para>
/// </summary>
public ref struct TextBuilder(Span<char> buffer)
{
    private readonly Span<char> _buffer = buffer;
    private int _length;

    /// <summary>What has been written so far.</summary>
    public readonly ReadOnlySpan<char> Written => _buffer[.._length];

    public void Append(ReadOnlySpan<char> text)
    {
        // Truncate rather than throw: a debug overlay must never take down a frame over a formatting edge case.
        var n = Math.Min(text.Length, _buffer.Length - _length);
        if (n > 0)
        {
            text[..n].CopyTo(_buffer[_length..]);
            _length += n;
        }
    }

    public void Append(char value)
    {
        if (_length < _buffer.Length)
        {
            _buffer[_length++] = value;
        }
    }

    public void Append(int value)
    {
        // InvariantCulture throughout: a French locale would render "7,0" instead of "7.0" and the reference
        // capture hash would then differ from machine to machine.
        if (value.TryFormat(_buffer[_length..], out var written, provider: CultureInfo.InvariantCulture))
        {
            _length += written;
        }
    }

    public void Append(long value)
    {
        if (value.TryFormat(_buffer[_length..], out var written, provider: CultureInfo.InvariantCulture))
        {
            _length += written;
        }
    }

    public void Append(float value, ReadOnlySpan<char> format)
    {
        if (value.TryFormat(_buffer[_length..], out var written, format, CultureInfo.InvariantCulture))
        {
            _length += written;
        }
    }
}
