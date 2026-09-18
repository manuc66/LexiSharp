namespace LexiSharp.Ranking;

/// <summary>
/// Okapi BM25 tuning knobs: term-frequency saturation (<c>k1</c>) and document-length
/// normalization (<c>b</c>).
/// </summary>
/// <param name="K1">Term-frequency saturation; higher values let frequent terms contribute more. Must be non-negative.</param>
/// <param name="B">Document-length normalization, in <c>[0, 1]</c>. <c>0</c> disables normalization.</param>
/// <remarks>
/// There is no universally best parameter pair: it depends on corpus size and homogeneity.
/// The presets below trade off saturation and length normalization; the sweet spot for a
/// given corpus can be found empirically with the parameter tuner.
/// </remarks>
public sealed record Bm25Parameters(double K1, double B)
{
    /// <summary>Well-balanced default profile for mixed corpora (k1 = 1.5, b = 0.75).</summary>
    public static Bm25Parameters Balanced { get; } = new(1.5, 0.75);

    /// <summary>Heavy length normalization and strong term saturation (k1 = 2.0, b = 1.0), suited to large heterogeneous corpora.</summary>
    public static Bm25Parameters Aggressive { get; } = new(2.0, 1.0);

    /// <summary>Light normalization (k1 = 1.0, b = 0.5), suited to small or homogeneous corpora where document length barely matters.</summary>
    public static Bm25Parameters Conservative { get; } = new(1.0, 0.5);
}