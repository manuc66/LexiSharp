using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Late-interaction reranker (ColBERT-style): scores each candidate with <b>MaxSim</b> between
/// the token-level embeddings of the query and of the document, then reorders by that score.
/// </summary>
/// <remarks>
/// <para>
/// Instead of collapsing a text into a single vector, ColBERT keeps one embedding per token and
/// defines the similarity of a query to a document as
/// <c>Σ_{q ∈ query tokens} max_{t ∈ document tokens} cos(q, t)</c> — each query token is matched
/// against its best document token, which is what makes it strong on multi-word and re-ranking
/// tasks. The cost is a matrix per document (token × dimension), which is why late interaction is
/// only used as a <b>second stage</b> over a shortlist <see cref="IReranker"/> already selected —
/// usually via a <see cref="CascadeRerankPipeline"/> cut down to the smallest final stage.
/// </para>
/// <para>
/// Document token embeddings are expected to have been computed once at index time and handed to
/// the reranker as a read-only dictionary (the same shape <see cref="MaximalMarginalRelevanceReranker"/>
/// uses for its vectors); queries are embedded at rerank time through
/// <see cref="Core.ITokenEmbeddingProvider"/>. Candidates without document vectors cannot be
/// scored and are dropped, as are candidates whose token embeddings were produced at a different
/// dimensionality than the query tokens (an inconsistent model, not a score). Scores that are
/// <c>0</c>, NaN or infinite are dropped as well. The input list is never mutated; when the
/// query yields no tokens, the incoming order is kept untouched.
/// </para>
/// </remarks>
public sealed class MaxSimReranker : IReranker
{
    private readonly ITokenEmbeddingProvider _model;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<ReadOnlyMemory<float>>> _documentVectors;
    private readonly int? _limit;
    private readonly double _minimumScore;

    /// <param name="model">Token-level encoder; embeds the query at rerank time.</param>
    /// <param name="documentVectors">
    /// Pre-computed token embeddings per document id (see remarks); entries may be missing.
    /// </param>
    /// <param name="limit">
    /// Optional final cut: only the first <paramref name="limit"/> re-ranked results are returned.
    /// Defaults to keeping every candidate.
    /// </param>
    /// <param name="minimumScore">Results scoring below this after the rerank are dropped.</param>
    public MaxSimReranker(
        ITokenEmbeddingProvider model,
        IReadOnlyDictionary<string, IReadOnlyList<ReadOnlyMemory<float>>> documentVectors,
        int? limit = null,
        double minimumScore = double.NegativeInfinity)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(documentVectors);

        if (limit is < 1)
            throw new ArgumentOutOfRangeException(nameof(limit), limit, "limit must be at least 1.");

        if (double.IsNaN(minimumScore))
            throw new ArgumentOutOfRangeException(nameof(minimumScore), minimumScore, "minimumScore must not be NaN.");

        _model = model;
        _documentVectors = documentVectors;
        _limit = limit;
        _minimumScore = minimumScore;
    }

    /// <inheritdoc />
    public string Name => $"MaxSim({_model.Name})";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        var queryTokens = _model.GetTokenEmbeddings(query);

        if (queryTokens.Count == 0)
            return candidates;

        var reranked = new List<SearchResult>(candidates.Count);

        foreach (var candidate in candidates)
        {
            if (!_documentVectors.TryGetValue(candidate.DocumentId, out var documentTokens)
                || documentTokens.Count == 0)
            {
                continue;
            }

            // A model trained on another embedding dimensionality is a data error, not a score:
            // drop the candidate rather than throwing mid-rerank. Query tokens are assumed to be
            // uniform (the model contract); each document token is checked instead.
            if (!AllSameDimension(queryTokens, documentTokens))
                continue;

            double score = MaxSim(queryTokens, documentTokens);

            if (double.IsNaN(score) || double.IsInfinity(score) || score <= 0)
                continue;

            if (score < _minimumScore)
                continue;

            reranked.Add(candidate with { Score = score });
        }

        return reranked
            .OrderByDescending(x => x.Score)
            .Take(_limit ?? reranked.Count)
            .ToList();
    }

    private static bool AllSameDimension(
        IReadOnlyList<ReadOnlyMemory<float>> queryTokens,
        IReadOnlyList<ReadOnlyMemory<float>> documentTokens)
    {
        int dimension = queryTokens[0].Span.Length;

        foreach (var token in documentTokens)
        {
            if (token.Span.Length != dimension)
                return false;
        }

        return true;
    }

    private static double MaxSim(
        IReadOnlyList<ReadOnlyMemory<float>> queryTokens,
        IReadOnlyList<ReadOnlyMemory<float>> documentTokens)
    {
        double total = 0;

        foreach (var queryToken in queryTokens)
        {
            float best = float.MinValue;

            foreach (var documentToken in documentTokens)
            {
                float similarity = VectorSimilarity.CosineSimilarity(queryToken.Span, documentToken.Span);

                if (similarity > best)
                    best = similarity;
            }

            total += best;
        }

        return total;
    }
}