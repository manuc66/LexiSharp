using System.Text.Json.Serialization;

namespace LexiSharp.WasmDemo;

/// <summary>One document as it is stored in <c>wwwroot/corpus.json</c>.</summary>
/// <param name="Id">Stable document id, used as the result label's fallback.</param>
/// <param name="Title">Human-readable title, indexed as a structured field.</param>
/// <param name="Category">The demo corpus's topic, kept for display.</param>
/// <param name="Text">The indexed body.</param>
internal sealed record CorpusDocument(string Id, string Title, string Category, string Text);

/// <summary>
/// Source-generated JSON for the corpus. Blazor WebAssembly trims in Release, and reflection-based
/// <see cref="System.Text.Json"/> is exactly the kind of thing trimming removes; generating the
/// reader also keeps the first load off the reflection path.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<CorpusDocument>))]
internal sealed partial class CorpusJsonContext : JsonSerializerContext;
