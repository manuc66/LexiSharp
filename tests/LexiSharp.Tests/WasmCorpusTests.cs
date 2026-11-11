using System.Linq;
using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Demo;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The demos' data files: the corpus both hosts read, and the vectors the browser demo's
/// "similar documents" panel needs.
/// </summary>
/// <remarks>
/// <para>
/// <c>samples/LexiSharp.Demo/corpus.json</c> is the one corpus. The server demo embeds it and maps
/// it to <see cref="SearchDocument"/>; the browser demo serves it as a static file. The first test
/// pins that mapping, because a loader that dropped <c>category</c> or reordered the rows would
/// leave both hosts internally consistent and the two demos showing different documents.
/// </para>
/// <para>
/// The embeddings are derived data: they must cover exactly the corpus ids, with one unit vector
/// per document. Editing the corpus without rerunning
/// <c>python3 scripts/generate-embeddings.py</c> is what this catches.
/// </para>
/// </remarks>
public sealed class WasmCorpusTests
{
    private static readonly string CorpusPath =
        Path.Combine(RepositoryRoot, "samples", "LexiSharp.Demo", "corpus.json");

    private static readonly string EmbeddingsPath =
        Path.Combine(RepositoryRoot, "samples", "LexiSharp.WasmDemo", "wwwroot", "embeddings.json");

    [Fact]
    public void TheSharedCorpusMapsToTheDocumentsTheDemosIndex()
    {
        CorpusDocument[] documents = ReadJson<CorpusDocument[]>(CorpusPath);
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
        CorpusDocument[] corpus = ReadJson<CorpusDocument[]>(CorpusPath);
        EmbeddingFile file = ReadJson<EmbeddingFile>(EmbeddingsPath);

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

    private static T ReadJson<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

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
