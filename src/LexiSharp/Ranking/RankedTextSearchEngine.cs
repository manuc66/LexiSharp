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
/// falls back to the atom's literal base term.
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

    /// <inheritdoc />
    public QueryFeature SupportedQueryFeatures => QueryFeature.Phrases | QueryFeature.Expansions;

    private readonly ITextIndex _index;
    private readonly ITextScorer _scorer;
    private readonly ITokenizer _tokenizer;
    private readonly Dictionary<string, string[]>? _synonyms;

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
    /// <exception cref="ArgumentException">
    /// A <paramref name="synonyms"/> entry tokenizes to zero or more than one term.
    /// </exception>
    public RankedTextSearchEngine(
        ITextIndex index,
        ITextScorer scorer,
        ITokenizer? tokenizer = null,
        SynonymMap? synonyms = null)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(scorer);

        _index = index;
        _scorer = scorer;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _synonyms = ResolveSynonyms(synonyms);
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
        if (options.IsEmpty)
            return Array.Empty<SearchResult>();

        if (_index.Count == 0)
            return Array.Empty<SearchResult>();

        var (parsed, queryTerms) = BuildQuery(query);

        if (queryTerms.Count == 0)
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

        // Bounded top-Window accumulation, worst-first, reproducing the exact semantics of
        // OrderByDescending(Score).Skip(offset).Take(limit): ties keep their enumeration order.
        // SearchResult objects are materialized only for the kept entries.
        int window = options.Window;
        var top = new List<(double Score, SearchDocument Document, long Ordinal)>(Math.Min(window, 1024));
        long ordinal = 0;

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

            // Facets count every document that passed all gates — the whole match set,
            // independent of the Offset/Limit window cut below.
            facets?.Count(document);

            InsertRanked(top, window, (score, document, ordinal++));
        }

        // The accumulated window holds at most Offset + Limit entries; skip the Offset prefix
        // of the best-first view to cut the requested page.
        int skip = Math.Min(options.Offset, top.Count);
        int count = top.Count - skip;
        var results = new SearchResult[count];

        for (int i = 0; i < count; i++)
        {
            var entry = top[top.Count - 1 - (skip + i)];
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
        // Equal scores are a tie-break against Ordinal, not a float-equality check on a computed
        // value; an epsilon comparison here would silently reorder identical-ranked documents.
        => lower.Score < higher.Score || (lower.Score == higher.Score && lower.Ordinal > higher.Ordinal); // NOSONAR:S1244

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
    private (ParsedQuery Parsed, IReadOnlyList<string> Terms) BuildQuery(ReadOnlySpan<char> query)
    {
        var parsed = QueryParser.Parse(query, _tokenizer);
        return (parsed, ResolveQueryTerms(parsed));
    }

    /// <summary>
    /// Resolves the literal query into concrete scoring terms: free terms gain their direct
    /// synonyms (one level, phrases stay literal), then prefix/fuzzy atoms expand against the
    /// index vocabulary — or fall back to their literal base term when the index cannot
    /// enumerate its vocabulary.
    /// </summary>
    private IReadOnlyList<string> ResolveQueryTerms(ParsedQuery parsed)
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
            throw new ArgumentException(
                $"Synonym entries must tokenize to exactly one term, but '{entry}' produced {terms.Count}.",
                "synonyms");
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
    public ScoreExplanation? Explain(string documentId, ReadOnlySpan<char> query)
    {
        if (_scorer is not IScoreExplainer explainer || !_index.Contains(documentId))
            return null;

        var (_, terms) = BuildQuery(query);
        return explainer.Explain(documentId, terms, _index);
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