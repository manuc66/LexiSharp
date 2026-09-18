namespace LexiSharp.Ranking;

/// <summary>One evaluated <c>(k1, b)</c> combination and the quality it achieved.</summary>
/// <param name="K1">The evaluated term-frequency saturation.</param>
/// <param name="B">The evaluated document-length normalization.</param>
/// <param name="MetricScore">Mean value of the tuned <see cref="TuningMetric"/> over the validation set.</param>
public sealed record Bm25GridPoint(double K1, double B, double MetricScore);

/// <summary>
/// The outcome of a <see cref="Bm25ParameterTuner.Tune"/> run: the best parameter pair found,
/// the quality it achieved and the full evaluation grid for inspection.
/// </summary>
/// <param name="Parameters">The best <see cref="Bm25Parameters"/> found on the grid.</param>
/// <param name="MetricScore">Mean metric value achieved by <paramref name="Parameters"/> over the validation set.</param>
/// <param name="Metric">The metric that was optimized.</param>
/// <param name="TopK">The retrieval depth used while evaluating candidates.</param>
/// <param name="Grid">Every evaluated grid point, in ascending <c>(k1, b)</c> order.</param>
public sealed record Bm25TuningResult(
    Bm25Parameters Parameters,
    double MetricScore,
    TuningMetric Metric,
    int TopK,
    IReadOnlyList<Bm25GridPoint> Grid);