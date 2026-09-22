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
/// <param name="Offset">
/// Number of top-ranked results to skip, for deep pagination: the returned page is the window
/// <c>[Offset, Offset + Limit)</c> of the ranking that <see cref="MinimumScore"/> and
/// <see cref="Filters"/> produce. Default: <c>0</c> (no skip). A negative offset makes the
/// request empty, like a non-positive <see cref="Limit"/>.
/// </param>
public sealed record SearchOptions(
    int Limit = 10,
    double MinimumScore = double.NegativeInfinity,
    IReadOnlyList<MetadataFilter>? Filters = null,
    int Offset = 0)
{
    public static readonly SearchOptions Default = new();

    /// <summary>
    /// True when the request cannot produce results at all: a non-positive <see cref="Limit"/>
    /// or a negative <see cref="Offset"/>. Engines check this once before doing any work.
    /// </summary>
    public bool IsEmpty => Limit <= 0 || Offset < 0;

    /// <summary>
    /// Number of top-ranked results an engine must produce before <see cref="Offset"/> is
    /// applied: <c>Offset + Limit</c>, saturated at <see cref="int.MaxValue"/>. Engines (and
    /// decorators requesting candidate pools from inner engines) use this as their working
    /// window so the final page <c>[Offset, Offset + Limit)</c> can be cut from it.
    /// </summary>
    public int Window => Offset > int.MaxValue - Limit ? int.MaxValue : Offset + Limit;

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
