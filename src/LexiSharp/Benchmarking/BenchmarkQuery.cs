namespace LexiSharp.Benchmarking;

/// <summary>
/// One labeled query of a benchmark run: a raw query together with the ids of the
/// documents a good ranking should surface.
/// </summary>
/// <param name="Id">Stable identifier, used to join the query with its qrels.</param>
/// <param name="Text">The raw query as a user would type it.</param>
/// <param name="RelevantDocumentIds">
/// Ids of the relevant documents. Queries with an empty set are loaded but excluded from the
/// metric averages (there is nothing to score them against). A grade-aware variant is not
/// needed here: the qrels of a retrieval benchmark are almost always binary.
/// </param>
public sealed record BenchmarkQuery(
    string Id,
    string Text,
    IReadOnlyCollection<string> RelevantDocumentIds);