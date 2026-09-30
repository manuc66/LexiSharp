using LexiSharp.Core;

namespace LexiSharp.Postgres;

/// <summary>
/// The document-id exclusion, for the engines that assemble their results from SQL.
/// </summary>
/// <remarks>
/// <para>
/// <c>SearchOptions.PassesFilters</c> is internal to the retrieval assembly, and the two assemblies
/// that drive the golden master are its friends — the CLI and the test suite — not this one. So the
/// gate is reimplemented here rather than called, the way
/// <c>ParadeDBTextSearchEngine.PassesFilters</c> already reimplemented it, and this file is where
/// that copy now lives for all five engines.
///
/// It was not merely duplicated. Four of the five did not have it: <c>PostgresTextSearchEngine</c>,
/// <c>PostgresSparseSearchEngine</c>,
/// <c>PostgresFuzzySearchEngine</c> and <c>PostgresVectorSearchEngine</c> never read
/// <see cref="SearchOptions.ExcludedDocumentIds"/>, so the option was silently ignored on every
/// Postgres backend, while the same flag on an in-memory engine excluded the document. The fifth
/// applied it but filled its page wrongly, which is what <see cref="FetchLimit"/> is for.
/// </para>
/// <para>
/// The filter is applied in the C# loop rather than pushed into the SQL. That is the choice this
/// codebase already makes for <see cref="SearchOptions.MinimumScore"/> — the query orders by score
/// descending and the threshold is applied on the way out — so the exclusion follows the same path
/// and the id list never becomes a parameter. The cost is that rows are transferred and then
/// dropped, which the over-fetch below bounds.
/// </para>
/// </remarks>
internal static class PostgresDocumentExclusion
{
    /// <summary>
    /// How many rows the query must fetch for the page to come back full once excluded documents are
    /// dropped from it.
    /// </summary>
    /// <remarks>
    /// The engines fetch <see cref="SearchOptions.Window"/> rows, filter, and then cut
    /// <c>[Offset, Offset + Limit)</c>. An excluded document inside that window therefore leaves a
    /// hole where a result should be, and the page comes back one short — the same hole the in-memory
    /// engines' own test <c>ExcludingOneDocumentStillFillsThePage</c> exists to prevent. Fetching one
    /// row per excluded id closes it, and saturates rather than overflowing on a pathological set.
    /// </remarks>
    /// <param name="options">The options for this search.</param>
    public static int FetchLimit(SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        int window = options.Window;
        int excluded = options.ExcludedDocumentIds?.Count ?? 0;

        return window > int.MaxValue - excluded ? int.MaxValue : window + excluded;
    }

    /// <summary>
    /// Whether the document survives the exclusion, which is the only gate this file applies: the
    /// metadata filters are already part of the generated SQL.
    /// </summary>
    /// <param name="options">The options for this search.</param>
    /// <param name="documentId">The id of the document the row holds.</param>
    public static bool Passes(SearchOptions options, string documentId)
    {
        ArgumentNullException.ThrowIfNull(options);

        var excluded = options.ExcludedDocumentIds;

        // The count test is not redundant: an empty set must not read as "exclude everything", which
        // is what a membership test written without it would do for a comparer that ignores it.
        return excluded is null || excluded.Count == 0 || !excluded.Contains(documentId);
    }
}
