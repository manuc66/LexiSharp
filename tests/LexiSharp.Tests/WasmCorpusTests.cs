using System.Linq;
using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Demo;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The in-browser demo loads its corpus from <c>samples/LexiSharp.WasmDemo/wwwroot/corpus.json</c>,
/// a copy of the server demo's built-in corpus. Two hosts, one corpus: until the services move to a
/// shared library, this is what stops the copy from drifting.
/// </summary>
/// <remarks>
/// The JSON is generated from <see cref="DemoCorpus.Build"/> and then frozen. Editing the server
/// corpus without regenerating the file would leave the browser demo quietly answering over
/// different documents — the kind of divergence no compiler sees and neither test suite would
/// otherwise cover.
/// </remarks>
public sealed class WasmCorpusTests
{
    [Fact]
    public void TheWasmCorpusMatchesTheServerDemosBuiltInCorpus()
    {
        string path = Path.Combine(
            RepositoryRoot, "samples", "LexiSharp.WasmDemo", "wwwroot", "corpus.json");

        CorpusDocument[] documents = JsonSerializer.Deserialize<CorpusDocument[]>(
            File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        IReadOnlyList<SearchDocument> expected = DemoCorpus.Build();

        Assert.Equal(expected.Count, documents.Length);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].Id, documents[i].Id);
            Assert.Equal(expected[i].Fields!["title"], documents[i].Title);
            Assert.Equal(expected[i].Fields!["category"], documents[i].Category);
            Assert.Equal(expected[i].Text, documents[i].Text);
        }
    }

    [Fact]
    public void TheWasmEmbeddingsCoverEveryDocumentWithUnitVectors()
    {
        string directory = Path.Combine(RepositoryRoot, "samples", "LexiSharp.WasmDemo", "wwwroot");

        CorpusDocument[] corpus = JsonSerializer.Deserialize<CorpusDocument[]>(
            File.ReadAllText(Path.Combine(directory, "corpus.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        EmbeddingFile file = JsonSerializer.Deserialize<EmbeddingFile>(
            File.ReadAllText(Path.Combine(directory, "embeddings.json")),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal("BAAI/bge-small-en-v1.5", file.Model);
        Assert.Equal(384, file.Dimension);
        Assert.True(file.Normalized);

        Assert.Equal(
            corpus.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal),
            file.Documents.Keys.OrderBy(id => id, StringComparer.Ordinal));

        foreach ((string id, float[] vector) in file.Documents)
        {
            Assert.Equal(file.Dimension, vector.Length);

            double norm = Math.Sqrt(vector.Sum(value => (double)value * value));
            Assert.InRange(norm, 0.9999, 1.0001);
            Assert.All(vector, value => Assert.True(float.IsFinite(value), $"{id} has a non-finite value."));
        }
    }

    private sealed record CorpusDocument(string Id, string Title, string Category, string Text);

    private sealed record EmbeddingFile(
        string Model,
        int Dimension,
        bool Normalized,
        string Generator,
        Dictionary<string, float[]> Documents);

    private static string RepositoryRoot
    {
        get
        {
            // The test assembly runs out of tests/.../bin/<config>/<tfm>; walk up to the root.
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
                directory = directory.Parent;

            Assert.NotNull(directory);
            return directory.FullName;
        }
    }
}
