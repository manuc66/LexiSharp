using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Query likelihood retrieval model with Jelinek-Mercer smoothing, a probabilistic
/// language-model alternative to BM25.
/// </summary>
/// <remarks>
/// <c>score(q, d) = Σ_t log P(t | d)</c> where
/// <c>P(t | d) = (1 − λ) · tf(t, d) / |d| + λ · cf(t) / C</c>.
/// The mixture weight <c>λ</c> interpolates between the document language model and the
/// collection model; values around <c>0.1–0.3</c> behave well in practice. Scores are
/// log-probabilities (negative for matching documents); a document sharing no term with
/// the query scores exactly <c>0</c>, so the engine's « score 0 means no match » rule applies.
/// </remarks>
public sealed class QueryLikelihoodScorer : ITextScorer, ITermOverlapScorer
{
    private readonly double _lambda;

    /// <param name="lambda">Collection-model weight in <c>(0, 1]</c>. Closer to 0 ⇒ smoother, more dependent on document.</param>
    public QueryLikelihoodScorer(double lambda = 0.2)
    {
        if (double.IsNaN(lambda) || lambda is <= 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(lambda), lambda, "lambda must be finite and within (0, 1].");

        _lambda = lambda;
    }

    /// <inheritdoc />
    public string Name => "QueryLikelihood";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentLength = index.DocumentLength(documentId);
        long collectionTokens = index.CorpusTokenCount;

        if (documentLength == 0 || collectionTokens == 0)
            return 0;

        double score = 0;
        bool sharesTerm = false;

        foreach (var term in TermDeduplicator.Distinct(queryTerms))
        {
            int tf = index.TermFrequency(documentId, term);
            int cf = index.CorpusFrequency(term);

            // Terms unseen in the whole corpus carry no information.
            if (cf == 0)
                continue;

            if (tf > 0)
                sharesTerm = true;

            double documentProbability = (double)tf / documentLength;
            double collectionProbability = (double)cf / collectionTokens;

            double probability = (1.0 - _lambda) * documentProbability + _lambda * collectionProbability;
            score += Math.Log(probability);
        }

        // Honor the "score 0 means no match" convention used by the search engine.
        return sharesTerm ? score : 0;
    }
}