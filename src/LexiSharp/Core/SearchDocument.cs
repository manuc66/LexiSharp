namespace LexiSharp.Core;

/// <summary>
/// A single unit of text that can be indexed and searched.
/// </summary>
/// <param name="Id">Stable, unique identifier of the document.</param>
/// <param name="Text">The textual content to index.</param>
/// <param name="Fields">Optional structured fields (e.g. <c>title</c>, <c>tags</c>) for field-aware scoring.</param>
/// <param name="Category">Optional category used for supervised classification.</param>
public sealed record SearchDocument(
    string Id,
    string Text,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? Category = null);