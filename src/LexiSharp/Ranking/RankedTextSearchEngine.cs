using System.Buffers;
using System.Runtime.InteropServices;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using LexiSharp.Similarity;

namespace LexiSharp.Ranking;

/// <summary>
/// The stock search engine: delegates the corpus storage to an <see cref="IReadOnlyTextIndex"/>,
/// delegates the relevance math to an <see cref="ITextScorer"/>, and takes care of
/// query tokenization, filtering, ranking and limiting.
/// </summary>
/// <remarks>
/// Scorers are interchangeable, so one engine instance can host several ranking
/// strategies by simply swapping the scorer.
/// <para>
/// The index is held as its read view, which is all a search needs — an index that cannot be written
/// to is searched as it is. The four mutating methods ask for the write capability when they are
/// called and refuse with <see cref="NotSupportedException"/> when the index does not have it, rather
/// than the constructor demanding it and the caller discovering the truth at the first write.
/// </para>
/// <para>
/// Convention: a document is considered "not a match" when its score is exactly
/// <c>0</c>; such documents are excluded from results unless <see cref="SearchOptions.MinimumScore"/>
/// is lowered. All built-in scorers (TF-IDF, BM25, boolean, query likelihood) honor this.
/// </para>
/// <para>
/// Double-quoted segments are parsed as <b>phrase queries</b> through
/// <see cref="QueryParser"/> (before tokenization — the tokenizer itself treats <c>"</c> as
/// an ordinary separator): their terms must appear at consecutive document positions, with
/// several phrases AND-ed together, while the free text around them keeps scoring as usual —
/// free terms never hard-filter a mixed query. Phrase checks assume a plain token stream:
/// n-gram tokenizers emit overlapping tokens and break the consecutive-position guarantee.
/// </para>
/// <para>
/// Free-text atoms may also carry <b>expansion operators</b>: <c>term*</c> matches every
/// vocabulary term starting with <c>term</c> (ordinal prefix), <c>term~</c>/<c>term~N</c>
/// fuzzy-matches within N edits (default 1, clamped to 0–2). Expansion runs at search time
/// against an <see cref="IVocabularyIndex"/>, keeps at most
/// <see cref="MaxExpansionsPerAtom"/> terms per atom — highest document frequency first,
/// then ordinal order — and never applies inside quotes. An index without vocabulary support
/// falls back to the atom's literal base term. With
/// <see cref="SearchOptions.FuzzyOnlyOutOfVocabulary"/>, a fuzzy atom whose base form is
/// already in the vocabulary resolves to that exact term and skips the near-variant
/// expansion: correctly spelled words are never degraded, only unknown ones are corrected.
/// </para>
/// <para>
/// A leading <c>+</c> or <c>-</c> on a whitespace-delimited token is a <b>required</b> or
/// <b>prohibited</b> operator: <c>+term</c> gates on the term's presence and scores it like the
/// same term written without the operator, while <c>-term</c> excludes every document containing
/// it and never scores it. Both gate before any relevance math is paid, and both apply to quoted
/// phrases (<c>+"a phrase"</c>, <c>-"a phrase"</c>) and to prefix/fuzzy atoms (<c>+term*</c>,
/// <c>-term~2</c>) as well as to literal terms. A <c>-</c> inside a token (<c>e-mail</c>) stays
/// literal. A query of only prohibited atoms is the complement of its gate, scored at <c>1</c>
/// for every surviving document; a query of only required atoms scores on exactly those terms.
/// </para>
/// <para>
/// A <b>field scope</b> restricts an atom to one field: <c>title:term</c>, <c>title:"a phrase"</c>,
/// <c>+title:term</c>, <c>-title:term</c>, and the expansion forms. It is recognized only when the
/// name before the colon is a field of this engine's index (or one the caller declared through
/// <see cref="SearchOptions.KnownFields"/>), so <c>http://x</c>, <c>12:30</c> and <c>key:value</c>
/// remain ordinary text. A scoped term gates the same way an unscoped one does (hard only with
/// <c>+</c>) and scores from its field's statistics; a scoped phrase must appear at consecutive
/// positions <i>inside</i> that field, never across a boundary. The feature is declared only when
/// the scorer implements <see cref="IFieldScopedScorer"/> (the built-in <c>Bm25FScorer</c> does)
/// and the index implements <see cref="IFieldStatisticsIndex"/>; otherwise the query is refused.
/// </para>
/// <para>
/// An optional <see cref="SynonymMap"/> additionally rewrites <b>free</b> query terms to
/// their direct synonyms — one-way edges via <c>Add</c>, bidirectional groups via
/// <c>AddEquivalent</c> — at one level only (never synonyms of synonyms) and never inside
/// quoted phrases. Required and prohibited terms are taken literally, so an operator does not
/// pull in synonyms. Entries are tokenized with the engine's tokenizer at construction; each
/// must reduce to exactly one term or the constructor throws.
/// </para>
/// </remarks>
public sealed class RankedTextSearchEngine : IFacetedSearchEngine, IQueryCostProbe, IExplainableSearchEngine, IQuerySyntaxSupport
{
    /// <summary>
    /// Upper bound on how many vocabulary terms a single prefix/fuzzy atom may contribute to
    /// the query, after the document-frequency/ordinal ranking.
    /// </summary>
    public const int MaxExpansionsPerAtom = 64;

    /// <summary>
    /// Upper bound on how many <i>distinct</i> prefix/fuzzy atoms one query may carry. A query
    /// asking for more is refused with <see cref="ArgumentException"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Each distinct atom costs one walk of the index vocabulary — a fuzzy atom compares the
    /// base term against every term the index knows — so without a bound the query string is a
    /// multiplier on that walk. Measured here on a 200 000-term vocabulary: one fuzzy atom 71 ms,
    /// fifty 1.1 s, two hundred (2 199 characters, inside a URL) 4.3 s, all of them matching nothing.
    /// </para>
    /// <para>
    /// Repeats are free: atoms are resolved through <see cref="QueryExpansionResolver"/>, which walks
    /// the vocabulary once per distinct atom and hands the same terms to every occurrence, so only
    /// distinct atoms count toward this budget. Thirty-two is well past what a query a person writes
    /// needs, and it bounds a single request to thirty-two walks instead of an arbitrary number.
    /// </para>
    /// <para>
    /// The bound is on the multiplier, not on the walk: one atom is still a full vocabulary scan, and
    /// an index of a million terms pays it once per atom. Making that scan sub-linear is a term index
    /// ordered for edit-distance search, not a smaller number here.
    /// </para>
    /// </remarks>
    public const int MaxExpansionAtoms = 32;

    /// <summary>
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks, so a
    /// dashboard can attribute a measurement to a specific engine.
    /// </summary>
    public const string EngineName = "RankedTextSearchEngine";

    /// <inheritdoc />
    public QueryFeatures SupportedQueryFeatures =>
        QueryFeatures.Phrases | QueryFeatures.Expansions | QueryFeatures.RequiredTerms | QueryFeatures.ProhibitedTerms
        | (SupportsFieldScopes ? QueryFeatures.FieldScopes : QueryFeatures.None);

    /// <summary>
    /// Whether this engine can honour a field scope: the scorer has to know how to score one
    /// field's statistics and the index has to record them.
    /// </summary>
    private bool SupportsFieldScopes => _scopedScorer is not null && _fieldStatistics is not null;

    private readonly IReadOnlyTextIndex _index;
    private readonly ITextScorer _scorer;
    private readonly IFieldScopedScorer? _scopedScorer;
    private readonly IFieldStatisticsIndex? _fieldStatistics;
    private readonly ITokenizer _tokenizer;
    private readonly Dictionary<string, string[]>? _synonyms;
    private readonly RetrievalTelemetry _telemetry;

    /// <summary>
    /// The scoring pass a query takes, for a caller that reports it — the cursor lane prints it and
    /// asserts it against allocation — and for the parity tests, which force one side.
    /// </summary>
    internal enum ScoringPass
    {
        /// <summary>No accumulating pass: each candidate is scored by document id.</summary>
        PerDocument,

        /// <summary>The term-at-a-time accumulator.</summary>
        Accumulator,

        /// <summary>The document-at-a-time cursor traversal.</summary>
        Cursor,
    }

    /// <summary>
    /// Which pass a query of this reach takes over this index, without running it. This is the same
    /// decision <see cref="TryRunSpanQuery"/> and <see cref="TryRunAccumulatingQuery"/> make, in one
    /// place, so a caller that reports the pass cannot tell a different story from the engine.
    /// </summary>
    /// <remarks>
    /// A query below the accumulation floor takes the per-document loop whichever side of the density
    /// threshold it is on, and an index that cannot lend its posting arrays takes the accumulator even
    /// when the density would prefer the cursor. A scorer that is not an
    /// <see cref="IAccumulatingQueryPlan"/> also takes the per-document loop, but that is a property of
    /// the scorer rather than of the reach, so it is not visible here; the lane measures the
    /// accumulating scorers.
    /// </remarks>
    internal static ScoringPass PassFor(IAccumulatingIndex index, int reachableDocuments)
    {
        if (reachableDocuments < AccumulationThreshold(index.OrdinalSpace))
            return ScoringPass.PerDocument;

        if (index is not IPostingArrayIndex arrays || arrays.LengthsByOrdinal is null)
            return ScoringPass.Accumulator;

        return reachableDocuments <= index.OrdinalSpace / CursorDensityDivisor
            ? ScoringPass.Cursor
            : ScoringPass.Accumulator;
    }

    /// <summary>
    /// Forces a scoring pass when the engine would take the accumulating path at all, for the parity
    /// tests and the lane's cross-check against allocation. <c>null</c> — the default — takes the
    /// measured decision.
    /// </summary>
    internal ScoringPass? ForcedPass { get; init; }

    /// <summary>
    /// Documents by ordinal, for the page window's ties and page cut. One delegate per engine, not
    /// per query: the index never changes, so nothing about the resolver does either.
    /// </summary>
    private readonly Func<int, SearchDocument?> _resolveOrdinal;

