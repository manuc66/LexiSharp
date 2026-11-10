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

    private sealed record CorpusDocument(string Id, string Title, string Category, string Text);

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
