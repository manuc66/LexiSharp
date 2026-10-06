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
    private readonly ITokenizer _tokenizer;
    /// <param name="tokenizer">Tokenizer used to normalize queries for similarity computation.</param>
    public QueryFeedbackHistory(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <summary>
    /// Records that the user chose <paramref name="documentId"/> for
    /// <paramref name="query"/>. If the same query-document pair is recorded
    /// multiple times, the association strength increases.
    /// </summary>
    public void Record(string query, string documentId)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(documentId);

        string key = NormalizeQuery(query);

        if (!_associations.TryGetValue(key, out var association))
        {
            association = new QueryAssociation(key, new Dictionary<string, int>(StringComparer.Ordinal), _tokenizer);
            _associations[key] = association;
        }

        association.DocumentCounts.TryGetValue(documentId, out int count);
        association.DocumentCounts[documentId] = count + 1;
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

        int maxCount = association.DocumentCounts.Count > 0
            ? association.DocumentCounts.Values.Max()
            : 1;

        return association.DocumentCounts
            .Select(kvp => (kvp.Key, (double)kvp.Value / maxCount))
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
    public IReadOnlyDictionary<string, double> GetFuzzyAssociationScores(
        string query,
        double minSimilarity = 0.3)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (minSimilarity <= 0 || minSimilarity > 1)
            throw new ArgumentOutOfRangeException(nameof(minSimilarity), minSimilarity,
                "Similarity must be in (0, 1].");

        var queryTokens = new HashSet<string>(_tokenizer.Tokenize(NormalizeQuery(query)), StringComparer.Ordinal);

        if (queryTokens.Count == 0)
            return EmptyScores;

        var documentScores = new Dictionary<string, double>(StringComparer.Ordinal);

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

            int maxCount = association.DocumentCounts.Count > 0
                ? association.DocumentCounts.Values.Max()
                : 1;

            foreach (var kvp in association.DocumentCounts)
            {
                double strength = similarity * ((double)kvp.Value / maxCount);

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
    public IReadOnlyList<(string Query, IReadOnlyDictionary<string, int> DocumentCounts)> Snapshot() =>
        _associations.Values
            .Select(a => (a.Query, (IReadOnlyDictionary<string, int>)a.DocumentCounts))
            .ToList();

    /// <summary>
    /// Replaces the entire history with <paramref name="entries"/>.
    /// </summary>
    /// <remarks>
    /// Counters are adopted rather than replayed through <see cref="Record"/>, which is what keeps
    /// a restore faithful: replaying would count each pair once and quietly halve the strength of
    /// a query a user had answered five times.
    /// </remarks>
    public void Restore(IEnumerable<(string Query, IReadOnlyDictionary<string, int> DocumentCounts)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        _associations.Clear();

        foreach (var (query, documentCounts) in entries)
        {
            string key = NormalizeQuery(query);

            _associations[key] = new QueryAssociation(
                key,
                new Dictionary<string, int>(documentCounts, StringComparer.Ordinal),
                _tokenizer);
        }
    }

    /// <summary>
    /// Returns the number of distinct queries in the history.
    /// </summary>
    public int Count => _associations.Count;

    private static string NormalizeQuery(string query) => query.Trim().ToLowerInvariant();

    private sealed class QueryAssociation
    {
        /// <param name="query">The recorded query, already normalized.</param>
        /// <param name="documentCounts">Document id to times it was chosen under this query.</param>
        /// <param name="tokenizer">
        /// The history's own tokenizer, held so the terms are computed once here rather than on
        /// every search: <see cref="GetFuzzyAssociations"/> walks every recorded query, so
        /// tokenizing inside that loop would make each search cost one tokenization per query ever
        /// recorded.
        /// </param>
        public QueryAssociation(string query, Dictionary<string, int> documentCounts, ITokenizer tokenizer)
        {
            Query = query;
            DocumentCounts = documentCounts;
            Tokens = new HashSet<string>(tokenizer.Tokenize(query), StringComparer.Ordinal);
        }

        public string Query { get; }

        public Dictionary<string, int> DocumentCounts { get; }

        /// <summary>
        /// The query's terms, computed once at construction. The tokenizer belongs to the history
        /// and cannot change, so these cannot go stale.
        /// </summary>
        public HashSet<string> Tokens { get; }
    }
}
