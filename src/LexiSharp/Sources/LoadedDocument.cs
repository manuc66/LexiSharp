using LexiSharp.Core;

namespace LexiSharp.Sources;

/// <summary>
/// A document loaded from an external source (a markdown file, a JSON record, a text file)
/// and ready to be indexed — the bridge between your data and the search index.
/// </summary>
/// <param name="Id">Stable, unique identifier of the document (a path, a JSON id, ...).</param>
/// <param name="Text">The textual content to index.</param>
/// <param name="Fields">
/// Optional structured metadata (e.g. <c>title</c>, <c>tags</c>, <c>source</c>) — reachable
/// through facet buckets and metadata filters.
/// </param>
/// <param name="Category">Optional category used for supervised classification.</param>
/// <param name="TextFields">
/// Optional named text sections of the document (e.g. <c>title</c>), the ones a field-weighted
/// scorer can weigh. Distinct from <paramref name="Fields"/>: a key may appear in both, remaining
/// filterable <i>and</i> searchable. Null when the source named no text field.
/// </param>
public sealed record LoadedDocument(
    string Id,
    string Text,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? Category = null,
    IReadOnlyDictionary<string, string>? TextFields = null)
{
    /// <summary>Converts the loaded document into the engine's <see cref="SearchDocument"/>.</summary>
    public SearchDocument ToSearchDocument() => new(Id, Text, Fields, Category, TextFields);
}
