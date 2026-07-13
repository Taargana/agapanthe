namespace Agapanthe.Core;

/// <summary>
/// A growable, REUSED list of <see cref="RenderItem"/>. Owned by the Renderer (two instances: the camera list
/// and the shadow-caster list). <see cref="Clear"/> resets the count without freeing the backing array, and
/// <see cref="Add"/> only reallocates when capacity is exceeded — so in steady state building a frame's lists
/// allocates nothing (spec §6, zero-alloc hot path). Not thread-safe: filled and consumed on the render thread
/// at the frame boundary.
/// </summary>
public sealed class RenderList
{
    private RenderItem[] _items;
    private int _count;

    public RenderList(int initialCapacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(initialCapacity);
        _items = initialCapacity == 0 ? [] : new RenderItem[initialCapacity];
    }

    public int Count => _count;

    /// <summary>Backing-array size. Exposed so tests can prove <see cref="Clear"/> keeps capacity (no realloc).</summary>
    public int Capacity => _items.Length;

    /// <summary>The items added since the last <see cref="Clear"/>, in insertion order.</summary>
    public ReadOnlySpan<RenderItem> Items => _items.AsSpan(0, _count);

    /// <summary>Resets to empty without releasing the backing array — the next frame refills it alloc-free.</summary>
    public void Clear() => _count = 0;

    public void Add(in RenderItem item)
    {
        if (_count == _items.Length)
        {
            Grow();
        }

        _items[_count++] = item;
    }

    /// <summary>
    /// Sorts the current items in place with a <b>struct</b> comparer (no boxing, no allocation) — the
    /// render-list builder uses this to impose the stable draw order (spec §6 condition b) before drawing.
    /// </summary>
    public void Sort<TComparer>(TComparer comparer)
        where TComparer : IComparer<RenderItem>
        => _items.AsSpan(0, _count).Sort(comparer);

    private void Grow()
    {
        var newCapacity = _items.Length == 0 ? 64 : _items.Length * 2;
        Array.Resize(ref _items, newCapacity);
    }
}
