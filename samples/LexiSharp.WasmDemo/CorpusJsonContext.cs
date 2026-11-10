using System.Text.Json.Serialization;

namespace LexiSharp.WasmDemo;

/// <summary>One document as it is stored in <c>wwwroot/corpus.json</c>.</summary>
/// <param name="Id">Stable document id, used as the result label's fallback.</param>
/// <param name="Title">Human-readable title, indexed as a structured field.</param>
/// <param name="Category">The demo corpus's topic, kept for display.</param>
/// <param name="Text">The indexed body.</param>
internal sealed record CorpusDocument(string Id, string Title, string Category, string Text);

/// <summary>
/// The precomputed document vectors in <c>wwwroot/embeddings.json</c>, with the metadata that
/// says where they came from. <see cref="Documents"/> is keyed by document id.
/// </summary>
/// <param name="Model">The model that produced the vectors, e.g. <c>BAAI/bge-small-en-v1.5</c>.</param>
/// <param name="Dimension">The length of every vector.</param>
/// <param name="Normalized">Whether the stored vectors are unit length.</param>
/// <param name="Generator">The tool and method that produced the file.</param>
/// <param name="Documents">Document id to embedding.</param>
internal sealed record EmbeddingFile(
    string Model,
    int Dimension,
    bool Normalized,
    string Generator,
    Dictionary<string, float[]> Documents);

/// <summary>
/// Source-generated JSON for the demo's static data. Blazor WebAssembly trims in Release, and
/// reflection-based <see cref="System.Text.Json"/> is exactly the kind of thing trimming removes;
/// generating the readers also keeps the first load off the reflection path.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<CorpusDocument>))]
[JsonSerializable(typeof(EmbeddingFile))]
internal sealed partial class CorpusJsonContext : JsonSerializerContext;
