namespace LexiSharp.Ranking;

/// <summary>
/// Order-preserving duplicate handling for query terms.
/// </summary>
/// <remarks>
/// <para>
/// The pattern in the scorers is per-document work: a search over 10 000 documents feeds
/// the same query terms into <see cref="LexiSharp.Core.ITextScorer.Score"/> 10 000 times. LINQ's
/// <c>Distinct</c> allocates a set (and a list) on every call, which is observable garbage.
/// The common case in the engine is a short, already-distinct term list; this helper turns
/// that case into a zero-allocation early return, and only allocates when duplicates are
/// actually present (direct callers passing a duplicated query) or the query is large.
/// </para>
/// <para>
/// Whether to deduplicate at all is a ranking decision, not an implementation detail, so
/// <see cref="QueryTermWeighting"/> chooses and this class carries it out. A classic search query
/// repeats nothing and both choices score identically; a query that <i>is</i> a document repeats
/// its content words, and there the two differ — measured on the ArguAna corpus, where every test
/// query is a whole ~200-word argument, counting query-term frequency instead of deduplicating is
/// worth +0.052 on nDCG@10 (0.219 to 0.271 at k1=0.9/b=0.4, 1,406 queries, English analysis). On
/// NFCorpus and SciFact, whose queries are short, it changes nothing to four decimals. A reference
/// implementation scores one clause per query-token occurrence, so it always counts; see
/// <see cref="QueryTermWeighting"/> for why the default here is the other choice, and for the
/// larger figure this repository used to publish for it and has withdrawn.
/// </para>
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
        // closure (exactly the allocation these helpers avoid).
        foreach (var term in terms) // NOSONAR:S3267
        {
            if (seen.Add(term))
                result.Add(term);
        }

        return result;
    }
}