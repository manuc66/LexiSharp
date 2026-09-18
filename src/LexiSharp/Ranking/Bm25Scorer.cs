using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Okapi BM25 relevance — the standard non-semantic ranking model for full-text search.
/// It blends term saturation (<c>k1</c>) with document-length normalization (<c>b</c>).
/// </summary>
/// <remarks>
/// <c>score(q,d) = Σ_t idf(t) * tf(t,d) * (k1 + 1) / (tf(t,d) + k1 * (1 − b + b · |d| / avgdl))</c>
/// with <c>idf(t) = ln(1 + (N − df(t) + 0.5) / (df(t) + 0.5))</c>.
/// </remarks>
public sealed class Bm25Scorer : ITextScorer
{
    private readonly double _k1;
    private readonly double _b;

    /// <param name="k1">Term-frequency saturation: higher values let frequent terms contribute more.</param>
    /// <param name="b">Document-length normalization, in <c>[0, 1]</c>. <c>0</c> disables normalization.</param>
    public Bm25Scorer(double k1 = 1.5, double b = 0.75)
    {
        if (k1 < 0) throw new ArgumentOutOfRangeException(nameof(k1), k1, "k1 must be non-negative.");
        if (b is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(b), b, "b must be within [0, 1].");

        _k1 = k1;
        _b = b;
    }

    /// <summary>Builds a scorer from a preset or tuned <see cref="Bm25Parameters"/> profile.</summary>
    public Bm25Scorer(Bm25Parameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (parameters.K1 < 0) throw new ArgumentOutOfRangeException(nameof(parameters), parameters, "k1 must be non-negative.");
        if (parameters.B is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(parameters), parameters, "b must be within [0, 1].");

        _k1 = parameters.K1;
        _b = parameters.B;
    }

    /// <inheritdoc />
    public string Name => "BM25";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.Count;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;

        if (documentCount == 0 || documentLength == 0 || averageLength <= 0)
            return 0;

        double score = 0;

        foreach (var term in queryTerms.Distinct(StringComparer.Ordinal))
        {
            int tf = index.TermFrequency(documentId, term);

            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(term);

            double idf = Math.Log(1.0 + (documentCount - df + 0.5) / (df + 0.5));

            double normalization = 1.0 - _b + _b * documentLength / averageLength;
            score += idf * tf * (_k1 + 1.0) / (tf + _k1 * normalization);
        }

        return score;
    }
}