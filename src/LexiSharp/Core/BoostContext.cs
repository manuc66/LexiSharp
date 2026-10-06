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
/// <see cref="Confidences"/> is computed on first read, not at construction. A boost that reads
/// only the payload — and the candidate-only <see cref="BoostedTextSearchEngine"/>, which is this
/// context's engine with the payload and the confidences discarded, is the degenerate case —
/// never pays for the walk and the array. Measured on a 500-document corpus at
/// <c>maxCandidates: 50</c>: the candidate-only engine allocates 8928 B/search where it did
/// 8880 B before this type existed, and a contextual boost that reads
/// <see cref="TopConfidence"/> allocates 9776 B.
/// </para>
/// <para>
/// <b>Why a class and not a <c>readonly record struct</c>.</b> Two reasons, and the first is the
/// one that decides it. This type is handed to the boost once per candidate, so a struct would be
/// copied on every one of those calls — and a copy cannot keep the memoized
/// <see cref="Confidences"/>: the lazy field would be written into a temporary that dies with the
/// statement, and the walk would run again per candidate, which is the cost this laziness exists to
/// avoid. A reference type keeps one array for the whole search.
/// </para>
/// <para>
/// The second is that the struct form is not free either. At four reference fields this is 32
/// bytes copied per candidate call — 1600 bytes of copying per search at
/// <c>maxCandidates: 50</c>, against 48 bytes of allocation for one instance per search
/// (32 + object header). The class is cheaper here, and it is the only form that can memoize.
/// The <c>readonly record struct</c> types in this namespace are the opposite case: small, copied
/// deliberately, and never asked to cache anything.
/// </para>
/// <para>
/// <see cref="Payload"/> is the caller's, typed as <typeparamref name="TPayload"/> because the
/// caller named it when it chose the engine. This library hands it through without reading it.
/// </para>
/// </remarks>
/// <typeparam name="TPayload">
/// The caller's per-search state, constrained to a reference type: see
/// <see cref="IContextualSearchEngine{TPayload}"/>.
/// </typeparam>
public sealed class BoostContext<TPayload>
    where TPayload : class
{
    private IReadOnlyList<double>? _confidences;

    internal BoostContext(
        string query,
        IReadOnlyList<SearchResult> results,
        TPayload payload,
        bool payloadSupplied)
    {
        Query = query;
        Results = results;
        Payload = payload;
        PayloadSupplied = payloadSupplied;
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
    /// for each result against its follower, and <c>0</c> for the last. A lone result is reported
    /// as <c>1</c> when its score is genuine and <c>0</c> otherwise. A result the inner engine
    /// scored <c>0</c> is not a match by engine convention and reads <c>0</c> here too.
    /// <para>
    /// These are <b>relative</b> confidences, not probabilities of relevance: see
    /// <see cref="Ranking.ScoreConfidence"/> for what that distinction costs, and
    /// <see cref="Ranking.CalibratedScoreConfidence"/> for a mapping fitted to labelled data.
    /// </para>
    /// <para>
    /// Two equal scores read as <c>0</c>, not as confidence: WinnerMargin is the gap to the
    /// follower, so a tie is no gap. A gate written as "boost when the confidence is below 0.5"
    /// and one written as "boost when it is above" therefore behave oppositely on a tie, and only
    /// one of them is asking the intended question.
    /// </para>
    /// </summary>
    public IReadOnlyList<double> Confidences =>
        _confidences ??= Ranking.ScoreConfidence.Compute(Results, Ranking.ScoreConfidenceMethod.WinnerMargin);

    /// <summary>
    /// Whether the search carried a payload at all, as opposed to
    /// <see cref="Payload"/> being <c>null</c>.
    /// </summary>
    /// <remarks>
    /// The two are different situations and this property is what tells them apart. Reached
    /// through <see cref="ITextSearchEngine.Search(string, SearchOptions?)"/>, no payload was
    /// ever offered and this is <c>false</c>; reached through
    /// <see cref="IContextualSearchEngine{TPayload}.Search(string, SearchOptions?, TPayload)"/>,
    /// one was offered and this is <c>true</c> — even if the caller passed <c>null</c>.
    /// <para>
    /// A boost that behaves differently depending on the payload should read this rather than
    /// testing <see cref="Payload"/> for null. A boost that has no use for a payload — the
    /// candidate-only case — reads neither, and never pays for the distinction.
    /// </para>
    /// </remarks>
    public bool PayloadSupplied { get; }

    /// <summary>
    /// The caller's per-search state, handed to the boost unchanged, or <c>null</c> when the
    /// search came through the payload-less overload.
    /// </summary>
    /// <remarks>
    /// See <see cref="PayloadSupplied"/> for how to tell "no payload was offered" from "a null
    /// payload was offered", which this property alone cannot.
    /// </remarks>
    public TPayload Payload { get; }

    /// <summary>
    /// The inner engine's top score, or <c>0</c> when it returned nothing. Exposed because "is the
    /// top result a clear one?" is the question a conditional boost asks first, and because a
    /// fitted calibrator takes a raw score rather than a relative confidence.
    /// </summary>
    /// <remarks>
    /// This is a raw score on whatever scale the inner engine produces — BM25, a dense similarity,
    /// a fusion score. A boost comparing it against a number has to know which, and a
    /// <see cref="Ranking.CalibratedScoreConfidence"/> fitted on one corpus says nothing about
    /// another engine's scale.
    /// </remarks>
    public double TopScore => Results.Count > 0 ? Results[0].Score : 0;

    /// <summary>
    /// The confidence of the top result, or <c>0</c> when the inner engine returned nothing.
    /// See <see cref="Confidences"/> for what the number means — in particular, that two equal
    /// top scores read as <c>0</c>.
    /// </summary>
    /// <remarks>
    /// Scale-invariant, which is what makes it comparable across engines in a way
    /// <see cref="TopScore"/> is not — at the cost of being relative to this result set rather
    /// than a probability of being right. See <see cref="Ranking.ScoreConfidence"/>.
    /// </remarks>
    public double TopConfidence => Confidences.Count > 0 ? Confidences[0] : 0;
}

/// <summary>
/// A boost that depends on the search as a whole, not only on the candidate in hand: it sees the
/// query, the pre-boost ranking, and the caller's payload.
/// </summary>
/// <param name="context">The search being adjusted; the same instance for every candidate.</param>
/// <param name="candidate">The candidate being boosted.</param>
/// <typeparam name="TPayload">
/// The caller's per-search state, constrained to a reference type: see
/// <see cref="IContextualSearchEngine{TPayload}"/>.
/// </typeparam>
public delegate ScoreBoost ContextualBoost<TPayload>(
    BoostContext<TPayload> context,
    SearchResult candidate)
    where TPayload : class;
