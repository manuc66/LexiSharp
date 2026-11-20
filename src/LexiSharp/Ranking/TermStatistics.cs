using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// The shared body of <see cref="IQueryTermStatistics.DescribeTerms"/> for the built-in scorers,
/// which differ only in the idf they assign a term.
/// </summary>
/// <remarks>
/// <para>
/// Deduplication is first-occurrence order, not a set: a caller reading the answer reads it in the
/// order the query was written, the same order <see cref="IScoreExplainer.Explain"/> reports
/// contributions in. A repeated term describes one term once — its frequency is a property of the
/// corpus, and the number of times the caller typed it is not part of that property.
/// </para>
/// <para>
/// The idf formulas are delegates rather than duplicated loops, so a scorer adds one line and
/// cannot drift from its own arithmetic. That the value equals the one the scorer's own scoring path
/// evaluates is pinned against <see cref="IScoreExplainer.Explain"/> for the two formula families.
/// </para>
/// </remarks>
internal static class TermStatistics
{
    /// <summary>BM25's idf: <c>ln(1 + (N − df + 0.5) / (df + 0.5))</c>.</summary>
    internal static double Bm25Idf(int documentCount, int documentFrequency) =>
        Math.Log(1.0 + ((documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));

    /// <summary>TF-IDF's smoothed idf: <c>log((N + 1) / (df + 1)) + 1</c>.</summary>
    internal static double SmoothedIdf(int documentCount, int documentFrequency) =>
        Math.Log((documentCount + 1.0) / (documentFrequency + 1.0)) + 1.0;

    internal static IReadOnlyList<QueryTermStatistic> Describe(
        IReadOnlyList<string> terms,
        IReadOnlyTextIndex index,
        Func<int, int, double> idf)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(index);

        if (terms.Count == 0)
            return Array.Empty<QueryTermStatistic>();

        int documentCount = index.StatisticDocumentCount;

        // A query that repeats a term would otherwise report it once per occurrence. The list is
        // filled in reading order, so first occurrence is the one kept.
        var seen = new HashSet<string>(terms.Count, StringComparer.Ordinal);
        var described = new List<QueryTermStatistic>(terms.Count);

        for (int i = 0; i < terms.Count; i++)
        {
            string term = terms[i];

            if (!seen.Add(term))
                continue;

            int documentFrequency = index.DocumentFrequency(term);
            described.Add(new QueryTermStatistic(
                term,
                documentFrequency,
                documentCount,
                idf(documentCount, documentFrequency)));
        }

        return described;
    }
}
