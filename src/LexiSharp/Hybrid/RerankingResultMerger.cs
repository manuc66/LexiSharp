using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Hybrid;

/// <summary>
/// Default merger: rebuilds a single temporary index from the <b>union</b> of candidate
/// documents returned by every engine, then re-scores every candidate with one shared
/// <see cref="ITextScorer"/> against that union. This yields one numerically comparable
/// ranking for all sources.
/// </summary>
/// <remarks>
/// Scoring happens against the union of candidates, so corpus statistics are local to the
/// retrieved set (approximate) rather than the full corpus. When exact global statistics
/// matter, prefer feeding the hybrid a single engine per corpus — the in-memory engine
/// derives its statistics from its own full index.
/// <para>
/// Tokens are normalized with <see cref="Tokenizer"/>/an injected <see cref="ITokenizer"/>;
/// pass the same tokenizer that built the source indexes so terms line up.
/// </para>
/// </remarks>
public sealed class RerankingResultMerger : IResultMerger
{
    private readonly ITextScorer _scorer;
    private readonly ITokenizer _tokenizer;

    /// <param name="scorer">The ranking strategy applied to the union (default: <see cref="Bm25Scorer"/>).</param>
    /// <param name="tokenizer">Tokenizer used for the query (default: <see cref="Tokenizer.Default"/>).</param>
    public RerankingResultMerger(ITextScorer? scorer = null, ITokenizer? tokenizer = null)
    {
        _scorer = scorer ?? new Bm25Scorer();
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public string Name => "Rerank";

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Merge(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults,
        string query)
    {
        ArgumentNullException.ThrowIfNull(perEngineResults);
        ArgumentNullException.ThrowIfNull(query);

        var documents = Union(perEngineResults);

        if (documents.Count == 0)
            return Array.Empty<SearchResult>();

        var queryTerms = _tokenizer.Tokenize(query);

        if (queryTerms.Count == 0)
            return Array.Empty<SearchResult>();

        var index = new InMemoryTextIndex(_tokenizer);
        index.Index(documents.Values);

        var merged = new List<SearchResult>(documents.Count);

        foreach (var document in documents.Values)
        {
            double score = _scorer.Score(document.Id, queryTerms, index);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                continue;

            merged.Add(new SearchResult(document.Id, score, document));
        }

        return merged
            .OrderByDescending(x => x.Score)
            .ToList();
    }

    private static Dictionary<string, SearchDocument> Union(
        IReadOnlyList<IReadOnlyList<SearchResult>> perEngineResults)
    {
        var documents = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);

        foreach (var results in perEngineResults)
        {
            foreach (var result in results)
            {
                documents.TryAdd(result.DocumentId, result.Document);
            }
        }

        return documents;
    }
}