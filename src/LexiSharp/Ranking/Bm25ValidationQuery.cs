namespace LexiSharp.Ranking;

/// <summary>
/// One labeled validation query of a <see cref="Bm25ParameterTuner"/>: a raw query together
/// with the ids of the documents a good ranking should surface.
/// </summary>
/// <param name="Query">The raw query as a user would type it; tokenized by the tuner's tokenizer.</param>
/// <param name="RelevantDocumentIds">Ids of the relevant documents (at least one).</param>
/// <param name="ExcludedDocumentIds">
/// Optional ids to leave out while this query is scored, whatever they score. The tuner builds its
/// own <see cref="LexiSharp.Core.SearchOptions"/> per query, so this is how a caller-level exclusion
/// — a source document to suppress, say — reaches the tuning run rather than only the final
/// measurement. Default: <c>null</c>.
/// </param>
public sealed record Bm25ValidationQuery(
    string Query,
    IReadOnlyCollection<string> RelevantDocumentIds,
    IReadOnlySet<string>? ExcludedDocumentIds = null);