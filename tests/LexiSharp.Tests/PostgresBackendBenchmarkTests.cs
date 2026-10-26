using System;
using System.Collections.Generic;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Postgres;
using LexiSharp.Ranking;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The backend lane: the pieces that decide what a row means, tested without a server, and the
/// lane itself, which needs one.
/// </summary>
/// <remarks>
/// What is asserted about a live backend is membership and the shape of the measurement, never a
/// score: the number is the database's ranking function at its own parameters, so pinning it here
/// would pin PostgreSQL rather than this harness. The wiring a test can own is that the corpus,
/// the qrels and the metric code are the ones the in-memory table uses.
/// </remarks>
public class PostgresBackendBenchmarkTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    [Theory]
    [InlineData("lexical", "Lexical")]
    [InlineData("paradedb", "ParadeDb")]
    public void TheEngineNamesParseToTheEnginesTheyName(string name, string expected)
    {
        // Compared by name rather than as the enum: the enum is internal to the harness and a public
        // test method cannot take an internal parameter type. Comparing names also makes a rename of
        // a case a test failure, which is what should happen — the name is what --backend accepts.
        BackendLane lane = BackendLane.Parse(name, "Host=db;Database=search", "simple");

        Assert.Equal(expected, lane.Kind.ToString());
    }

    [Fact]
    public void TheLexicalRowCanBeWeightedPerFieldAndItsNameSaysSo()
    {
        // A lexical nDCG with and without setweight is two configurations; a table that labelled
        // both "PostgreSQL lexical" would be two rows a reader could not tell apart.
        var weights = new Dictionary<string, PostgresFieldWeight> { ["title"] = PostgresFieldWeight.A };

        BackendLane weighted = BackendLane.Parse(
            "lexical", "Host=db;Database=search", "simple", fieldWeights: weights);

        Assert.Equal(weights, weighted.FieldWeights);
        Assert.Contains("field weights title=A", weighted.Description, StringComparison.Ordinal);
        Assert.Contains(
            weighted.Caveats,
            caveat => caveat.Contains("per-field length normalisation", StringComparison.Ordinal));

        BackendLane neutral = BackendLane.Parse("lexical", "Host=db;Database=search", "simple");

        Assert.Null(neutral.FieldWeights);
        Assert.DoesNotContain("field weights", neutral.Description, StringComparison.Ordinal);
        Assert.Contains(
            neutral.Caveats,
            caveat => caveat.Contains("same weight", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("paradedb")]
    [InlineData("vector")]
    [InlineData("sparse")]
    public void FieldWeightsAreRefusedByTheBackendsThatDoNotRankOnTheTsv(string kind)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendLane.Parse(
            kind,
            "Host=db;Database=search",
            "simple",
            fieldWeights: new Dictionary<string, PostgresFieldWeight> { ["title"] = PostgresFieldWeight.A },
            // Every kind but sparse accepts and ignores this; sparse would refuse the combination
            // below for a missing directory first, which is a different refusal than the one asserted.
            sparseVectorsDirectory: "/tmp/lexisharp-unused-vectors"));

        Assert.Contains("--backend-field-weights", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownEngineNameIsRefusedAndTheMessageListsEveryNameItAccepts()
    {
        // The message is what a caller reads after a typo, so it names the five lanes rather than
        // leaving the reader to grep for them. `sparse` is one of them: the engine has a row now.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => BackendLane.Parse("sqlite", "Host=db;Database=search", "simple"));

        foreach (string name in new[] { "lexical", "paradedb", "vector", "fuzzy", "sparse" })
            Assert.Contains(name, error.Message, StringComparison.Ordinal);

        Assert.Contains("sqlite", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSparseBackendNeedsTheVectorsDirectoryItIndexesAndSaysWhatItIsFor()
    {
        // The engine has no model: a sparse row without vectors would index nothing. The refusal
        // names the flag and rules out the plausible substitute, which is the dense lane's --dense.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => BackendLane.Parse("sparse", "Host=db;Database=search", "simple"));

        Assert.Contains("--sparse", error.Message, StringComparison.Ordinal);
        Assert.Contains("--dense is not a substitute", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSparseBackendIsTheEngineItNamesAndKeepsItsVectorsDirectory()
    {
        BackendLane lane = BackendLane.Parse(
            "sparse", "Host=db;Database=search", "simple",
            sparseVectorsDirectory: "/tmp/lexisharp-arguana-vectors");

        Assert.Equal("Sparse", lane.Kind.ToString());
        Assert.Equal(Path.GetFullPath("/tmp/lexisharp-arguana-vectors"), lane.SparseVectorsDirectory);
        Assert.Contains("sparsevec", lane.Description, StringComparison.Ordinal);
        Assert.Contains("inner product", lane.Description, StringComparison.Ordinal);
        Assert.Contains("ef_search 40", lane.Description, StringComparison.Ordinal);
        Assert.Contains(
            lane.Caveats, caveat => caveat.Contains("not the model", StringComparison.Ordinal));
        Assert.Contains(
            lane.Caveats, caveat => caveat.Contains("verbatim", StringComparison.Ordinal));
    }

    [Fact]
    public void AVectorsDirectoryGivenToANonSparseBackendIsAcceptedBecauseSparseIsAlsoItsOwnRow()
    {
        // `--backend lexical --sparse <dir>` asks for two rows over one corpus: the SQL lexical row
        // and the in-memory sparse row. Refusing the flag here would refuse a legitimate command.
        BackendLane lane = BackendLane.Parse(
            "lexical", "Host=db;Database=search", "simple",
            sparseVectorsDirectory: "/tmp/lexisharp-arguana-vectors");

        Assert.Equal("Lexical", lane.Kind.ToString());
        Assert.NotNull(lane.SparseVectorsDirectory);
    }

    [Fact]
    public void AK1AndBGivenToTheSparseBackendIsRefusedBecauseItsRankingIsADotProduct()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendLane.Parse(
            "sparse", "Host=db;Database=search", "simple", new Bm25Parameters(0.9, 0.4),
            sparseVectorsDirectory: "/tmp/lexisharp-arguana-vectors"));

        Assert.Contains("inner product", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEfSearchValueGivenToTheSparseBackendIsRefusedBecauseTheEngineCannotReachIt()
    {
        // The row runs at pgvector's default 40: accepting the flag would label it with a value no
        // code path set. A server-level setting would apply, and the message says so.
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendLane.Parse(
            "sparse", "Host=db;Database=search", "simple", efSearchValues: [160],
            sparseVectorsDirectory: "/tmp/lexisharp-arguana-vectors"));

        Assert.Contains("hnsw.ef_search", error.Message, StringComparison.Ordinal);
        Assert.Contains("default 40", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTableIsDerivedFromTheDatasetRatherThanLeftAtTheSharedDefault()
    {
        // The engines truncate the table before indexing. A measurement that defaulted to
        // lexisharp_documents would empty an application's corpus to score a benchmark.
        Assert.Equal("lexisharp_eval_nfcorpus", new BackendLane(BackendKind.Lexical, "cs", "simple").TableFor("nfcorpus"));
        Assert.NotEqual("lexisharp_documents", new BackendLane(BackendKind.Lexical, "cs", "simple").TableFor("scifact"));
    }

    [Fact]
    public void ADatasetNameThatIsNotATableSuffixIsRefusedByName()
    {
        // The backends quote table names and accept [A-Za-z0-9_] only, so a slash or a space would
        // be an identifier injection at worst and a syntax error at best. Neither is a measurement.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => new BackendLane(BackendKind.Lexical, "cs", "simple").TableFor("nfcorpus/../x"));

        Assert.Contains("nfcorpus/../x", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLexicalBackendKeepsTheTitleAsAField()
    {
        // The shared documents table generates tsv from content || text_fields, so the title is
        // searchable through the field. Flattening it here too would count it twice.
        SearchDocument document = Document("d1", "Bile salts", "bile salts emulsify fat");

        IReadOnlyList<SearchDocument> shaped = PostgresBackendBenchmark.Shape(BackendKind.Lexical, [document]);

        Assert.Same(document, Assert.Single(shaped));
    }

    [Fact]
    public void TheParadeDbBackendGetsTheTitleInTheIndexedText()
    {
        // Its pg_search index is declared over content alone: a title left in text_fields would be
        // stored and never searched, which is the same corpus indexed with fewer terms on one side.
        SearchDocument document = Document("d1", "Bile salts", "bile salts emulsify fat");

        SearchDocument shaped = Assert.Single(PostgresBackendBenchmark.Shape(BackendKind.ParadeDb, [document]));

        Assert.Equal("Bile salts\nbile salts emulsify fat", shaped.Text);
        Assert.Null(shaped.TextFields);
        Assert.Equal("d1", shaped.Id);
    }

    [Fact]
    public void FlatteningATitleKeepsTheMetadataFieldsAndTheCategory()
    {
        var document = new SearchDocument(
            "d1",
            "body",
            new Dictionary<string, string> { ["kind"] = "article" },
            "fable",
            new Dictionary<string, string> { ["title"] = "Bile salts" });

        SearchDocument shaped = Assert.Single(PostgresBackendBenchmark.Shape(BackendKind.ParadeDb, [document]));

        Assert.Equal("article", shaped.Fields!["kind"]);
        Assert.Equal("fable", shaped.Category);
    }

    [Fact]
    public void ATitlelessDocumentIsPassedThroughUnchanged()
    {
        // ArguAna has no titles at all, so this is the shape of a whole corpus rather than an edge.
        SearchDocument document = Document("d1", string.Empty, "an argument");

        Assert.Same(document, Assert.Single(PostgresBackendBenchmark.Shape(BackendKind.ParadeDb, [document])));
    }

    [Fact]
    public void AK1AndBAreIndexTimeArgumentsAndReachTheTokenizer()
    {
        // pg_search takes k1/b as per-field tokenizer arguments, so matching the published operating
        // point is a decision made when the index is built. If the values did not reach the
        // tokenizer the row would be measured at the defaults while claiming 0.9/0.4.
        BackendLane lane = BackendLane.Parse(
            "paradedb", "Host=db;Database=search", "simple", new Bm25Parameters(0.9, 0.4));

        Assert.Contains("k1=0.9", lane.Tokenizer, StringComparison.Ordinal);
        Assert.Contains("b=0.4", lane.Tokenizer, StringComparison.Ordinal);
        Assert.Contains("k1=0.9, b=0.4", lane.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutK1AndBTheRowSaysItRanAtTheDefaults()
    {
        BackendLane lane = BackendLane.Parse("paradedb", "Host=db;Database=search", "simple");

        Assert.Equal("pdb.simple('ascii_folding=true')", lane.Tokenizer);
        Assert.Contains("Tantivy's defaults", lane.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AK1AndBGivenToTheLexicalBackendIsRefusedRatherThanIgnored()
    {
        // ts_rank_cd has no k1/b. Accepting the flag would produce a row labelled with parameters
        // that never entered the ranking.
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendLane.Parse(
            "lexical", "Host=db;Database=search", "simple", new Bm25Parameters(0.9, 0.4)));

        Assert.Contains("ts_rank_cd", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APostgreSQLAnalysisConfigurationGivenToParadeDbIsRefused()
    {
        // ParadeDB reads its own tokenizer declaration; --backend-config would be a flag that does
        // nothing on that engine, which is worse than one that is missing.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => BackendLane.Parse("paradedb", "Host=db;Database=search", "english"));

        Assert.Contains("--backend-config", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVectorBackendDefaultsToOneEfSearchValueAndNamesIt()
    {
        // pgvector's own default is 40. A row that did not say which ef_search it ran at would be a
        // recall number with no candidate list attached to it.
        BackendLane lane = BackendLane.Parse("vector", "Host=db;Database=search", "simple");

        Assert.Equal("Vector", lane.Kind.ToString());
        Assert.Equal([40], lane.EfValues);
        Assert.Contains("ef_search 40", lane.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void TheVectorBackendKeepsEveryRequestedEfSearchValueInOrder()
    {
        BackendLane lane = BackendLane.Parse(
            "vector", "Host=db;Database=search", "simple", efSearchValues: [160, 10]);

        // Order is the caller's: the rows print in the order the flag lists them, so a curve reads
        // the way the command was typed rather than sorted behind the caller's back.
        Assert.Equal([160, 10], lane.EfValues);
        Assert.Contains("ef_search 160, 10", lane.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEfSearchValueGivenToABm25BackendIsRefused()
    {
        // Neither ts_rank_cd nor pdb.score reads hnsw.ef_search. Accepting the flag would label a row
        // with a parameter that never entered it.
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendLane.Parse(
            "lexical", "Host=db;Database=search", "simple", efSearchValues: [40]));

        Assert.Contains("--backend-ef", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AK1AndBGivenToTheVectorBackendIsRefused()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendLane.Parse(
            "vector", "Host=db;Database=search", "simple", new Bm25Parameters(0.9, 0.4)));

        Assert.Contains("cosine", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OneRowPrintsAsASentenceAndSeveralPrintAsATable()
    {
        // The lexical and ParadeDB lanes produce one number; the vector lane produces a curve. A
        // one-row table would be ceremony, and a sentence for a curve would bury the axis.
        var single = new BackendMeasurement(
            "engine", "table", 10,
            [new BackendRow(string.Empty, new ConfigResult("engine", 7, 0.25, 0.2, 0.5, 0.1, TimeSpan.Zero))],
            []);

        Assert.Contains("Result: nDCG@10 0.2500", PostgresBackendBenchmark.Format(single, "nfcorpus"), StringComparison.Ordinal);

        var curve = new BackendMeasurement(
            "engine", "table", 10,
            [
                new BackendRow("10", new ConfigResult("ef=10", 7, 0.25, 0.2, 0.5, 0.1, TimeSpan.Zero)),
                new BackendRow("160", new ConfigResult("ef=160", 7, 0.26, 0.2, 0.5, 0.1, TimeSpan.Zero)),
            ],
            []);

        string formatted = PostgresBackendBenchmark.Format(curve, "nfcorpus");

        Assert.Contains("ef_search", formatted, StringComparison.Ordinal);
        Assert.Contains("       10 |", formatted, StringComparison.Ordinal);
        Assert.Contains("      160 |", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("Result:", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRowScoresTheEngineItIsGivenOverTheTestQueriesOnly()
    {
        // Two judged queries and one without a judgement: the harness loads the third and leaves it
        // out of the average rather than scoring it as a zero, and the row's query count has to say
        // so or a reader cannot tell 2 from 3.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("a", string.Empty, "alpha"), new BeirDocument("b", string.Empty, "beta")],
            Queries = [new BeirQuery("q1", "alpha"), new BeirQuery("q2", "beta"), new BeirQuery("q3", "gamma")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
                ["q2"] = new Dictionary<string, double> { ["b"] = 1 },
            },
        };

        var engine = new FixedEngine(new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["alpha"] = ["a"],
            ["beta"] = ["b"],
            ["gamma"] = ["b"],
        });

        ConfigResult result = Evaluation.RunEngine(corpus, engine, "stub", topK: 10, limit: null, NdcgGain.Exponential);

        Assert.Equal(2, result.Queries);
        Assert.Equal(1.0, result.NdcgAt10);
        Assert.DoesNotContain("gamma", engine.Queries, StringComparer.Ordinal);
    }

    [Fact]
    public void ABackendThatReturnsNothingScoresZeroRatherThanBeingAveragedOut()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("a", string.Empty, "alpha")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
            },
        };

        ConfigResult result = Evaluation.RunEngine(
            corpus, new FixedEngine(new Dictionary<string, string[]>(StringComparer.Ordinal)), "stub",
            topK: 10, limit: null, NdcgGain.Exponential);

        Assert.Equal(0.0, result.NdcgAt10);
        Assert.Equal(1, result.Queries);
    }

    [SkippableFact]
    public void TheLexicalLaneMeasuresALiveServerAndDropsItsTable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents =
            [
                new BeirDocument("a", "Bile salts", "bile salts emulsify dietary fat"),
                new BeirDocument("b", "Unrelated", "vesicle fusion in neurons"),
            ],
            Queries = [new BeirQuery("q1", "bile salts")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
            },
        };

        var lane = new BackendLane(BackendKind.Lexical, ConnectionString!, "simple");

        BackendMeasurement measurement = PostgresBackendBenchmark.Measure(
            lane, corpus, "lexical_lane_test", topK: 10, limit: null, NdcgGain.Exponential,
            excludeQueryDocument: false, jobs: 1);

        Assert.Equal(2, measurement.Documents);
        Assert.Equal(1, measurement.Rows[0].Result.Queries);
        Assert.Equal(1.0, measurement.Rows[0].Result.NdcgAt10);
        Assert.False(TableExists(measurement.Table), "The lane drops the table it built.");
    }

    [SkippableFact]
    public void TheParadeDbLaneMeasuresALiveServerAndDropsItsTable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        if (!ParadeDBAvailable(ConnectionString!))
            Skip.If(true, "pg_search extension not available on the target instance.");

        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents =
            [
                new BeirDocument("a", "Bile salts", "bile salts emulsify dietary fat"),
                new BeirDocument("b", "Unrelated", "vesicle fusion in neurons"),
            ],
            Queries = [new BeirQuery("q1", "bile salts")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
            },
        };

        var lane = new BackendLane(BackendKind.ParadeDb, ConnectionString!, "simple");

        BackendMeasurement measurement = PostgresBackendBenchmark.Measure(
            lane, corpus, "paradedb_lane_test", topK: 10, limit: null, NdcgGain.Exponential,
            excludeQueryDocument: false, jobs: 1);

        Assert.Equal(2, measurement.Documents);
        Assert.Equal(1, measurement.Rows[0].Result.NdcgAt10);
        Assert.False(TableExists(measurement.Table), "The lane drops the table it built.");
    }

    [Fact]
    public void ARefusalIsFoundThroughTheAggregateAParallelQueryLoopPutsAroundIt()
    {
        // RunConfig scores queries with Parallel.For, so the engine's refusal arrives inside an
        // AggregateException — sometimes at a second level. Missing that is how a clean "this
        // dataset is not measurable" turns back into an unhandled stack trace.
        var refusal = new NotSupportedException("engine refuses prefix operators");
        var error = new AggregateException(
            new InvalidOperationException("another query failed"),
            new AggregateException(refusal));

        Assert.Same(refusal, PostgresBackendBenchmark.FindRefusal(error));
    }

    [Fact]
    public void AnErrorThatIsNotARefusalIsLeftAlone()
    {
        Assert.Null(PostgresBackendBenchmark.FindRefusal(new InvalidOperationException("no connection")));
    }

    [Fact]
    public void ARefusalIsRestatedAsHowManyQueriesItCostsAndWhyTheRestAreNotASubstitute()
    {
        // 5 of ArguAna's 1,406 queries carry a `*` the engines read as a prefix operator. A metric
        // over the other 1,401 would sit in a table where every neighbour is 1,406 queries.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("a", string.Empty, "alpha")],
            Queries = [new BeirQuery("q1", "alpha"), new BeirQuery("q2", "alpha*"), new BeirQuery("q3", "beta")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
                ["q2"] = new Dictionary<string, double> { ["a"] = 1 },
                ["q3"] = new Dictionary<string, double> { ["a"] = 1 },
            },
        };

        var refusal = new NotSupportedException("engine does not support prefix/fuzzy operators");

        NotSupportedException restated = PostgresBackendBenchmark.Refusal(
            refusal,
            new FixedEngine(new Dictionary<string, string[]>(StringComparer.Ordinal), QueryFeatures.None),
            Evaluation.BuildQueries(corpus, limit: null, excludeQueryDocument: false));

        Assert.Contains("1 of 3", restated.Message, StringComparison.Ordinal);
        Assert.Contains("q2", restated.Message, StringComparison.Ordinal);
        Assert.Same(refusal, restated.InnerException);
    }

    [Fact]
    public void ARefusalWithNoIdentifiableQuerySaysSoRatherThanBlamingTheCorpus()
    {
        // An engine that declares nothing about its syntax gets no count from this harness. Printing
        // "0 of N" would read as a corpus defect, which is the opposite of what happened.
        var queries = new List<EvaluatedQuery>
        {
            new(new BeirQuery("q1", "alpha"), new Dictionary<string, double> { ["a"] = 1 }),
        };

        NotSupportedException restated = PostgresBackendBenchmark.Refusal(
            new NotSupportedException("engine refused it"),
            new FixedEngine(new Dictionary<string, string[]>(StringComparer.Ordinal)),
            queries);

        Assert.Contains("no row is produced", restated.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("0 of", restated.Message, StringComparison.Ordinal);
    }

    [SkippableFact]
    public void TheVectorLaneMeasuresOneRowPerEfSearchAndDropsItsTable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        if (!ExtensionAvailable(ConnectionString!, "vector"))
            Skip.If(true, "the vector extension is not available on the target instance.");

        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("a", string.Empty, "alpha"), new BeirDocument("b", string.Empty, "beta")],
            Queries = [new BeirQuery("q1", "find alpha")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
            },
        };

        // The query text differs from both document texts on purpose: the provider keys one
        // dictionary by text, so a query that read "alpha" would overwrite the document vector
        // indexed under the same key.
        var dense = new DenseVectors(
            [[1f, 0f], [0f, 1f]],
            new Dictionary<string, float[]>(StringComparer.Ordinal) { ["find alpha"] = [1f, 0f] },
            Dimension: 2);

        var lane = BackendLane.Parse("vector", ConnectionString!, "simple", efSearchValues: [10, 160]);

        BackendMeasurement measurement = PostgresBackendBenchmark.Measure(
            lane, corpus, "vector_lane_test", topK: 10, limit: null, NdcgGain.Exponential,
            excludeQueryDocument: false, jobs: 1, dense);

        Assert.Equal(["10", "160"], measurement.Rows.Select(row => row.Label));
        Assert.All(measurement.Rows, row => Assert.Equal(1.0, row.Result.NdcgAt10));
        Assert.False(TableExists(measurement.Table), "The lane drops the table it built.");
    }

    [Fact]
    public void AnUnsupportedQueryIsRefusedBeforeAnythingIsIndexed()
    {
        // The engines refuse at search time, which for this lane is after the corpus was loaded into
        // a table. ArguAna's dense run died that way: 8,674 documents indexed, then an unhandled
        // exception on the first quoted query.
        var queries = new List<EvaluatedQuery>
        {
            new(new BeirQuery("q1", "alpha beta"), new Dictionary<string, double> { ["a"] = 1 }),
            new(new BeirQuery("q2", "\"alpha beta\""), new Dictionary<string, double> { ["a"] = 1 }),
            new(new BeirQuery("q3", "gamma"), new Dictionary<string, double> { ["a"] = 1 }),
        };

        NotSupportedException refusal = Assert.Throws<NotSupportedException>(() =>
            PostgresBackendBenchmark.RefuseUnsupported(
                new FixedEngine(new Dictionary<string, string[]>(StringComparer.Ordinal), QueryFeatures.None),
                queries));

        Assert.Contains("1 of 3", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("q2", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEngineThatDeclaresPhrasesIsNotRefusedForAQuotedQuery()
    {
        var queries = new List<EvaluatedQuery>
        {
            new(new BeirQuery("q1", "\"alpha beta\""), new Dictionary<string, double> { ["a"] = 1 }),
        };

        PostgresBackendBenchmark.RefuseUnsupported(
            new FixedEngine(new Dictionary<string, string[]>(StringComparer.Ordinal), QueryFeatures.Phrases),
            queries);
    }

    [Fact]
    public void TheFuzzyBackendDefaultsToOneSimilarityThresholdAndNamesIt()
    {
        // pg_trgm's own default is 0.3. A fuzzy row that did not say which threshold it ran at would
        // be a metric with no match rule attached to it, and at 0.3 a prose query usually matches
        // nothing: the threshold is the row's most consequential parameter.
        BackendLane lane = BackendLane.Parse("fuzzy", "Host=db;Database=search", "simple");

        Assert.Equal("Fuzzy", lane.Kind.ToString());
        Assert.Equal([0.3], lane.Thresholds);
        Assert.Contains("similarity threshold 0.3", lane.Description, StringComparison.Ordinal);
        Assert.Contains(
            lane.Caveats, caveat => caveat.Contains("in-sample threshold sweep", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFuzzyBackendKeepsEveryRequestedThresholdInOrderAndPrintsThemAsTheAxis()
    {
        // The sweep prints in the order the flag listed it, and each row is labelled with its own
        // threshold: a table whose rows all read "pg_trgm similarity" would be points a reader could
        // not tell apart.
        BackendLane lane = BackendLane.Parse(
            "fuzzy", "Host=db;Database=search", "simple", similarityThresholds: [0.05, 0.3, 0.001]);

        Assert.Equal([0.05, 0.3, 0.001], lane.Thresholds);
        Assert.Contains("0.05, 0.3, 0.001", lane.Description, StringComparison.Ordinal);

        var curve = new BackendMeasurement(
            "engine", "table", 10,
            [
                new BackendRow("0.3", new ConfigResult("t=0.3", 7, 0.0, 0.0, 0.0, 0.0, TimeSpan.Zero)),
                new BackendRow("0.01", new ConfigResult("t=0.01", 7, 0.1, 0.1, 0.1, 0.1, TimeSpan.Zero)),
            ],
            [],
            RowAxis: "threshold");

        string formatted = PostgresBackendBenchmark.Format(curve, "nfcorpus");

        Assert.Contains("threshold", formatted, StringComparison.Ordinal);
        Assert.Contains("        0.3 |", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("ef_search", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void ASimilarityThresholdGivenToANonFuzzyBackendIsRefused()
    {
        // The lexical, ParadeDB and vector engines match by term or by embedding; none reads
        // pg_trgm's threshold. Accepting the flag would label a row with a parameter that never
        // entered it.
        ArgumentException error = Assert.Throws<ArgumentException>(() => BackendLane.Parse(
            "paradedb", "Host=db;Database=search", "simple", similarityThresholds: [0.05]));

        Assert.Contains("--backend-threshold", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFuzzyBackendGetsTheTitleInTheIndexedText()
    {
        // Its index covers the content column alone, like ParadeDB's: a title left in text_fields
        // would be stored and never compared, so the corpus on one side of a comparison would carry
        // fewer trigrams than the other.
        SearchDocument document = Document("d1", "Bile salts", "bile salts emulsify fat");

        SearchDocument shaped = Assert.Single(PostgresBackendBenchmark.Shape(BackendKind.Fuzzy, [document]));

        Assert.Equal("Bile salts\nbile salts emulsify fat", shaped.Text);
        Assert.Null(shaped.TextFields);
    }

    [Fact]
    public void TheSparseBackendGetsTheTitleInTheTextTheEncoderHashed()
    {
        // The producer hashed title + "\n" + text, and the engine has no field concept, so this is the
        // only text whose SHA-256 is in the vector file. A title left in text_fields would be stored
        // and never scored, and every document would then look like a missing vector.
        SearchDocument document = Document("d1", "Bile salts", "bile salts emulsify fat");

        SearchDocument shaped = Assert.Single(PostgresBackendBenchmark.Shape(BackendKind.Sparse, [document]));

        Assert.Equal("Bile salts\nbile salts emulsify fat", shaped.Text);
        Assert.Null(shaped.TextFields);
    }

    [SkippableFact]
    public void TheFuzzyLaneMeasuresOneRowPerThresholdAndDropsItsTable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        if (!ExtensionAvailable(ConnectionString!, "pg_trgm"))
            Skip.If(true, "the pg_trgm extension is not available on the target instance.");

        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents =
            [
                new BeirDocument("a", "Bile salts", "bile salts emulsify dietary fat"),
                new BeirDocument("b", "Unrelated", "vesicle fusion in neurons"),
            ],
            Queries = [new BeirQuery("q1", "bile salts")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
            },
        };

        var lane = BackendLane.Parse(
            "fuzzy", ConnectionString!, "simple", similarityThresholds: [0.3, 0.01]);

        BackendMeasurement measurement = PostgresBackendBenchmark.Measure(
            lane, corpus, "fuzzy_lane_test", topK: 10, limit: null, NdcgGain.Exponential,
            excludeQueryDocument: false, jobs: 1);

        Assert.Equal(2, measurement.Documents);
        Assert.Equal(["0.3", "0.01"], measurement.Rows.Select(row => row.Label));
        Assert.Equal(1, measurement.Rows[1].Result.Queries);
        Assert.False(TableExists(measurement.Table), "The lane drops the table it built.");
    }

    [SkippableFact]
    public void TheSparseLaneMeasuresTheIndexAndTheExactScanAndDropsItsTable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        if (!ExtensionAvailable(ConnectionString!, "vector"))
            Skip.If(true, "the vector extension is not available on the target instance.");

        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("a", string.Empty, "alpha beta"), new BeirDocument("b", string.Empty, "beta gamma")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double> { ["a"] = 1 },
            },
        };

        // A directory on disk, in the producer's format: the lane reads it exactly as it reads
        // ArguAna's, and the two-row contract does not depend on the size of the corpus.
        using var vectors = new SparseVectorDirectory("sparse_lane_test");
        vectors.Write(
            documents: [("alpha beta", "alpha:1\tbeta:0.5"), ("beta gamma", "beta:1\tgamma:1")],
            queries: [("alpha", "alpha:1")]);

        var lane = BackendLane.Parse(
            "sparse", ConnectionString!, "simple", sparseVectorsDirectory: vectors.Path);

        BackendMeasurement measurement = PostgresBackendBenchmark.Measure(
            lane, corpus, "sparse_lane_test", topK: 10, limit: null, NdcgGain.Exponential,
            excludeQueryDocument: false, jobs: 1);

        Assert.Equal(2, measurement.Documents);
        Assert.Equal(2, measurement.Rows.Count);

        // "exact" is the row after DROP INDEX, always. The first row's label is the plan's answer:
        // a two-document table is small enough that PostgreSQL may skip the index altogether, which
        // is why the label is read from EXPLAIN rather than assumed.
        Assert.Contains(measurement.Rows[0].Label, new[] { "hnsw", "sequential" });
        Assert.Equal("exact", measurement.Rows[^1].Label);
        Assert.All(measurement.Rows, row => Assert.Equal(1.0, row.Result.NdcgAt10));
        Assert.Contains(
            measurement.Caveats, caveat => caveat.Contains("plan PostgreSQL chose", StringComparison.Ordinal));
        Assert.False(TableExists(measurement.Table), "The lane drops the table it built.");
    }

    private static SearchDocument Document(string id, string title, string text) =>
        new(id, text, null, null, string.IsNullOrEmpty(title) ? null : new Dictionary<string, string> { ["title"] = title });

    private static bool TableExists(string table)
    {
        using var connection = new NpgsqlConnection(ConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @t";
        command.Parameters.AddWithValue("t", table);

        return (long)command.ExecuteScalar()! > 0;
    }

    private static bool ParadeDBAvailable(string connectionString) => ExtensionAvailable(connectionString, "pg_search");

    private static bool ExtensionAvailable(string connectionString, string extension)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT count(*) FROM pg_available_extensions WHERE name = @name";
            command.Parameters.AddWithValue("name", extension);
            return (long)command.ExecuteScalar()! > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>An engine that answers from a table of ids, so the harness's wiring is the only variable.</summary>
    private sealed class FixedEngine(
        IReadOnlyDictionary<string, string[]> rankings,
        QueryFeatures supported = QueryFeatures.Phrases) : ITextSearchEngine, IQuerySyntaxSupport
    {
        public QueryFeatures SupportedQueryFeatures { get; } = supported;

        public List<string> Queries { get; } = [];

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public bool Remove(string documentId) => false;

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(
            string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
        {
            Queries.Add(query);

            if (!rankings.TryGetValue(query, out string[]? ids))
                return [];

            return ids
                .Select(id => new SearchResult(id, 1.0, new SearchDocument(id, string.Empty)))
                .Take(options?.Limit ?? int.MaxValue)
                .ToList();
        }
    }
}
