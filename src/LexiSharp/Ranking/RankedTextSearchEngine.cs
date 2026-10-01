using LexiSharp.Core;
using LexiSharp.Linguistics;
using LexiSharp.Similarity;

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
/// An optional <see cref="SynonymMap"/> additionally rewrites <b>free</b> query terms to
/// their direct synonyms — one-way edges via <c>Add</c>, bidirectional groups via
/// <c>AddEquivalent</c> — at one level only (never synonyms of synonyms) and never inside
/// quoted phrases. Entries are tokenized with the engine's tokenizer at construction; each
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
    /// Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks, so a
    /// dashboard can attribute a measurement to a specific engine.
    /// </summary>
    public const string EngineName = "RankedTextSearchEngine";

    /// <inheritdoc />
    public QueryFeature SupportedQueryFeatures => QueryFeature.Phrases | QueryFeature.Expansions;

    private readonly ITextIndex _index;
    private readonly ITextScorer _scorer;
    private readonly ITokenizer _tokenizer;
    private readonly Dictionary<string, string[]>? _synonyms;
    private readonly RetrievalTelemetry _telemetry;

    /// <param name="index">The corpus index backing the engine.</param>
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
        ITextIndex index,
        ITextScorer scorer,
        ITokenizer? tokenizer = null,
        SynonymMap? synonyms = null,
        RetrievalTelemetry? telemetry = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(scorer);

        _index = index;
        _scorer = scorer;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _synonyms = ResolveSynonyms(synonyms);
        _telemetry = telemetry ?? RetrievalTelemetry.None;
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _index.Index(documents);
        _telemetry.IndexChanged(EngineName, _index);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _index.Add(document);
        _telemetry.IndexChanged(EngineName, _index);
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        bool removed = _index.Remove(documentId);
        _telemetry.IndexChanged(EngineName, _index);
        return removed;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _index.Clear();
        _telemetry.IndexChanged(EngineName, _index);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return Search(query.AsSpan(), options);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null) =>
        RunQuery(query, options ?? SearchOptions.Default, null);

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
        var collector = facetFields is { Count: > 0 } ? new FacetCollector(facetFields) : null;
        var results = RunQuery(query, options ?? SearchOptions.Default, collector);

        return new FacetedSearchResult(
            results,
            collector is null ? Array.Empty<FacetBucket>() : collector.Build());
    }

    /// <summary>
    /// One pass over the candidate documents: parse/resolve the query once, score and gate
    /// every candidate, optionally count facet values for the documents that match
    /// (<paramref name="facets"/>), and cut the requested page from the bounded top window.
    /// </summary>
    private IReadOnlyList<SearchResult> RunQuery(ReadOnlySpan<char> query, SearchOptions options, FacetCollector? facets)
    {
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

        var (parsed, queryTerms) = BuildQuery(query, options);

        if (queryTerms.Count == 0)
        {
            if (instrumented)
                _telemetry.Warning(EngineName, "query produced no searchable term: nothing to match against the vocabulary");

            return Array.Empty<SearchResult>();
        }

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
        var plan = _scorer is IQueryPlannableScorer plannable
            ? plannable.CreatePlan(queryTerms, _index)
            : null;

        // Bounded top-Window accumulation, worst-first, reproducing the exact semantics of
        // OrderByDescending(Score).Skip(offset).Take(limit) with equal scores ordered by document id.
        // SearchResult objects are materialized only for the kept entries.
        int window = options.Window;
        var top = new TopRankedWindow(window, options.TieBreak);
        long ordinal = 0;

        // One dictionary lookup per query term, feeding both of the decisions below. They are two
        // readings of the same quantity — how much of the corpus this query can reach — so it is
        // asked once rather than once per decision.
        int reachableDocuments = SumDocumentFrequencies(_index, distinctQueryTerms);

        // The term-at-a-time pass, when the plan and the index can both offer it. It subsumes the
        // candidate-or-scan choice below: the posting walks produce the matched set themselves, so
        // there is no union to build and no corpus to walk when the query terms are rare — and it
        // does not have to be re-made per query shape. That choice stays as the fallback for
        // everything this pass declines.
        if (!TryRunAccumulatingQuery(plan, reachableDocuments, parsed, options, facets, top, out ordinal))
        {
            // Convention: a score of exactly 0 means "not a match".
            // When the index can enumerate the documents sharing at least one query term
            // (ICandidateIndex) and the scorer provably returns 0 for every document that
            // shares none (ITermOverlapScorer), score only those candidates: same results,
            // same order, far fewer distance computations. Otherwise fall back to the full scan.
            var candidateDocuments =
                _index is ICandidateIndex candidateIndex &&
                _scorer is ITermOverlapScorer &&
                CandidatesCoverFractionOfCorpus(reachableDocuments, _index.Count)
                    ? Candidates(candidateIndex, distinctQueryTerms)
                    : _index.Documents;

            foreach (var document in candidateDocuments)
            {
                // Structured filters gate the corpus before any relevance math is paid for.
                if (!options.PassesFilters(document))
                    continue;

                // Quoted segments are a hard positional gate: every phrase must appear at
                // consecutive positions, checked before any relevance math is paid.
                if (parsed.HasPhrases && !MatchesPhrases(document.Id, parsed.Phrases))
                    continue;

                double score = plan is null
                    ? _scorer.Score(document.Id, distinctQueryTerms, _index)
                    : plan.Score(document.Id);

                if (double.IsNaN(score) || double.IsInfinity(score) || score == 0 || score < options.MinimumScore)
                    continue;

                facets?.Count(document);

                top.Add(score, document, ordinal);
                ordinal++;
            }
        }

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
    /// returning <c>false</c> — having changed nothing but a rented buffer — when the query has
    /// to go the per-document way instead.
    /// </summary>
    /// <remarks>
    /// The gates this path refuses are the two that are cheaper before scoring than after:
    /// a metadata filter and a phrase both reject documents, and both do it by walking positions
    /// or fields, so a selective filter over a large corpus would have most of its score
    /// arithmetic thrown away. The term-at-a-time pass has already paid for the arithmetic by the
    /// time it can apply either, which is the opposite of what those requests want. Everything
    /// else — the rare-term and head-term regimes alike — is a win, because the pass replaces
    /// <c>2 · terms · documents</c> string hashes with one walk of the posting entries that
    /// actually exist.
    /// <para>
    /// The matched set arrives in posting order rather than corpus order. Nothing downstream
    /// depends on the difference: the gates are per document, the facet counts are increments, and
    /// <see cref="TopRankedWindow"/> breaks ties on the document id rather than on arrival order.
    /// </para>
    /// </remarks>
    private bool TryRunAccumulatingQuery(
        ISearchQueryPlan? plan,
        int reachableDocuments,
        ParsedQuery parsed,
        SearchOptions options,
        FacetCollector? facets,
        TopRankedWindow top,
        out long ordinal)
    {
        ordinal = 0;

        if (plan is not IAccumulatingQueryPlan accumulating ||
            _index is not IAccumulatingIndex index ||
            _scorer is not ITermOverlapScorer ||
            parsed.HasPhrases ||
            options.Filters is { Count: > 0 })
        {
            return false;
        }

        // The pass clears a buffer sized to the corpus before it scores anything, so a query too
        // small to amortize that is better served by the per-document loop. See the constants.
        int minimumEntries = Math.Max(
            MinimumPostingEntriesForAccumulation,
            index.OrdinalSpace / AccumulationCorpusDivisor);

        if (reachableDocuments < minimumEntries)
            return false;

        var accumulator = ScoreAccumulator.Rent(index.OrdinalSpace);

        try
        {
            if (!accumulating.TryAccumulate(index, accumulator))
                return false;

            // The id exclusion, hoisted out of the loop for the same reason it is the first line of
            // PassesFilters: the overwhelmingly common case is a null set, so what the per-candidate
            // loop must pay for it is one null test. This pass did not pay even that.
            //
            // It did not apply the exclusion at all, and the loop below does not call
            // PassesFilters, which is where the exclusion lives — so on any query that takes this
            // path, an excluded document was ranked and returned. The path is declined for small
            // corpora and for queries that touch few documents, which is exactly why the six tests
            // for this option all passed: their fixture is three documents of identical text, so
            // every one of them runs the fallback and never reaches here. ArguAna is the opposite
            // case — 8,674 documents and 645-word queries — and the published measurement for that
            // corpus is produced with the query's own document dropped, so the comparison was made
            // against a ranking that did not drop it.
            var excluded = options.ExcludedDocumentIds;

            for (int i = 0; i < accumulator.Count; i++)
            {
                int candidate = accumulator.OrdinalAt(i);
                var document = index.DocumentAt(candidate)!;
                // The plan's last step, because this path never calls Score and a score that is narrowed
                // at the end of the sum has to be narrowed somewhere.
                double score = accumulating.Finalise(accumulator[candidate]);

                if (double.IsNaN(score) || double.IsInfinity(score) || score == 0 || score < options.MinimumScore)
                    continue;

                if (excluded is not null && excluded.Count > 0 && excluded.Contains(document.Id))
                    continue;

                facets?.Count(document);

                top.Add(score, document, ordinal);
                ordinal++;
            }

            return true;
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
    /// Sum of the query terms' document frequencies: an upper bound on the union of the candidate
    /// documents, and the unit both the accumulation threshold and the candidate-or-scan choice
    /// below are written in.
    /// </summary>
    private static int SumDocumentFrequencies(ITextIndex index, IReadOnlyList<string> queryTerms)
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
    /// Whether the phrase's terms sit at positions <c>p, p+1, …</c> for some occurrence of
    /// its first term. Position lists come straight from the index (sorted, no allocation).
    /// </summary>
    private bool MatchesPhrase(string documentId, IReadOnlyList<string> phrase)
    {
        var anchors = _index.GetTermPositions(documentId, phrase[0]);

        for (int a = 0; a < anchors.Count; a++)
        {
            int start = anchors[a];
            bool consecutive = true;

            for (int i = 1; i < phrase.Count; i++)
            {
                if (!ContainsPosition(_index.GetTermPositions(documentId, phrase[i]), start + i))
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
    /// Shared by <see cref="Search(string, SearchOptions)"/> and <see cref="Explain(string, string)"/>: parse the raw query, then
    /// resolve synonyms and vocabulary expansions into the concrete scoring terms. The
    /// parsed form still carries the literal phrase constraints for the positional gate.
    /// </summary>
    private (ParsedQuery Parsed, IReadOnlyList<string> Terms) BuildQuery(ReadOnlySpan<char> query, SearchOptions options)
    {
        // The literal path skips the query language entirely, so the terms are the tokenizer's and a
        // quotation mark is a separator like any other. Everything downstream reads the same shape.
        if (!options.ParseQuerySyntax)
        {
            var literal = _tokenizer.Tokenize(query);
            return (new ParsedQuery(
                literal,
                Array.Empty<IReadOnlyList<string>>(),
                literal,
                Array.Empty<QueryExpansion>()), literal);
        }

        var parsed = QueryParser.Parse(query, _tokenizer);
        return (parsed, ResolveQueryTerms(parsed, options.FuzzyOnlyOutOfVocabulary));
    }

    /// <summary>
    /// Resolves the literal query into concrete scoring terms: free terms gain their direct
    /// synonyms (one level, phrases stay literal), then prefix/fuzzy atoms expand against the
    /// index vocabulary — or fall back to their literal base term when the index cannot
    /// enumerate its vocabulary. When <paramref name="fuzzyOnlyOutOfVocabulary"/> is set, a
    /// fuzzy atom whose base term is already in the vocabulary resolves to that exact term
    /// only (no near-variant expansion), so only unknown words get corrected.
    /// </summary>
    private IReadOnlyList<string> ResolveQueryTerms(ParsedQuery parsed, bool fuzzyOnlyOutOfVocabulary)
    {
        if (_synonyms is null && !parsed.HasExpansions)
            return parsed.AllTerms;

        var terms = new List<string>(parsed.AllTerms);

        if (_synonyms is not null)
        {
            for (int i = 0; i < parsed.FreeTerms.Count; i++)
                AppendSynonyms(terms, parsed.FreeTerms[i]);
        }

        if (parsed.HasExpansions)
        {
            var vocabulary = _index as IVocabularyIndex;

            for (int i = 0; i < parsed.Expansions.Count; i++)
            {
                var expansion = parsed.Expansions[i];

                if (vocabulary is null)
                {
                    terms.Add(expansion.BaseTerm);
                    continue;
                }

                if (expansion.Kind == QueryExpansionKind.Prefix)
                    ExpandPrefix(terms, vocabulary, expansion);
                else if (fuzzyOnlyOutOfVocabulary && _index.DocumentFrequency(expansion.BaseTerm) > 0)
                    terms.Add(expansion.BaseTerm);
                else
                    ExpandFuzzy(terms, vocabulary, expansion);
            }
        }

        return terms;
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
    /// Appends every vocabulary term starting with the base (ordinal), highest document
    /// frequency first then ordinal order, capped at <see cref="MaxExpansionsPerAtom"/>.
    /// </summary>
    private static void ExpandPrefix(List<string> terms, IVocabularyIndex index, QueryExpansion expansion)
    {
        var matches = new List<string>();

        foreach (var term in index.Vocabulary)
        {
            if (term.StartsWith(expansion.BaseTerm, StringComparison.Ordinal))
                matches.Add(term);
        }

        matches.Sort((a, b) =>
        {
            int byFrequency = index.DocumentFrequency(b).CompareTo(index.DocumentFrequency(a));
            return byFrequency != 0 ? byFrequency : string.CompareOrdinal(a, b);
        });

        int count = Math.Min(matches.Count, MaxExpansionsPerAtom);

        for (int i = 0; i < count; i++)
            terms.Add(matches[i]);
    }

    /// <summary>
    /// Appends every vocabulary term within <see cref="QueryExpansion.MaxEdits"/> Levenshtein
    /// edits of the base — closest first, then highest document frequency, then ordinal —
    /// capped at <see cref="MaxExpansionsPerAtom"/>.
    /// </summary>
    private static void ExpandFuzzy(List<string> terms, IVocabularyIndex index, QueryExpansion expansion)
    {
        var candidates = new List<(string Term, int Distance)>();

        foreach (var term in index.Vocabulary)
        {
            // Cheap length gate before the O(m·n) distance: |len difference| ≤ budget.
            if (Math.Abs(term.Length - expansion.BaseTerm.Length) > expansion.MaxEdits)
                continue;

            int distance = LevenshteinDistance.Distance(expansion.BaseTerm, term);

            if (distance <= expansion.MaxEdits)
                candidates.Add((term, distance));
        }

        candidates.Sort((a, b) =>
        {
            int byDistance = a.Distance.CompareTo(b.Distance);
            if (byDistance != 0)
                return byDistance;

            int byFrequency = index.DocumentFrequency(b.Term).CompareTo(index.DocumentFrequency(a.Term));
            return byFrequency != 0 ? byFrequency : string.CompareOrdinal(a.Term, b.Term);
        });

        int count = Math.Min(candidates.Count, MaxExpansionsPerAtom);

        for (int i = 0; i < count; i++)
            terms.Add(candidates[i].Term);
    }

    /// <summary>
    /// Explains why a document received the score it did for a query, by delegating to the
    /// scorer's <see cref="IScoreExplainer"/> capability when it has one.
    /// </summary>
    /// <param name="documentId">Id of the document to explain.</param>
    /// <param name="query">
    /// The raw query; parsed and resolved with the same <see cref="BuildQuery"/> path as
    /// <see cref="Search(string, SearchOptions)"/> (quoted segments contribute their terms, synonyms and expansion
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
    /// <param name="query">The raw query; parsed and resolved like <see cref="Search(ReadOnlySpan{char}, SearchOptions?)"/>.</param>
    /// <returns>
    /// A <see cref="ScoreExplanation"/>, or <c>null</c> when the active scorer cannot explain
    /// itself (it does not implement <see cref="IScoreExplainer"/>) or the document is unknown.
    /// </returns>
    /// <remarks>
    /// The query is resolved with the default expansion behavior
    /// (<see cref="SearchOptions.FuzzyOnlyOutOfVocabulary"/> is <c>false</c>): explanation has
    /// no per-search options of its own, so it always states the full literal resolution.
    /// </remarks>
    public ScoreExplanation? Explain(string documentId, ReadOnlySpan<char> query)
    {
        if (_scorer is not IScoreExplainer explainer || !_index.Contains(documentId))
            return null;

        var (_, terms) = BuildQuery(query, SearchOptions.Default);
        return explainer.Explain(documentId, terms, _index);
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

        var parsed = QueryParser.Parse(query, _tokenizer);
        long total = 0;

        for (int i = 0; i < parsed.AllTerms.Count; i++)
            total += _index.DocumentFrequency(parsed.AllTerms[i]);

        return total;
    }

    /// <summary>
    /// Per-field value counts over the documents that pass every match gate — independent of
    /// the Offset/Limit window. Built once per <see cref="SearchWithFacets(string, SearchOptions, IReadOnlyList{string})"/> call.
    /// </summary>
    private sealed class FacetCollector
    {
        private readonly string[] _fields;
        private readonly Dictionary<string, int>[] _counts;

        public FacetCollector(IReadOnlyList<string> fields)
        {
            var distinct = new List<string>(fields.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < fields.Count; i++)
            {
                var field = fields[i];
                if (!string.IsNullOrEmpty(field) && seen.Add(field))
                    distinct.Add(field);
            }

            _fields = distinct.ToArray();
            _counts = new Dictionary<string, int>[_fields.Length];

            for (int i = 0; i < _fields.Length; i++)
                _counts[i] = new Dictionary<string, int>(StringComparer.Ordinal);
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
        }

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
    }
}