    /// <param name="index">
    /// The corpus index backing the engine. Only the read view is asked for, because that is all a
    /// search needs — and all an index that cannot be written to, such as one read from a segment,
    /// can offer. The four mutating methods ask for the write capability when they are called and
    /// refuse rather than pretending they worked.
    /// </param>
    /// <param name="scorer">The ranking strategy (TF-IDF, BM25, ...).</param>
    /// <param name="tokenizer">
    /// Tokenizer used for queries. Should be consistent with the one the index was
    /// built with, otherwise query terms will not match indexed terms.
    /// </param>
    /// <param name="synonyms">
    /// Optional synonym edges applied to free query terms. Tokenized with
    /// <paramref name="tokenizer"/> at construction; every entry must yield exactly one term.
    /// </param>
    /// <param name="telemetry">
    /// Optional observability sink reporting search latency, candidate counts and index size.
    /// Defaults to <see cref="RetrievalTelemetry.None"/>, which records nothing and costs nothing.
    /// </param>
    /// <exception cref="ArgumentException">
    /// A <paramref name="synonyms"/> entry tokenizes to zero or more than one term.
    /// </exception>
    public RankedTextSearchEngine(
        IReadOnlyTextIndex index,
        ITextScorer scorer,
        ITokenizer? tokenizer = null,
        SynonymMap? synonyms = null,
        RetrievalTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(scorer);

        _index = index;
        _scorer = scorer;
        _scopedScorer = scorer as IFieldScopedScorer;
        _fieldStatistics = index as IFieldStatisticsIndex;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _synonyms = ResolveSynonyms(synonyms);
        _telemetry = telemetry ?? RetrievalTelemetry.None;
        _resolveOrdinal = ResolveOrdinal;
        _resolveId = index is IOrdinalIdIndex ids ? ids.IdAt : null;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The index this engine holds is read-only.</exception>
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        Writable().Index(documents);
        _telemetry.IndexChanged(EngineName, _index);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The index this engine holds is read-only.</exception>
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Writable().Add(document);
        _telemetry.IndexChanged(EngineName, _index);
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The index this engine holds is read-only.</exception>
    public bool Remove(string documentId)
    {
        bool removed = Writable().Remove(documentId);
        _telemetry.IndexChanged(EngineName, _index);
        return removed;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The index this engine holds is read-only.</exception>
    public void Clear()
    {
        Writable().Clear();
        _telemetry.IndexChanged(EngineName, _index);
    }

    /// <summary>
    /// The index as something that accepts writes, or a refusal that says why.
    /// </summary>
    /// <remarks>
    /// A search reads through <see cref="IReadOnlyTextIndex"/>, which is all a corpus that cannot be
    /// written to — one read from a segment, for instance — is able to offer. These four methods are
    /// the only place that needs more, so they ask for it here rather than the constructor assuming it
    /// and the caller finding out at the first write.
    /// </remarks>
    private ITextIndex Writable() =>
        _index as ITextIndex
        ?? throw new NotSupportedException(
            $"{EngineName} holds a read-only index ({_index.GetType().Name}): Index, Add, Remove and Clear need an index that accepts writes (ITextIndex).");

    /// <summary>The document for an ordinal, for the window's ties and page cut.</summary>
    private SearchDocument? ResolveOrdinal(int ordinal) =>
        _index is IAccumulatingIndex acc ? acc.DocumentAt(ordinal) : null;

    /// <summary>
    /// The id for an ordinal without decoding the document, when the index can answer it that way.
    /// </summary>
    /// <remarks>
    /// The window's tie-break compares ids and only the page cut needs the document, so an index that
    /// answers the id alone keeps a segment from decoding a stored document per comparison. Measured on
    /// a corpus whose documents tie, that was the whole of a store's per-query allocation.
    /// </remarks>
    private readonly Func<int, string?>? _resolveId;

    /// <summary>The ordinal of a candidate document, or a refusal that says why it cannot be had.</summary>
    private int OrdinalOf(string documentId)
    {
        if (_index is not IAccumulatingIndex acc)
            throw new NotSupportedException(
                $"{EngineName} keeps its page by ordinal, and {_index.GetType().Name} does not enumerate its ordinals");

        return acc.OrdinalOf(documentId);
    }

    /// <summary>
    /// The ordinal of a candidate document, or <c>-1</c> when this index cannot enumerate its
    /// ordinals — a caller-resolved document goes to the window in that case.
    /// </summary>
    private int TryOrdinalOf(string documentId) =>
        _index is IAccumulatingIndex acc ? acc.OrdinalOf(documentId) : -1;

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Search(query.AsSpan(), options, cancellationToken);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null, CancellationToken cancellationToken = default) =>
        RunQuery(query, options ?? SearchOptions.Default, null, cancellationToken);

    /// <inheritdoc />
    public FacetedSearchResult SearchWithFacets(
        string query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchWithFacets(query.AsSpan(), options, facetFields);
    }

    /// <inheritdoc />
    public FacetedSearchResult SearchWithFacets(
        ReadOnlySpan<char> query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null)
    {
        var collector = FacetCollector.Create(facetFields, numeric: null);
        var results = RunQuery(query, options ?? SearchOptions.Default, collector, CancellationToken.None);

        return new FacetedSearchResult(
            results,
            collector is null ? Array.Empty<FacetBucket>() : collector.Build());
    }

    /// <inheritdoc />
    public FacetedSearchResult SearchWithNumericFacets(
        string query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null,
        IReadOnlyList<NumericFacetRequest>? numericFacets = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchWithNumericFacets(query.AsSpan(), options, facetFields, numericFacets);
    }

    /// <inheritdoc />
    public FacetedSearchResult SearchWithNumericFacets(
        ReadOnlySpan<char> query,
        SearchOptions? options = null,
        IReadOnlyList<string>? facetFields = null,
        IReadOnlyList<NumericFacetRequest>? numericFacets = null)
    {
        var collector = FacetCollector.Create(facetFields, numericFacets);
        var results = RunQuery(query, options ?? SearchOptions.Default, collector, CancellationToken.None);

        return new FacetedSearchResult(
            results,
            collector is null ? Array.Empty<FacetBucket>() : collector.Build())
        {
            NumericBuckets = collector is null ? Array.Empty<NumericFacetBucket>() : collector.BuildNumeric(),
        };
    }

    /// <summary>
    /// One pass over the candidate documents: parse/resolve the query once, score and gate
    /// every candidate, optionally count facet values for the documents that match
    /// (<paramref name="facets"/>), and cut the requested page from the bounded top window.
    /// </summary>
    private IReadOnlyList<SearchResult> RunQuery(
        ReadOnlySpan<char> query,
        SearchOptions options,
        FacetCollector? facets,
        CancellationToken cancellationToken)
    {
        // A search whose token is already cancelled does no work. The loops below check again every
        // 1024 candidates: one branch per search here, and one per thousand on the scoring path, so a
        // long corpus stays interruptible without putting a check in front of every document.
        cancellationToken.ThrowIfCancellationRequested();

        // One clock read per search, and only when a sink is attached: an un-instrumented engine
        // never reads the timer at all.
        bool instrumented = _telemetry.IsEnabled;
        long started = instrumented ? RetrievalTelemetry.StartTimer() : 0;

        if (options.IsEmpty)
            return Array.Empty<SearchResult>();

        if (_index.Count == 0)
        {
            if (instrumented)
                _telemetry.Warning(EngineName, "query served an empty index: no documents indexed");

            return Array.Empty<SearchResult>();
        }

        // The field vocabulary a `name:` prefix may name. Resolved only when the query contains a
        // colon, because without one no prefix can be a scope: a search that does not use the
        // syntax allocates nothing for it. `null` means "no field grammar" (the literal path, or a
        // query whose only fields are the default one, which has no writable name).
        var knownFields = KnownFieldNames(query, options);

        // The span pass first: a plain query whose terms are slices of the query text is scored
        // without ever materializing them. It declines — and the string path below then runs
        // unchanged — when the terms are not slices, when the scorer or the index cannot resolve them
        // as such, or when the query is too rare to amortize the accumulation buffer.
        //
        // The last argument is the same capability TryOrdinalOf tests: an index that enumerates its
        // ordinals hands the window a document ordinal that is the document's position in it, which
        // is the order documents were added. That is what TieBreak.InsertionOrder compares, so the
        // order a tied page comes back in does not depend on which of the two passes ran.
        var top = new TopRankedWindow(
            options.WindowFor(_index.Count),
            options.TieBreak,
            _resolveOrdinal,
            _resolveId,
            ordinalsAreInsertionPositions: _index is IAccumulatingIndex);
        var spanRun = TryRunSpanQuery(query, options, knownFields, facets, top, cancellationToken);

        if (spanRun is { } span)
        {
            return CutPage(
                options, top, span.Counts.Ordinal, span.Counts.ScoredDocuments, span.ScoreStarted, started, instrumented);
        }

        var prepared = BuildQuery(query, options, knownFields);
        var parsed = prepared.Parsed;
        var queryTerms = prepared.Terms;
        var scopedTerms = prepared.ScopedTerms;
        var requiredExpansionTerms = prepared.RequiredExpansionTerms;
        var prohibitedExpansionTerms = prepared.ProhibitedExpansionTerms;

        // A query whose only positive content is a prohibited operator is still a query: it
        // matches the complement of what it excludes. Everything else with no positive term
        // (a whitespace query, a punctuation-only one, a vacuous phrase) matches nothing.
        bool negativeOnly = queryTerms.Count == 0 && scopedTerms.Count == 0 && parsed is { HasProhibited: true };

        if (queryTerms.Count == 0 && scopedTerms.Count == 0 && !negativeOnly)
        {
            if (instrumented)
                _telemetry.Warning(EngineName, "query produced no searchable term: nothing to match against the vocabulary");

            return Array.Empty<SearchResult>();
        }

        // The cost sheet, read once. `Tokens` is recorded here rather than beside the scoring
        // below because the count belongs to the parse, which has happened and will not happen
        // again: a query that goes on to match nothing has still cost this many tokens, and the
        // stage row below would otherwise be the only record of a search that returned nothing.
        var costs = options.Costs;
        costs?.AddTokens(queryTerms.Count + scopedTerms.Count);

        var distinctQueryTerms = DistinctTermList.Wrap(TermDeduplicator.Distinct(queryTerms));

        // Precompute the query-level corpus constants (idf, collection probabilities, ...) once
        // per search instead of per candidate document when the scorer supports it.
        //
        // The plan gets the *raw* terms, not the deduplicated ones, and that is the whole reason
        // this call is not the tidy `CreatePlan(distinctQueryTerms, _index)` it looks like it should
        // be. Whether a term repeated in the query counts once or once per occurrence is a ranking
        // decision that belongs to the scorer (QueryTermWeighting), and handing it a list it can
        // prove is already distinct answers the question before it is asked: Bm25Scorer.Terms
        // short-circuits on DistinctTermList, so with this line as it was, QueryFrequency and
        // Distinct produced bit-identical rankings through the engine, on every metric, on 1,406
        // ArguAna queries. The setting was not broken by a later change; it never worked.
        //
        // The deduplicated list is still what the two decisions below get, and that is the half
        // that would break if it were unified with the above. SumDocumentFrequencies and the
        // candidate enumeration both want each term counted once: a query repeating a term thirty
        // times would otherwise claim thirty times the reach, push itself over the accumulation
        // threshold and change scoring path for a query that matches the same documents. The two
        // lists answer different questions and are not interchangeable.
        //
        // Cost on the default path: nothing. TermDeduplicator.Distinct returns its input when it
        // can prove the list has no duplicates, so a query with no repeated term resolves to the
        // same instance it would have before, and the plan sees the same terms.
        //
        // A pure-negative query has no positive term to plan: its survivors are scored at 1.0
        // below, and handing a scorer an empty term list would ask it a question it was not
        // written for.
        var plan = queryTerms.Count > 0 && _scorer is IQueryPlannableScorer plannable
            ? plannable.CreatePlan(queryTerms, _index)
            : null;

        long ordinal = 0;

        // The terms a candidate may share with this query: the unscoped scoring terms plus the
        // names of the scoped ones. A scoped term's document frequency over the whole corpus is an
        // upper bound on its field's (a document that has the term only in another field is still
        // enumerated and then scored/scoped to zero), so the union is a safe candidate set. Without
        // a scope the list is the scoring list itself and this costs nothing.
        IReadOnlyList<string> candidateTerms = scopedTerms.Count == 0
            ? distinctQueryTerms
            : UnionOf(distinctQueryTerms, scopedTerms);

        // One dictionary lookup per query term, feeding both of the decisions below. They are two
        // readings of the same quantity — how much of the corpus this query can reach — so it is
        // asked once rather than once per decision.
        int reachableDocuments = SumDocumentFrequencies(_index, candidateTerms);

        // The term-at-a-time pass, when the plan and the index can both offer it. It subsumes the
        // candidate-or-scan choice below: the posting walks produce the matched set themselves, so
        // there is no union to build and no corpus to walk when the query terms are rare — and it
        // does not have to be re-made per query shape. That choice stays as the fallback for
        // everything this pass declines.
        // Scored under both paths below, so the clock brackets the branch rather than one arm of it:
        // the two cost differently by a wide margin, and a sheet that timed whichever arm ran
        // would still be right about the search it describes.
        long scoreStarted = costs is not null ? RetrievalTelemetry.StartTimer() : 0;

        // Documents whose relevance score was computed, whichever path below computes it. This is
        // not `ordinal`, which counts the documents that *matched* and reached the top window: a
        // document scored to zero was paid for and is not counted there, so a cost sheet built on
        // ordinal would under-report the work by every non-matching candidate.
        long scoredDocuments = 0;

        if (TryRunAccumulatingQuery(plan, reachableDocuments, parsed, options, facets, top, cancellationToken) is { } accumulated)
        {
            ordinal = accumulated.Ordinal;
            scoredDocuments = accumulated.ScoredDocuments;
        }
        else
        {
            // A candidate's document is read only by a metadata filter and by a facet counter. Scoring
            // needs its id, the window needs its ordinal, and TopRankedWindow resolves the page's
            // documents at the cut — so when neither of those readers is in play the engine takes ids
            // and nothing else. That is the difference between decoding the candidate set and decoding
            // the page: a query-likelihood search takes this path, and its latency was dominated by
            // materializing documents it read one field of.
            bool documentFree = options.Filters is not { Count: > 0 } && facets is null;

            // The ids, from the candidate enumeration when the terms reach little enough of the corpus
            // and from the ordinal space when they do not. Both are ids-only, so a store never decodes
            // a document it is not returning; the two differ in which documents they visit, not in what
            // they cost per visit. An index that cannot answer by id keeps the document path below.
            //
            // A pure-negative query has no positive term to enumerate from, and its complement is the
            // whole corpus minus the excluded documents, so both enumerations are skipped and the full
            // scan below runs.
            bool hasPositiveTerms = candidateTerms.Count > 0;
            IEnumerable<string>? candidateIds = null;

            if (documentFree && hasPositiveTerms && _scorer is ITermOverlapScorer)
            {
                if (_index is ICandidateIdIndex idIndex &&
                    CandidatesCoverFractionOfCorpus(reachableDocuments, _index.Count))
                {
                    candidateIds = idIndex.GetCandidateIds(candidateTerms);
                }
                else if (_index is IAccumulatingIndex ordinalSpace && _index is IOrdinalIdIndex ordinalIds)
                {
                    candidateIds = EveryId(ordinalSpace, ordinalIds);
                }
            }

            if (candidateIds is not null)
            {
                int checkedIds = 0;

                foreach (string documentId in candidateIds)
                {
                    if ((checkedIds++ & 0x3FF) == 0)
                        cancellationToken.ThrowIfCancellationRequested();

                    ScoreCandidate(documentId, document: null);
                }

                return CutPage(options, top, ordinal, scoredDocuments, scoreStarted, started, instrumented);
            }

            // Convention: a score of exactly 0 means "not a match".
            // When the index can enumerate the documents sharing at least one query term
            // (ICandidateIndex) and the scorer provably returns 0 for every document that
            // shares none (ITermOverlapScorer), score only those candidates: same results,
            // same order, far fewer distance computations. Otherwise fall back to the full scan.
            var candidateDocuments =
                hasPositiveTerms &&
                _index is ICandidateIndex candidateIndex &&
                _scorer is ITermOverlapScorer &&
                CandidatesCoverFractionOfCorpus(reachableDocuments, _index.Count)
                    ? Candidates(candidateIndex, candidateTerms)
                    : _index.Documents;

            int checkedDocuments = 0;

            foreach (var document in candidateDocuments)
            {
                if ((checkedDocuments++ & 0x3FF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();

                ScoreCandidate(document.Id, document);
            }
        }

        // Scores one candidate. `document` is null on the id-only path, where the gates that read a
        // document are provably not in play and the page cut resolves it.
        void ScoreCandidate(string documentId, SearchDocument? document)
        {
            // Structured filters gate the corpus before any relevance math is paid for.
            if (document is not null && !options.PassesFilters(document))
                return;

            // Phrase gates, the required/prohibited operators and the field scopes are hard
            // corpus gates: every one is checked before any relevance math is paid for.
            if (parsed is { } gated
                && (gated.HasPhrases || gated.HasQualifiers || gated.HasFieldScopes)
                && !PassesQueryGates(
                    documentId, gated, requiredExpansionTerms, prohibitedExpansionTerms,
                    prepared.RequiredScopedExpansions, prepared.ProhibitedScopedExpansions))
            {
                return;
            }

            // A pure-negative query carries no relevance signal, so its survivors get the unit
            // score: a document is "matched" by not being excluded, and the configured tie-break
            // orders the page. A scorer is never asked to score an empty term list here.
            //
            // The unscoped and the field-scoped parts are two sums added together, in that order,
            // so a query mixing `title:foo` with a free `bar` ranks on the evidence each asked for.
            // A scoped-only query never calls the scorer's unscoped entry point with an empty term
            // list: the scoped interface is the whole question.
            double score = negativeOnly ? 1.0 : ScoreUnscoped(documentId) + ScoreScoped(documentId);

            scoredDocuments++;

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0 || score < options.MinimumScore)
                return;

            if (document is not null)
                facets?.Count(document);

            top.Add(score, TryOrdinalOf(documentId), document, ordinal);
            ordinal++;
        }

        double ScoreUnscoped(string documentId)
        {
            if (distinctQueryTerms.Count == 0)
                return 0;

            return plan is null
                ? _scorer.Score(documentId, distinctQueryTerms, _index)
                : plan.Score(documentId);
        }

        double ScoreScoped(string documentId) =>
            scopedTerms.Count == 0 || _scopedScorer is null
                ? 0
                : _scopedScorer.ScoreScoped(documentId, scopedTerms, _index);

        return CutPage(options, top, ordinal, scoredDocuments, scoreStarted, started, instrumented);
    }

    /// <summary>
    /// Cuts the requested page out of the window and records what the search cost.
    /// </summary>
    /// <remarks>
    /// The rounding, the trace and the telemetry belong to the answer rather than to the path that
    /// produced the ranking, so both scoring paths arrive here and this is the only place they run.
    /// </remarks>
    private IReadOnlyList<SearchResult> CutPage(
        SearchOptions options,
        TopRankedWindow top,
        long ordinal,
        long scoredDocuments,
        long scoreStarted,
        long started,
        bool instrumented)
    {
        // `scoredDocuments` is what relevance math was paid for — the documents that cleared the
        // filter and phrase gates on the path taken — so it is the honest ItemCount whichever arm
        // ran. Recorded before the page cut, so the elapsed time covers scoring and not the cut,
        // and `Windows` stays 0: a scorer is handed no request and cannot report how many it looked
        // at.
        if (options.Costs is { } costs)
            costs.Record(new SearchCostStage("score", scoredDocuments, 0, RetrievalTelemetry.ElapsedMs(scoreStarted)));

        // The accumulated window holds at most Offset + Limit entries; skip the Offset prefix
        // of the best-first view to cut the requested page.
        int skip = Math.Min(options.Offset, top.Count);
        int count = top.Count - skip;
        var results = new SearchResult[count];
        top.CopyBestTo(results, skip, count);

        // After the page is cut, and only on the page: the rounding reads the score above each one, so
        // it is a statement about the set being returned rather than about any document. Applying it
        // before the cut would move scores by neighbours that are not in the answer.
        if (options.ScoreRounding == ScoreRounding.FourDecimals)
            ScoreRoundingStep.Apply(results);

        // Tracing happens here, on the cut page, not inside the scoring loop above: the score is
        // already in hand, so a trace costs O(limit) after the fact and nothing per candidate.
        options.Trace?.RecordScoreStage(_scorer.Name, results);

        if (instrumented)
        {
            // ordinal counts every document that passed the filters and phrase gates, which is the
            // number relevance math was actually paid for -- not the corpus, not the page.
            _telemetry.SearchCompleted(
                EngineName, started, results.Length, (int)Math.Min(ordinal, (long)int.MaxValue));
        }

        return results;
    }

    /// <summary>
    /// The documents an accumulation pass recorded, gated, counted and offered to the window.
    /// </summary>
    /// <remarks>
    /// One loop for both kinds of plan, which differ only in how they filled the accumulator. One
    /// gate list, shared with the per-document loop, rather than this pass's own: a separate list is
    /// how the id exclusion came to be missing from this path while the other applied it — the two
    /// drifted, and nothing in the shape of the code objected.
    /// </remarks>
    private AccumulatedCounts FinishAccumulatedPass(
        IAccumulatingIndex index,
        ScoreAccumulator accumulator,
        IAccumulatedScoreFinisher finisher,
        SearchOptions options,
        FacetCollector? facets,
        TopRankedWindow top,
        CancellationToken cancellationToken)
    {
        long ordinal = 0;

        // The filters, the exclusions and the facet counter are the only costs that read a document,
        // so the document is resolved exactly when one of them is in play: a plain search keeps this
        // pass by ordinal and never decodes a candidate's document until the page cut. That is the
        // deferral that makes a segment index's finish pass cheap in the first place.
        bool needsDocument = options.Filters is { Count: > 0 }
            || options.ExcludedDocumentIds is not null
            || facets is not null;

        for (int i = 0; i < accumulator.Count; i++)
        {
            if ((i & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            int candidate = accumulator.OrdinalAt(i);

            // The plan's last step, because this path never calls Score and a score that is narrowed
            // at the end of the sum has to be narrowed somewhere.
            double score = finisher.Finalise(accumulator[candidate]);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0 || score < options.MinimumScore)
                continue;

            // An empty filter list and no exclusions make PassesFilters answer without reading the
            // document, so the common case is the same answer with none of the resolution.
            if (needsDocument)
            {
                // The accumulator hands out ordinals for the candidates it has just scored, so the
                // index resolves every one of them. A null here is a broken candidate set, and naming
                // it beats a dereference on the result path.
                var document = index.DocumentAt(candidate)
                    ?? throw new InvalidOperationException(
                        $"the accumulator produced candidate {candidate}, which the index cannot resolve");

                if (!options.PassesFilters(document))
                    continue;

                facets?.Count(document);
            }

            top.Add(score, candidate, resolved: null, ordinal: ordinal);
            ordinal++;
        }

        // Every recorded ordinal was scored — the buffer holds exactly the candidates the pass
        // accumulated a weight for, whether or not a finalised score survived the gate above. That
        // makes this the same quantity the per-document loop counts one at a time.
        return new AccumulatedCounts(ordinal, accumulator.Count);
    }

    /// <summary>
    /// The query path that never materializes a term: the tokenizer fills slices of the query text,
    /// the index resolves each slice by span and the plan folds them.
    /// </summary>
    /// <remarks>
    /// Everything this cannot serve returns <c>null</c> and the caller falls back to the string path
    /// unchanged — a phrase, a prefix or fuzzy atom or a configured synonym resolves to terms that
    /// are not in the query text; a tokenizer, a scorer or an index without the span capability has
    /// no such path; and a query too rare to amortize the accumulation buffer is the one the
    /// per-document loop serves better.
    /// </remarks>
    private (AccumulatedCounts Counts, long ScoreStarted)? TryRunSpanQuery(
        ReadOnlySpan<char> query,
        SearchOptions options,
        IReadOnlySet<string>? knownFields,
        FacetCollector? facets,
        TopRankedWindow top,
        CancellationToken cancellationToken)
    {
        if (_synonyms is not null || _tokenizer is not ISpanTokenizer tokenizer)
            return null;

        // A phrase or an operator is what the parser exists for; a plain query is the case this path
        // serves. Asking the predicate the string path asks is what keeps the two in step — without
        // it a quoted query would reach the vocabulary with its quotes tokenized away and its
        // positional gate lost. A field scope is never plain, so a scoped query takes the string
        // path. The literal mode has no query language at all, so it is plain by construction and
        // needs no predicate.
        if (options.ParseQuerySyntax && !QueryParser.IsPlain(query, knownFields))
            return null;

        if (_scorer is not IQueryPlannableScorer plannable || _index is not ISpanAccumulatingIndex spanIndex)
            return null;

        // The same gates the string path applies before it accumulates, so that whenever this pass
        // runs the string path would have run it too: a scorer that does not return zero for a
        // document sharing none of the terms, and a filtered query whose filter the pass would not
        // honour, both decline here rather than producing a second answer to the same question.
        if (_scorer is not ITermOverlapScorer)
            return null;

        if (options.Filters is { Count: > 0 } && !options.AccumulateFilteredQueries)
            return null;

        // A text can produce at most one term per character, and the tokenizer declines rather than
        // overflowing when it needs more room than this.
        var rented = ArrayPool<NormalizedTerm>.Shared.Rent(query.Length + 1);

        try
        {
            int count = tokenizer.TokenizeNormalized(query, rented);

            if (count <= 0)
                return null;

            var terms = rented.AsSpan(0, count);

            // The reach of the query, counted once per distinct term exactly as the string path
            // counts it: a query repeating a term must not push itself over the accumulation
            // threshold here and stay under it there, which would be two searches with two different
            // paths and two different cost sheets for one query.
            int reachable = 0;

            for (int i = 0; i < terms.Length; i++)
            {
                if (!NormalizedTerm.IsRepeated(terms, i, query))
                    reachable += spanIndex.SpanDocumentFrequency(terms[i].View(query));
            }

            if (reachable < AccumulationThreshold(spanIndex.OrdinalSpace))
                return null;

            if (plannable.CreateSpanPlan(terms, query, _index) is not { } plan)
                return null;

            // Recorded here rather than beside the tokenization: the count belongs to the parse that
            // is now certainly this path's, and a decline above hands the query to the string path,
            // which records it itself.
            options.Costs?.AddTokens(terms.Length);

            // Read here rather than at the top of the caller's branch, so the sheet's score stage
            // covers the fold and not the parse — the same accounting the string path has.
            long scoreStarted = options.Costs is not null ? RetrievalTelemetry.StartTimer() : 0;

            // The document-at-a-time pass, when the index can lend the postings as arrays and the
            // query reaches little enough of the corpus to be worth more than the accumulator's
            // fixed cost. It writes the window directly, so it runs before the accumulator is
            // rented — and the plan declines before its first write, which makes the fall-through
            // safe.
            if (spanIndex is ISpanPostingArrayIndex arrayIndex && ShouldUseCursorPass(spanIndex, reachable))
            {
                var sink = new DocumentScoreSink(spanIndex, plan, options, facets, top, cancellationToken);

                if (plan.TryScoreSpanDocuments(arrayIndex, terms, query, sink))
                    return (sink.Counts, scoreStarted);
            }

            var accumulator = ScoreAccumulator.Rent(spanIndex.OrdinalSpace);

            try
            {
                if (!plan.TryAccumulateSpans(spanIndex, terms, query, accumulator))
                    return null;

                return (FinishAccumulatedPass(spanIndex, accumulator, plan, options, facets, top, cancellationToken), scoreStarted);
            }
            finally
            {
                accumulator.Reset();
                accumulator.Dispose();
            }
        }
        finally
        {
            ArrayPool<NormalizedTerm>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Floor on the total document frequency a query needs before the term-at-a-time pass runs.
    /// </summary>
    /// <remarks>
    /// The floor exists for the pass's fixed setup, not for its per-entry work. It clears a buffer
    /// sized to the corpus before it scores anything.
    /// <para>
    /// <b>Measured at three corpus sizes</b>, reported as the break-even where the two loops cost
    /// the same. Both loops are timed over the same query and the same corpus in one process, the
    /// per-document one forced by a metadata filter that rejects nothing.
    /// </para>
    /// <list type="bullet">
    /// <item><description>10,000 documents — 0.5 µs fixed, 12.8 ns per posting entry, 96 ns per candidate scored, break-even <b>≈ 6</b></description></item>
    /// <item><description>100,000 documents — 1 µs fixed, 11.5 ns per entry, 245 ns per candidate, break-even <b>≈ 4</b></description></item>
    /// <item><description>1,000,000 documents — 16.1 µs fixed, 12.6 ns per entry, 295 ns per candidate, break-even <b>≈ 57</b></description></item>
    /// </list>
    /// <para>
    /// Two things fall out of that. The new pass's per-entry cost is <b>flat across two orders of
    /// magnitude</b> (12.8 / 11.5 / 12.6 ns) while the old loop's per-candidate cost triples
    /// (96 / 245 / 295 ns) as the postings dictionaries outgrow the cache — which is why the win
    /// grows with corpus size rather than staying put. And the break-even barely moves, so the
    /// fixed cost is not what should be setting the threshold.
    /// </para>
    /// <para>
    /// <b>What is not measured:</b> anything above a million documents. Extrapolating the fixed
    /// cost alone would put a 10-million-document corpus somewhere near 400, which is the
    /// direction <see cref="AccumulationCorpusDivisor"/> encodes — but at that size the
    /// per-candidate cost is the part with the least evidence behind it, and it is the part that
    /// sets the crossing.
    /// </para>
    /// <para>
    /// The three "fixed cost" figures above are the weak column of that table and should not be
    /// read as an end-to-end intercept. Part of that cost is an <c>Array.Clear</c> of the whole
    /// ordinal space, timed alone on this host at 196 ns / 2.35 µs / 18.5 µs for 10,000 /
    /// 100,000 / 1,000,000 — so at the two larger sizes the clear alone exceeds the recorded
    /// figure, and the measurements behind the table are not affine in document frequency, which
    /// is what an intercept would require. The two slopes (12.8 / 11.5 / 12.6 ns per entry and
    /// 96 / 245 / 295 ns per candidate) survived a re-measurement at 100,000 documents; the
    /// intercepts did not, and neither did the two outer corpus rows, which were not re-run. See
    /// the correction in <c>docs/benchmarks.md</c>.
    /// </para>
    /// </remarks>
    private const int MinimumPostingEntriesForAccumulation = 8;

    /// <summary>
    /// The minimum reachable documents a query needs for the accumulation pass to be taken, for a
    /// given index. Internal so a test can assert its fixture <i>reaches</i> the path rather than
    /// silently falling back and passing for the wrong reason.
    /// </summary>
    internal static int AccumulationThreshold(int ordinalSpace) =>
        Math.Max(MinimumPostingEntriesForAccumulation, ordinalSpace / AccumulationCorpusDivisor);

    /// <summary>
    /// How the floor above scales with the corpus: one posting entry per this many documents.
    /// </summary>
    /// <remarks>
    /// Fitted to the three break-evens above (6 / 4 / 57): this divisor gives 8 / 8 / 50 against
    /// them. The divisor of 1,024 that came first — a guess, made before any of this was measured —
    /// gives 9 / 97 / 976, and hands back a 1.2-2.4× win at two of the three scales.
    /// <para>
    /// The scaling has to be this weak because <b>both</b> sides of the crossing grow with the
    /// corpus: the pass clears an <c>N</c>-byte buffer, and the per-document loop's lookups get
    /// slower as the postings dictionaries outgrow the cache. Scaling the threshold on the fixed
    /// cost alone over-corrects for that second effect, which is exactly what the 1,024 did.
    /// </para>
    /// </remarks>
    private const int AccumulationCorpusDivisor = 20_000;

    /// <summary>
    /// Scores the query in one term-at-a-time pass and cuts the window from the matched set,
    /// returning the counts it produced — or <c>null</c>, having changed nothing but a rented
    /// buffer, when the query has to go the per-document way instead.
    /// </summary>
    /// <remarks>
    /// The gates this path refuses are the ones that are cheaper before scoring than after: a
    /// quoted phrase rejects documents by walking positions, so a phrase-heavy query over a large
    /// corpus would have most of its score arithmetic thrown away. That refusal stands.
    /// <para>
    /// A <see cref="SearchOptions.Filters"/> entry is not in that class, and
    /// <see cref="SearchOptions.AccumulateFilteredQueries"/> — on by default — is how a filter reaches
    /// this pass anyway. The reason is a price rather than a defect: the per-document loop scores
    /// <b>every surviving candidate by document id</b>, one id-keyed length lookup plus one per query
    /// term. That price is 42x on arguana, 8.2x on scifact and 5.9x on nfcorpus when the filter keeps
    /// everything, and falls monotonically as the filter tightens. The trade is that this pass scores
    /// the candidates a <i>very</i> selective filter discards; the option goes back to the other path
    /// for a caller whose filter keeps almost nothing.
    /// </para>
    /// <para>
    /// Everything else — the rare-term and head-term regimes alike — is a win either way, because
    /// the pass replaces <c>2 · terms · documents</c> string hashes with one walk of the posting
    /// entries that actually exist.
    /// </para>
    /// <para>
    /// The matched set arrives in posting order rather than corpus order. Nothing downstream
    /// depends on the difference: the gates are per document, the facet counts are increments, and
    /// under <see cref="SearchOptions.TieBreak"/> the tie is decided on the document id or on the
    /// document's ordinal — both of which this pass takes from the accumulator, not from the order
    /// the terms happened to be folded in.
    /// </para>
    /// </remarks>
    private AccumulatedCounts? TryRunAccumulatingQuery(
        ISearchQueryPlan? plan,
        int reachableDocuments,
        ParsedQuery? parsed,
        SearchOptions options,
        FacetCollector? facets,
        TopRankedWindow top,
        CancellationToken cancellationToken)
    {
        // A metadata filter reaches this pass through AccumulateFilteredQueries alone. The tie-break
        // no longer has anything to do with it: a tie is decided on the document id or on the
        // document's ordinal, and both are carried by the ordinals this pass folds, so the page is
        // the same whichever side served the query. Refusing the pass here would only move a
        // filtered query from a fast loop to a slow one.
        bool filterWouldBeHonoured = options.Filters is { Count: > 0 };

        if (plan is not IAccumulatingQueryPlan accumulating ||
            _index is not IAccumulatingIndex index ||
            _scorer is not ITermOverlapScorer ||
            parsed is { HasPhrases: true } ||
            parsed is { HasQualifiers: true } ||
            parsed is { HasFieldScopes: true } ||
            (filterWouldBeHonoured && !options.AccumulateFilteredQueries))
        {
            return null;
        }

        // The pass clears a buffer sized to the corpus before it scores anything, so a query too
        // small to amortize that is better served by the per-document loop. See the constants.
        int minimumEntries = AccumulationThreshold(index.OrdinalSpace);

        if (reachableDocuments < minimumEntries)
            return null;

        // The document-at-a-time alternative, for an index that can lend the query terms' postings as
        // arrays. The plan declines before its first write when it cannot serve the pass; the
        // accumulator below is then the answer, unchanged.
        if (index is IPostingArrayIndex arrayIndex && ShouldUseCursorPass(index, reachableDocuments))
        {
            var sink = new DocumentScoreSink(index, accumulating, options, facets, top, cancellationToken);

            if (accumulating.TryScoreDocuments(arrayIndex, sink))
                return sink.Counts;
        }

        var accumulator = ScoreAccumulator.Rent(index.OrdinalSpace);

        try
        {
            if (!accumulating.TryAccumulate(index, accumulator))
                return null;

            return FinishAccumulatedPass(index, accumulator, accumulating, options, facets, top, cancellationToken);
        }
        finally
        {
            // Only the recorded ordinals hold state, so the buffer is reusable after clearing those
            // — and the rented arrays go back whether the pass completed or the plan declined.
            accumulator.Reset();
            accumulator.Dispose();
        }
    }

    /// <summary>
    /// What the term-at-a-time pass produced: the ordinals it recorded, and the documents it scored
    /// to produce them.
    /// </summary>
    /// <param name="Ordinal">Documents that matched and reached the top window.</param>
    /// <param name="ScoredDocuments">
    /// Documents whose relevance score was computed, including those that scored zero. Not the same
    /// number as <paramref name="Ordinal"/>: a document paid for and rejected is not counted in either,
    /// but a document paid for and scored zero is counted here and not there.
    /// </param>
    private readonly record struct AccumulatedCounts(long Ordinal, long ScoredDocuments);

    /// <summary>
    /// The share of the corpus a query may reach, at most, for the document-at-a-time cursor pass to
    /// be preferred: one reached document per this many in the index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The cursor pass removes the accumulator's corpus-sized clear but pays a per-document cursor scan
    /// and a window offer, so it wins on <b>sparse</b> queries and loses on dense ones at every corpus
    /// size measured. The divisor below sits inside the measured interval, and the list records where
    /// each interval is.
    /// </para>
    /// <para>
    /// <b>Measured 2026-10-26</b> with <c>cursor &lt;size&gt; --sweep</c>, alternating the build
    /// before and after the cursor pass, on the Zipf corpus the scale lane builds. Interval in which
    /// the crossing lies, at each size, as (last reached term that won, first that lost):
    /// </para>
    /// <list type="bullet">
    /// <item><description>10,000 — wins at df 50 (2.50 against 2.80 µs), loses at df 250 (10.20 against 9.90 µs)</description></item>
    /// <item><description>25,000 — wins at df 250 (11.60 against 12.10 µs), loses at df 1,005 (38.70 against 36.50 µs)</description></item>
    /// <item><description>50,000 — wins at df 250 (9.90 against 11.30 µs), loses at df 5,014 (107.30 against 144.60 µs)</description></item>
    /// <item><description>100,000 — wins at df 250 (10.00 against 11.00 µs), loses at df 1,002 (48.80 against 45.00 µs)</description></item>
    /// <item><description>500,000 — wins at df 1,000 (50.60 against 57.90 µs), loses at df 5,006 (155.00 against 143.80 µs)</description></item>
    /// </list>
    /// <para>
    /// The 50,000 row's losing figure is the one doubtful reading: the baseline's p50 there was
    /// 144.6 µs against a minimum of 82.0, while the cursor pass read 107.3 against 105.5, so the
    /// relative order of the medians rests on a noisy baseline. The divisor of 200 gives a cap of
    /// 50 / 125 / 250 / 500 / 2,500 at those sizes, which is inside every interval above.
    /// <b>Not measured:</b> the exact crossing between each pair, and any corpus size other than
    /// those five.
    /// </para>
    /// </remarks>
    private const int CursorDensityDivisor = 200;

    /// <summary>Whether the cursor pass should serve this corpus and this query reach.</summary>
    private bool ShouldUseCursorPass(IAccumulatingIndex index, int reachableDocuments) =>
        (ForcedPass ?? PassFor(index, reachableDocuments)) == ScoringPass.Cursor;

    /// <summary>
    /// The engine's end of a document-at-a-time pass: the same gates, the same finalisation and the
    /// same window the accumulator's finish loop applies, one document at a time.
    /// </summary>
    /// <remarks>
    /// It exists because the traversal must not know what a candidate costs past its score. The
    /// checks here are deliberately the same list <see cref="FinishAccumulatedPass"/> runs, in the
    /// same order — that is what makes the two passes produce one answer rather than two.
    /// </remarks>
    private sealed class DocumentScoreSink(
        IAccumulatingIndex index,
        IAccumulatedScoreFinisher finisher,
        SearchOptions options,
        FacetCollector? facets,
        TopRankedWindow top,
        CancellationToken cancellationToken) : IDocumentScoreSink
    {
        private readonly bool _needsDocument = options.Filters is { Count: > 0 }
            || options.ExcludedDocumentIds is not null
            || facets is not null;

        /// <summary>Documents that reached the window.</summary>
        public long Ordinal { get; private set; }

        /// <summary>Documents whose relevance score was computed, zero scores included.</summary>
        public long Scored { get; private set; }

        public AccumulatedCounts Counts => new(Ordinal, Scored);

        public void Score(int ordinal, double score)
        {
            Scored++;

            if ((Scored & 0x3FF) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            score = finisher.Finalise(score);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0 || score < options.MinimumScore)
                return;

            if (_needsDocument)
            {
                var document = index.DocumentAt(ordinal)
                    ?? throw new InvalidOperationException(
                        $"the document pass produced candidate {ordinal}, which the index cannot resolve");

                if (!options.PassesFilters(document))
                    return;

                facets?.Count(document);
            }

            top.Add(score, ordinal, resolved: null, ordinal: Ordinal);
            Ordinal++;
        }
    }

    /// <summary>
    /// Sum of the query terms' document frequencies: an upper bound on the union of the candidate
    /// documents, and the unit both the accumulation threshold and the candidate-or-scan choice
    /// below are written in.
    /// </summary>
    private static int SumDocumentFrequencies(IReadOnlyTextIndex index, IReadOnlyList<string> queryTerms)
    {
        if (index.Count == 0)
            return 0;

        long total = 0;

        foreach (var term in queryTerms)
            total += index.DocumentFrequency(term);

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>
    /// The candidate documents to score, in whatever order the index can produce them cheaply.
    /// </summary>
    /// <remarks>
    /// The order is not load-bearing, and that is the whole point. The loop in
    /// <see cref="RunQuery"/> is order-independent: filters and the phrase gate are per document,
    /// scoring is per document, the facet counts are increments, and the page is cut by
    /// <c>TopRankedWindow</c>, whose order is total — score descending, ties broken by ordinal
    /// document id. So the window keeps the same best documents, and writes the same page,
    /// whatever order they were offered in. <c>CandidateEnumerationOrderTests</c> is what makes
    /// that a checked property rather than a comment.
    /// <para>
    /// An index offering <see cref="IUnorderedCandidateIndex"/> therefore skips the ordering
    /// pass in <see cref="ICandidateIndex.GetCandidateDocuments"/>, which re-walks the whole
    /// corpus to hand candidates back in corpus order. That pass costs O(corpus) however few
    /// documents matched: measured on a 10,000-document index, 0.001 ms for the union against
    /// 0.242 ms for the union plus the re-walk, for a 35-document candidate set.
    /// </para>
    /// <para>
    /// The public method keeps its documented order either way. This is the engine declining to
    /// pay for it, not the contract being narrowed.
    /// </para>
    /// </remarks>
    private static IEnumerable<SearchDocument> Candidates(
        ICandidateIndex index, IReadOnlyList<string> queryTerms) =>
        index is IUnorderedCandidateIndex unordered
            ? unordered.GetCandidatesUnordered(queryTerms)
            : index.GetCandidateDocuments(queryTerms);

    /// <summary>
    /// Every live document's id, in corpus order, read from the ordinal space rather than by
    /// enumerating documents — the id-only counterpart of <see cref="IReadOnlyTextIndex.Documents"/>.
    /// </summary>
    /// <remarks>
    /// The full-scan path visits the whole corpus. An index that answers an ordinal with an id can
    /// serve it without materializing anything, which is what a store needs: enumerating its
    /// <c>Documents</c> decodes every document's text, and the engine reads one field of each.
    /// </remarks>
    private static IEnumerable<string> EveryId(IAccumulatingIndex accumulating, IOrdinalIdIndex ids)
    {
        int space = accumulating.OrdinalSpace;

        for (int ordinal = 0; ordinal < space; ordinal++)
        {
            if (ids.IdAt(ordinal) is { } id)
                yield return id;
        }
    }

    /// <summary>
    /// Every hard query gate that is not a metadata filter: required and prohibited literal terms,
    /// required and prohibited prefix/fuzzy atoms (one gate per atom), and required, prohibited
    /// and unqualified phrases. A document must pass every one before it is scored.
    /// </summary>
    /// <remarks>
    /// A required atom must be present and a prohibited one absent; nothing here contributes to a
    /// score. The resolved expansion term sets are passed separately because the vocabulary is
    /// consulted once per query, not once per candidate: <c>-term*</c> is one gate over every term
    /// the prefix matched, never one gate per term.
    /// </remarks>
    private bool PassesQueryGates(
        string documentId,
        ParsedQuery parsed,
        IReadOnlyList<string[]> requiredExpansions,
        IReadOnlyList<string[]> prohibitedExpansions,
        IReadOnlyList<ScopedExpansion> requiredScopedExpansions,
        IReadOnlyList<ScopedExpansion> prohibitedScopedExpansions)
    {
        var required = parsed.RequiredTerms;

        for (int i = 0; i < required.Count; i++)
        {
            if (_index.TermFrequency(documentId, required[i]) == 0)
                return false;
        }

        var prohibited = parsed.ProhibitedTerms;

        for (int i = 0; i < prohibited.Count; i++)
        {
            if (_index.TermFrequency(documentId, prohibited[i]) > 0)
                return false;
        }

        for (int i = 0; i < requiredExpansions.Count; i++)
        {
            if (!AnyTermPresent(documentId, requiredExpansions[i]))
                return false;
        }

        for (int i = 0; i < prohibitedExpansions.Count; i++)
        {
            if (AnyTermPresent(documentId, prohibitedExpansions[i]))
                return false;
        }

        if (!PassesScopedGates(
            documentId, parsed, requiredScopedExpansions, prohibitedScopedExpansions))
        {
            return false;
        }

        if (!MatchesPhrases(documentId, parsed.Phrases))
            return false;

        if (!MatchesPhrases(documentId, parsed.RequiredPhrases))
            return false;

        for (int i = 0; i < parsed.ProhibitedPhrases.Count; i++)
        {
            if (MatchesPhrase(documentId, parsed.ProhibitedPhrases[i]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// The field-scoped half of the gates: required terms must be in their field, prohibited ones
    /// must be absent from it, phrases must match at consecutive positions inside it, and an
    /// expansion gate is satisfied by any one of its resolved terms in the field.
    /// </summary>
    private bool PassesScopedGates(
        string documentId,
        ParsedQuery parsed,
        IReadOnlyList<ScopedExpansion> requiredScopedExpansions,
        IReadOnlyList<ScopedExpansion> prohibitedScopedExpansions)
    {
        var fields = parsed.HasFieldScopes ? _fieldStatistics : null;

        if (fields is null)
            return true; // unreachable: a scoped query without field statistics was refused at parse

        for (int i = 0; i < parsed.RequiredScopedTerms.Count; i++)
        {
            var term = parsed.RequiredScopedTerms[i];

            if (fields.FieldTermFrequency(documentId, term.Field, term.Term) == 0)
                return false;
        }

        for (int i = 0; i < parsed.ProhibitedScopedTerms.Count; i++)
        {
            var term = parsed.ProhibitedScopedTerms[i];

            if (fields.FieldTermFrequency(documentId, term.Field, term.Term) > 0)
                return false;
        }

        for (int i = 0; i < requiredScopedExpansions.Count; i++)
        {
            var gate = requiredScopedExpansions[i];

            if (!AnyScopedTermPresent(documentId, gate.Field, gate.Terms))
                return false;
        }

        for (int i = 0; i < prohibitedScopedExpansions.Count; i++)
        {
            var gate = prohibitedScopedExpansions[i];

            if (AnyScopedTermPresent(documentId, gate.Field, gate.Terms))
                return false;
        }

        for (int i = 0; i < parsed.ScopedPhrases.Count; i++)
        {
            if (!MatchesScopedPhrase(documentId, parsed.ScopedPhrases[i]))
                return false;
        }

        for (int i = 0; i < parsed.RequiredScopedPhrases.Count; i++)
        {
            if (!MatchesScopedPhrase(documentId, parsed.RequiredScopedPhrases[i]))
                return false;
        }

        for (int i = 0; i < parsed.ProhibitedScopedPhrases.Count; i++)
        {
            if (MatchesScopedPhrase(documentId, parsed.ProhibitedScopedPhrases[i]))
                return false;
        }

        return true;
    }

    /// <summary>Whether the document contains at least one of <paramref name="terms"/>.</summary>
    private bool AnyTermPresent(string documentId, IReadOnlyList<string> terms)
    {
        for (int i = 0; i < terms.Count; i++)
        {
            if (_index.TermFrequency(documentId, terms[i]) > 0)
                return true;
        }

        return false;
    }

    /// <summary>Whether the field carries at least one of the resolved expansion terms.</summary>
    private bool AnyScopedTermPresent(string documentId, string field, IReadOnlyList<string> terms)
    {
        var fields = _fieldStatistics!;

        for (int i = 0; i < terms.Count; i++)
        {
            if (fields.FieldTermFrequency(documentId, field, terms[i]) > 0)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether every phrase appears at consecutive document positions; phrases are AND-ed
    /// (one failing constraint rejects the document).
    /// </summary>
    private bool MatchesPhrases(string documentId, IReadOnlyList<IReadOnlyList<string>> phrases)
    {
        for (int p = 0; p < phrases.Count; p++)
        {
            if (!MatchesPhrase(documentId, phrases[p]))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a scoped phrase sits at consecutive positions inside its field. The positions come
    /// from <see cref="IFieldStatisticsIndex.FieldTermPositions"/>, so an occurrence in another
    /// field can neither satisfy the phrase nor bridge into it.
    /// </summary>
    private bool MatchesScopedPhrase(string documentId, FieldScopedPhrase phrase) =>
        MatchesPhrase(documentId, phrase.Terms, phrase.Field);

    /// <summary>
    /// Whether the phrase's terms sit at positions <c>p, p+1, …</c> for some occurrence of
    /// its first term. Position lists come straight from the index (sorted, no allocation), or
    /// from the field's own positions when <paramref name="field"/> is set.
    /// </summary>
    private bool MatchesPhrase(string documentId, IReadOnlyList<string> phrase, string? field = null)
    {
        var anchors = Positions(documentId, phrase[0], field);

        if (phrase.Count == 1)
            return anchors.Count > 0;

        // Ask each following term for its positions once, not once per anchor. The anchor loop used to
        // re-ask inside itself, so a document whose first term occurs A times paid A+1 look-ups of the
        // rest of the phrase instead of one each. On an in-memory index that is a dictionary read; on a
        // store GetTermPositions re-tokenizes the stored text, so the repeated call was the phrase
        // gate's whole cost — measured at 1.45 GB and 69x the in-memory time for one two-term phrase
        // query over a 20,000-document store.
        if (phrase.Count == 2)
        {
            var second = Positions(documentId, phrase[1], field);

            for (int a = 0; a < anchors.Count; a++)
            {
                if (ContainsPosition(second, anchors[a] + 1))
                    return true;
            }

            return false;
        }

        var followers = new IReadOnlyList<int>[phrase.Count - 1];

        for (int i = 1; i < phrase.Count; i++)
            followers[i - 1] = Positions(documentId, phrase[i], field);

        for (int a = 0; a < anchors.Count; a++)
        {
            int start = anchors[a];
            bool consecutive = true;

            for (int i = 1; i < phrase.Count; i++)
            {
                if (!ContainsPosition(followers[i - 1], start + i))
                {
                    consecutive = false;
                    break;
                }
            }

            if (consecutive)
                return true;
        }

        return false;
    }

    /// <summary>
    /// One term's positions: the whole document's when <paramref name="field"/> is <c>null</c>,
    /// the field's own run otherwise.
    /// </summary>
    private IReadOnlyList<int> Positions(string documentId, string term, string? field) =>
        field is null
            ? _index.GetTermPositions(documentId, term)
            : _fieldStatistics!.FieldTermPositions(documentId, field, term);

    /// <summary>Membership test over an index position list (ascending, typically short).</summary>
    private static bool ContainsPosition(IReadOnlyList<int> positions, int position)
    {
        for (int i = 0; i < positions.Count; i++)
        {
            if (positions[i] == position)
                return true;

            if (positions[i] > position)
                return false; // ascending: no later entry can match
        }

        return false;
    }

    /// <summary>
    /// The resolved form of a parsed query: the concrete scoring terms, the scoped ones, and the
    /// vocabulary matches of every required and prohibited expansion atom (one entry per atom, in
    /// query order) so the gates do not resolve the vocabulary per candidate document.
    /// </summary>
    private readonly record struct ResolvedQuery(
        IReadOnlyList<string> ScoringTerms,
        IReadOnlyList<FieldScopedTerm> ScopedTerms,
        IReadOnlyList<ScopedExpansion> RequiredScopedExpansions,
        IReadOnlyList<ScopedExpansion> ProhibitedScopedExpansions,
        IReadOnlyList<string[]> RequiredExpansionTerms,
        IReadOnlyList<string[]> ProhibitedExpansionTerms);

    /// <summary>
    /// One field-scoped expansion atom's resolved vocabulary matches, kept together with the field
    /// so the gate can ask "is any one of these in that field".
    /// </summary>
    private readonly record struct ScopedExpansion(string Field, string[] Terms);

    /// <summary>
    /// The query a search is about to run: the parsed form (for the gates), the unscoped scoring
    /// terms, the scoped scoring terms, and the resolved expansion gates. A struct, because it is
    /// built once per search and never escapes: a class here would put one allocation on the
    /// matching-nothing path that the engine's budget test pins.
    /// </summary>
    private readonly record struct PreparedQuery(
        ParsedQuery? Parsed,
        IReadOnlyList<string> Terms,
        IReadOnlyList<FieldScopedTerm> ScopedTerms,
        IReadOnlyList<string[]> RequiredExpansionTerms,
        IReadOnlyList<string[]> ProhibitedExpansionTerms,
        IReadOnlyList<ScopedExpansion> RequiredScopedExpansions,
        IReadOnlyList<ScopedExpansion> ProhibitedScopedExpansions)
    {
        public static readonly PreparedQuery Literal = new(
            null,
            Array.Empty<string>(),
            Array.Empty<FieldScopedTerm>(),
            Array.Empty<string[]>(),
            Array.Empty<string[]>(),
            Array.Empty<ScopedExpansion>(),
            Array.Empty<ScopedExpansion>());
    }

    /// <summary>
    /// Shared by <see cref="Search(string, SearchOptions?, CancellationToken)"/> and <see cref="Explain(string, string)"/>: parse the raw query, then
    /// resolve synonyms and vocabulary expansions into the concrete scoring terms. The parsed
    /// form still carries the literal phrase and operator constraints for the gates, and is null
    /// for a query that has none — a plain one, which the query language leaves alone.
    /// </summary>
    /// <remarks>
    /// The two expansion term sets are the resolved vocabulary matches of
    /// <c>+term*</c>/<c>-term*</c> atoms, one entry per atom, so a gate can ask "<i>at least one
    /// of these</i>" or "<i>none of these</i>" without resolving the vocabulary per candidate
    /// document. A scoped expansion carries its field too, in
    /// <see cref="PreparedQuery.RequiredScopedExpansions"/> and its prohibited counterpart.
    /// </remarks>
    private PreparedQuery BuildQuery(
        ReadOnlySpan<char> query,
        SearchOptions options,
        IReadOnlySet<string>? knownFields)
    {
        // The literal path skips the query language entirely, so the terms are the tokenizer's and a
        // quotation mark is a separator like any other. A leading - is ordinary text here too, and
        // so is a colon. Every downstream reader gets the same shape.
        if (!options.ParseQuerySyntax)
            return PreparedQuery.Literal with { Terms = TokenizeQueryTerms(query) };

        // A plain query is what the query language would produce for it anyway: the tokenizer's
        // terms, no phrases, no expansion atoms and no scopes. Building the ParsedQuery record to
        // say that costs 48 bytes a search, so the record is built only when there is something in
        // it. Synonyms are the one resolution a plain query still needs, and that path keeps the
        // parsed form rather than duplicating the expansion here.
        if (_synonyms is null && QueryParser.IsPlain(query, knownFields))
            return PreparedQuery.Literal with { Terms = TokenizeQueryTerms(query) };

        var parsed = QueryParser.Parse(query, _tokenizer, knownFields);
        ValidateFieldScopes(parsed);
        var resolved = ResolveQueryTerms(parsed, options.FuzzyOnlyOutOfVocabulary);

        return new PreparedQuery(
            parsed,
            resolved.ScoringTerms,
            resolved.ScopedTerms,
            resolved.RequiredExpansionTerms,
            resolved.ProhibitedExpansionTerms,
            resolved.RequiredScopedExpansions,
            resolved.ProhibitedScopedExpansions);
    }

    /// <summary>
    /// Refuses a field-scoped query the engine cannot honor, loudly: a scorer without
    /// <see cref="IFieldScopedScorer"/>, an index without per-field statistics, or a name the index
    /// does not have. The alternative — treating the prefix as text or returning a zero score —
    /// would answer a different question than the one asked.
    /// </summary>
    private void ValidateFieldScopes(ParsedQuery parsed)
    {
        if (!parsed.HasFieldScopes)
            return;

        foreach (string field in ScopeFields(parsed))
        {
            if (!HasField(field))
            {
                throw new NotSupportedException(
                    $"{EngineName} cannot scope a query to field '{field}': the index's fields are " +
                    $"{string.Join(", ", _index.Fields)}. Check the spelling or declare the name in " +
                    $"{nameof(SearchOptions)}.{nameof(SearchOptions.KnownFields)}.");
            }
        }

        if (_fieldStatistics is null)
        {
            throw new NotSupportedException(
                $"{EngineName} cannot honor a field-scoped term: {_index.GetType().Name} does not " +
                $"implement {nameof(IFieldStatisticsIndex)}, so no field's statistics can be read. " +
                $"Build the corpus with {nameof(LexiSharp.Indexing.InMemoryTextIndex)}.");
        }

        if (_scopedScorer is null)
        {
            throw new NotSupportedException(
                $"{EngineName} cannot honor a field-scoped term: {_scorer.Name} does not implement " +
                $"{nameof(IFieldScopedScorer)}, so it cannot score one field's statistics. Use " +
                $"{nameof(Bm25FScorer)} for a field-scoped query.");
        }
    }

    /// <summary>Every field the query scopes an atom to, in first-appearance order.</summary>
    private static List<string> ScopeFields(ParsedQuery parsed)
    {
        var fields = new List<string>(2);

        void Add(string field)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (string.Equals(fields[i], field, StringComparison.Ordinal))
                    return;
            }

            fields.Add(field);
        }

        foreach (var term in parsed.ScopedTerms)
            Add(term.Field);

        foreach (var term in parsed.ProhibitedScopedTerms)
            Add(term.Field);

        foreach (var phrase in parsed.ScopedPhrases)
            Add(phrase.Field);

        foreach (var phrase in parsed.RequiredScopedPhrases)
            Add(phrase.Field);

        foreach (var phrase in parsed.ProhibitedScopedPhrases)
            Add(phrase.Field);

        foreach (var expansion in parsed.Expansions)
        {
            if (expansion.Field is { } field)
                Add(field);
        }

        return fields;
    }

    /// <summary>Whether this index has <paramref name="field"/> (ordinal, as the index names them).</summary>
    private bool HasField(string field)
    {
        foreach (string candidate in _index.Fields)
        {
            if (string.Equals(candidate, field, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// The field names the query language may read as a <c>name:</c> prefix for this search:
    /// <see cref="SearchOptions.KnownFields"/> when the caller declared one, otherwise the index's
    /// own named fields. <c>null</c> when there is no field grammar to apply — the literal path,
    /// a query with no colon (the common case, which then allocates nothing), or an index whose
    /// only field is the unnamed default.
    /// </summary>
    private IReadOnlySet<string>? KnownFieldNames(ReadOnlySpan<char> query, SearchOptions options)
    {
        if (!options.ParseQuerySyntax || query.IndexOf(':') < 0)
            return null;

        if (options.KnownFields is { } declared)
            return declared.Count > 0 ? declared : null;

        IReadOnlyCollection<string> fields = _index.Fields;

        if (fields.Count <= 1)
            return null;

        var names = new HashSet<string>(fields.Count - 1, StringComparer.Ordinal);

        foreach (string field in fields)
        {
            if (field.Length > 0)
                names.Add(field);
        }

        return names.Count == 0 ? null : names;
    }

    /// <summary>
    /// The query's terms, tokenized straight into a list sized for a query rather than for a
    /// document: the tokenizer's own list is sized for the latter and costs 184 bytes, against 120
    /// here, and this is the path that runs per search rather than per document.
    /// </summary>
    private List<string> TokenizeQueryTerms(ReadOnlySpan<char> query)
    {
        var terms = new List<string>(8);
        _tokenizer.TokenizeInto(query, terms);
        return terms;
    }

    /// <summary>
    /// The union of the unscoped scoring terms and the scoped terms' names, each once, in the
    /// order they first appear. Used for the candidate set and its reach: a scoped term's
    /// corpus-wide document frequency is an upper bound on its field's.
    /// </summary>
    private static List<string> UnionOf(
        IReadOnlyList<string> terms, IReadOnlyList<FieldScopedTerm> scopedTerms)
    {
        var union = new List<string>(terms.Count + scopedTerms.Count);
        var seen = new HashSet<string>(terms.Count + scopedTerms.Count, StringComparer.Ordinal);

        for (int i = 0; i < terms.Count; i++)
        {
            if (seen.Add(terms[i]))
                union.Add(terms[i]);
        }

        for (int i = 0; i < scopedTerms.Count; i++)
        {
            if (seen.Add(scopedTerms[i].Term))
                union.Add(scopedTerms[i].Term);
        }

        return union;
    }

    /// <summary>
    /// Resolves the literal query into concrete scoring terms: free terms gain their direct
    /// synonyms (one level, phrases stay literal, and required/prohibited terms stay literal),
    /// then prefix/fuzzy atoms expand against the index vocabulary — or fall back to their
    /// literal base term when the index cannot enumerate its vocabulary. When
    /// <paramref name="fuzzyOnlyOutOfVocabulary"/> is set, a fuzzy atom whose base term is already
    /// in the vocabulary resolves to that exact term only (no near-variant expansion), so only
    /// unknown words get corrected.
    /// </summary>
    /// <remarks>
    /// A required atom contributes its resolved terms to the score — it is a gate <i>and</i> a
    /// scoring term, exactly as the same atom without <c>+</c> — and its resolved set is returned
    /// so the presence gate can ask whether any one of them occurs. A prohibited atom contributes
    /// nothing to the score and returns only its resolved set, so the gate can ask whether any one
    /// of them occurs. The vocabulary is therefore consulted once per query, not once per
    /// candidate document.
    /// <para>
    /// A scoped atom resolves the same way, but into <see cref="ResolvedQuery.ScopedTerms"/> (or a
    /// <see cref="ScopedExpansion"/> gate), so a synonym or an expanded term never escapes the
    /// field the query asked for: scoped terms are taken literally, exactly as required and
    /// prohibited terms are.
    /// </para>
    /// </remarks>
    private ResolvedQuery ResolveQueryTerms(ParsedQuery parsed, bool fuzzyOnlyOutOfVocabulary)
    {
        if (_synonyms is null && !parsed.HasExpansions)
        {
            return new ResolvedQuery(
                parsed.AllTerms,
                parsed.ScopedTerms,
                Array.Empty<ScopedExpansion>(),
                Array.Empty<ScopedExpansion>(),
                Array.Empty<string[]>(),
                Array.Empty<string[]>());
        }

        var terms = new List<string>(parsed.AllTerms);
        var scoped = new List<FieldScopedTerm>(parsed.ScopedTerms);

        if (_synonyms is not null)
        {
            for (int i = 0; i < parsed.FreeTerms.Count; i++)
                AppendSynonyms(terms, parsed.FreeTerms[i]);
        }

        List<string[]>? required = null;
        List<string[]>? prohibited = null;
        List<ScopedExpansion>? requiredScoped = null;
        List<ScopedExpansion>? prohibitedScoped = null;

        if (parsed.HasExpansions)
        {
            var expansions = new QueryExpansionResolver(_index as IVocabularyIndex, fuzzyOnlyOutOfVocabulary);

            for (int i = 0; i < parsed.Expansions.Count; i++)
            {
                var expansion = parsed.Expansions[i];
                string[] resolved = expansions.Resolve(expansion);

                if (expansion.Field is { } field)
                {
                    var gate = new ScopedExpansion(field, resolved);

                    if (expansion.Operator == QueryTermOperator.Prohibited)
                    {
                        (prohibitedScoped ??= new List<ScopedExpansion>()).Add(gate);
                        continue;
                    }

                    for (int t = 0; t < resolved.Length; t++)
                        scoped.Add(new FieldScopedTerm(field, resolved[t]));

                    if (expansion.Operator == QueryTermOperator.Required)
                        (requiredScoped ??= new List<ScopedExpansion>()).Add(gate);

                    continue;
                }

                if (expansion.Operator == QueryTermOperator.Prohibited)
                {
                    (prohibited ??= new List<string[]>()).Add(resolved);
                    continue;
                }

                for (int t = 0; t < resolved.Length; t++)
                    terms.Add(resolved[t]);

                if (expansion.Operator == QueryTermOperator.Required)
                    (required ??= new List<string[]>()).Add(resolved);
            }
        }

        return new ResolvedQuery(
            terms,
            scoped,
            (IReadOnlyList<ScopedExpansion>?)requiredScoped ?? Array.Empty<ScopedExpansion>(),
            (IReadOnlyList<ScopedExpansion>?)prohibitedScoped ?? Array.Empty<ScopedExpansion>(),
            (IReadOnlyList<string[]>?)required ?? Array.Empty<string[]>(),
            (IReadOnlyList<string[]>?)prohibited ?? Array.Empty<string[]>());
    }

    /// <summary>Appends the free term's direct synonyms, when the map defines any.</summary>
    private void AppendSynonyms(List<string> terms, string freeTerm)
    {
        if (_synonyms!.TryGetValue(freeTerm, out var synonyms))
        {
            for (int i = 0; i < synonyms.Length; i++)
                terms.Add(synonyms[i]);
        }
    }

    /// <summary>
    /// Tokenizes every map entry with the engine tokenizer — each must yield exactly one
    /// term — and flattens the edges into a per-term synonym table (direct edges only, so
    /// expansion stays one level deep and non-transitive).
    /// </summary>
    private Dictionary<string, string[]>? ResolveSynonyms(SynonymMap? map)
    {
        if (map is null || map.IsEmpty)
            return null;

        var resolved = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (source, target) in map.OneWay)
            AddEdge(resolved, RequireSingleTerm(source), RequireSingleTerm(target));

        foreach (var group in map.Groups)
        {
            // Every member to every other member: bidirectional by construction.
            var members = new string[group.Length];
            for (int i = 0; i < group.Length; i++)
                members[i] = RequireSingleTerm(group[i]);

            for (int i = 0; i < members.Length; i++)
            {
                for (int j = 0; j < members.Length; j++)
                {
                    if (i != j)
                        AddEdge(resolved, members[i], members[j]);
                }
            }
        }

        var table = new Dictionary<string, string[]>(resolved.Count, StringComparer.Ordinal);

        foreach (var (term, targets) in resolved)
            table[term] = targets.ToArray();

        return table;
    }

    private static void AddEdge(Dictionary<string, HashSet<string>> resolved, string source, string target)
    {
        if (!resolved.TryGetValue(source, out var targets))
        {
            targets = new HashSet<string>(StringComparer.Ordinal);
            resolved[source] = targets;
        }

        targets.Add(target);
    }

    /// <summary>Tokenizes one raw map entry; it must reduce to exactly one term.</summary>
    private string RequireSingleTerm(string entry)
    {
        var terms = _tokenizer.Tokenize(entry);

        if (terms.Count != 1)
        {
            // nameof, not a literal: ParamName is meant to name a parameter of *this* method, and a
            // caller reading "synonyms" off the exception has no way to find the argument it is
            // about — the parameter is `entry`, and only nameof keeps the two from drifting.
            throw new ArgumentException(
                $"Synonym entries must tokenize to exactly one term, but '{entry}' produced {terms.Count}.",
                nameof(entry));
        }

        return terms[0];
    }

    /// <summary>
    /// Explains why a document received the score it did for a query, by delegating to the
    /// scorer's <see cref="IScoreExplainer"/> capability when it has one.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">
    /// The raw query; parsed and resolved with the same <see cref="BuildQuery"/> path as
    /// <see cref="Search(string, SearchOptions?, CancellationToken)"/> (quoted segments contribute their terms, synonyms and expansion
    /// atoms resolve against the map/vocabulary) and tokenized with the engine's tokenizer.
    /// </param>
    /// <returns>
    /// A <see cref="ScoreExplanation"/>, or <c>null</c> when the active scorer cannot explain
    /// itself (it does not implement <see cref="IScoreExplainer"/>) or the document is unknown.
    /// </returns>
    public ScoreExplanation? Explain(string documentId, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Explain(documentId, query.AsSpan());
    }

    /// <summary>
    /// Explains why a document received the score it did for a query, without materializing
    /// the query as a string.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">The raw query; parsed and resolved like <see cref="Search(ReadOnlySpan{char}, SearchOptions?, CancellationToken)"/>.</param>
    /// <returns>
    /// A <see cref="ScoreExplanation"/>, or <c>null</c> when the active scorer cannot explain
    /// itself (it does not implement <see cref="IScoreExplainer"/>) or the document is unknown.
    /// </returns>
    /// <remarks>
    /// The query is resolved with the default expansion behavior
    /// (<see cref="SearchOptions.FuzzyOnlyOutOfVocabulary"/> is <c>false</c>): explanation has
    /// no per-search options of its own, so it always states the full literal resolution.
    /// <para>
    /// A field-scoped query is <b>refused</b> rather than explained without its scoped part:
    /// <see cref="ScoreExplanation"/> has no field per contribution, so a total that omitted
    /// <c>title:term</c> would be a smaller number than the search actually ranked on.
    /// </para>
    /// </remarks>
    /// <exception cref="NotSupportedException">The query carries a field scope.</exception>
    public ScoreExplanation? Explain(string documentId, ReadOnlySpan<char> query)
    {
        if (_scorer is not IScoreExplainer explainer || !_index.Contains(documentId))
            return null;

        var prepared = BuildQuery(query, SearchOptions.Default, KnownFieldNames(query, SearchOptions.Default));

        if (prepared.Parsed is { HasFieldScopes: true })
        {
            throw new NotSupportedException(
                $"{EngineName}.Explain does not model field-scoped terms: a ScoreExplanation has no " +
                "field per contribution. Run the search and compare its scores instead.");
        }

        return explainer.Explain(documentId, prepared.Terms, _index);
    }

    /// <summary>
    /// Upper-bound estimate of the fraction of the corpus covered by the document union of
    /// the query terms (the sum of per-term document frequencies), used to decide whether
    /// scoring candidates is cheaper than scoring the whole corpus.
    /// </summary>
    /// <summary>
    /// Whether scoring the union of the query terms' documents is cheaper than scoring the corpus.
    /// </summary>
    private static bool CandidatesCoverFractionOfCorpus(int reachableDocuments, int documentCount) =>
        documentCount == 0 || (double)reachableDocuments / documentCount < 0.5;

    /// <summary>
    /// Heuristic cost for <see cref="RoutedSearchEngine"/>: the sum of the literal query terms'
    /// document frequencies, an upper bound of the candidate union. Empty requests and empty
    /// indexes cost zero. Prefix/fuzzy expansions and synonyms resolve only at search time and
    /// are deliberately not counted, so the estimate is a lower bound in their presence.
    /// </summary>
    /// <param name="query">The raw query, parsed with the engine's tokenizer.</param>
    /// <param name="options">The options the search would run with.</param>
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.IsEmpty || _index.Count == 0)
            return 0;

        var parsed = QueryParser.Parse(query, _tokenizer, KnownFieldNames(query, options));
        long total = 0;

        for (int i = 0; i < parsed.AllTerms.Count; i++)
            total += _index.DocumentFrequency(parsed.AllTerms[i]);

        // A scoped term's corpus-wide frequency is an upper bound on its field's, so counting it
        // keeps the estimate an upper bound of the candidate union rather than a lower one.
        for (int i = 0; i < parsed.ScopedTerms.Count; i++)
            total += _index.DocumentFrequency(parsed.ScopedTerms[i].Term);

        return total;
    }

    /// <summary>
    /// Per-field value counts over the documents that pass every match gate — independent of
    /// the Offset/Limit window. Built once per faceted search.
    /// </summary>
    /// <remarks>
    /// String values are counted as they arrive. Numeric values are parsed on the way in and their
    /// numbers buffered, because a histogram's boundaries are the match set's own minimum and
    /// maximum, which are only known once the pass is over. The buffer is one <see cref="double"/>
    /// per counted document per numeric facet, which is the price of deriving boundaries from the
    /// data in a single pass.
    /// </remarks>
    private sealed class FacetCollector
    {
        private readonly string[] _fields;
        private readonly Dictionary<string, int>[] _counts;
        private readonly NumericFacetRequest[] _numeric;
        private readonly List<double>[] _numericValues;
        private readonly int[] _unparseable;

        /// <summary>
        /// A collector for the requested facets, or <c>null</c> when neither kind was asked for —
        /// which is what keeps the document-reading paths free of a facet check.
        /// </summary>
        public static FacetCollector? Create(
            IReadOnlyList<string>? fields,
            IReadOnlyList<NumericFacetRequest>? numeric) =>
            fields is { Count: > 0 } || numeric is { Count: > 0 }
                ? new FacetCollector(fields, numeric)
                : null;

        private FacetCollector(
            IReadOnlyList<string>? fields,
            IReadOnlyList<NumericFacetRequest>? numeric)
        {
            _fields = DistinctFields(fields);
            _counts = new Dictionary<string, int>[_fields.Length];

            for (int i = 0; i < _fields.Length; i++)
                _counts[i] = new Dictionary<string, int>(StringComparer.Ordinal);

            var requests = new List<NumericFacetRequest>(numeric?.Count ?? 0);

            if (numeric is not null)
            {
                for (int i = 0; i < numeric.Count; i++)
                {
                    if (numeric[i] is not null)
                        requests.Add(numeric[i]);
                }
            }

            _numeric = requests.ToArray();
            _numericValues = new List<double>[_numeric.Length];
            _unparseable = new int[_numeric.Length];

            for (int i = 0; i < _numeric.Length; i++)
                _numericValues[i] = new List<double>();
        }

        private static string[] DistinctFields(IReadOnlyList<string>? fields)
        {
            if (fields is not { Count: > 0 })
                return Array.Empty<string>();

            var distinct = new List<string>(fields.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (!string.IsNullOrEmpty(field) && seen.Add(field))
                    distinct.Add(field);
            }

            return distinct.ToArray();
        }

        public void Count(SearchDocument document)
        {
            var documentFields = document.Fields;

            if (documentFields is null)
                return;

            for (int i = 0; i < _fields.Length; i++)
            {
                // A document missing the field simply does not count for it.
                if (!documentFields.TryGetValue(_fields[i], out var value) || value is null)
                    continue;

                var counts = _counts[i];
                counts.TryGetValue(value, out int current);
                counts[value] = current + 1;
            }

            for (int i = 0; i < _numeric.Length; i++)
            {
                // The same absence rule as the string facets: no field, no count — and no entry in
                // UnparseableCount either, because an absent value is not a malformed one.
                if (!documentFields.TryGetValue(_numeric[i].Field, out var value) || value is null)
                    continue;

                if (TryParseNumber(value, out double number))
                    _numericValues[i].Add(number);
                else
                    _unparseable[i]++;
            }
        }

        /// <summary>
        /// The numeric parse, deliberately the same call <see cref="MetadataFilter"/> compares with:
        /// <c>NumberStyles.Any</c> in the invariant culture. <see cref="double.NaN"/> and the
        /// infinities parse successfully and are still refused — a range has no place to put them.
        /// </summary>
        private static bool TryParseNumber(string value, out double number) =>
            double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number);

        public IReadOnlyList<FacetBucket> Build()
        {
            var buckets = new List<FacetBucket>(_fields.Length);

            for (int i = 0; i < _fields.Length; i++)
            {
                // No counted document carries this field: no bucket.
                if (_counts[i].Count == 0)
                    continue;

                var values = new List<FacetValue>(_counts[i].Count);

                foreach (var pair in _counts[i])
                    values.Add(new FacetValue(pair.Key, pair.Value));

                values.Sort(static (a, b) =>
                {
                    int byCount = b.Count.CompareTo(a.Count);
                    return byCount != 0 ? byCount : string.CompareOrdinal(a.Value, b.Value);
                });

                buckets.Add(new FacetBucket(_fields[i], values));
            }

            return buckets;
        }

        public IReadOnlyList<NumericFacetBucket> BuildNumeric()
        {
            var buckets = new List<NumericFacetBucket>(_numeric.Length);

            for (int i = 0; i < _numeric.Length; i++)
            {
                // The string facets' rule, applied to the raw values: no counted document carries
                // the field, so there is nothing to report. A field that is carried but holds no
                // parseable value is reported, with no ranges and the count of what failed to
                // parse — the difference between "nobody has this" and "nobody has a number".
                if (_numericValues[i].Count == 0 && _unparseable[i] == 0)
                    continue;

                _numericValues[i].Sort();

                // An explicit request already knows its edges, so it reports them even when nothing
                // parsed; a histogram's edges come from the values, so with none there is nothing to
                // report.
                IReadOnlyList<NumericFacetRange> ranges = _numeric[i].IsHistogram
                    ? _numericValues[i].Count == 0
                        ? Array.Empty<NumericFacetRange>()
                        : Histogram(_numericValues[i], _numeric[i].BucketCount)
                    : ExplicitRanges(_numericValues[i], _numeric[i].Boundaries!);

                buckets.Add(new NumericFacetBucket(_numeric[i].Field, ranges, _unparseable[i]));
            }

            return buckets;
        }

        private static IReadOnlyList<NumericFacetRange> ExplicitRanges(
            List<double> sorted,
            IReadOnlyList<double> boundaries)
        {
            var ranges = new NumericFacetRange[boundaries.Count - 1];
            var values = CollectionsMarshal.AsSpan(sorted);

            for (int i = 0; i + 1 < boundaries.Count; i++)
            {
                // [from, to): lower_bound(from)..lower_bound(to) is exactly the values in the range.
                int count = LowerBound(values, boundaries[i + 1]) - LowerBound(values, boundaries[i]);
                ranges[i] = new NumericFacetRange(boundaries[i], boundaries[i + 1], count);
            }

            return ranges;
        }

        private static IReadOnlyList<NumericFacetRange> Histogram(List<double> sorted, int bucketCount)
        {
            var values = CollectionsMarshal.AsSpan(sorted);
            double min = values[0];
            double max = values[^1];

            // Every value identical: one point bucket, inclusive on both sides. Dividing (max - min)
            // by the bucket count here would be a division by zero, and [x, x) would count nothing.
            if (min == max)
                return new[] { new NumericFacetRange(min, max, values.Length, UpperInclusive: true) };

            var edges = new double[bucketCount + 1];
            double width = (max - min) / bucketCount;

            edges[0] = min;

            for (int i = 1; i < bucketCount; i++)
            {
                // Clamped rather than computed straight: (max - min) / bucketCount can round such
                // that the last interior edge lands on or past max, which would report a range whose
                // From is greater than its To. Non-decreasing is all this array has to be.
                edges[i] = Math.Clamp(min + i * width, edges[i - 1], max);
            }

            edges[bucketCount] = max;

            var counts = new int[bucketCount];

            for (int i = 0; i < values.Length; i++)
            {
                // The bucket whose From is the last edge at or below the value: upper_bound - 1 is
                // exactly the [from, to) membership rule, and the clamp puts a value equal to max in
                // the last bucket, which is the one closed at the top.
                int index = Math.Clamp(UpperBound(edges, values[i]) - 1, 0, bucketCount - 1);
                counts[index]++;
            }

            var ranges = new NumericFacetRange[bucketCount];

            for (int i = 0; i < bucketCount; i++)
                ranges[i] = new NumericFacetRange(edges[i], edges[i + 1], counts[i], i == bucketCount - 1);

            return ranges;
        }

        /// <summary>Index of the first element that is not less than <paramref name="key"/>.</summary>
        private static int LowerBound(ReadOnlySpan<double> values, double key)
        {
            int low = 0;
            int high = values.Length;

            while (low < high)
            {
                int middle = low + ((high - low) >> 1);

                if (values[middle] < key)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }

        /// <summary>Index of the first element that is greater than <paramref name="key"/>.</summary>
        private static int UpperBound(ReadOnlySpan<double> values, double key)
        {
            int low = 0;
            int high = values.Length;

            while (low < high)
            {
                int middle = low + ((high - low) >> 1);

                if (values[middle] <= key)
                    low = middle + 1;
                else
                    high = middle;
            }

            return low;
        }
    }
}