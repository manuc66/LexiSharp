namespace LexiSharp.Core;

/// <summary>
/// The two posting statistics an upper bound on a term's score is built from: the largest term
/// frequency any document carrying the term has, and the smallest token count among those documents.
/// </summary>
/// <remarks>
/// <para>
/// The pair is what makes a bound valid without scoring a document. A term's contribution to a
/// length-normalized score rises with its frequency and falls with the document length, so the value
/// at <c>(MaxFrequency, MinDocumentLength)</c> is at or above every document's, even when the two
/// statistics come from two different documents.
/// </para>
/// <para>
/// It is deliberately not a score: converting it to one needs the scorer's parameters and the
/// corpus's inverse document frequency, and those belong to the query, not to the index.
/// </para>
/// </remarks>
/// <param name="MaxFrequency">
/// Largest number of occurrences of the term in one document; zero when no document carries it.
/// </param>
/// <param name="MinDocumentLength">
/// Smallest token count among the documents carrying the term; zero when none does.
/// </param>
public readonly record struct TermFrequencyBound(int MaxFrequency, int MinDocumentLength)
{
    /// <summary>
    /// Measures the bound from a term's postings, given token counts indexed by ordinal.
    /// </summary>
    /// <remarks>
    /// Internal because it is the one piece of arithmetic the in-memory index and the segment writer
    /// have to agree on. Both call it over arrays they already hold, so a stored bound and a lazily
    /// built one cannot disagree about what the maximum or the minimum is.
    /// </remarks>
    /// <param name="ordinals">The term's document ordinals, ascending.</param>
    /// <param name="frequencies">The term's frequency at the same position in <paramref name="ordinals"/>.</param>
    /// <param name="lengthsByOrdinal">Token count per document ordinal.</param>
    internal static TermFrequencyBound Measure(
        ReadOnlySpan<int> ordinals,
        ReadOnlySpan<int> frequencies,
        ReadOnlySpan<int> lengthsByOrdinal)
    {
        int maxFrequency = 0;
        int minLength = int.MaxValue;

        for (int i = 0; i < ordinals.Length; i++)
        {
            int frequency = frequencies[i];

            if (frequency > maxFrequency)
                maxFrequency = frequency;

            int length = lengthsByOrdinal[ordinals[i]];

            if (length < minLength)
                minLength = length;
        }

        return ordinals.Length == 0
            ? default
            : new TermFrequencyBound(maxFrequency, minLength);
    }
}
