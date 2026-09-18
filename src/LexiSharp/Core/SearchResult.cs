namespace LexiSharp.Core;

/// <summary>
/// A document ranked against a query.
/// </summary>
/// <param name="DocumentId">Identifier of the matched document.</param>
/// <param name="Score">Relevance score produced by an <see cref="ITextScorer"/>. Higher is better.</param>
/// <param name="Document">The original indexed document.</param>
public sealed record SearchResult(
    string DocumentId,
    double Score,
    SearchDocument Document);