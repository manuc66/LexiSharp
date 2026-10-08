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
    public PostingView(ReadOnlySpan<int> ordinals, ReadOnlySpan<int> frequencies, ReadOnlySpan<int> lengths)
    {
        Ordinals = ordinals;
        Frequencies = frequencies;
        Lengths = lengths;
    }

    /// <summary>Document ordinals, ascending, one per document carrying the term.</summary>
    public ReadOnlySpan<int> Ordinals { get; }

    /// <summary>Term frequency in the document at the same position in <see cref="Ordinals"/>.</summary>
    public ReadOnlySpan<int> Frequencies { get; }

    /// <summary>Token count per ordinal, in the layout the accumulation loop reads.</summary>
    public ReadOnlySpan<int> Lengths { get; }

    /// <summary>Number of posting entries.</summary>
    public int Count => Ordinals.Length;
}
