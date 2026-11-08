using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Linguistics;
using System.Text;

namespace LexiSharp.Expansion;

/// <summary>
/// A decorator that implements multi-hop evidence retrieval in the style of the SAG2 architecture:
/// an initial BM25 pass, then passage-to-term-to-passage expansion, where a contextual evidence
/// scorer (JEV or one of its open, locally-runnable alternatives: jevos, Von, Decider, ...)
/// judges each candidate against the evidence chain assembled so far.
/// </summary>
/// <remarks>
/// <para>
/// <b>LexiSharp never runs a model.</b> The contextual decision model is supplied by the consumer
/// behind <see cref="IContextualEvidenceScorer"/> (or its async counterpart, adapted with
/// <see cref="SyncContextualEvidenceScorerAdapter"/>). With a model-free scorer such as
/// <see cref="Hybrid.ContextualTermOverlapScorer"/> the decorator still runs end to end and
/// exercises the loop, but it cannot recognise "bridge" passages whose value only appears once an
/// intermediate fact has been retrieved — that requires a real decision model.
/// </para>
/// <para>
/// The inner engine is expected to be a lexical BM25 engine (in-memory or Postgres). Corpus
/// statistics for term rarity are read from the supplied index when present; when it is
/// <c>null</c> the rarity factor is neutral (1), which keeps the decorator composable with any
/// engine but weakens term selection.
/// </para>
/// <para>
/// This is a reference implementation of the published loop. It simplifies two details of the
/// reference architecture: multi-word name phrases are not extracted (only ordinary tokens), and
/// the final determinantal diversity reordering is approximated by the existing MMR-free
/// score ordering. Both are documented limitations, not silent behaviour.
/// </para>
/// </remarks>
public sealed class ContextualExpansionSearchEngine : ITextSearchEngine
{
    private readonly ITextSearchEngine _inner;
    private readonly IContextualEvidenceScorer _scorer;
    private readonly IReadOnlyTextIndex? _index;
    private readonly ITokenizer _tokenizer;

    private readonly int _b;
    private readonly int _s;
    private readonly int _l;
    private readonly int _p;
    private readonly int _h;

    /// <param name="inner">The lexical engine that performs BM25 retrieval (and expansion queries).</param>
    /// <param name="scorer">The contextual decision model behind the seam.</param>
    /// <param name="index">Optional corpus index providing document frequencies for term rarity. May be null.</param>
    /// <param name="tokenizer">Tokenizer used to split text; defaults to <see cref="Tokenizer.Default"/>.</param>
    /// <param name="candidateLimit">Initial / expansion candidate limit B (default 100).</param>
    /// <param name="seedsPerRound">New seeds per round s (default 10).</param>
    /// <param name="expansionTerms">Expansion terms per round L (default 20).</param>
    /// <param name="protectedInitial">Protected initial passages p (default 5).</param>
    /// <param name="additionalRounds">Additional expansion rounds H (default 2).</param>
    public ContextualExpansionSearchEngine(
        ITextSearchEngine inner,
        IContextualEvidenceScorer scorer,
        IReadOnlyTextIndex? index = null,
        ITokenizer? tokenizer = null,
        int candidateLimit = 100,
        int seedsPerRound = 10,
        int expansionTerms = 20,
        int protectedInitial = 5,
        int additionalRounds = 2)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(scorer);

        if (candidateLimit <= 0) throw new ArgumentOutOfRangeException(nameof(candidateLimit));
        if (seedsPerRound <= 0) throw new ArgumentOutOfRangeException(nameof(seedsPerRound));
        if (expansionTerms <= 0) throw new ArgumentOutOfRangeException(nameof(expansionTerms));
        if (protectedInitial < 0) throw new ArgumentOutOfRangeException(nameof(protectedInitial));
        if (additionalRounds < 0) throw new ArgumentOutOfRangeException(nameof(additionalRounds));

