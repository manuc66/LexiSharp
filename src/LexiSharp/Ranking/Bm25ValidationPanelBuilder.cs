namespace LexiSharp.Ranking;

/// <summary>Builds a <see cref="Bm25ParameterTuner"/> validation panel from labelled decisions.</summary>
public static class Bm25ValidationPanelBuilder
{
    /// <summary>
    /// Groups labelled (query, relevantDocumentId) pairs into <see cref="Bm25ValidationQuery"/> entries.
    /// </summary>
    public static IReadOnlyList<Bm25ValidationQuery> Build(
        IEnumerable<(string Query, string RelevantDocumentId)> decisions)
    {
        ArgumentNullException.ThrowIfNull(decisions);

        var groups = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var (query, relevantId) in decisions)
        {
            if (!groups.TryGetValue(query, out var set))
            {
                set = new HashSet<string>(StringComparer.Ordinal);
                groups[query] = set;
            }

            set.Add(relevantId);
        }

        return groups
            .Select(pair => new Bm25ValidationQuery(pair.Key, pair.Value))
            .ToList();
    }
}
