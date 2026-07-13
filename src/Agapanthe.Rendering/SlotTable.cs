using Agapanthe.Graphics;

namespace Agapanthe.Rendering;

/// <summary>
/// The generational slot map behind the engine-wide handles (spec §3.2). One table per resource kind; a handle is
/// an <c>(index, generation)</c> pair into it.
/// <para>
/// A freed slot bumps its generation, so every handle minted for it before the free is permanently stale:
/// <see cref="Resolve"/> then throws instead of returning whatever now occupies the slot. Fresh slots start at
/// generation 1, so a default handle (index 0, generation 0) never resolves either.
/// </para>
/// <para>
/// GPU-free by construction (<typeparamref name="T"/> is just a reference), which is what makes the handle
/// lifecycle testable without a device.
/// </para>
/// </summary>
internal sealed class SlotTable<T>(string label)
    where T : class
{
    private T?[] _values = new T?[16];
    private uint[] _generations = new uint[16];
    private readonly Stack<int> _free = new();

    /// <summary>The high-water mark of allocated slots (free slots included).</summary>
    public int Count { get; private set; }

    /// <summary>Stores <paramref name="value"/> in a recycled or fresh slot and mints the handle for it.</summary>
    public (int Index, uint Generation) Add(T value)
    {
        int index;
        if (_free.Count > 0)
        {
            index = _free.Pop(); // its generation was already bumped on Free
        }
        else
        {
            if (Count == _values.Length)
            {
                Array.Resize(ref _values, _values.Length * 2);
                Array.Resize(ref _generations, _generations.Length * 2);
            }

            index = Count++;
            _generations[index] = 1;
        }

        _values[index] = value;
        return (index, _generations[index]);
    }

    /// <summary>
    /// Empties a slot and bumps its generation. The value itself is NOT disposed — ownership of disposal stays with
    /// the caller, which frees slots and disposes GPU objects in its own order.
    /// </summary>
    public void Free(int index)
    {
        _values[index] = null;
        _generations[index]++;
        _free.Push(index);
    }

    /// <summary>
    /// Resolves a handle. An out-of-range index, a freed slot, or a generation mismatch (a handle from an unloaded
    /// model) is a hard error (spec §4) — never a silently wrong draw.
    /// </summary>
    public T Resolve(int index, uint generation)
    {
        if ((uint)index >= (uint)Count)
        {
            throw new GraphicsException($"{label} handle index {index} is out of range ({Count} slots).");
        }

        if (_generations[index] != generation || _values[index] is not { } value)
        {
            throw new GraphicsException(
                $"{label} handle {index} is stale (handle generation {generation}, slot generation " +
                $"{_generations[index]}): its model was unloaded.");
        }

        return value;
    }
}
