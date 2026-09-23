namespace LexiSharp.Benchmarking;

/// <summary>
/// Mean retrieval metrics of one benchmark configuration over the judged queries, at the
/// configured <c>topK</c>. Every value lives in <c>[0, 1]</c>; all numerators and denominators
/// are the ones <see cref="LexiSharp.Ranking.RetrievalMetrics"/> documents.
/// </summary>
/// <param name="NdcgAtK">Mean nDCG@k (binary relevance).</param>
/// <param name="MapAtK">Mean average precision at k (MAP@k).</param>
/// <param name="MrrAtK">Mean reciprocal rank at k (MRR@k).</param>
/// <param name="RecallAtK">Mean recall@k.</param>
/// <param name="PrecisionAtK">Mean precision@k.</param>
/// <param name="F1AtK">Mean F1@k.</param>
public sealed record BenchmarkMetrics(
    double NdcgAtK,
    double MapAtK,
    double MrrAtK,
    double RecallAtK,
    double PrecisionAtK,
    double F1AtK);