using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The sparse row's contract with its producer: the digest that keys a vector, the file format, and
/// what happens when a text has no vector. These are the failures that would silently lower a metric
/// — a key computed over different bytes, a line skipped, a missing vector read as "no match" — so
/// each one is refused by name rather than absorbed.
/// </summary>
public class SparseVectorsTests
{
    /// <summary>
    /// The digests the Python encoder writes, reproduced from C#. The fixture is produced by the
    /// script's own <c>digest()</c>, so a change to either side's encoding — a BOM, a normalization
    /// step, upper- versus lowercase hex — fails here instead of producing vectors that are never
    /// found and a row that reads as a model result.
    /// </summary>
    [Fact]
    public void TheHashAgreesWithTheEncoderOnATinyFixture()
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "sparse", "hash-fixture.jsonl");
        Assert.True(File.Exists(fixture), $"fixture missing: {fixture}");

        int checkedTexts = 0;

        foreach (string line in File.ReadLines(fixture))
        {
            using JsonDocument record = JsonDocument.Parse(line);
            string text = record.RootElement.GetProperty("text").GetString()!;
            string expected = record.RootElement.GetProperty("sha256").GetString()!;

            string actual = SparseVectorSet.HashText(text);

            Assert.Equal(expected, actual);
            Assert.Equal(actual.ToLowerInvariant(), actual);
            Assert.Equal(64, actual.Length);
            checkedTexts++;
        }

        Assert.Equal(6, checkedTexts);
    }

    [Fact]
    public void ADocumentIsEncodedFromItsTitleInFrontOfItsBody()
    {
        // The producer's rule, so that a corpus whose titles were dropped on one side is a visible
        // difference rather than a weaker ranking attributed to the model.
        Assert.Equal("Bile salts\nbile salts emulsify fat", SparseVectorSet.DocumentText("Bile salts", "bile salts emulsify fat"));
        Assert.Equal("bile salts emulsify fat", SparseVectorSet.DocumentText("", "bile salts emulsify fat"));
        Assert.Equal("bile salts emulsify fat", SparseVectorSet.DocumentText(null, "bile salts emulsify fat"));
    }

    [Fact]
    public async System.Threading.Tasks.Task TheVectorsLoadAndTheProviderServesBothRoles()
    {
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile salts emulsify fat", "bile:2.5\tfat:1.25")],
                        queries: [("bile salts", "bile:2\t##salts:0.5")]);

        SparseVectorSet set = SparseVectorSet.Load(directory.Path, "test");

        Assert.Equal("SPLADE-v3 (ONNX, 128-token truncation, top-64 terms, vectors precomputed offline)",
            set.Manifest.Description);
        Assert.Single(set.Documents);
        Assert.Single(set.Queries);
        Assert.Equal(2.5f, set.Documents[SparseVectorSet.HashText("bile salts emulsify fat")]["bile"]);

        var provider = new SparseVectorProvider(set);

        IReadOnlyDictionary<string, float> passage = await provider
            .GetSparseEmbeddingAsync("bile salts emulsify fat", EmbeddingUse.Passage);
        Assert.Equal(1.25f, passage["fat"]);

        IReadOnlyDictionary<string, float> query = await provider
            .GetSparseEmbeddingAsync("bile salts", EmbeddingUse.Query);
        Assert.Equal(2f, query["bile"]);
    }

    [Fact]
    public void ATermThatIsItselfAColonSurvivesTheSplit()
    {
        // WordPiece tokens carry no whitespace but they do carry punctuation, and the vocabulary
        // holds ':' and '##：'. Splitting on the first colon would make the weight '1.5' and drop
        // the token, silently turning "no weight" into a term called ":".
        using var directory = new VectorDirectory();
        directory.Write(documents: [("a: b", ":1.5\t##::2")], queries: [("a", ":0.5")]);

        SparseVectorSet set = SparseVectorSet.Load(directory.Path, "test");

        IReadOnlyDictionary<string, float> vector =
            set.Documents[SparseVectorSet.HashText("a: b")];

        Assert.Equal(1.5f, vector[":"]);
        Assert.Equal(2f, vector["##:"]);
    }

    [Fact]
    public async System.Threading.Tasks.Task AMissingVectorForAQueryIsRefusedByNameRatherThanReadAsNoMatch()
    {
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile", "bile:1")], queries: [("bile salts", "bile:1")]);

        SparseVectorSet set = SparseVectorSet.Load(directory.Path, "test");
        var provider = new SparseVectorProvider(set);

        SparseVectorsException error = await Assert.ThrowsAsync<SparseVectorsException>(() =>
            provider.GetSparseEmbeddingAsync("nothing here", EmbeddingUse.Query));

        Assert.Contains("no precomputed query vector", error.Message, StringComparison.Ordinal);
        Assert.Contains(SparseVectorSet.HashText("nothing here"), error.Message, StringComparison.Ordinal);
        Assert.Contains("nothing here", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAbsentVectorsDirectoryIsRefusedWithTheCommandThatWouldCreateIt()
    {
        string absent = Path.Combine(Path.GetTempPath(), "lexisharp-sparse-absent-" + Guid.NewGuid().ToString("n"));

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseVectorSet.Load(absent, "nfcorpus"));

        Assert.Contains("no precomputed vectors", error.Message, StringComparison.Ordinal);
        Assert.Contains("encode_splade.py", error.Message, StringComparison.Ordinal);
        Assert.Contains("No row is produced without them", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVectorFileWithoutItsManifestIsRefused()
    {
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile", "bile:1")], queries: [("bile", "bile:1")], writeManifest: false);

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseVectorSet.Load(directory.Path, "test"));

        Assert.Contains("cannot say which model", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMalformedLineIsRefusedWithItsLineNumber()
    {
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile", "bile:1")], queries: [("bile", "bile:1")]);

        File.WriteAllText(
            Path.Combine(directory.Path, "test.docs.tsv"),
            $"{SparseVectorSet.HashText("bile")}\tbile:1\n{SparseVectorSet.HashText("fat")}\tfat\n");

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseVectorSet.Load(directory.Path, "test"));

        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
        Assert.Contains("`token:weight`", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANonPositiveWeightIsRefusedRatherThanTreatedAsAbsent()
    {
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile", "bile:0")], queries: [("bile", "bile:1")]);

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseVectorSet.Load(directory.Path, "test"));

        Assert.Contains("treat a non-positive weight as absent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADuplicateTermIsRefused()
    {
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile", "bile:1\tbile:2")], queries: [("bile", "bile:1")]);

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseVectorSet.Load(directory.Path, "test"));

        Assert.Contains("twice", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedTextWithTheSameVectorIsAccepted()
    {
        // Two BEIR documents can share a title and a body — NFCorpus holds 40 such pairs — and a vector
        // is a function of the text. The second line is not a producer bug, so it is not refused.
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile", "bile:1"), ("bile", "bile:1")], queries: [("bile", "bile:1")]);

        SparseVectorSet set = SparseVectorSet.Load(directory.Path, "test");

        Assert.Single(set.Documents);
    }

    [Fact]
    public void RepeatedTextWithADifferentVectorIsRefused()
    {
        using var directory = new VectorDirectory();
        directory.Write(documents: [("bile", "bile:1"), ("bile", "bile:2")], queries: [("bile", "bile:1")]);

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseVectorSet.Load(directory.Path, "test"));

        Assert.Contains("repeats the digest", error.Message, StringComparison.Ordinal);
        Assert.Contains("line 2", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowIsScoredThroughTheSameMetricsAsEveryOtherRow()
    {
        BeirCorpus corpus = Corpus();

        using var directory = new VectorDirectory();
        directory.Write(
            documents:
            [
                ("Bile salts\nbile salts emulsify dietary fat", "bile:2\tsalts:2\tfat:1"),
                ("Vesicle fusion\nvesicle fusion releases neurotransmitters", "vesicle:2\tfusion:2"),
            ],
            queries:
            [
                ("bile salts", "bile:1\tsalts:1"),
                ("vesicle fusion", "vesicle:1\tfusion:1"),
            ]);

        SparseMeasurement measurement = SparseBenchmark.Measure(
            new SparseLane(directory.Path), corpus, "test", topK: 10, limit: null, NdcgGain.Linear);

        Assert.Equal(2, measurement.Documents);
        Assert.Equal(2, measurement.Result.Queries);
        Assert.Equal(1.0, measurement.Result.NdcgAt10, precision: 6);
        Assert.Contains("precomputed offline", measurement.Description, StringComparison.Ordinal);
        Assert.Contains(measurement.Caveats, caveat => caveat.Contains("no encode cost", StringComparison.Ordinal));
    }

    [Fact]
    public void ACorpusTheVectorsDoNotCoverIsRefusedWithTheCountAndAnExample()
    {
        BeirCorpus corpus = Corpus();

        using var directory = new VectorDirectory();
        directory.Write(
            documents: [("Bile salts\nbile salts emulsify dietary fat", "bile:1")],
            queries:
            [
                ("bile salts", "bile:1"),
                ("vesicle fusion", "vesicle:1"),
            ]);

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseBenchmark.Measure(
                new SparseLane(directory.Path), corpus, "test", topK: 10, limit: null, NdcgGain.Linear));

        Assert.Contains("1 of 2 test documents", error.Message, StringComparison.Ordinal);
        Assert.Contains("'d2'", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ATestQueryWithoutAVectorIsRefusedTheseToo()
    {
        BeirCorpus corpus = Corpus();

        using var directory = new VectorDirectory();
        directory.Write(
            documents:
            [
                ("Bile salts\nbile salts emulsify dietary fat", "bile:1"),
                ("Vesicle fusion\nvesicle fusion releases neurotransmitters", "vesicle:1"),
            ],
            queries: [("bile salts", "bile:1")]);

        SparseVectorsException error = Assert.Throws<SparseVectorsException>(() =>
            SparseBenchmark.Measure(
                new SparseLane(directory.Path), corpus, "test", topK: 10, limit: null, NdcgGain.Linear));

        Assert.Contains("1 of 2 test queries in test have no vector", error.Message, StringComparison.Ordinal);
        Assert.Contains("'q2'", error.Message, StringComparison.Ordinal);
        Assert.Contains("reads as a ranking result", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async System.Threading.Tasks.Task AnAbsentVectorsDirectoryIsRefusedByTheHarnessBeforeAnyCorpusIsRead()
    {
        // The lane's argument is a directory, and a path that is not there is a misspelled flag rather
        // than a corpus that cannot be measured. Refused up front, so no run spends its first minutes
        // downloading a corpus to then produce a table with no sparse row in it.
        string absent = Path.Combine(Path.GetTempPath(), "lexisharp-sparse-absent-" + Guid.NewGuid().ToString("n"));
        TextWriter original = Console.Error;
        var captured = new StringWriter();

        try
        {
            Console.SetError(captured);
            int exitCode = await LexiSharp.Eval.Program.Main(
                ["--dataset", "nfcorpus", "--no-tuned", "--sparse", absent]);

            Assert.Equal(2, exitCode);
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Contains("--sparse", captured.ToString(), StringComparison.Ordinal);
        Assert.Contains("does not exist", captured.ToString(), StringComparison.Ordinal);
        Assert.Contains("encode_splade.py", captured.ToString(), StringComparison.Ordinal);
    }

    private static BeirCorpus Corpus() => new()
    {
        Name = "test",
        Documents =
        [
            new BeirDocument("d1", "Bile salts", "bile salts emulsify dietary fat"),
            new BeirDocument("d2", "Vesicle fusion", "vesicle fusion releases neurotransmitters"),
        ],
        Queries = [new BeirQuery("q1", "bile salts"), new BeirQuery("q2", "vesicle fusion")],
        TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
        {
            ["q1"] = new Dictionary<string, double> { ["d1"] = 1 },
            ["q2"] = new Dictionary<string, double> { ["d2"] = 1 },
        },
    };

    /// <summary>
    /// A vector directory written the way the encoder writes one, so the loader is exercised on the
    /// real shape: a manifest beside two tab-separated files keyed by digest.
    /// </summary>
    private sealed class VectorDirectory : IDisposable
    {
        public VectorDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "lexisharp-sparse-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Write(
            IReadOnlyList<(string Text, string Terms)> documents,
            IReadOnlyList<(string Text, string Terms)> queries,
            bool writeManifest = true)
        {
            File.WriteAllText(System.IO.Path.Combine(Path, "test.docs.tsv"), Lines(documents), new UTF8Encoding(false));
            File.WriteAllText(System.IO.Path.Combine(Path, "test.queries.tsv"), Lines(queries), new UTF8Encoding(false));

            if (!writeManifest)
                return;

            File.WriteAllText(
                System.IO.Path.Combine(Path, "test.meta.json"),
                """
                {
                  "model": "SPLADE-v3",
                  "base_model": "naver/splade-v3",
                  "license": "CC BY-NC-SA 4.0",
                  "model_file": "splade-v3.onnx",
                  "model_sha256": "9e472022f478fec8c4afaa397e68f9c90bb5ff98f0f5c0c6bb195cf2b388ab30",
                  "quantised": false,
                  "truncate": 128,
                  "top_k": 64,
                  "document_text": "title + \\n + text when the record has a title, else text",
                  "tokenizer": "test",
                  "runtime": "test",
                  "seconds": 1.5,
                  "documents": { "texts": 1, "terms_min": 1, "terms_mean": 2.0, "terms_max": 2, "seconds": 1.0 },
                  "queries": { "texts": 1, "terms_min": 1, "terms_mean": 1.0, "terms_max": 1, "seconds": 0.5 }
                }
                """,
                new UTF8Encoding(false));
        }

        private static string Lines(IReadOnlyList<(string Text, string Terms)> texts) =>
            string.Concat(texts.Select(text => $"{SparseVectorSet.HashText(text.Text)}\t{text.Terms}\n"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // A temp directory left behind is not a test failure.
            }
        }
    }
}
