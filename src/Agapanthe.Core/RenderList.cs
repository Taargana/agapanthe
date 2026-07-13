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
    /// Sorts the items in place by <see cref="RenderItem.SortKey"/> ascending, imposing the stable draw order
    /// (spec §6 condition b) — Arch iterates by archetype/chunk, which is not insertion-stable.
    /// <para>
    /// Hand-written insertion sort rather than <c>Span.Sort(comparer)</c>: the BCL overload allocates ~88 bytes
    /// per call even with a struct comparer (it boxes it into <c>IComparer&lt;T&gt;</c> internally), which breaks
    /// the zero-alloc-per-frame invariant — measured, not assumed. Insertion sort is allocation-free, stable, and
    /// O(n) on the near-sorted input we actually have (spawn order ≈ chunk order).
    /// </para>
    /// <para><b>M4</b>: with thousands of culled entities and a real material/depth key the O(n²) worst case must
    /// be replaced — an LSD radix sort over the 64-bit key with a reused scratch buffer is the natural fit
    /// (O(n), still zero-alloc).</para>
    /// </summary>
    public void SortByKey()
    {
        var items = _items.AsSpan(0, _count);
        for (var i = 1; i < items.Length; i++)
        {
            var item = items[i];
            var j = i - 1;
            while (j >= 0 && items[j].SortKey > item.SortKey)
            {
                items[j + 1] = items[j];
                j--;
            }

            items[j + 1] = item;
        }
    }

    private void Grow()
    {
        var newCapacity = _items.Length == 0 ? 64 : _items.Length * 2;
        Array.Resize(ref _items, newCapacity);
    }
}
