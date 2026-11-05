using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using System.Text;

namespace LexiSharp.Expansion;

/// <summary>
/// A decorator engine that generates query variants from a term corrector's uncertainty and
/// fuses their results, instead of committing to a single correction or widening the query.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="CorrectingTextSearchEngine"/>, which joins the original and corrected terms
/// into one bag-of-words query, this decorator runs each variant as a separate search and fuses
/// the result lists. A query such as <c>helo wrold</c> becomes two (or more) complete queries —
/// <c>helo world</c>, <c>hello wrold</c>, <c>hello world</c> — each scored independently, then
/// merged by reciprocal rank fusion. The variant that matches the corpus better surfaces its
/// documents without the noise of a widened single query.
/// </para>
/// <para>
/// Variants are generated per-term: for each query term the corrector returns, the top correction
/// (if different from the original) produces one variant where that term is replaced. The original
/// query is always included. The number of variants is bounded by the number of query terms, not
/// by the Cartesian product, so cost stays linear in query length.
/// </para>
/// <para>
/// Writes are forwarded to the inner engine unchanged.
/// </para>
/// </remarks>
public sealed class ReformulatingTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    private readonly ITextSearchEngine _inner;
    private readonly ITermCorrector _corrector;
    private readonly ITokenizer _tokenizer;
    private readonly CalibratedScoreConfidence? _calibrator;

    /// <param name="inner">The engine that ultimately scores each variant.</param>
    /// <param name="corrector">Produces candidate replacements per term.</param>
    /// <param name="tokenizer">Tokenizer used to split the raw query; defaults to <see cref="Tokenizer.Default"/>.</param>
    public ReformulatingTextSearchEngine(
        ITextSearchEngine inner,
        ITermCorrector corrector,
        ITokenizer? tokenizer = null)
        : this(inner, corrector, tokenizer, null)
    {
    }

    /// <param name="inner">The engine that ultimately scores each variant.</param>
    /// <param name="corrector">Produces candidate replacements per term.</param>
    /// <param name="tokenizer">Tokenizer used to split the raw query; defaults to <see cref="Tokenizer.Default"/>.</param>
    /// <param name="calibrator">
    /// Optional calibrator for the inner engine's scores. When provided, variants are fused
    /// by <see cref="Hybrid.BayesianFusionMerger"/> instead of reciprocal rank fusion,
    /// preserving score magnitude across variants. Must be trained.
    /// </param>
    public ReformulatingTextSearchEngine(
        ITextSearchEngine inner,
        ITermCorrector corrector,
        ITokenizer? tokenizer,
        CalibratedScoreConfidence? calibrator)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(corrector);

        if (calibrator is not null && !calibrator.IsTrained)
            throw new ArgumentException("The calibrator must be trained.", nameof(calibrator));

        _inner = inner;
        _corrector = corrector;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _calibrator = calibrator;
    }

    /// <summary>The corrector applied to every query.</summary>
    public ITermCorrector Corrector => _corrector;

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
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var variants = BuildVariants(query);

        if (variants.Count <= 1)
            return _inner.Search(query, options, cancellationToken);

        var perVariant = new List<IReadOnlyList<SearchResult>>(variants.Count);

        foreach (var variant in variants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            perVariant.Add(_inner.Search(variant, options, cancellationToken));
        }

        if (_calibrator is not null)
        {
            var calibrators = Enumerable.Repeat(_calibrator, perVariant.Count).ToArray();
            return new Hybrid.BayesianFusionMerger(calibrators).Merge(perVariant, query);
        }

        return FuseRrf(perVariant, options);
    }

    /// <inheritdoc />
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _inner is IQueryCostProbe probe
            ? probe.EstimateCandidateCount(query, options)
            : long.MaxValue;
    }

    /// <summary>
    /// Builds the list of query variants: the original plus one per term that has a correction
    /// distinct from the original (top correction only).
    /// </summary>
    internal IReadOnlyList<string> BuildVariants(string query)
    {
        var terms = _tokenizer.Tokenize(query);

        if (terms.Count == 0)
            return new[] { query };

        var variants = new List<string> { query };

        for (int t = 0; t < terms.Count; t++)
        {
            var corrections = _corrector.Correct(terms[t]);

            // Find the best correction that differs from the original term.
            string? bestCorrection = null;
            foreach (var (candidate, _) in corrections)
            {
                if (!string.Equals(candidate, terms[t], StringComparison.Ordinal))
                {
                    bestCorrection = candidate;
                    break;
                }
            }

            if (bestCorrection is null)
                continue;

            // Build a variant with this term replaced.
            var variantTerms = terms.ToList();
            variantTerms[t] = bestCorrection;
            variants.Add(string.Join(' ', variantTerms));
        }

        // Deduplicate.
        return variants.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Fuses multiple result lists by reciprocal rank fusion (k = 60).
    /// </summary>
    private static IReadOnlyList<SearchResult> FuseRrf(
        IReadOnlyList<IReadOnlyList<SearchResult>> perVariant,
        SearchOptions? options)
    {
        const int k = 60;

        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        var docs = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);

        foreach (var results in perVariant)
        {
            for (int i = 0; i < results.Count; i++)
            {
                string id = results[i].DocumentId;
                scores[id] = scores.GetValueOrDefault(id) + 1.0 / (k + i + 1);
                if (!docs.ContainsKey(id))
                    docs[id] = results[i].Document;
            }
        }

        int limit = options?.Limit > 0 ? options.Limit : int.MaxValue;

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => new SearchResult(kv.Key, kv.Value, docs[kv.Key]))
            .ToList();
    }
}