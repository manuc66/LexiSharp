using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Demo;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The demo's <c>--corpus</c> and <c>--segmentation</c> options: which corpus is loaded, which
/// tokens exist, and what the page says about both.
/// </summary>
/// <remarks>
/// Both options decide something a reader takes at face value — the corpus a lane's score was
/// measured on, and the token a term became — so a wrong answer here is a wrong answer on the page,
/// and neither is visible from the code alone. The segmentation cases are checked against the
/// tokenizer rather than against the description's own wording, because the description is the claim
/// under test.
/// </remarks>
public sealed class DemoCorpusCatalogTests
{
    [Fact]
    public void TheDefaultCorpusNeedsNoDataDirectory()
    {
        // The whole reason the option exists in this shape: a fresh clone runs with no options and
        // no download, so the built-in corpus must resolve without a harness data directory.
        DemoCorpusSet corpus = DemoCorpusCatalog.Load(DemoCorpusCatalog.BuiltIn, null, "/nonexistent");

        Assert.Equal(DemoCorpusCatalog.BuiltIn, corpus.Key);
        Assert.NotEmpty(corpus.Documents);
        Assert.NotEmpty(corpus.SampleQueries);
    }

    [Fact]
    public void AnUnknownCorpusNamesEveryValueItAccepts()
    {
        // A caller who typed the wrong key has to learn the right one from the message alone.
        DemoCorpusException error = Assert.Throws<DemoCorpusException>(
            () => DemoCorpusCatalog.Load("beir", null, "/nonexistent"));

        Assert.Contains(DemoCorpusCatalog.BuiltIn, error.Message, StringComparison.Ordinal);

        foreach (string key in new[] { "nfcorpus", "scifact", "arguana" })
            Assert.Contains(key, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnExplicitDataDirectoryIsUsedAsGivenAndNeverSearched()
    {
        // --data-dir is a decision, not a hint: FindDataDirectory returns it verbatim, so a caller
        // pointing at the wrong path is told about that path rather than having a different directory
        // silently substituted for it. That also makes it the only way to reach this message when the
        // machine does hold a fetched corpus -- leaving the option unset searches upward from the
        // working directory and would find it.
        string absent = Path.Combine(Path.GetTempPath(), "lexisharp-absent-" + Guid.NewGuid().ToString("n"));

        DemoCorpusException error = Assert.Throws<DemoCorpusException>(
            () => DemoCorpusCatalog.Load("scifact", absent, "/nonexistent"));

        Assert.Contains(absent, error.Message, StringComparison.Ordinal);
        Assert.Contains("not in", error.Message, StringComparison.Ordinal);
        Assert.Contains("LexiSharp.Eval", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ADataDirectoryWithoutTheCorpusSaysWhichOneIsMissing()
    {
        // The "found a directory, corpus not in it" message, and a different one: this is what a
        // caller reads to tell "wrong path" from "fetch it". Pinned as its own case because an existing
        // but empty directory is found, not missing, and that is the distinction the branches turn on.
        using DataDirectory empty = DataDirectory.Empty();

        DemoCorpusException error = Assert.Throws<DemoCorpusException>(
            () => DemoCorpusCatalog.Load("arguana", empty.Path, "/nonexistent"));

        Assert.Contains("arguana", error.Message, StringComparison.Ordinal);
        Assert.Contains(empty.Path, error.Message, StringComparison.Ordinal);
        Assert.Contains("LexiSharp.Eval", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AHitCardShowsTheTitleFromATextFieldRatherThanTheDocumentId()
    {
        // The bug this file exists partly to pin. A BEIR corpus carries its title as a TextFields
        // entry -- the shape BuildDocuments builds, and the shape the index reads for field-aware
        // scoring -- while the card's title was being read from Fields. Nothing about the code looked
        // wrong: the lookup found no field and returned the document id, so every card on the one
        // corpus whose documents have titles was titled with its id, and only running it showed that.
        DemoSearchService service = new(
            new DemoCorpusSet(
                "test",
                "test",
                [new SearchDocument("doc-1", "statin breast cancer survival", TextFields: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "Statin use and breast cancer survival" })],
                ["statin breast cancer"]),
            WordSegmentation.UnicodeWordBoundaries);

        HitResult hit = service.Compare("breast cancer", limit: 1).Lanes[0].Hits[0];

        Assert.Equal("doc-1", hit.Id);
        Assert.Equal("Statin use and breast cancer survival", hit.Title);
    }

    [Fact]
    public void AHitCardFallsBackToTheDocumentIdWhenTheCorpusHasNoTitle()
    {
        // The other side of the same lookup, and the reason it can be a fallback rather than a crash:
        // the built-in corpus has no titles, and an id in that slot is honest where a wrong title
        // would not be.
        DemoSearchService service = new(
            new DemoCorpusSet(
                "test",
                "test",
                [new SearchDocument("doc-1", "statin breast cancer survival", Category: "test")],
                ["statin breast cancer"]),
            WordSegmentation.UnicodeWordBoundaries);

        HitResult hit = service.Compare("breast cancer", limit: 1).Lanes[0].Hits[0];

        Assert.Equal("doc-1", hit.Title);
        Assert.Equal("test", hit.Category);
    }

    [Fact]
    public void ACorpusIsAvailableOnlyWhenItsFilesAre()
    {
        // Availability is a property of the machine, decided per data directory -- never a constant,
        // or the UI offers a corpus this run cannot open.
        using DataDirectory empty = DataDirectory.Empty();

        DemoCorpusOption[] options = [.. DemoCorpusCatalog.Describe(empty.Path, "/nonexistent")];

        DemoCorpusOption builtIn = Assert.Single(options, o => o.Key == DemoCorpusCatalog.BuiltIn);
        Assert.True(builtIn.Available);

        foreach (DemoCorpusOption beir in options.Where(o => o.Key != DemoCorpusCatalog.BuiltIn))
            Assert.False(beir.Available);

        Assert.Equal(4, options.Length);
    }

    [Fact]
    public void ACorpusOnDiskIsOfferedByName()
    {
        // The other half of the previous case: a corpus whose files are present is listed as
        // available, so the two tests together pin availability to the files rather than to a list.
        using DataDirectory data = DataDirectory.WithBeirCorpus("scifact");

        DemoCorpusOption scifact = Assert.Single(
            DemoCorpusCatalog.Describe(data.Path, "/nonexistent"),
            o => o.Key == "scifact");

        Assert.True(scifact.Available);
        Assert.NotEmpty(scifact.Label);
    }

    [Fact]
    public void ABeirCorpusCarriesItsTitleAsATextFieldAndItsKeyAsCategory()
    {
        // The shape the harness's Evaluation.BuildDocuments builds, which is what makes a demo figure
        // comparable to a harness one at all. Category is set because the hit card renders it
        // unconditionally: an unset category would put the document id in that slot.
        using DataDirectory data = DataDirectory.WithBeirCorpus("nfcorpus");

        DemoCorpusSet corpus = DemoCorpusCatalog.Load("nfcorpus", data.Path, "/nonexistent");

        SearchDocument first = corpus.Documents[0];
        Assert.Equal("doc-1", first.Id);
        Assert.Equal("body text", first.Text);
        Assert.Equal("a title", first.TextFields!["title"]);
        Assert.Equal("nfcorpus", first.Category);
        Assert.Null(first.Fields);
    }

    [Fact]
    public void ALineWithoutATitleIsIndexedWithoutTheField()
    {
        // ArguAna carries no title, and a field with an empty value is not the same as no field.
        using DataDirectory data = DataDirectory.WithBeirCorpus("arguana", includeTitle: false);

        DemoCorpusSet corpus = DemoCorpusCatalog.Load("arguana", data.Path, "/nonexistent");

        Assert.Null(corpus.Documents[0].TextFields);
        Assert.Equal("body text", corpus.Documents[0].Text);
    }

    [Fact]
    public void SampleQueriesAreTheOnesWithAPositiveJudgement()
    {
        // A chip is a query the corpus can answer, so an unjudged one has no known relevant document
        // and does not belong on the page.
        using DataDirectory data = DataDirectory.WithBeirCorpus("scifact");

        DemoCorpusSet corpus = DemoCorpusCatalog.Load("scifact", data.Path, "/nonexistent");

        Assert.Equal(["judged query", "another judged query"], corpus.SampleQueries);
    }

    [Fact]
    public void AQrelsFileThatJudgesNothingLeavesTheQueriesUnfiltered()
    {
        // Not the same as no usable query. A missing qrels file cannot filter, and neither can one whose
        // every score is zero or negative -- the id set comes back empty, which disables the filter
        // rather than emptying the chips. A corpus is not unusable because nothing has been judged yet,
        // so the demo offers every query it has, including the unjudged ones.
        using DataDirectory none = DataDirectory.WithBeirCorpus("scifact", judgedScores: [0, 0]);
        using DataDirectory missing = DataDirectory.WithBeirCorpus("scifact", writeQrels: false);

        foreach (string path in new[] { none.Path, missing.Path })
            Assert.Equal(
                ["judged query", "another judged query", "never judged"],
                DemoCorpusCatalog.Load("scifact", path, "/nonexistent").SampleQueries);
    }

    [Fact]
    public void ACorpusWithNoUsableQueryIsRefusedRatherThanServedWithUnanswerableChips()
    {
        // Judged, positively, and none of the corpus's own queries is among them: the filter is active
        // and admits nothing, so there is no chip that has a known relevant document and the caller is
        // told rather than handed a page of queries this corpus cannot answer. The id has to be one the
        // queries.jsonl does not carry -- judging q3 positively does admit a chip, which is the case the
        // previous test covers.
        using DataDirectory data = DataDirectory.WithBeirCorpus("scifact", judgedQueryIds: ["q-unknown"]);

        DemoCorpusException error = Assert.Throws<DemoCorpusException>(
            () => DemoCorpusCatalog.Load("scifact", data.Path, "/nonexistent"));

        Assert.Contains("qrels/test.tsv", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQrelsFileWithoutAHeaderStillContributesItsJudgements()
    {
        // The header is a convention, not a guarantee: reading rows positionally and skipping only
        // what does not parse keeps a judgement that a header-sniffing reader would have dropped.
        using DataDirectory data = DataDirectory.WithBeirCorpus("scifact", qrelsHeader: false);

        DemoCorpusSet corpus = DemoCorpusCatalog.Load("scifact", data.Path, "/nonexistent");

        Assert.Equal(["judged query", "another judged query"], corpus.SampleQueries);
    }

    [Fact]
    public void FilesUnpackedStraightIntoTheDataDirectoryAreFound()
    {
        // The harness leaves either layout behind depending on how it was fetched, and which one is on
        // disk is not something the demo gets to choose.
        using DataDirectory data = DataDirectory.WithBeirCorpus("scifact", perDatasetDirectory: false);

        DemoCorpusSet corpus = DemoCorpusCatalog.Load("scifact", data.Path, "/nonexistent");

        Assert.NotEmpty(corpus.Documents);
    }

    [Fact]
    public void AnUnknownSegmentationNamesEveryValueItAccepts()
    {
        DemoOptionException error = Assert.Throws<DemoOptionException>(() => DemoAnalysis.Resolve("uax-29"));

        foreach (string name in DemoAnalysis.Names)
            Assert.Contains(name, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("flat", WordSegmentation.Flat)]
    [InlineData("uax29", WordSegmentation.UnicodeWordBoundaries)]
    public void EveryAcceptedSegmentationNameResolvesAndNamesBack(string name, WordSegmentation expected)
    {
        Assert.Equal(expected, DemoAnalysis.Resolve(name));
        Assert.Equal(name, DemoAnalysis.Name(expected));
    }

    [Fact]
    public void TheDefaultSegmentationIsTheOneTheDocsCallTheDefault()
    {
        // The demo's own corpus ships text with `1,000` and `don't` in it, so this is observable
        // without a BEIR download.
        Assert.Equal("uax29", DemoAnalysis.DefaultName);
        Assert.Contains(DemoAnalysis.DefaultName, DemoAnalysis.Names);
    }

    [Theory]
    [InlineData("uax29", new[] { "1,000", "don't", "stop", "e.g", "this", "a:b_c" })]
    [InlineData("flat", new[] { "000", "don", "stop", "this" })]
    public void TheSegmentationDescriptionNamesTermsTheTokenizerActuallyProduces(string name, string[] expected)
    {
        // The description is a claim about the tokenizer, so it is checked against the tokenizer: the
        // expected terms are asserted as the tokenizer's whole output first, so this cannot pass by
        // agreeing with a description that is wrong about the corpus. A wording that reads correctly
        // and names a term no analysis produces is the failure this pins -- it reached /api/meta and
        // --help before anything compared the two.
        ITokenizer tokenizer = new Tokenizer(new TokenizerOptions
        {
            WordSegmentation = DemoAnalysis.Resolve(name),
        });

        Assert.Equal(expected, tokenizer.Tokenize("1,000 don't stop; e.g. this a:b_c"));

        // What the description has to name differs by rule, and the difference is the point. `flat` is
        // a single sentence -- "every non-word character ends a word" -- so its description carries a
        // worked example of what that does to a token. `uax29` is a table of conditions, so naming the
        // conditions is what predicts a case, and a worked example would be an illustration of one
        // rather than a substitute for the rule.
        string description = DemoAnalysis.Describe(DemoAnalysis.Resolve(name));
        string[] required = name == "flat"
            ? ["000", "don"]
            : ["comma and semicolon between two digits", "colon between two letters", "apostrophes within one class"];

        foreach (string fragment in required)
            Assert.Contains(fragment, description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFlatDescriptionDoesNotPromiseALoneDigit()
    {
        // Named separately because it is the specific sentence that was wrong: a term shorter than two
        // characters is dropped unless KeepSingleCharTerms is set, so `1,000` yields `000` and the `1`
        // is absent rather than present-and-light.
        string description = DemoAnalysis.Describe(WordSegmentation.Flat);

        Assert.Contains("000", description, StringComparison.Ordinal);
        Assert.DoesNotContain("1 and 000", description, StringComparison.Ordinal);
    }

    /// <summary>A throwaway harness-shaped data directory, and the BEIR files inside it.</summary>
    private sealed class DataDirectory : IDisposable
    {
        private DataDirectory(string path) => Path = path;

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);

        /// <summary>An existing data directory holding no corpus: what a machine without a fetch has.</summary>
        public static DataDirectory Empty()
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lexisharp-demo-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(root);

            return new DataDirectory(root);
        }

        public static DataDirectory WithBeirCorpus(
            string key,
            bool includeTitle = true,
            bool perDatasetDirectory = true,
            bool qrelsHeader = true,
            bool writeQrels = true,
            string[]? judgedQueryIds = null,
            double[]? judgedScores = null)
        {
            string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lexisharp-demo-" + Guid.NewGuid().ToString("n"));
            string directory = perDatasetDirectory ? System.IO.Path.Combine(root, key) : root;
            Directory.CreateDirectory(System.IO.Path.Combine(directory, "qrels"));

            WriteLines(System.IO.Path.Combine(directory, "corpus.jsonl"), includeTitle
                ? ["""{"_id": "doc-1", "title": "a title", "text": "body text"}"""]
                : ["""{"_id": "doc-1", "text": "body text"}"""]);

            // A malformed JSONL line is deliberately absent. Neither the demo nor the harness's
            // BeirCorpus guards JsonDocument.Parse, so a corrupt line throws from both -- matching
            // the harness is the contract, and a demo that silently skipped such a line would index
            // a different corpus than the harness ranks.
            WriteLines(System.IO.Path.Combine(directory, "queries.jsonl"),
            [
                """{"_id": "q1", "text": "judged query"}""",
                """{"_id": "q2", "text": "another judged query"}""",
                """{"_id": "q3", "text": "never judged"}""",
                """{"_id": "q4", "text": ""}""",
                "",
            ]);

            if (writeQrels)
            {
                string[] ids = judgedQueryIds ?? ["q1", "q2"];
                string[] rows = [.. ids.Select((id, index) => $"{id}\tdoc-1\t{judgedScores?[index] ?? 1}")];

                WriteLines(System.IO.Path.Combine(directory, "qrels", "test.tsv"), qrelsHeader
                    ? ["query-id\tcorpus-id\tscore", .. rows]
                    : [.. rows, "malformed row"]);
            }

            return new DataDirectory(root);
        }

        private static void WriteLines(string path, IEnumerable<string> lines) =>
            File.WriteAllText(path, string.Join('\n', lines) + '\n');
    }
}
