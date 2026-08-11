using System.Numerics;

namespace Agapanthe.Ui;

/// <summary>
/// The frame's UI, as a flat append-only list of quads (UI-1).
/// <para>
/// <b>Why buffered rather than immediate.</b> Application logic runs during the tick stages (Input, Simulation,
/// PostSimulation) while the command list only exists during Render — the two phases are disjoint, so a caller
/// cannot record GPU commands where it decides what to draw. Accumulating plain structs and consuming them once at
/// the end of the frame is the standard answer, and it keeps GPU types out of gameplay code entirely.
/// </para>
/// <para>
/// <b>Draw order is submission order</b> — painter's algorithm, no sorting, no z field. The renderer emits the span
/// verbatim, so a quad added later draws on top.
/// </para>
/// <para>
/// <b>Zero allocation in steady state.</b> The backing array only ever grows, by doubling, and is never released:
/// after the first few frames the high-water mark is reached and <see cref="Add"/> never allocates again. Single
/// owner thread — the same one that runs the tick.
/// </para>
/// </summary>
public sealed class UiDrawList
{
    private UiQuad[] _quads;
    private int _count;

    public UiDrawList(int initialCapacity = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialCapacity);
        _quads = new UiQuad[initialCapacity];
    }

    /// <summary>Number of quads accumulated this frame.</summary>
    public int Count => _count;

    /// <summary>The frame's quads, in submission order. Valid until the next <see cref="Add"/> or <see cref="Clear"/>.</summary>
    public ReadOnlySpan<UiQuad> Quads => _quads.AsSpan(0, _count);

    /// <summary>Drops every quad, keeping the capacity. Called once at the start of each frame.</summary>
    public void Clear() => _count = 0;

    /// <summary>Appends one quad. Amortised O(1) and allocation-free once the high-water mark is reached.</summary>
    public void Add(in UiQuad quad)
    {
        if (_count == _quads.Length)
        {
            Array.Resize(ref _quads, _quads.Length * 2);
        }

        _quads[_count++] = quad;
    }

    /// <summary>
    /// Appends a solid rectangle, sampling the atlas's reserved white texel so it shares the text's atlas, pipeline
    /// and draw call. <paramref name="whiteTexelUv"/> comes from the font.
    /// </summary>
    public void AddRect(Vector4 rect, Vector4 whiteTexelUv, uint rgba)
        => Add(new UiQuad(rect, whiteTexelUv, rgba, flags: 0u));
}
