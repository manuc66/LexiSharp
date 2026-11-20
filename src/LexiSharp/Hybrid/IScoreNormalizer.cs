using LexiSharp.Core;

namespace LexiSharp.Hybrid;

/// <summary>
/// One engine's scores mapped onto a comparable scale: subtract <see cref="Offset"/>, then divide by
/// <see cref="Scale"/> — <c>(s − offset) / scale</c>.
/// </summary>
/// <remarks>
/// <para>
/// Two numbers rather than one because the two normalizers that matter need both: dividing by the
/// largest score is a pure divisor (<c>offset = 0</c>), while stretching a list across <c>[0, 1]</c>
/// first subtracts its smallest score — without the subtraction a list whose scores all sit between
/// 4.0 and 4.1 would normalize into <c>[0.97, 1]</c>, which spreads nothing.
/// </para>
/// <para>
/// <b>The degenerate scale maps to <c>0</c>.</b> An engine whose scores cannot be measured — nothing
/// finite, nothing positive — has no scale, and its documents contribute nothing to the blend rather
/// than an arbitrary <c>1</c>. That is the behaviour both score mergers already had, and the reason
/// it is stated here instead of left to each merger.
/// </para>
/// </remarks>
/// <param name="Offset">Value subtracted from every score before dividing.</param>
/// <param name="Scale">Divisor; a non-positive value means "this engine cannot be normalized".</param>
public readonly record struct ScoreNormalization(double Offset, double Scale)
{
    /// <summary>The mapping an engine with nothing to measure gets: every contribution is <c>0</c>.</summary>
    public static readonly ScoreNormalization None = new(0, 0);

    /// <summary>
    /// Maps one raw score onto the normalized scale.
    /// </summary>
    /// <param name="score">The engine's own score.</param>
    /// <returns>
    /// <c>(score − Offset) / Scale</c>, or <c>0</c> when <see cref="Scale"/> is not positive. The
    /// result is not clamped: a score outside the measured range normalizes outside <c>[0, 1]</c>,
    /// which is the only honest reading of a value the normalizer never saw.
    /// </returns>
    public double Apply(double score) =>
        Scale > 0 ? (score - Offset) / Scale : 0;
}

/// <summary>
/// Strategy that puts one source engine's scores on a scale comparable with another's, before a
/// score-blending merger combines them.
/// </summary>
/// <remarks>
/// <para>
/// BM25 is unbounded and grows with the query's information content; a cosine similarity is in
/// <c>[−1, 1]</c>; a cross-encoder returns a logit. Summing or averaging those numbers without a
/// mapping is meaningless, and the mapping is the whole difference between a hybrid blend that works
/// and one whose lexical half silently dominates.
/// </para>
/// <para>
/// <b>Measured over the list it is given</b>, which is the candidates one engine returned for one
/// query — not the corpus, and not a calibration set. A normalizer is therefore stateless and needs
/// no fit: what it adapts to, it reads from the very list it is normalizing. The caveat is that a
/// list holding one score cannot reveal a distribution, so the normalizers that measure a spread say
/// what they do in that case rather than dividing by zero.
/// </para>
/// <para>
/// <b>Two mergers consume this</b>: <see cref="WeightedScoreResultMerger"/> (a weighted mean) and
/// <see cref="CombSumResultMerger"/> (a sum, and through it <see cref="CombMNZResultMerger"/>). The
/// default on all three is <see cref="MaxScoreNormalizer"/>, which is the behaviour they had before
/// this seam existed.
/// </para>
/// </remarks>
public interface IScoreNormalizer
{
    /// <summary>Short name, used in merge traces and reports — e.g. <c>"max"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Measures one engine's result list and returns the mapping its scores go through.
    /// </summary>
    /// <param name="results">One engine's ordered results for one query; empty when it matched nothing.</param>
    /// <returns>
    /// The mapping, or <see cref="ScoreNormalization.None"/> when the list holds no score this
    /// normalizer can measure. Must not throw for an empty list.
    /// </returns>
    ScoreNormalization Measure(IReadOnlyList<SearchResult> results);
}
