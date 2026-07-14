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

    // Radix-sort scratch, reused across frames (grown only with capacity, like _items) so sorting allocates
    // nothing in steady state. _keys mirrors each item's SortKey; the sort permutes INDICES (4 bytes) between
    // _indexA/_indexB rather than moving 88-byte RenderItems, then gathers once into _scratchItems.
    private ulong[] _keys = [];
    private int[] _indexA = [];
    private int[] _indexB = [];
    private RenderItem[] _scratchItems = [];

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

    /// <summary>
    /// The current items as a MUTABLE span, for in-place compaction only (P3-M2 D3.c, two-pass shadow cull): the
    /// World keeps the surviving casters at the front, then calls <see cref="Truncate"/>. Do not <see cref="Add"/>
    /// while holding this span — a grow would invalidate it.
    /// </summary>
    public Span<RenderItem> ItemsMutable => _items.AsSpan(0, _count);

    /// <summary>Drops every item past <paramref name="newCount"/> (kept in [0, current count]) — the tail-drop step
    /// of an in-place compaction. The backing array is untouched, so the next frame still refills alloc-free.</summary>
    public void Truncate(int newCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(newCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(newCount, _count);
        _count = newCount;
    }

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
    /// Sorts the items by <see cref="RenderItem.SortKey"/> ascending, imposing the deterministic draw order
    /// (spec §6 condition b) — Arch iterates by archetype/chunk, which is not insertion-stable, so the key must
    /// carry its own tie-break (see <see cref="RenderItem.ComposeSortKey"/>).
    /// <para>
    /// <b>LSD radix sort</b> over the 64-bit key, eight 8-bit passes, O(n). It replaced an insertion sort that was
    /// O(n) only while the key was the spawn order (near-sorted input); once the key leads with the material (M4),
    /// culling and multiple materials make the input arbitrary and insertion sort's O(n²) bites at thousands of
    /// entities. Radix is unconditionally O(n) and order-independent.
    /// </para>
    /// <para>
    /// <b>Zero-alloc.</b> The passes permute 4-byte indices (not 88-byte items) between two reused scratch
    /// buffers; a single gather then writes the items in order. Every buffer is a field grown only with capacity,
    /// and the per-pass histogram is <c>stackalloc</c> — nothing heap-allocates in steady state. Radix is not
    /// comparison-based, so unlike <c>Span.Sort(comparer)</c> it never boxes a comparer (the ~88 B/call trap this
    /// type has always avoided).
    /// </para>
    /// </summary>
    public void SortByKey()
    {
        if (_count < 2)
        {
            return;
        }

        EnsureSortScratch();

        for (var i = 0; i < _count; i++)
        {
            _keys[i] = _items[i].SortKey;
            _indexA[i] = i;
        }

        // Eight LSD passes over the 8 bytes of the key. An even number of passes, so the result lands back in
        // _indexA (src and dst swap each pass, starting from _indexA). The histogram is stack-allocated ONCE and
        // cleared per pass (a stackalloc inside the loop would risk overflowing the stack — CA2014).
        Span<int> counts = stackalloc int[256];
        var src = _indexA;
        var dst = _indexB;
        for (var shift = 0; shift < 64; shift += 8)
        {
            counts.Clear();
            for (var i = 0; i < _count; i++)
            {
                counts[(int)((_keys[src[i]] >> shift) & 0xFF)]++;
            }

            var sum = 0;
            for (var b = 0; b < 256; b++)
            {
                var c = counts[b];
                counts[b] = sum; // exclusive prefix sum → the bucket's start offset
                sum += c;
            }

            for (var i = 0; i < _count; i++)
            {
                var bucket = (int)((_keys[src[i]] >> shift) & 0xFF);
                dst[counts[bucket]++] = src[i];
            }

            (src, dst) = (dst, src);
        }

        // src (== _indexA) now holds the ascending permutation. Gather once, then swap the sorted buffer in.
        for (var i = 0; i < _count; i++)
        {
            _scratchItems[i] = _items[src[i]];
        }

        (_items, _scratchItems) = (_scratchItems, _items);
    }

    private void EnsureSortScratch()
    {
        if (_keys.Length >= _items.Length)
        {
            return;
        }

        var capacity = _items.Length;
        _keys = new ulong[capacity];
        _indexA = new int[capacity];
        _indexB = new int[capacity];
        _scratchItems = new RenderItem[capacity];
    }

    private void Grow()
    {
        var newCapacity = _items.Length == 0 ? 64 : _items.Length * 2;
        Array.Resize(ref _items, newCapacity);
    }
}
