using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Classic TF-IDF relevance. A document matches when it contains at least one query term;
/// rare terms are weighted more than common ones.
/// </summary>
/// <remarks>
/// <c>score(t, d) = tf(t, d) * idf(t)</c> with
/// <c>idf(t) = log((N + 1) / (df(t) + 1)) + 1</c>.
/// This smoothed variant guarantees a positive idf, so a score of 0 truly means « no match ».
/// </remarks>
public sealed class TfIdfScorer : ITextScorer
{
    /// <inheritdoc />
    public string Name => "TF-IDF";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.Count;

        if (documentCount == 0)
            return 0;

        double score = 0;

        foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            int tf = index.TermFrequency(documentId, term);

            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(term);

            double idf = Math.Log((documentCount + 1.0) / (df + 1.0)) + 1.0;
            score += tf * idf;
        }

        return score;
    }
}