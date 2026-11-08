using System.Buffers;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Expansion;

/// <summary>
/// A decorator engine that weights the terms of each query before ranking it, through an
/// <see cref="IQueryTermWeighter"/>, and then searches with a scorer rebuilt for that query.
/// </summary>
/// <remarks>
/// <para>
/// <b>One scorer per query, because a weight belongs to a query.</b> The prototype scorer is shared
/// by every search; the decorator asks the weighter for one weight per term, calls
/// <see cref="IWeightedQueryScorer.WithTermWeights"/> for a private copy and runs the search with
/// that copy. Nothing is stored on the prototype, so concurrent searches cannot observe each
/// other's weights — which is why this is a per-search engine rather than a mutable field on a
/// shared scorer.
/// </para>
/// <para>
/// <b>Syntax-bearing queries pass through untouched.</b> A quoted phrase, a prefix (<c>term*</c>) or
/// a fuzzy operator (<c>term~</c>) has a meaning the tokenizer does not preserve, and reweighting a
/// term inside it would silently change which documents match. Those queries are scored with the
/// prototype, exactly as <see cref="ExpandingTextSearchEngine"/> leaves them alone.
/// </para>
/// <para>
/// <b>The surface is the plain engine contract.</b> The inner engine is built per search, so the
/// decorator cannot truthfully forward <see cref="IFacetedSearchEngine"/>,
/// <see cref="IDetailedSearchEngine"/> or <see cref="IExplainableSearchEngine"/> — it does not know
/// the engine a caller means. An application that needs those capabilities keeps them on the
/// undecorated engine and reweights at the layer that owns the weights.
/// </para>
/// </remarks>
public sealed class ReweightingTextSearchEngine : ITextSearchEngine
{
    private static readonly SearchValues<char> SyntaxCharacters = SearchValues.Create("~*\"");

    private readonly ITextIndex _index;
    private readonly IWeightedQueryScorer _scorer;
    private readonly IQueryTermWeighter _weighter;
    private readonly ITokenizer _tokenizer;

    /// <param name="index">The corpus every search reads. Writes are forwarded to it.</param>
    /// <param name="scorer">
    /// The unweighted prototype. It is never modified; each search gets a copy carrying that
    /// query's weights.
    /// </param>
    /// <param name="weighter">Computes the weights from the tokenized query.</param>
    /// <param name="tokenizer">
    /// Tokenizer used to split the raw query before weighting; should be the one the index was
    /// built with, otherwise the weights name terms the scorer will not see.
    /// </param>
    public ReweightingTextSearchEngine(
        ITextIndex index,
        IWeightedQueryScorer scorer,
        IQueryTermWeighter weighter,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(scorer);
        ArgumentNullException.ThrowIfNull(weighter);

        _index = index;
        _scorer = scorer;
        _weighter = weighter;
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
    public bool Remove(string documentId) => _index.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _index.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(
        string query,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        // The raw text reaches the inner engine: weighting changed how a term is scored, not which
        // terms the query has, so a second tokenization by that engine costs nothing and keeps one
        // parser in the pipeline.
        ITextSearchEngine engine = Engine(ScorerFor(query));
        return engine.Search(query, options, cancellationToken);
    }

    /// <summary>
    /// The prototype, or a weighted copy when the query can be tokenized and weighted. A query
    /// carrying search syntax keeps the prototype: its terms are not the tokenizer's terms.
    /// </summary>
    private ITextScorer ScorerFor(string query)
    {
        if (query.AsSpan().ContainsAny(SyntaxCharacters))
            return _scorer;

        IReadOnlyList<string> terms = _tokenizer.Tokenize(query);

        if (terms.Count == 0)
            return _scorer;

        IReadOnlyDictionary<string, double> weights = _weighter.Weigh(terms);

        return weights.Count == 0 ? _scorer : _scorer.WithTermWeights(weights);
    }

    private RankedTextSearchEngine Engine(ITextScorer scorer) => new(_index, scorer, _tokenizer);
}
