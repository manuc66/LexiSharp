namespace LexiSharp.Core;

/// <summary>
/// What a boost function is told about the search it is adjusting: the query, the ranking the
/// inner engine produced before any boost, the caller's payload, and the confidence of each
/// candidate in that ranking.
/// </summary>
/// <remarks>
/// <para>
/// One context is built per <c>Search</c> call and handed to the boost function for every
/// candidate, so a boost that depends on the whole ranking — "only when the top two are close" —
/// costs one evaluation per query rather than one per candidate.
/// </para>
/// <para>
/// <see cref="Results"/> is the candidate pool the inner engine returned, <b>before</b> any boost,
/// in its own ranking order with its own scores. A boost reading it sees the ranking as it stood
/// before this decorator touched it, which is what makes "was the base ranking already clear?"
/// answerable at all.
/// </para>
/// <para>
/// <see cref="Payload"/> is the caller's, typed as <typeparamref name="TPayload"/> because the
/// caller named it when it chose the engine. This library hands it through without reading it.
/// </para>
/// </remarks>
/// <typeparam name="TPayload">The caller's per-search state.</typeparam>
public sealed class BoostContext<TPayload>
{
    private readonly IReadOnlyList<double> _confidences;

    internal BoostContext(
        string query,
        IReadOnlyList<SearchResult> results,
        IReadOnlyList<double> confidences,
        TPayload payload)
    {
        Query = query;
        Results = results;
        _confidences = confidences;
        Payload = payload;
    }

    /// <summary>The raw query text this search was issued with, before tokenization.</summary>
    public string Query { get; }

    /// <summary>
    /// The candidate pool the inner engine returned for this query, in its ranking order and with
    /// its own scores — the ranking as it stood before any boost was applied.
    /// </summary>
    public IReadOnlyList<SearchResult> Results { get; }

    /// <summary>
    /// One confidence per entry of <see cref="Results"/>, in the same order, from
    /// <see cref="Ranking.ScoreConfidenceMethod.WinnerMargin"/>: <c>1 - score_next / score_current</c>
    /// for each result against its follower, and <c>0</c> for the last. A result the inner engine
    /// scored <c>0</c> is not a match by engine convention and reads <c>0</c> here too.
    /// <para>
    /// These are <b>relative</b> confidences, not probabilities of relevance: see
    /// <see cref="Ranking.ScoreConfidence"/> for what that distinction costs, and
    /// <see cref="Ranking.CalibratedScoreConfidence"/> for a mapping fitted to labelled data.
    /// </para>
    /// </summary>
    public IReadOnlyList<double> Confidences => _confidences;

    /// <summary>The caller's per-search state, handed to the boost unchanged.</summary>
    public TPayload Payload { get; }

    /// <summary>
    /// The inner engine's top score, or <c>0</c> when it returned nothing. Exposed because "is the
    /// top result a clear one?" is the question a conditional boost asks first, and because a
    /// fitted calibrator takes a raw score rather than a relative confidence.
    /// </summary>
    public double TopScore => Results.Count > 0 ? Results[0].Score : 0;

    /// <summary>
    /// The confidence of the top result, or <c>0</c> when the inner engine returned nothing.
    /// A single clear result reads as fully confident; see <see cref="Confidences"/>.
    /// </summary>
    public double TopConfidence => _confidences.Count > 0 ? _confidences[0] : 0;
}

/// <summary>
/// A boost that depends on the search as a whole, not only on the candidate in hand: it sees the
/// query, the pre-boost ranking, and the caller's payload.
/// </summary>
/// <param name="context">The search being adjusted; the same instance for every candidate.</param>
/// <param name="candidate">The candidate being boosted.</param>
/// <typeparam name="TPayload">The caller's per-search state.</typeparam>
public delegate ScoreBoost ContextualBoost<TPayload>(
    BoostContext<TPayload> context,
    SearchResult candidate);
