using System.Text.Json;
using LexiSharp.Core;

namespace LexiSharp.Demo;

/// <summary>
/// A small, hand-written corpus about authentication, information retrieval and databases,
/// padded with off-topic documents. It is dense in vocabulary overlaps so that corpus-derived
/// semantic expansion has something to learn, while the distractors prove it does not fire
/// everywhere.
/// </summary>
/// <remarks>
/// The documents live in <c>samples/LexiSharp.Demo/corpus.json</c>, which is also served as a
/// static file by the browser demo. That file is the source of truth: this class embeds it in the
/// server assembly and maps it to <see cref="SearchDocument"/>, so a document is edited in one
/// place and both hosts see it. Until the services move to a shared library, this is what keeps
/// the two demos on the same corpus.
/// </remarks>
public static class DemoCorpus
{
    /// <summary>Ready-to-use queries for the demo's suggestion chips.</summary>
    public static readonly IReadOnlyList<string> SampleQueries =
    [
        "refresh token",
        "short lived credential",
        "ranking quality",
        "session expiry",
        "embeddings",
        "inverted index",
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>Builds the indexed documents (title and category as structured fields).</summary>
    public static IReadOnlyList<SearchDocument> Build() =>
        Load().Select(entry => new SearchDocument(
            entry.Id,
            entry.Text,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["title"] = entry.Title,
                ["category"] = entry.Category,
            },
            entry.Category)).ToList();

    private static List<CorpusEntry> Load()
    {
        using Stream stream = typeof(DemoCorpus).Assembly.GetManifestResourceStream("LexiSharp.Demo.corpus.json")
            ?? throw new InvalidOperationException(
                "The embedded corpus.json is missing. Check the EmbeddedResource item in LexiSharp.Demo.csproj.");

        return JsonSerializer.Deserialize<List<CorpusEntry>>(stream, JsonOptions)
            ?? throw new InvalidOperationException("corpus.json is empty.");
    }

    /// <summary>One row of <c>corpus.json</c>; the shape the browser demo's JavaScript sees too.</summary>
    private sealed record CorpusEntry(string Id, string Title, string Category, string Text);
}
