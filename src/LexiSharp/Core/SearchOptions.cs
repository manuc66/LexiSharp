namespace LexiSharp.Core;

/// <summary>
/// Controls how a search is performed and how results are returned.
/// </summary>
/// <param name="Limit">Maximum number of results to return.</param>
/// <param name="MinimumScore">Results with a score below this value are discarded.</param>
/// <param name="Filters">
/// Optional structured filters over <see cref="SearchDocument.Fields"/>; every filter must be
/// satisfied (AND) for a document to be returned. Filtering happens before scoring, so
/// unsatisfying documents cost no relevance computation at all.
/// </param>
public sealed record SearchOptions(
    int Limit = 10,
    double MinimumScore = double.NegativeInfinity,
    IReadOnlyList<MetadataFilter>? Filters = null)
{
    public static readonly SearchOptions Default = new();

    /// <summary>
    /// Whether the document passes every configured filter. Documents always pass when no
    /// filter is set.
    /// </summary>
    internal bool PassesFilters(SearchDocument document)
    {
        if (Filters is null || Filters.Count == 0)
            return true;

        // Per-document hot path: a LINQ All() would allocate an enumerator per candidate, so the
        // short-circuit loop is deliberate.
        foreach (var filter in Filters) // NOSONAR:S3267
        {
            if (!filter.Matches(document))
                return false;
        }

        return true;
    }
}
