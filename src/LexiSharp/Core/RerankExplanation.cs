namespace LexiSharp.Core;

/// <summary>
/// Decomposition of a rerank decision: the signals that contributed to a candidate's final
/// score, each with its weight and value, plus a human-readable summary.
/// </summary>
/// <remarks>
/// <para>
/// Produced by an <see cref="IRerankerExplainer"/>. The explanation is computed lazily (only when
/// <see cref="IRerankerExplainer.ExplainRerank"/> is called), so the hot path of
/// <see cref="IReranker.Rerank"/> stays allocation-free. A reranker that cannot explain itself
/// simply does not implement the interface; callers use pattern matching.
/// </para>
/// </remarks>
public sealed record RerankExplanation(
    string DocumentId,
    double FinalScore,
    IReadOnlyList<RerankSignal> Signals,
    string Summary);

/// <summary>A single signal's contribution to a reranked score.</summary>
/// <param name="Name">Signal name, e.g. <c>"term-overlap"</c>, <c>"maxsim"</c>, <c>"diversity"</c>.</param>
/// <param name="Weight">Weight applied to the signal (0..1 or any real).</param>
/// <param name="Value">Raw value of the signal before weighting.</param>
/// <param name="Contribution">Weighted contribution to the final score.</param>
public sealed record RerankSignal(
    string Name,
    double Weight,
    double Value,
    double Contribution);