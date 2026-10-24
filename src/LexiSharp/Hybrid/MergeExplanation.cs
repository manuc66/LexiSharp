namespace LexiSharp.Hybrid;

/// <summary>
/// The arithmetic a weighted merge applied: one weight and one divisor per source engine.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Core.DetailedSearchResult.Contributions"/> gives the raw score each engine assigned a
/// document, and this record gives the two numbers needed to turn those raw scores into the merged
/// one — <c>score = Σ wᵢ·sᵢ/divisorᵢ</c>. Without the divisors the contributions are readable but
/// not checkable: two engines on different scales produce a number no reader can reproduce.
/// </para>
/// <para>
/// Both lists are indexed by the engine order the merge was given, which is the order the source
/// labels come in. A label-indexed reading is what the trace writes, because a caller holding only
/// the trace should not have to know that order to join the two.
/// </para>
/// <para>
/// A divisor is <c>0</c> for an engine that returned nothing, and the merge treats that engine's
/// contribution as <c>0</c> rather than dividing — the same value, so a reader recomputing by hand
/// gets the same answer instead of an infinity.
/// </para>
/// </remarks>
/// <param name="Weights">Weight applied to each engine's normalised score, in engine order.</param>
/// <param name="Normalization">
/// Divisor applied to each engine's raw scores before weighting, in engine order: that engine's
/// largest finite score in this search.
/// </param>
public sealed record MergeExplanation(
    IReadOnlyList<double> Weights,
    IReadOnlyList<double> Normalization);
