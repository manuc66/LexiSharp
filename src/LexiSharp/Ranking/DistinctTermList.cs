namespace LexiSharp.Ranking;

/// <summary>
/// Marker over an already-distinct list of query terms.
/// </summary>
/// <remarks>
/// The search engine deduplicates the query terms once per query and hands the result to the
/// scorer for every candidate document; without a marker, each scorer would redundantly
/// re-run its deduplication on each call. Scorers that honor the marker (return the wrapped
/// list as-is) keep full semantics — a duplicated query produces the same scores as a
/// distinct one — while paying O(1) per document instead of O(k²) string comparisons.
/// </remarks>
internal sealed class DistinctTermList : IReadOnlyList<string>
{
    private readonly IReadOnlyList<string> _terms;

    private DistinctTermList(IReadOnlyList<string> distinctTerms)
    {
        _terms = distinctTerms;
    }

    /// <summary>Wraps an already-deduplicated list of terms.</summary>
    public static DistinctTermList Wrap(IReadOnlyList<string> distinctTerms) => new(distinctTerms);

    public string this[int index] => _terms[index];

    public int Count => _terms.Count;

    public IEnumerator<string> GetEnumerator() => _terms.GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _terms.GetEnumerator();
}