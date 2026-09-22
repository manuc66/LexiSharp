namespace LexiSharp.Core;

/// <summary>
/// A single unit of text that can be indexed and searched.
/// </summary>
/// <param name="Id">Stable, unique identifier of the document.</param>
/// <param name="Text">The textual content to index.</param>
/// <param name="Fields">Optional structured fields (e.g. <c>title</c>, <c>tags</c>) for field-aware scoring.</param>
/// <param name="Category">Optional category used for supervised classification.</param>
/// <param name="TextFields">
/// Optional named free-text sections of the document (e.g. <c>title</c>, <c>description</c>),
/// distinct from <see cref="Fields"/>. <see cref="Fields"/> is metadata used for filtering;
/// <c>TextFields</c> is additional text an embedding-backed engine can embed in place of — or
/// alongside — <see cref="Text"/>. Which field gets embedded is chosen on the engine's options
/// (e.g. <c>PostgresVectorOptions.EmbeddingTextField</c>); with no selector, <see cref="Text"/>
/// remains the embedded text.
/// </param>
public sealed record SearchDocument(
    string Id,
    string Text,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? Category = null,
    IReadOnlyDictionary<string, string>? TextFields = null);