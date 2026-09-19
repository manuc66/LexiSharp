namespace LexiSharp.Ranking;

/// <summary>
/// The quality metric a <see cref="Bm25ParameterTuner"/> maximizes over its validation set.
/// </summary>
public enum TuningMetric
{
    /// <summary>Precision@k of the retrieved ranking (see <see cref="RetrievalMetrics.PrecisionAtK"/>).</summary>
    Precision,

    /// <summary>Recall@k of the retrieved ranking (see <see cref="RetrievalMetrics.RecallAtK"/>).</summary>
    Recall,

    /// <summary>F1@k, the harmonic mean of precision and recall (see <see cref="RetrievalMetrics.F1AtK"/>).</summary>
    F1,

    /// <summary>nDCG@k with binary relevance (see <see cref="RetrievalMetrics.NdcgAtK(IReadOnlyCollection{string}, IReadOnlyCollection{string}, int)"/>).</summary>
    Ndcg,
}