using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>
/// The stock search engine: delegates the corpus storage to an <see cref="ITextIndex"/>,
/// delegates the relevance math to an <see cref="ITextScorer"/>, and takes care of
/// query tokenization, filtering, ranking and limiting.
/// </summary>
/// <remarks>
/// Scorers are interchangeable, so one engine instance can host several ranking
/// strategies by simply swapping the scorer.
/// <para>
/// Convention: a document is considered "not a match" when its score is exactly
/// <c>0</c>; such documents are excluded from results unless <see cref="SearchOptions.MinimumScore"/>
/// is lowered. All built-in scorers (TF-IDF, BM25, boolean, query likelihood) honor this.
/// </para>
/// </remarks>
public sealed class RankedTextSearchEngine : ITextSearchEngine
{
    private readonly ITextIndex _index;
    private readonly ITextScorer _scorer;
    private readonly ITokenizer _tokenizer;

    /// <param name="index">The corpus index backing the engine.</param>
    /// <param name="scorer">The ranking strategy (TF-IDF, BM25, ...).</param>
    /// <param name="tokenizer">
    /// Tokenizer used for queries. Should be consistent with the one the index was
    /// built with, otherwise query terms will not match indexed terms.
    /// </param>
    public RankedTextSearchEngine(
        ITextIndex index,
        ITextScorer scorer,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(scorer);

        _index = index;
        _scorer = scorer;
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _index.Index(documents);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _index.Add(document);
    }

    /// <inheritdoc />
    public void Remove(string documentId) => _index.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _index.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.Limit <= 0)
            return Array.Empty<SearchResult>();

        var queryTerms = _tokenizer.Tokenize(query);

        if (queryTerms.Count == 0 || _index.Count == 0)
            return Array.Empty<SearchResult>();

        var results = new List<SearchResult>(Math.Min(_index.Count, options.Limit * 4));

        // Convention: a score of exactly 0 means "not a match".
        foreach (var document in _index.Documents)
        {
            double score = _scorer.Score(document.Id, queryTerms, _index);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                continue;

            if (score >= options.MinimumScore)
            {
                results.Add(new SearchResult(document.Id, score, document));
            }
        }

        return results
            .OrderByDescending(x => x.Score)
            .Take(options.Limit)
            .ToList();
    }

    /// <summary>
    /// Explains why a document received the score it did for a query, by delegating to the
    /// scorer's <see cref="IScoreExplainer"/> capability when it has one.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">The raw query; tokenized with the engine's tokenizer.</param>
    /// <returns>
    /// A <see cref="ScoreExplanation"/>, or <c>null</c> when the active scorer cannot explain
    /// itself (it does not implement <see cref="IScoreExplainer"/>) or the document is unknown.
    /// </returns>
    public ScoreExplanation? Explain(string documentId, string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (_scorer is not IScoreExplainer explainer || !_index.Contains(documentId))
            return null;

        return explainer.Explain(documentId, _tokenizer.Tokenize(query), _index);
    }
}