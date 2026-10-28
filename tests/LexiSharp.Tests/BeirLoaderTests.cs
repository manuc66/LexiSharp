using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Loading a BEIR-format directory for a corpus that carries no published quality figure.
/// </summary>
/// <remarks>
/// The harness's own list pins a corpus's expected nDCG beside its checksum, because every corpus
/// there is a row in the quality table. A benchmark lane needs the loader without the figure:
/// measuring the cursor pass's density crossing on real documents asks for the documents, not for
/// what BM25 scores on them. These tests pin the half of the loader that does not download — which
/// directory it accepts and what it parses out of it — because the download half needs a network and
/// a checksum nobody can compute offline.
/// </remarks>
public class BeirLoaderTests : IDisposable
{
    private const string UnreachableUrl = "http://127.0.0.1:1/unreachable.zip";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "lexisharp-beir-" + Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ALocalCorpusIsReadByItsOwnDirectoryName()
    {
        WriteCorpus(Path.Combine(_root, "bench-only"), title: "a title");

        BeirCorpus corpus = await BeirLoader.LoadOrDownloadAsync(_root, "bench-only", UnreachableUrl, "not-checked");

        BeirDocument document = Assert.Single(corpus.Documents);

        Assert.Equal("doc-1", document.Id);
        Assert.Equal("a title", document.Title);
        Assert.Equal("body text", document.Text);
        Assert.Equal("bench-only", corpus.Name);
        Assert.Equal("q1", Assert.Single(corpus.Queries).Id);
    }

    /// <summary>
    /// A document with no title field reads back with an empty one rather than null, which is what the
    /// index hands to a field-scoped scorer.
    /// </summary>
    [Fact]
    public async Task AMissingTitleIsEmptyRatherThanNull()
    {
        WriteCorpus(_root, title: null);

        BeirCorpus corpus = await BeirLoader.LoadOrDownloadAsync(_root, "flat", UnreachableUrl, "not-checked");

        Assert.Equal(string.Empty, Assert.Single(corpus.Documents).Title);
    }

    /// <summary>
    /// A directory holding a corpus but no qrels is not that corpus: the loader tries to fetch the
    /// real one instead of indexing half of a dataset. Half a corpus scores a plausible number, which
    /// is the failure mode this pins against.
    /// </summary>
    [Fact]
    public async Task ADirectoryWithoutQrelsIsNotTakenAsTheCorpus()
    {
        WriteCorpus(_root, title: null, writeQrels: false);

        await Assert.ThrowsAsync<HttpRequestException>(
            () => BeirLoader.LoadOrDownloadAsync(_root, "flat", UnreachableUrl, "not-checked"));
    }

    private void WriteCorpus(string directory, string? title, bool writeQrels = true)
    {
        Directory.CreateDirectory(Path.Combine(directory, "qrels"));

        File.WriteAllLines(
            Path.Combine(directory, "corpus.jsonl"),
            [
                title is null
                    ? """{"_id": "doc-1", "text": "body text"}"""
                    : $$"""{"_id": "doc-1", "title": "{{title}}", "text": "body text"}""",
            ]);

        File.WriteAllLines(Path.Combine(directory, "queries.jsonl"), ["""{"_id": "q1", "text": "a query"}"""]);

        if (writeQrels)
            File.WriteAllLines(Path.Combine(directory, "qrels", "test.tsv"), ["query-id\tdoc-id\tscore", "q1\tdoc-1\t1"]);
    }
}
