using System.Buffers;
using System.Text;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Expansion;

/// <summary>
/// A decorator engine that widens the <b>query</b> with related terms before delegating to the
/// inner engine — the query-side counterpart of <see cref="Indexing.ExpansionTextIndex"/>, which
/// widens <i>documents</i> at index time.
/// </summary>
/// <remarks>
/// <para>
/// The raw query is tokenized, handed to the <see cref="ITermExpander"/>, and the original terms
/// plus the expansion terms are joined back into one bag-of-words query for the inner engine.
/// A query such as <c>refresh</c> becomes <c>refresh access token oauth session</c>, so documents
/// that never mention the literal term but carry its corpus-derived associates are reached — the
/// same semantic reach as the index-time expansion, applied at search time, with no reindexing.
/// </para>
/// <para>
/// Two honest limits, inherited from the seam it reuses: expansion terms are <b>not weighted</b>
/// (<see cref="ExpandedTerm.Weight"/> is ignored, the inner scorer sees plain terms), and queries
/// carrying explicit syntax (<c>"</c>, <c>*</c>, <c>~</c>) are passed through untouched so
/// phrases, prefixes and fuzzy operators keep their exact meaning.
/// </para>
/// <para>
/// Writes are forwarded to the inner engine unchanged. Combine a literal engine and an expanding
/// engine in a <see cref="Hybrid.HybridTextSearchEngine"/> to keep both the precise and the
/// expanded ranking, merged by their <c>ReciprocalRankFusionMerger</c>.
/// </para>
/// <para>
/// The capability surface is forwarded too, and that forwarding is <i>truthful</i> in a way it
/// is not for a score-rewriting decorator: this engine returns the inner engine's results 1:1
/// against the <i>expanded</i> query, so <see cref="IFacetedSearchEngine"/>,
/// <see cref="IDetailedSearchEngine"/> and <see cref="IExplainableSearchEngine"/> describe the
/// same result set the expanded search returns — facets count the expanded matches, and an
/// explanation breaks down the score the expanded search would have produced. The wrapped engine
/// is the one that actually answers, so a wrapper whose inner lacks a capability throws
/// <see cref="NotSupportedException"/> naming the wrapped type rather than silently returning
/// something else.
/// </para>
/// </remarks>
public sealed class ExpandingTextSearchEngine : ITextSearchEngine, IQueryCostProbe, IFacetedSearchEngine, IDetailedSearchEngine, IExplainableSearchEngine
{
    private static readonly SearchValues<char> SyntaxCharacters = SearchValues.Create("~*\"");
    private const char Separator = ' ';

    private readonly ITextSearchEngine _inner;
    private readonly ITermExpander _expander;
    private readonly ITokenizer _tokenizer;

    /// <param name="inner">The engine that ultimately scores the search.</param>
    /// <param name="expander">Produces the related terms added to each query.</param>
    /// <param name="tokenizer">
    /// Tokenizer used to split the raw query before expansion; should be the one the inner
    /// engine uses, otherwise the expanded terms will not line up with the indexed terms.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="inner"/> or <paramref name="expander"/> is null.</exception>
    public ExpandingTextSearchEngine(ITextSearchEngine inner, ITermExpander expander, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(expander);

        _inner = inner;
        _expander = expander;
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _inner.Index(documents);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _inner.Add(document);
    }

    /// <inheritdoc />
    public bool Remove(string documentId) => _inner.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _inner.Search(ExpandQuery(query), options);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IFacetedSearchEngine"/>.</exception>
    public FacetedSearchResult SearchWithFacets(
        string query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Faceted().SearchWithFacets(ExpandQuery(query), options, facetFields);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IDetailedSearchEngine"/>.</exception>
    public IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Detailed().SearchWithDetails(ExpandQuery(query), options);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped engine does not implement <see cref="IExplainableSearchEngine"/>.</exception>
    public ScoreExplanation? Explain(string documentId, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Explainable().Explain(documentId, ExpandQuery(query));
    }

    private IFacetedSearchEngine Faceted() =>
        _inner as IFacetedSearchEngine
        ?? throw new NotSupportedException($"{_inner.GetType().Name} does not implement {nameof(IFacetedSearchEngine)}.");

    private IDetailedSearchEngine Detailed() =>
        _inner as IDetailedSearchEngine
        ?? throw new NotSupportedException($"{_inner.GetType().Name} does not implement {nameof(IDetailedSearchEngine)}.");

    private IExplainableSearchEngine Explainable() =>
        _inner as IExplainableSearchEngine
        ?? throw new NotSupportedException($"{_inner.GetType().Name} does not implement {nameof(IExplainableSearchEngine)}.");

    /// <summary>
    /// Estimates the inner engine's candidate count for the <i>expanded</i> query, so the
    /// routing cost reflects the extra terms. Falls back to <see cref="long.MaxValue"/> when the
    /// inner engine cannot probe.
    /// </summary>
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _inner is IQueryCostProbe probe
            ? probe.EstimateCandidateCount(ExpandQuery(query.ToString()), options)
            : long.MaxValue;
    }

    /// <summary>
    /// Rewrites the raw query into its expanded form: original terms first, then the distinct
    /// expansion terms. Syntax-bearing, empty and unexpanded queries are returned unchanged.
    /// </summary>
    private string ExpandQuery(string query)
    {
        if (query.AsSpan().ContainsAny(SyntaxCharacters))
            return query;

        var terms = _tokenizer.Tokenize(query);

        if (terms.Count == 0)
            return query;

        var expansion = _expander.Expand(terms);

        if (expansion.Count == 0)
            return query;

        var seen = new HashSet<string>(terms, StringComparer.Ordinal);
        var builder = new StringBuilder(query.Length + (expansion.Count * 8));

        for (int i = 0; i < terms.Count; i++)
        {
            if (i > 0)
                builder.Append(Separator);

            builder.Append(terms[i]);
        }

        foreach (var term in expansion)
        {
            if (string.IsNullOrEmpty(term.Term) || !seen.Add(term.Term))
                continue;

            builder.Append(Separator);
            builder.Append(term.Term);
        }

        return builder.ToString();
    }
}
