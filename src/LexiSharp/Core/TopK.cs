namespace LexiSharp.Core;

/// <summary>
/// A bounded, best-first buffer: keeps the <c>capacity</c> items that sort first under a comparer.
/// </summary>
/// <remarks>
/// The buffer owns its array and exposes the used prefix through <see cref="Items"/>, so a caller
/// that needs to aggregate the winners in place does not allocate a second list. Inserting a
/// candidate that beats the current worst is O(k) only when the buffer is full and the candidate
/// actually enters; a scan of a corpus that mostly misses the top k pays one comparison per item.
/// </remarks>
/// <typeparam name="T">The ranked item type.</typeparam>
internal sealed class TopK<T>
{
    private readonly T[] _items;
    private readonly IComparer<T> _comparer;
    private int _count;

    /// <param name="capacity">Maximum number of items to keep; zero keeps none.</param>
    /// <param name="comparer">Orders items best-first: a negative result means the left item wins.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is negative.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="comparer"/> is null.</exception>
    public TopK(int capacity, IComparer<T> comparer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        ArgumentNullException.ThrowIfNull(comparer);

        _items = capacity == 0 ? Array.Empty<T>() : new T[capacity];
        _comparer = comparer;
    }

    /// <summary>Number of items currently held.</summary>
    public int Count => _count;

    /// <summary>One held item, in best-first order.</summary>
    public T this[int index] => _items[index];

    /// <summary>The used prefix, which the caller may reorder and overwrite once the scan is done.</summary>
    public Span<T> Items => _items.AsSpan(0, _count);

    /// <summary>Offers a candidate; it enters only when it beats the current worst.</summary>
    public void Add(T candidate)
    {
        if (_items.Length == 0)
            return;

        int index;

        if (_count < _items.Length)
        {
            index = _count++;
        }
        else
        {
            if (!IsBetter(candidate, _items[^1]))
                return;

            index = _items.Length - 1;
        }

        while (index > 0 && IsBetter(candidate, _items[index - 1]))
        {
            _items[index] = _items[index - 1];
            index--;
        }

        _items[index] = candidate;
    }

    private bool IsBetter(T left, T right) => _comparer.Compare(left, right) < 0;
}
