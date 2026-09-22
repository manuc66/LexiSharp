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

        var distinctQueryTerms = DistinctTermList.Wrap(TermDeduplicator.Distinct(queryTerms));

        // Precompute the query-level corpus constants (idf, collection probabilities, ...) once
        // per search instead of per candidate document when the scorer supports it.
        var plan = _scorer is IQueryPlannableScorer plannable
            ? plannable.CreatePlan(distinctQueryTerms, _index)
            : null;

        // Convention: a score of exactly 0 means "not a match".
        // When the index can enumerate the documents sharing at least one query term
        // (ICandidateIndex) and the scorer provably returns 0 for every document that
        // shares none (ITermOverlapScorer), score only those candidates: same results,
        // same order, far fewer distance computations. Otherwise fall back to the full scan.
        var candidateDocuments =
            _index is ICandidateIndex candidateIndex &&
            _scorer is ITermOverlapScorer &&
            CandidatesCoverFractionOfCorpus(_index, distinctQueryTerms) < 0.5
                ? candidateIndex.GetCandidateDocuments(distinctQueryTerms)
                : _index.Documents;

        // Bounded top-L accumulation, worst-first, reproducing the exact semantics of
        // OrderByDescending(Score).Take(limit): ties keep their enumeration order. SearchResult
        // objects are materialized only for the kept entries.
        var top = new List<(double Score, SearchDocument Document, long Ordinal)>(options.Limit);
        long ordinal = 0;

        foreach (var document in candidateDocuments)
        {
            // Structured filters gate the corpus before any relevance math is paid for.
            if (!options.PassesFilters(document))
                continue;

            double score = plan is null
                ? _scorer.Score(document.Id, distinctQueryTerms, _index)
                : plan.Score(document.Id);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0 || score < options.MinimumScore)
                continue;

            InsertRanked(top, options.Limit, (score, document, ordinal++));
        }

        var results = new SearchResult[top.Count];

        for (int i = 0; i < results.Length; i++)
        {
            var entry = top[top.Count - 1 - i];
            results[i] = new SearchResult(entry.Document.Id, entry.Score, entry.Document);
        }

        return results;
    }

    /// <summary>
    /// Inserts an entry into the worst-first top-L list, dropping the current worst when full.
    /// An entry ranks above another when its score is higher, or its score is equal and it was
    /// enumerated earlier — byte-for-byte the behavior of the stable descending sort.
    /// </summary>
    private static void InsertRanked(
        List<(double Score, SearchDocument Document, long Ordinal)> top,
        int limit,
        (double Score, SearchDocument Document, long Ordinal) entry)
    {
        if (top.Count < limit)
        {
            top.Add(entry);

            for (int j = top.Count - 1; j > 0; j--)
            {
                if (IsRankedAscending(top[j - 1], top[j]))
                    break;

                (top[j - 1], top[j]) = (top[j], top[j - 1]);
            }

            return;
        }

        if (IsRankedAscending(entry, top[0]))
            return;

        top[0] = entry;

        for (int j = 0; j < top.Count - 1; j++)
        {
            if (IsRankedAscending(top[j], top[j + 1]))
                break;

            (top[j], top[j + 1]) = (top[j + 1], top[j]);
        }
    }

    /// <summary>Whether <paramref name="lower"/> may sit before <paramref name="higher"/> in the worst-first list.</summary>
    private static bool IsRankedAscending(
        (double Score, SearchDocument Document, long Ordinal) lower,
        (double Score, SearchDocument Document, long Ordinal) higher)
        => lower.Score < higher.Score || (lower.Score == higher.Score && lower.Ordinal > higher.Ordinal);

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

    /// <summary>
    /// Upper-bound estimate of the fraction of the corpus covered by the document union of
    /// the query terms (the sum of per-term document frequencies), used to decide whether
    /// scoring candidates is cheaper than scoring the whole corpus.
    /// </summary>
    private static double CandidatesCoverFractionOfCorpus(ITextIndex index, IReadOnlyList<string> queryTerms)
    {
        if (index.Count == 0)
            return 1;

        long documentUnionUpperBound = 0;

        foreach (var term in queryTerms)
            documentUnionUpperBound += index.DocumentFrequency(term);

        return (double)documentUnionUpperBound / index.Count;
    }
}