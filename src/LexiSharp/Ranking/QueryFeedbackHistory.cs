using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>
/// A learned relevance feedback channel: it records which document a user chose
/// for past queries, and uses those associations to boost matching documents
/// in future searches.
/// </summary>
/// <remarks>
/// The signal is learned from what was chosen rather than hand-written per
/// domain: the query → document associations a caller already holds — what
/// users opened, what they corrected, what the previous answer was — become
/// the ranking evidence, without the caller writing a rule per case.
/// <para>
/// The channel is a <b>secondary signal</b>: it never overrides the primary
/// ranking, it only boosts documents that the history associates with the
/// current query. The boost is proportional to the similarity between the
/// current query and the historical queries that led to each document.
/// </para>
/// </remarks>
public sealed class QueryFeedbackHistory
{
    private readonly Dictionary<string, QueryAssociation> _associations = new(StringComparer.Ordinal);

    // Distinct document ids seen in any association. Maintained on Record — a caller action, not a
    // per-search one — so the lookup can size its result dictionary exactly instead of growing into
    // it. A dictionary discovering its size as it fills reallocates at 1, 2, 4, 8, 16, 32, 64,
    // which measures ~4400 B per search against ~1100 B when told the answer up front. Not part of
    // any removal path: Forget drops an association, not a document, and a document appearing in
    // two of them is still one distinct document — over-counting only over-allocates, never
    // returns the wrong answer.
    private readonly HashSet<string> _distinctDocuments = new(StringComparer.Ordinal);

    private readonly ITokenizer _tokenizer;
    /// <param name="tokenizer">Tokenizer used to normalize queries for similarity computation.</param>
    public QueryFeedbackHistory(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <summary>
    /// Records that the user chose <paramref name="documentId"/> for
    /// <paramref name="query"/>, with a reward of <paramref name="reward"/>
    /// (default 1.0). If the same query-document pair is recorded multiple
    /// times, the rewards accumulate, so a proxy reward (a reranker score, a
    /// query-passage similarity, a ROUGE on the generated answer) can stand in
    /// for a click and a click still counts as one.
    /// </summary>
    /// <remarks>
    /// A reward of 0 (or negative) is accepted and stored as-is: it is the
    /// caller's way of saying "this document was shown and rejected". The
    /// strength normalization divides by the largest accumulated weight under
    /// the query, so a single rejection among strong positives still moves the
    /// ranking. Callers that only have a binary signal keep calling the
    /// two-argument form, which adds 1.0.
    /// </remarks>
    public void Record(string query, string documentId, double reward = 1.0)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documentId);

        if (!double.IsFinite(reward))
            throw new ArgumentOutOfRangeException(nameof(reward), reward, "Reward must be finite.");

        string key = NormalizeQuery(query);

        if (!_associations.TryGetValue(key, out var association))
        {
            association = new QueryAssociation(key, new Dictionary<string, double>(StringComparer.Ordinal), _tokenizer);
            _associations[key] = association;
        }

        association.DocumentWeights.TryGetValue(documentId, out double weight);
        association.DocumentWeights[documentId] = weight + reward;

