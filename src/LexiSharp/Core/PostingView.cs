namespace LexiSharp.Core;

/// <summary>
/// One term's posting list as the index holds it: ordinals, term frequencies and the document
/// lengths the fold reads, all by reference and none of them copied.
/// </summary>
/// <remarks>
/// This exists so a query term can be resolved without being materialized as a string first. The
/// three spans view the index's own arrays and live exactly as long as the index does not rebuild
/// them, which is what the caller of <see cref="ISpanAccumulatingIndex.TryResolvePostings"/> is
/// told; the caller folds them within the same search.
/// </remarks>
internal readonly ref struct PostingView
{
    public PostingView(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> lengths,
        ReadOnlySpan<int> positions = default,
        object? positionsOwner = null)
    {
        Ordinals = ordinals;
        Frequencies = frequencies;
        Lengths = lengths;

        // Held as the owner, not only as the span: a span does not keep its source alive, and a view
        // handed back as spans over a reusable buffer would point at something a collector may take
        // before the caller finished with it. A source whose positions live in an array it owns passes
        // that array; one whose positions are shared passes the holder.
        _positionsOwner = positionsOwner;
        Positions = positions;
    }

    private readonly object? _positionsOwner;

    /// <summary>Document ordinals, ascending, one per document carrying the term.</summary>
    public ReadOnlySpan<int> Ordinals { get; }

    /// <summary>Term frequency in the document at the same position in <see cref="Ordinals"/>.</summary>
    public ReadOnlySpan<int> Frequencies { get; }

    /// <summary>Token count per ordinal, in the layout the accumulation loop reads.</summary>
    public ReadOnlySpan<int> Lengths { get; }

    /// <summary>
    /// Every entry's term positions, in entry order: <c>Frequencies[i]</c> positions for entry
    /// <c>i</c>, ascending within the entry. Its length is the sum of <see cref="Frequencies"/>.
    /// </summary>
    /// <remarks>
    /// Empty when the caller has none to give — the accumulation loop never reads this, and a source
    /// that only accumulates can leave it out.
    /// </remarks>
    public ReadOnlySpan<int> Positions { get; }

    /// <summary>Number of posting entries.</summary>
    public int Count => Ordinals.Length;
}