        _inner = inner;
        _scorer = scorer;
        _index = index;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _b = candidateLimit;
        _s = seedsPerRound;
        _l = expansionTerms;
        _p = protectedInitial;
        _h = additionalRounds;
    }

    /// <summary>The contextual decision model this engine scores with.</summary>
    public IContextualEvidenceScorer Scorer => _scorer;

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
        var ranked = Run(query, cancellationToken);
        int limit = options is not null && options.Limit > 0 ? options.Limit : ranked.Count;
        if (limit < ranked.Count)
            ranked = ranked.Take(limit).ToList();
        return ranked;
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null, CancellationToken cancellationToken = default) =>
        Search(query.ToString(), options, cancellationToken);

    private List<SearchResult> Run(string query, CancellationToken ct)
    {
        int n = _index is not null ? _index.StatisticDocumentCount : 0;
        if (n <= 0) n = 1;

        var queryTerms = new HashSet<string>(_tokenizer.Tokenize(query), StringComparer.Ordinal);

        // Round 0
        var c0 = InnerSearch(query, _b, ct);
        if (c0.Count == 0)
            return new List<SearchResult>();

        var p = c0.Take(_p).ToList();
        var history = new Dictionary<string, double>(StringComparer.Ordinal); // id -> latest score
        var visited = new HashSet<string>(StringComparer.Ordinal); // identifiers scored
        var expanded = new HashSet<string>(StringComparer.Ordinal); // expanded seeds

        // rank map: id -> retrieval rank (1-based) in the list it entered through
        var rankOf = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < c0.Count; i++)
            if (!rankOf.ContainsKey(c0[i].DocumentId))
                rankOf[c0[i].DocumentId] = i + 1;

        var u0 = Dedup(c0);
        var w0 = u0;
        var context0 = Array.Empty<SearchResult>();
        var r0 = ScorePool(w0, query, context0, rankOf, history, ct);
        visited.UnionWith(w0.Select(x => x.DocumentId));

        // current ranking
        List<SearchResult> current = r0;
        // accumulated seeds
        var accumulatedSeeds = new List<SearchResult>();

        for (int round = 1; round <= _h; round++)
        {
            ct.ThrowIfCancellationRequested();

            // select seeds: first _s of current not in expanded
            var seeds = current
                .Where(x => !expanded.Contains(x.DocumentId))
                .Take(_s)
                .ToList();

            if (seeds.Count == 0)
                break;

            // expansion terms
            var terms = SelectTerms(seeds, queryTerms, n, ct);
            if (terms.Count == 0)
                break;

            expanded.UnionWith(seeds.Select(x => x.DocumentId));
            accumulatedSeeds.AddRange(seeds);
            // dedup accumulated
            var accUnique = accumulatedSeeds
                .GroupBy(x => x.DocumentId, StringComparer.Ordinal)
                .Select(g => g.First())
                .ToList();
            accumulatedSeeds = accUnique;

            // retrieve expansion candidates
            var cNew = ExpandRetrieve(terms, query, rankOf, ct);
            if (cNew.Count == 0)
                break;

            // pool U
            List<SearchResult> u = Dedup(p.Concat(accumulatedSeeds).Concat(cNew).ToList());

            // round 2 (last) history substitution: keep at most _b scored; if r==_h (last), apply
            List<SearchResult> w;
            IReadOnlyList<SearchResult> ctx;

            bool isLast = round == _h;
            if (isLast)
            {
                // context: previous ranking top 10 with tentative prior scores
                ctx = current.Take(10).ToList();
                // W: first 20 unseen of U not in history, then old scored candidates by latest score
                var unseen = u.Where(x => !visited.Contains(x.DocumentId)).Take(20).ToList();
                var old = u.Where(x => visited.Contains(x.DocumentId))
                    .OrderByDescending(x => history.TryGetValue(x.DocumentId, out var sc) ? sc : 0)
                    .ToList();
                var combined = old.Concat(unseen).ToList();
                // dedup
                var seenIds = new HashSet<string>(StringComparer.Ordinal);
                var deduped = new List<SearchResult>();
                foreach (var x in combined)
                {
                    if (seenIds.Add(x.DocumentId))
                        deduped.Add(x);
                }
                w = deduped.Take(_b + 25).ToList(); // allow protected; final trim later
                // Actually pool size bound ~125; trim to _b+25
                if (w.Count > _b + 25) w = w.Take(_b + 25).ToList();
            }
            else
            {
                ctx = accumulatedSeeds
                    .Concat(cNew.Take(10))
                    .GroupBy(x => x.DocumentId, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .Take(10 + accumulatedSeeds.Count)
                    .ToList();
                // E1 = A1 ∪ First_10(C1)
                ctx = accumulatedSeeds
                    .Concat(cNew.Take(10))
                    .GroupBy(x => x.DocumentId, StringComparer.Ordinal)
                    .Select(g => g.First())
                    .ToList();
                w = Dedup(u);
            }

            // trim w to reasonable bound
            if (w.Count > _b + 25) w = w.Take(_b + 25).ToList();

            var scored = ScorePool(w, query, ctx, rankOf, history, ct);
            visited.UnionWith(w.Select(x => x.DocumentId));
            current = scored;
        }

        // final: take first B
        var final = current.Take(_b).ToList();
        return final;
    }

    private List<SearchResult> InnerSearch(string q, int n, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var opts = new SearchOptions(Limit: n);
        return _inner.Search(q, opts, ct).ToList();
    }

    private List<SearchResult> Dedup(IEnumerable<SearchResult> items)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<SearchResult>();
        foreach (var x in items)
        {
            if (seen.Add(x.DocumentId))
                result.Add(x);
        }
        return result;
    }

    private List<SearchResult> ScorePool(
        List<SearchResult> pool,
        string query,
        IReadOnlyList<SearchResult> context,
        Dictionary<string, int> rankOf,
        Dictionary<string, double> history,
        CancellationToken ct)
    {
        // score each
        var scored = new List<(SearchResult item, double score)>(pool.Count);
        foreach (var item in pool)
        {
            ct.ThrowIfCancellationRequested();
            double s;
            try
            {
                s = _scorer.Score(query, item.Document, context);
            }
            catch
            {
                s = 0;
            }
            if (double.IsNaN(s) || double.IsInfinity(s)) s = 0;
            if (s < 0) s = 0;
            if (s > 1) s = 1;
            history[item.DocumentId] = s;
            scored.Add((item, s));
        }
        // sort: desc score, desc 1/(60+rank), asc id
        scored.Sort((a, b) =>
        {
            int c = b.score.CompareTo(a.score);
            if (c != 0) return c;
            int ra = rankOf.TryGetValue(a.item.DocumentId, out var r1) ? r1 : int.MaxValue;
            int rb = rankOf.TryGetValue(b.item.DocumentId, out var r2) ? r2 : int.MaxValue;
            double ta = 1.0 / (60.0 + ra);
            double tb = 1.0 / (60.0 + rb);
            int c2 = tb.CompareTo(ta);
            if (c2 != 0) return c2;
            return string.CompareOrdinal(a.item.DocumentId, b.item.DocumentId);
        });
        // rebuild SearchResult with new score
        var result = new List<SearchResult>(scored.Count);
        foreach (var (item, sc) in scored)
            result.Add(new SearchResult(item.DocumentId, sc, item.Document));
        return result;
    }

    private List<string> SelectTerms(
        List<SearchResult> seeds,
        HashSet<string> queryTerms,
        int n,
        CancellationToken ct)
    {
        // term -> max priority across seeds
        var priorities = new Dictionary<string, double>(StringComparer.Ordinal);

        // compute m_r
        double mR = 0;
        foreach (var seed in seeds)
            if (seed.Score > mR) mR = seed.Score;
        if (mR <= 0) mR = 1; // avoid; use formula with 0.5 base handled

        foreach (var seed in seeds)
        {
            double seedWeight = mR > 0 ? (0.5 + 0.5 * seed.Score / mR) : 0.5;
            if (mR == 0) seedWeight = 0.5;

            var text = seed.Document.Text;
            var sentences = SplitSentences(text);
            // build per-sentence expansion units
            // For simplicity: tokens of whole text as terms; sentence query overlap = sum I(v) over distinct tokens shared with query in that sentence
            foreach (var sentence in sentences)
            {
                var sentTokens = new List<string>(_tokenizer.Tokenize(sentence));
                var sentDistinct = new HashSet<string>(sentTokens, StringComparer.Ordinal);
                // query connection for this sentence
                double queryConn = 0;
                foreach (var v in sentDistinct)
                {
                    if (queryTerms.Contains(v))
                        queryConn += Idf(v, n);
                }
                // expansion units: tokens not in names; here just tokens
                foreach (var t in sentDistinct)
                {
                    if (queryTerms.Contains(t)) continue; // exclude original query terms
                    int df = DocFreq(t);
                    if (_index is not null && df <= 0) continue; // zero df excluded (only when stats available)
                    // tf counts in whole doc's B(u); approximate by sentence count
                    // We'll accumulate tf per (t, seed) below; here per-sentence. Use a simpler approach: count tf per seed.
                }
            }

            // tf per term in seed
            var tf = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var sentence in sentences)
            {
                foreach (var t in _tokenizer.Tokenize(sentence))
                {
                    if (queryTerms.Contains(t)) continue;
                    if (DocFreq(t) <= 0) continue;
                    tf[t] = tf.GetValueOrDefault(t) + 1;
                }
            }

            foreach (var kv in tf)
            {
                var t = kv.Key;
                int tfv = kv.Value;
                int df = DocFreq(t);
                double idf = Idf(t, n);
                // g_q: max over sentences containing t of sum I(v) for v in sentence ∩ query
                double gq = 0;
                foreach (var sentence in sentences)
                {
                    var st = new HashSet<string>(_tokenizer.Tokenize(sentence), StringComparer.Ordinal);
                    if (!st.Contains(t)) continue;
                    double conn = 0;
                    foreach (var v in st)
                        if (queryTerms.Contains(v))
                            conn += Idf(v, n);
                    if (conn > gq) gq = conn;
                }
                double priority = (1 + Math.Log(tfv)) * idf * (1 + gq) * seedWeight;
                if (!priorities.TryGetValue(t, out var cur) || priority > cur)
                    priorities[t] = priority;
            }
        }

        // select top L by priority desc, tie-break lexical asc
        var selected = priorities
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(_l)
            .Select(kv => kv.Key)
            .ToList();
        return selected;
    }

    private List<SearchResult> ExpandRetrieve(
        List<string> terms,
        string query,
        Dictionary<string, int> rankOf,
        CancellationToken ct)
    {
        // For each term, inner.Search(term, B); collect unseen; interleave by rank in term-priority order
        var perTermLists = new List<List<SearchResult>>(terms.Count);
        foreach (var term in terms)
        {
            ct.ThrowIfCancellationRequested();
            var list = InnerSearch(term, _b, ct);
            // filter to those matching the term (they do by search) and unseen? collect all; dedup later
            perTermLists.Add(list);
        }

        var collected = new List<SearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // interleave by rank position across terms in priority order
        int maxLen = perTermLists.Count == 0 ? 0 : perTermLists.Max(x => x.Count);
        for (int pos = 0; pos < maxLen && collected.Count < _b; pos++)
        {
            foreach (var list in perTermLists)
            {
                if (pos >= list.Count) continue;
                var item = list[pos];
                if (seen.Add(item.DocumentId))
                {
                    if (!rankOf.ContainsKey(item.DocumentId))
                        rankOf[item.DocumentId] = collected.Count + 1; // approximate rank
                    collected.Add(item);
                    if (collected.Count >= _b) break;
                }
            }
        }
        return collected;
    }

    private double Idf(string term, int n)
    {
        if (_index is null) return 1.0; // neutral rarity when no corpus stats
        int df = DocFreq(term);
        if (df <= 0) return 0;
        return Math.Log(1.0 + (n - df + 0.5) / (df + 0.5));
    }

    private int DocFreq(string term)
    {
        if (_index is not null)
            return _index.DocumentFrequency(term);
        return 0;
    }

    private static List<string> SplitSentences(string text)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            sb.Append(ch);
            if (ch == '.' || ch == '!' || ch == '?' || ch == '\n')
            {
                var s = sb.ToString().Trim();
                if (s.Length > 0) result.Add(s);
                sb.Clear();
            }
        }
        var tail = sb.ToString().Trim();
        if (tail.Length > 0) result.Add(tail);
        if (result.Count == 0 && text.Trim().Length > 0) result.Add(text.Trim());
        return result;
    }
}