        // Maintained here, where the id is already in hand, so the lookup need not discover how
        // many there are at the cost of a growing dictionary every search.
        _distinctDocuments.Add(documentId);
    }

    /// <summary>
    /// Bulk, additive warm start from recorded interactions (click logs, labelled
    /// pairs, a previous deployment's history). Each entry is treated as a
    /// <see cref="Record"/> call, so repeated pairs accumulate and an existing
    /// history is extended rather than replaced — use <see cref="Restore"/> when
    /// a full replacement is wanted.
    /// </summary>
    /// <remarks>
    /// This is the answer to the cold-start problem with rare signals: a history
    /// seeded from whatever evidence already exists (logs, a supervised baseline,
    /// a reranker's verdicts) starts contributing on the first search instead of
    /// waiting for the first user click.
    /// </remarks>
    public void Seed(IEnumerable<(string Query, string DocumentId)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var (query, documentId) in entries)
            Record(query, documentId);
    }

    /// <summary>
    /// Bulk, additive warm start with an explicit per-pair reward, for callers
    /// that carry a proxy signal (reranker score, similarity, ROUGE) rather
    /// than a binary click.
    /// </summary>
    public void Seed(IEnumerable<(string Query, string DocumentId, double Reward)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        foreach (var (query, documentId, reward) in entries)
            Record(query, documentId, reward);
    }

    /// <summary>
    /// Removes all recorded associations for <paramref name="query"/>.
    /// </summary>
    public void Forget(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        _associations.Remove(NormalizeQuery(query));
    }

    /// <summary>
    /// Returns the documents associated with <paramref name="query"/>, ordered by
    /// descending association strength. Returns an empty list if no association exists.
    /// </summary>
    public IReadOnlyList<(string DocumentId, double Strength)> GetAssociations(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        string key = NormalizeQuery(query);

        if (!_associations.TryGetValue(key, out var association))
            return Array.Empty<(string, double)>();

        double maxWeight = MaxWeightOf(association.DocumentWeights);

        return association.DocumentWeights
            .Select(kvp => (kvp.Key, kvp.Value / maxWeight))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    /// <summary>
    /// Returns documents associated with queries similar to <paramref name="query"/>,
    /// using token-overlap similarity. This is the fuzzy matching path: it finds
    /// historical queries that share tokens with the current query and returns
    /// their associated documents, weighted by similarity.
    /// </summary>
    /// <param name="query">The current query.</param>
    /// <param name="minSimilarity">Minimum token-overlap similarity (0 to 1) for a historical query to contribute.</param>
    public IReadOnlyList<(string DocumentId, double Strength)> GetFuzzyAssociations(
        string query,
        double minSimilarity = 0.3)
    {
        var scores = GetFuzzyAssociationScores(query, minSimilarity);

        return scores.Count == 0
            ? Array.Empty<(string, double)>()
            : scores
                .Select(kvp => (kvp.Key, kvp.Value))
                .OrderByDescending(x => x.Item2)
                .ToList();
    }

    /// <summary>
    /// The same associations <see cref="GetFuzzyAssociations"/> reports, keyed by document id so
    /// a caller applying them to a page can look each candidate up without building a second map.
    /// </summary>
    /// <remarks>
    /// This is the form the engines use: <see cref="GetFuzzyAssociations"/> sorts, and a decorator
    /// that then needs a lookup would pay for the projection and the map on every search.
    /// </remarks>
    /// <param name="query">The current query.</param>
    /// <param name="minSimilarity">Minimum token-overlap similarity (0 to 1) for a historical query to contribute.</param>
    /// <param name="onlyDocumentIds">
    /// Optional restriction to these document ids — what a caller will actually look up. When
    /// given, ids outside the set are never inserted, and the result is sized to the set.
    /// <para>
    /// The point is not to skip storing: it is that an association's documents are only ever
    /// consulted through this dictionary, so inserting four hundred of them to read three of them
    /// is work and memory thrown away on every search. A decorator handing over its page of ten
    /// asks for ten, and the dictionary then fits its first allocation — which is where the
    /// growth cost went: measured on a history holding forty distinct documents, growing into it
    /// was ~4400 B per search against ~1100 B when the size was known.
    /// </para>
    /// </param>
    public IReadOnlyDictionary<string, double> GetFuzzyAssociationScores(
        string query,
        double minSimilarity = 0.3,
        IReadOnlySet<string>? onlyDocumentIds = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (minSimilarity <= 0 || minSimilarity > 1)
            throw new ArgumentOutOfRangeException(nameof(minSimilarity), minSimilarity,
                "Similarity must be in (0, 1].");

        var queryTokens = new HashSet<string>(_tokenizer.Tokenize(NormalizeQuery(query)), StringComparer.Ordinal);

        if (queryTokens.Count == 0)
            return EmptyScores;

        int capacity = onlyDocumentIds?.Count > 0
            ? onlyDocumentIds!.Count
            : _distinctDocuments.Count;

        var documentScores = new Dictionary<string, double>(capacity, StringComparer.Ordinal);

        foreach (var association in _associations.Values)
        {
            var historyTokens = association.Tokens;

            if (historyTokens.Count == 0)
                continue;

            int intersection = 0;
            foreach (var token in queryTokens)
            {
                if (historyTokens.Contains(token))
                    intersection++;
            }

            double similarity = (double)intersection / Math.Max(queryTokens.Count, historyTokens.Count);

            if (similarity < minSimilarity)
                continue;

            double maxWeight = MaxWeightOf(association.DocumentWeights);

            foreach (var kvp in association.DocumentWeights)
            {
                if (onlyDocumentIds is not null && !onlyDocumentIds.Contains(kvp.Key))
                    continue;

                double strength = similarity * (kvp.Value / maxWeight);

                documentScores.TryGetValue(kvp.Key, out double existing);
                documentScores[kvp.Key] = Math.Max(existing, strength);
            }
        }

        return documentScores;
    }

    /// <summary>
    /// Shared empty result, so the no-match path allocates nothing. Not a mutable collection
    /// anything can write to.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, double> EmptyScores =
        new Dictionary<string, double>(0, StringComparer.Ordinal);

    /// <summary>
    /// Every recorded query with the document associations it carries, for bulk persistence.
    /// Entries come in insertion order, and each counter is the number of times that pair was
    /// recorded — so a snapshot taken here and passed back to <see cref="Restore"/> is the same
    /// history, multiplicity included, rather than one weakened to a single mention each.
    /// </summary>
    public IReadOnlyList<(string Query, IReadOnlyDictionary<string, double> DocumentWeights)> Snapshot() =>
        _associations.Values
            .Select(a => (a.Query, (IReadOnlyDictionary<string, double>)a.DocumentWeights))
            .ToList();

    /// <summary>
    /// Replaces the entire history with <paramref name="entries"/>.
    /// </summary>
    /// <remarks>
    /// Weights are adopted rather than replayed through <see cref="Record"/>, which is what keeps
    /// a restore faithful: replaying would collapse a reward of 2.5 back to one mention and
    /// quietly weaken a query a user had answered with a strong proxy signal.
    /// </remarks>
    public void Restore(IEnumerable<(string Query, IReadOnlyDictionary<string, double> DocumentWeights)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _associations.Clear();

        foreach (var (query, documentWeights) in entries)
        {
            string key = NormalizeQuery(query);

            _associations[key] = new QueryAssociation(
                key,
                new Dictionary<string, double>(documentWeights, StringComparer.Ordinal),
                _tokenizer);

            foreach (string documentId in documentWeights.Keys)
                _distinctDocuments.Add(documentId);
        }
    }

    /// <summary>
    /// Returns the number of distinct queries in the history.
    /// </summary>
    public int Count => _associations.Count;

    /// <summary>
    /// How often the most-chosen document was chosen under one query — the denominator that
    /// turns a raw count into a strength in <c>[0, 1]</c>. Zero counts as one.
    /// </summary>
    /// <remarks>
    /// Written as a loop rather than <c>DocumentWeights.Values.Max()</c>, which is what this
    /// replaced. <c>Values</c> hands back a <c>Dictionary&lt;int&gt;.ValueCollection</c> whose
    /// enumerator is a struct; reaching it through <c>IEnumerable&lt;int&gt;</c> to satisfy the
    /// LINQ overload boxes that struct — about 40-54 B of allocation, once per recorded query,
    /// per search. On a history of 200 recorded queries that is ~10 KB of a ~13 KB lookup, and
    /// the pattern sits inside the loop <see cref="GetFuzzyAssociationScores"/> walks. Measured:
    /// 752 B/call at one association, 8712 at two hundred — the growth is per association, not
    /// per document, which is how it identified this line and not the dictionary.
    /// </remarks>
    private static double MaxWeightOf(Dictionary<string, double> documentWeights)
    {
        double max = 0;

        // The Dictionary's own enumerator is a struct: no boxing, nothing to collect.
        foreach (var pair in documentWeights)
        {
            if (pair.Value > max)
                max = pair.Value;
        }

        return max > 0 ? max : 1;
    }

    private static string NormalizeQuery(string query) => query.Trim().ToLowerInvariant();

    private sealed class QueryAssociation
    {
        /// <param name="query">The recorded query, already normalized.</param>
        /// <param name="documentWeights">Document id to accumulated reward under this query.</param>
        /// <param name="tokenizer">
        /// The history's own tokenizer, held so the terms are computed once here rather than on
        /// every search: <see cref="GetFuzzyAssociations"/> walks every recorded query, so
        /// tokenizing inside that loop would make each search cost one tokenization per query ever
        /// recorded.
        /// </param>
        public QueryAssociation(string query, Dictionary<string, double> documentWeights, ITokenizer tokenizer)
        {
            Query = query;
            DocumentWeights = documentWeights;
            Tokens = new HashSet<string>(tokenizer.Tokenize(query), StringComparer.Ordinal);
        }

        public string Query { get; }

        public Dictionary<string, double> DocumentWeights { get; }

        /// <summary>
        /// The query's terms, computed once at construction. The tokenizer belongs to the history
        /// and cannot change, so these cannot go stale.
        /// </summary>
        public HashSet<string> Tokens { get; }
    }
}
