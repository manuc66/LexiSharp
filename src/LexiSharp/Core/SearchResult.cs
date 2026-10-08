namespace LexiSharp.Core;

/// <summary>
/// A document ranked against a query.
/// </summary>
/// <remarks>
/// A value type, and deliberately: a page of results is read once, and an object per row costs a
/// header and a pointer the answer never needed. Measured on a page of ten, a search allocates
/// 240 bytes less this way. The price is that <c>default(SearchResult)</c> is a value nobody
/// produced — a null identifier and a zero score — so a caller holding one should ask whether it
/// came from a search rather than whether it is null.
/// </remarks>
/// <param name="DocumentId">Identifier of the matched document.</param>
/// <param name="Score">Relevance score produced by an <see cref="ITextScorer"/>. Higher is better.</param>
/// <param name="Document">The original indexed document.</param>
public readonly record struct SearchResult(
    string DocumentId,
    double Score,
    SearchDocument Document);