namespace LexiSharp.Ranking;

/// <summary>
/// One labeled validation query of a <see cref="Bm25ParameterTuner"/>: a raw query together
/// with the ids of the documents a good ranking should surface.
/// </summary>
/// <param name="Query">The raw query as a user would type it; tokenized by the tuner's tokenizer.</param>
/// <param name="RelevantDocumentIds">Ids of the relevant documents (at least one).</param>
public sealed record Bm25ValidationQuery(
    string Query,
    IReadOnlyCollection<string> RelevantDocumentIds);