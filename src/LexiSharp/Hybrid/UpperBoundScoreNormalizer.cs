using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// Divides a lexical engine's scores by the theoretical upper bound for the query, when the engine
/// can report it, so a score becomes a fraction of what the query could have scored rather than a
/// fraction of the best document the page happened to contain.
/// </summary>
/// <remarks>
/// <para>
/// The bound is the scorer's own — <see cref="IScoreBoundScorer"/> computes it from the query's terms,
/// the corpus's inverse document frequencies and the posting statistics. A page maximum, by contrast,
/// is set by one document: an engine whose best hit is weak still contributes a full unit under
/// <see cref="MaxScoreNormalizer"/>, and an engine whose best hit is strong contributes the same unit.
/// The bound does not move with the page.
/// </para>
/// <para>
/// <b>The bound can be loose, and that is measured rather than assumed.</b> It is the sum of each
/// term's independent maximum, so it bounds a document that achieved every term's best value at once.
/// No such document need exist. A loose bound scales a query's scores down as a whole; the ranking is
/// unchanged and the fusion weight is what absorbs it. Whether that is better than the page maximum is
/// a question for a complementary hybrid lane, not for this type's argument.
/// </para>
/// <para>
/// <b>It falls back.</b> A query with syntax, a scorer without a bound, an index without posting
/// statistics, or an empty result list all measure exactly as <see cref="MaxScoreNormalizer"/> would.
/// The fallback is stated because a reader comparing two runs needs to know which mapping each query
/// actually used.
/// </para>
/// </remarks>
public sealed class UpperBoundScoreNormalizer : IContextualScoreNormalizer
{
    private static readonly MaxScoreNormalizer Fallback = new();

    /// <inheritdoc />
    public string Name => "upper-bound";

    /// <inheritdoc />
    /// <remarks>
    /// A list-only call has no query bound to divide by, so it is the historical page maximum. This is
    /// what keeps the type usable as an ordinary <see cref="IScoreNormalizer"/> rather than one that
    /// only works inside a hybrid that knows to pass a context.
    /// </remarks>
    public ScoreNormalization Measure(IReadOnlyList<SearchResult> results) => Fallback.Measure(results);

    /// <inheritdoc />
    public ScoreNormalization Measure(
        IReadOnlyList<SearchResult> results,
        ScoreNormalizationContext context)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(context);

        // An engine that returned nothing has no scores to normalize, and the merge explanation's
        // contract is that its divisor is 0. A query bound is still a number, but reporting it here
        // would claim a scale for a lane that contributed nothing.
        if (results.Count == 0)
            return ScoreNormalization.None;

        if (context.QueryUpperBound is double upperBound &&
            upperBound > 0 &&
            double.IsFinite(upperBound))
        {
            // The observed maximum is a guard against a bound a rounding step pushed below a score it
            // was meant to bound. It should never bind; a score above 1 is worse than a slightly
            // looser divisor, because the normalized value is presented as a fraction of the maximum.
            double observed = 0;

            for (int i = 0; i < results.Count; i++)
            {
                double score = results[i].Score;

                if (double.IsFinite(score) && score > observed)
                    observed = score;
            }

            return new ScoreNormalization(0, Math.Max(upperBound, observed));
        }

        return Fallback.Measure(results);
    }
}
