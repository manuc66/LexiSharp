using System;
using System.Collections.Generic;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Eval;
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
    public void AnUnknownEngineNameIsRefusedAndTheMessageSaysWhatIsMissing()
    {
        // The sparse and fuzzy engines implement ITextSearchEngine too, so their absence from the list
        // is a decision a caller will ask about. The message answers it, because a bare
        // "unknown backend" sends the reader looking for the wrong defect.
        ArgumentException error = Assert.Throws<ArgumentException>(
            () => BackendLane.Parse("sparse", "Host=db;Database=search", "simple"));

        Assert.Contains("lexical", error.Message, StringComparison.Ordinal);
        Assert.Contains("no sparse model", error.Message, StringComparison.Ordinal);
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
