namespace LexiSharp.Core;

/// <summary>
/// The contribution of a single query term to a document's score, as broken down by
/// an <see cref="IScoreExplainer"/>.
/// </summary>
/// <param name="Term">The (normalized) query term.</param>
/// <param name="TermFrequency">Occurrences of the term in the document.</param>
/// <param name="DocumentFrequency">Number of documents containing the term.</param>
/// <param name="InverseDocumentFrequency">Scorer-specific IDF value of the term.</param>
/// <param name="Score">The amount this term added to the document's total score.</param>
public sealed record TermContribution(
    string Term,
    double TermFrequency,
    double DocumentFrequency,
    double InverseDocumentFrequency,
    double Score);

/// <summary>
/// A transparent breakdown of why a document received its score for a query: global corpus
/// figures, per-term contributions and the scorer's parameter values.
/// </summary>
/// <param name="DocumentId">Id of the explained document.</param>
/// <param name="Algorithm">Name of the scorer that produced the explanation.</param>
/// <param name="TotalScore">The overall relevance score (the sum of the term contributions).</param>
/// <param name="DocumentLength">Number of tokens in the document.</param>
/// <param name="AverageDocumentLength">Mean document length across the corpus.</param>
/// <param name="LengthRatio">
/// Document length divided by the average document length; below 1 means shorter than average.
/// </param>
/// <param name="LengthNormalization">
/// Scorer-defined length normalization factor applied to term frequencies (1 when the scorer
/// normalizes length differently or not at all).
/// </param>
/// <param name="Terms">Per-term contributions, in query order; terms absent from the document are omitted.</param>
/// <param name="Parameters">The scorer's tuning parameters (e.g. <c>k1</c>, <c>b</c> for BM25).</param>
public sealed record ScoreExplanation(
    string DocumentId,
    string Algorithm,
    double TotalScore,
    int DocumentLength,
    double AverageDocumentLength,
    double LengthRatio,
    double LengthNormalization,
    IReadOnlyList<TermContribution> Terms,
    IReadOnlyDictionary<string, double> Parameters);