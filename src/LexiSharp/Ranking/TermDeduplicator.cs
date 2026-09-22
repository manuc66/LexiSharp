namespace LexiSharp.Ranking;

/// <summary>
/// Order-preserving duplicate removal for query terms.
/// </summary>
/// <remarks>
/// The pattern in the scorers is per-document work: a search over 10 000 documents feeds
/// the same query terms into <see cref="LexiSharp.Core.ITextScorer.Score"/> 10 000 times. LINQ's
/// <c>Distinct</c> allocates a set (and a list) on every call, which is observable garbage.
/// The common case in the engine is a short, already-distinct term list; this helper turns
/// that case into a zero-allocation early return, and only allocates when duplicates are
/// actually present (direct callers passing a duplicated query) or the query is large.
/// </remarks>
internal static class TermDeduplicator
{
    /// <summary>
    /// Returns the input list itself when it can prove it is already distinct — no allocation —
    /// otherwise a new copy without the duplicates, preserving first-occurrence order like
    /// LINQ's <c>Enumerable.Distinct</c>.
    /// </summary>
    public static IReadOnlyList<string> Distinct(IReadOnlyList<string> terms)
    {
        int count = terms.Count;
        if (count <= 1)
            return terms;

        if (count <= 8)
        {
            for (int i = 0; i < count; i++)
            {
                for (int j = i + 1; j < count; j++)
                {
                    if (string.Equals(terms[i], terms[j], StringComparison.Ordinal))
                        return Deduplicate(terms);
                }
            }

            return terms;
        }

        return Deduplicate(terms);
    }

    private static IReadOnlyList<string> Deduplicate(IReadOnlyList<string> terms)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        // Stateful duplicate filter: LINQ Where would capture a growing HashSet in a per-call
        // closure (exactly the allocation these helpers avoid). // NOSONAR:S3267
        foreach (var term in terms)
        {
            if (seen.Add(term))
                result.Add(term);
        }

        return result;
    }
}