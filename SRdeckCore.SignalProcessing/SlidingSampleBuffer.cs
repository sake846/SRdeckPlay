namespace SRdeckCore.SignalProcessing;

/// <summary>
/// Indexable circular storage for streaming sample histories. Logical indices
/// stay contiguous while consumed prefixes are discarded in O(1).
/// </summary>
public sealed class SlidingSampleBuffer<T>(int initialCapacity) where T : struct
{
    private T[] items = initialCapacity == 0 ? [] : new T[initialCapacity];
    private int head;

    public int Count { get; private set; }
    public int Capacity => items.Length;

    public T this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            return items[PhysicalIndex(index)];
        }
        set
        {
            if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
            items[PhysicalIndex(index)] = value;
        }
    }

    public T this[Index index]
    {
        get => this[index.GetOffset(Count)];
        set => this[index.GetOffset(Count)] = value;
    }

    public void Add(T value)
    {
        if (Count == items.Length) EnsureCapacity(Count + 1);
        items[PhysicalIndex(Count)] = value;
        Count++;
    }

    public void EnsureCapacity(int capacity)
    {
        if (capacity <= items.Length) return;
        int newCapacity = Math.Max(capacity, Math.Max(4, items.Length * 2));
        var replacement = new T[newCapacity];
        CopyTo(replacement);
        items = replacement;
        head = 0;
    }

    public void RemoveFirst(int count)
    {
        if ((uint)count > (uint)Count) throw new ArgumentOutOfRangeException(nameof(count));
        if (count == 0) return;
        int nextHead = head + count;
        head = nextHead < items.Length ? nextHead : nextHead - items.Length;
        Count -= count;
        if (Count == 0) head = 0;
    }

    public void Clear()
    {
        head = 0;
        Count = 0;
    }

    public void TrimExcess()
    {
        if (Count == items.Length) return;
        if (Count == 0)
        {
            items = [];
            head = 0;
            return;
        }
        var replacement = new T[Count];
        CopyTo(replacement);
        items = replacement;
        head = 0;
    }

    private int PhysicalIndex(int logicalIndex)
    {
        int index = head + logicalIndex;
        return index < items.Length ? index : index - items.Length;
    }

    /// <summary>Copies the logical contents in order, including across the circular wrap boundary.</summary>
    public void CopyTo(Span<T> destination)
    {
        if (destination.Length < Count)
            throw new ArgumentException("The destination is too small.", nameof(destination));
        if (Count == 0) return;
        int firstLength = Math.Min(Count, items.Length - head);
        items.AsSpan(head, firstLength).CopyTo(destination);
        if (firstLength < Count)
            items.AsSpan(0, Count - firstLength).CopyTo(destination[firstLength..]);
    }
}
