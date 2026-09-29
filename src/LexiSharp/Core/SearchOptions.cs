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
/// <param name="FuzzyOnlyOutOfVocabulary">
/// When set, a fuzzy term whose base form already exists in the index vocabulary matches its
/// exact form only — the near-variant expansion is skipped. Only genuinely out-of-vocabulary
/// words are corrected, so a correctly spelled word can no longer be degraded by unrelated
/// close variants. Default: <c>false</c> (start from the base form and add close variants).
/// </param>
/// <param name="ExcludedDocumentIds">
/// Optional set of document ids to leave out of the ranking, whatever they score. Excluded
/// documents are gated before scoring, so they cost no relevance computation. Default:
/// <c>null</c> — nothing is excluded, and the check costs one null test per candidate.
/// </param>
/// <param name="Trace">
/// Optional <see cref="SearchTrace"/> to record each ranking stage into. Default: <c>null</c> —
/// no engine records, allocates or formats anything. See <see cref="SearchTrace"/> for the bounds
/// and the truncation contract.
/// </param>
public sealed record SearchOptions(
    int Limit = 10,
    double MinimumScore = double.NegativeInfinity,
    IReadOnlyList<MetadataFilter>? Filters = null,
    int Offset = 0,
    bool FuzzyOnlyOutOfVocabulary = false,
    IReadOnlySet<string>? ExcludedDocumentIds = null,
    SearchTrace? Trace = null)
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
    /// Whether the document passes every configured filter and is not excluded by id. Documents
    /// always pass when neither is set.
    /// </summary>
    internal bool PassesFilters(SearchDocument document)
    {
        // Id exclusion is checked first and on its own line because it is the one gate that can be
        // set without a filter list, and this method runs once per candidate document. The null
        // test is what the overwhelmingly common case pays; the lookup is only reached by a caller
        // that asked for exclusions.
        var excluded = ExcludedDocumentIds;

        if (excluded is not null && excluded.Count > 0 && excluded.Contains(document.Id))
            return false;

        var filters = Filters;

        if (filters is null || filters.Count == 0)
            return true;

        // Indexed, not foreach, and this is not a style preference. `Filters` is an
        // `IReadOnlyList<MetadataFilter>`, so a foreach resolves `GetEnumerator()` *through the
        // interface*, which boxes or heap-allocates an enumerator on every call — and this method
        // is called once per candidate document, i.e. once per document in the corpus on a
        // filtered full scan. Measured on a 10,000-document corpus with one filter: 288,072 bytes
        // per search before, and 0 after, with the same result. The comment that used to sit here
        // said a LINQ `All()` would allocate per candidate; the `foreach` allocated per candidate
        // all the same, which is the kind of thing that survives because nobody measures it.
        for (int i = 0; i < filters.Count; i++)
        {
            if (!filters[i].Matches(document))
                return false;
        }

        return true;
    }
}
