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
/// <param name="ParseQuerySyntax">
/// When <c>true</c> (the default), the query is read as query syntax: <c>"a phrase"</c> requires
/// positional adjacency, <c>term*</c> is a prefix and <c>term~</c> is fuzzy. When <c>false</c>, the
/// query is literal text and <c>QueryParser</c> is not consulted at all, so the terms are exactly
/// <c>ITokenizer.Tokenize(query)</c>.
/// <para>
/// The distinction matters because a double quote is ordinary text in prose, and a query language
/// cannot tell a quotation mark from a phrase delimiter. Measured on BEIR ArguAna, where all 1,406
/// test queries are whole arguments: 138 of them contain a straight <c>"</c> — 9.8% — and each of
/// those had the quoted span turned into a mandatory phrase, after which no document but the query's
/// own could satisfy it. 91 of them returned nothing at all and 20 returned a single result, at any
/// retrieval depth. Set this to <c>false</c> for a corpus of natural-language queries; a search over
/// prose that silently drops 9.8% of its queries is not a ranking question, it is a lost query.
/// </para>
/// </param>
/// <param name="TieBreak">
/// Two documents with the same score have no ranking between them, so something has to decide.
/// <see cref="Ranking.TieBreak.DocumentId"/> — the default — orders them by ordinal document id, which
/// is stable across machines and independent of how the corpus was loaded.
/// <see cref="Ranking.TieBreak.InsertionOrder"/> orders them by the position the engine produced them
/// in, which is what a system that breaks ties as documents are added does; use it to reproduce such a
/// system, and expect the result to depend on the load order rather than only on the documents.
/// </param>
public sealed record SearchOptions(
    int Limit = 10,
    double MinimumScore = double.NegativeInfinity,
    IReadOnlyList<MetadataFilter>? Filters = null,
    int Offset = 0,
    bool FuzzyOnlyOutOfVocabulary = false,
    IReadOnlySet<string>? ExcludedDocumentIds = null,
    SearchTrace? Trace = null,
    bool ParseQuerySyntax = true,
    Ranking.TieBreak TieBreak = Ranking.TieBreak.DocumentId)
{
    /// <summary>
    /// The rounding a system that writes its scores down applies before it writes them, or
    /// <see cref="Ranking.ScoreRounding.None"/>.
    /// </summary>
    /// <remarks>
    /// Internal, and reachable through <see cref="WithScoreRounding"/> rather than as a constructor
    /// parameter. The reason is what it does: it rounds to four decimals and then walks down each run
    /// of near-equal scores, which is lossy by construction — it merges scores differing by less than
    /// half a ten-thousandth, and it makes one document's returned score depend on which other
    /// documents came back beside it, so the same query against the same index reports two different
    /// scores for it at two different page sizes. A public option named <c>ScoreRounding</c> invites
    /// a caller to believe it only affects presentation, and it does not: a metric measured on a
    /// rounded run measures a different ranking function. Only the evaluation harness has a reason to
    /// want it, and it is the only thing that does.
    /// </remarks>
    internal Ranking.ScoreRounding ScoreRounding { get; init; } = Ranking.ScoreRounding.None;

    /// <summary>These options with the write-down rounding applied.</summary>
    /// <remarks>
    /// A method rather than a constructor parameter, so that the ordinary call sites cannot pass it by
    /// accident. Naming the method is the signal that something other than searching is being asked
    /// for.
    /// </remarks>
    internal SearchOptions WithScoreRounding(Ranking.ScoreRounding rounding) => this with
    {
        ScoreRounding = rounding,
    };

    /// <summary>The defaults every positional parameter already has: ten hits, no floor, no offset.</summary>
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
