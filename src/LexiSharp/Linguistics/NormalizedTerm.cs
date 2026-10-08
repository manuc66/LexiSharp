namespace LexiSharp.Linguistics;

/// <summary>
/// One normalized term of a tokenized text: a slice of the text that produced it, or the string it
/// had to be materialized into.
/// </summary>
/// <remarks>
/// A term is its own normalized form when the source already holds it — lowercase ASCII, under an
/// analysis that does not fold, stem, filter or trim — and that is the common case for a query. There
/// the term needs no string at all, and <see cref="Materialized"/> is <c>null</c>: a caller that only
/// looks terms up in a vocabulary can resolve them by span, which is what this type exists to make
/// possible.
/// </remarks>
/// <param name="Start">
/// Zero-based index in the source of the term's first character. Read only when
/// <paramref name="Materialized"/> is <c>null</c>.
/// </param>
/// <param name="Length">
/// Number of source characters the term covers, starting at <paramref name="Start"/>. Read only when
/// <paramref name="Materialized"/> is <c>null</c>.
/// </param>
/// <param name="Materialized">
/// The normalized term, when it is not the source slice: a folded, stemmed, trimmed or otherwise
/// replaced term.
/// </param>
public readonly record struct NormalizedTerm(int Start, int Length, string? Materialized)
{
    /// <summary>
    /// The term's normalized characters: the source slice when the term is its own normal form, the
    /// materialized string otherwise.
    /// </summary>
    /// <param name="source">The text the term was tokenized from.</param>
    public ReadOnlySpan<char> View(ReadOnlySpan<char> source) =>
        Materialized is null ? source.Slice(Start, Length) : Materialized.AsSpan();

    /// <summary>
    /// Whether the term at <paramref name="index"/> repeats one that appeared before it, compared as
    /// characters.
    /// </summary>
    /// <remarks>
    /// This is the query's deduplication rule as a search needs it: a scorer that counts a repeated
    /// term once keeps the first occurrence, and one that counts it per occurrence keeps them all.
    /// It is asked of the terms in order, over at most a handful of them, and allocates nothing.
    /// </remarks>
    /// <param name="terms">The query terms, in query order.</param>
    /// <param name="index">Position of the term to test.</param>
    /// <param name="source">The text the terms slice.</param>
    internal static bool IsRepeated(ReadOnlySpan<NormalizedTerm> terms, int index, ReadOnlySpan<char> source)
    {
        var term = terms[index].View(source);

        for (int i = 0; i < index; i++)
        {
            if (terms[i].View(source).SequenceEqual(term))
                return true;
        }

        return false;
    }
}
