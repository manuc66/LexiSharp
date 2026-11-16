using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Postgres;
using LexiSharp.Ranking;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The stock-PostgreSQL BM25 engine: portability (no extension beyond <c>unaccent</c>), the score
/// itself, and the two things it does differently from every other SQL backend — a cached corpus
/// snapshot and a bounded candidate pool.
/// </summary>
public class PostgresBm25SearchEngineTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static string Table(string suffix) => $"lexisharp_bm25_{suffix}_{Guid.NewGuid():N}"[..44];

    private static PostgresBm25SearchEngine NewEngine(
        string suffix, Func<PostgresBm25Options, PostgresBm25Options>? configure = null)
    {
        var options = new PostgresBm25Options { Table = Table(suffix) };

        return new PostgresBm25SearchEngine(
            ConnectionString!, configure is null ? options : configure(options));
    }

    private static void Run(
        string suffix,
        Action<PostgresBm25SearchEngine> body,
        Func<PostgresBm25Options, PostgresBm25Options>? configure = null)
    {
        using var engine = NewEngine(suffix, configure);

        try
        {
            body(engine);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    /// <summary>Whether the physical table exists, asked of the server rather than inferred.</summary>
    private static bool TableExists(string table)
    {
        using var connection = new NpgsqlConnection(ConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        command.Parameters.AddWithValue("name", "public." + table);

        return (bool)command.ExecuteScalar()!;
    }

    /// <summary>
    /// Plain lowercase ASCII, so PostgreSQL's <c>simple</c> analysis and LexiSharp's tokenizer
    /// produce the same terms — which is what makes the two scorers comparable at all.
    /// </summary>
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "quick brown fox"),
        new("d2", "quick quick brown brown brown"),
        new("d3", "lazy dog sleeps"),
        new("d4", "quick thinking saves the day"),
        new("d5", "unrelated content here"),
    ];

    [SkippableFact]
    public void Search_ScoresWhatTheInMemoryBm25Scores()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("parity", engine =>
        {
            engine.Index(Corpus);

            var index = new InMemoryTextIndex();
            index.Index(Corpus);

            var scorer = new Bm25Scorer(1.5, 0.75);
            var terms = index.Tokenizer.Tokenize("quick brown");

            var results = engine.Search("quick brown", new SearchOptions(Limit: 10));

            Assert.Equal(
                results.Select(result => result.DocumentId),
                results.OrderByDescending(result => result.Score)
                    .ThenBy(result => result.DocumentId, StringComparer.Ordinal)
                    .Select(result => result.DocumentId));

            foreach (SearchResult result in results)
            {
                double expected = scorer.Score(result.DocumentId, terms, index);

                Assert.InRange(result.Score, expected - 1e-9, expected + 1e-9);
            }

            // d3 and d5 share no term with the query: BM25 scores them 0, so they are absent.
            Assert.Equal(3, results.Count);
        });
    }

    [SkippableFact]
    public void Search_IsAccentInsensitive()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("accents", engine =>
        {
            engine.Add(new SearchDocument("cafe", "un café à Paris"));

            Assert.Equal("cafe", engine.Search("CAFE").Single().DocumentId);
        });
    }

    [SkippableFact]
    public void Search_EmptyQueryOrEmptyTableReturnsNothing()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("empty", engine =>
        {
            Assert.Empty(engine.Search("quick"));

            engine.Index(Corpus);

            Assert.Empty(engine.Search("   "));
            Assert.Empty(engine.Search("quantum"));
        });
    }

    [SkippableFact]
    public void Search_HonoursAQuotedPhraseAsAGate()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("phrase", engine =>
        {
            engine.Index(Corpus);

            Assert.Equal(
                "d1",
                engine.Search("\"brown fox\"").Single().DocumentId);

            Assert.Empty(engine.Search("\"fox brown\""));
        });
    }

    [SkippableFact]
    public void Search_HonoursMetadataFilters()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("filters", engine =>
        {
            engine.Index(
            [
                new("article", "machine learning systems",
                    new Dictionary<string, string> { ["kind"] = "article" }),
                new("note", "machine learning notes",
                    new Dictionary<string, string> { ["kind"] = "note" }),
            ]);

            var results = engine.Search("machine learning", new SearchOptions(
                Limit: 10,
                Filters: [new MetadataFilter("kind", MetadataFilterOperator.Equal, "note")]));

            Assert.Equal("note", Assert.Single(results).DocumentId);
        });
    }

    [SkippableFact]
    public void Search_ExcludesTheDocumentAndStillFillsThePage()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("excluded", engine =>
        {
            engine.Index(
                new[] { "d1", "d2", "d3", "d4" }
                    .Select(id => new SearchDocument(id, "shared common term")));

            var all = engine.Search("shared common term", new SearchOptions(Limit: 10));
            Assert.Equal(4, all.Count);

            var after = engine.Search("shared common term", new SearchOptions(
                Limit: 10,
                ExcludedDocumentIds: new HashSet<string>(StringComparer.Ordinal) { all[0].DocumentId }));

            Assert.DoesNotContain(after, result => result.DocumentId == all[0].DocumentId);
            Assert.Equal(3, after.Count);
        });
    }

    [SkippableFact]
    public void Add_InvalidatesTheStatisticsSnapshot()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("invalidate", engine =>
        {
            engine.Index(Corpus);

            double before = engine.Search("quick").Single(result => result.DocumentId == "d1").Score;

            engine.Add(new SearchDocument("d6", "quick"));

            var after = engine.Search("quick", new SearchOptions(Limit: 10));

            // The new document is a candidate immediately (the SQL reads the table), and the idf
            // moved because the snapshot was rebuilt: N grew from 4 to 5.
            Assert.Contains(after, result => result.DocumentId == "d6");
            Assert.NotEqual(before, after.Single(result => result.DocumentId == "d1").Score);
        });
    }

    [SkippableFact]
    public void Remove_InvalidatesTheStatisticsSnapshot()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("remove", engine =>
        {
            engine.Index(Corpus);

            Assert.True(engine.Remove("d2"));
            Assert.DoesNotContain(engine.Search("quick", new SearchOptions(Limit: 10)),
                result => result.DocumentId == "d2");

            Assert.False(engine.Remove("d2"));
        });
    }

    [SkippableFact]
    public void RefreshStatistics_PicksUpAWriteIssuedByAnotherEngine()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        string table = Table("refresh");

        using var bm25 = new PostgresBm25SearchEngine(
            ConnectionString!, new PostgresBm25Options { Table = table });

        using var lexical = new PostgresTextSearchEngine(
            ConnectionString!, new PostgresIndexOptions { Table = table });

        try
        {
            bm25.Index([new SearchDocument("d1", "alpha")]);
            Assert.Equal(1, bm25.RefreshStatistics());

            // A write through the lexical engine on the shared table invalidates nothing here: the
            // snapshot is this engine's, not the table's.
            lexical.Add(new SearchDocument("d2", "beta"));

            Assert.Equal(2, bm25.RefreshStatistics());

            // Candidates were never stale — that is the live SQL — so the document is found either
            // way; what the refresh restores is its idf.
            Assert.Equal("d2", bm25.Search("beta").Single().DocumentId);
        }
        finally
        {
            bm25.DropSchema();
        }
    }

    [SkippableFact]
    public void Index_ReplacesTheCorpus()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("reindex", engine =>
        {
            engine.Index(Corpus);
            engine.Index([new SearchDocument("only", "quick brown fox")]);

            var results = engine.Search("quick brown", new SearchOptions(Limit: 10));

            Assert.Equal("only", Assert.Single(results).DocumentId);
        });
    }

    [Theory]
    [InlineData(1000, 10, 0, 1000)]
    [InlineData(1000, 10, 2000, 2010)]
    [InlineData(5, 10, 0, 10)]
    [InlineData(0, 10, 0, 100_000)]
    [InlineData(1000, 10, int.MaxValue, 100_000)]
    public void PoolSize_WidensToThePageAndCapsAtTheCeiling(
        int candidatePool, int limit, int offset, int expected)
    {
        var options = new PostgresBm25Options { CandidatePool = candidatePool };

        int pool = PostgresBm25SearchEngine.PoolSize(options, new SearchOptions(Limit: limit, Offset: offset));

        Assert.Equal(expected, pool);
    }

    [Fact]
    public void GateTerms_TakesTheRarestTermsThatFitTheBudget()
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["common"] = 900,
            ["rare"] = 5,
            ["rarer"] = 1,
            ["absent"] = 0,
        };

        string[] terms = PostgresBm25SearchEngine.GateTerms(
            ["common", "rare", "rarer", "absent"], term => frequencies[term], budget: 10);

        // 1 + 5 <= 10, and adding 900 would not.
        Assert.Equal(["rarer", "rare"], terms);
    }

    [Fact]
    public void GateTerms_FallsBackWhenTheRarestTermExceedsTheBudget()
    {
        var frequencies = new Dictionary<string, int> { ["a"] = 20, ["b"] = 30 };

        string[] terms = PostgresBm25SearchEngine.GateTerms(
            ["a", "b"], term => frequencies[term], budget: 10);

        // Empty is the caller's signal to gate on the whole query: a gate it cannot afford is not
        // a smaller gate, it is a different query.
        Assert.Empty(terms);
    }

    [Fact]
    public void GateTerms_IsDeterministicOnEqualFrequencies()
    {
        var frequencies = new Dictionary<string, int> { ["b"] = 5, ["a"] = 5 };

        string[] terms = PostgresBm25SearchEngine.GateTerms(
            ["b", "a"], term => frequencies[term], budget: 100);

        Assert.Equal(["a", "b"], terms);
    }

    [SkippableFact]
    public void RareTermGate_ExcludesADocumentThatMatchesOnlyCommonTerms()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        // df(alpha) = 1, df(beta) = 2. With a budget of 2, only alpha fits: the gate is alpha, and
        // d2 — which matches beta but not alpha — is not a candidate. With the gate off, it is.
        Run("gate", engine =>
        {
            engine.Index(
            [
                new("d1", "alpha beta"),
                new("d2", "beta gamma"),
                new("d3", "gamma delta"),
            ]);

            Assert.Equal("d1", Assert.Single(engine.Search("alpha beta")).DocumentId);
        }, options => options with { RareTermGateBudget = 2 });

        Run("nogate", engine =>
        {
            engine.Index(
            [
                new("d1", "alpha beta"),
                new("d2", "beta gamma"),
                new("d3", "gamma delta"),
            ]);

            Assert.Equal(
                ["d1", "d2"],
                engine.Search("alpha beta", new SearchOptions(Limit: 10))
                    .Select(result => result.DocumentId)
                    .Order(StringComparer.Ordinal));
        }, options => options with { RareTermGateBudget = 2, RareTermGate = false });
    }

    [SkippableFact]
    public void RareTermGate_FallsBackToTheWholeQueryWhenNothingFitsTheBudget()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        // df(beta) = 2 and df(gamma) = 2, both above a budget of 1: the gate is empty, so the
        // engine ranks the whole match set and every matching document comes back.
        Run("gatefallback", engine =>
        {
            engine.Index(
            [
                new("d1", "alpha beta"),
                new("d2", "beta gamma"),
                new("d3", "gamma delta"),
            ]);

            Assert.Equal(
                ["d1", "d2", "d3"],
                engine.Search("beta gamma", new SearchOptions(Limit: 10))
                    .Select(result => result.DocumentId)
                    .Order(StringComparer.Ordinal));
        }, options => options with { RareTermGateBudget = 1 });
    }

    [Fact]
    public void GateTerms_WithAZeroBudgetSelectsNothing()
    {
        Assert.Empty(PostgresBm25SearchEngine.GateTerms(["a"], _ => 1, budget: 0));
    }

    [SkippableFact]
    public void EnsureSchema_IsIdempotentAndNamesItsTable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine("schema");

        try
        {
            // The constructor already installed it; this is the early return.
            engine.EnsureSchema();

            Assert.True(TableExists(engine.TestTableName));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public async Task EnsureSchemaAsync_CreatesTheTableWhenTheConstructorDidNot()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = new PostgresBm25SearchEngine(
            ConnectionString!,
            new PostgresBm25Options { Table = Table("async") },
            autoCreateSchema: false);

        try
        {
            Assert.False(TableExists(engine.TestTableName));

            await engine.EnsureSchemaAsync();

            // The second call is the early return.
            await engine.EnsureSchemaAsync();

            engine.Add(new SearchDocument("d1", "quick brown fox"));

            Assert.Equal("d1", engine.Search("quick").Single().DocumentId);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Constructor_AutoCreateSchemaFalseLeavesTheTableAlone()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = new PostgresBm25SearchEngine(
            ConnectionString!,
            new PostgresBm25Options { Table = Table("noschema") },
            autoCreateSchema: false);

        Assert.False(TableExists(engine.TestTableName));
    }

    [SkippableFact]
    public void Search_OnAQueryOfStopWordsReturnsNothing()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("stopwords", engine =>
        {
            engine.Add(new SearchDocument("d1", "the quick brown fox"));

            // Every term is a stop word under the English configuration, so the query has no
            // lexeme at all and there is nothing to gate or score.
            Assert.Empty(engine.Search("the of and"));
        }, options => options with { TextSearchConfig = "english" });
    }

    [SkippableFact]
    public void Search_HonoursMinimumScore()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("minscore", engine =>
        {
            engine.Index(Corpus);

            Assert.NotEmpty(engine.Search("quick", new SearchOptions(Limit: 10)));
            Assert.Empty(engine.Search("quick", new SearchOptions(Limit: 10, MinimumScore: double.MaxValue)));
        });
    }

    [SkippableFact]
    public void Search_WithQuerySyntaxOffTreatsTheTextAsAValue()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("syntaxoff", engine =>
        {
            engine.Index(Corpus);

            // ParseQuerySyntax: false is the caller saying "literal text": the parser is not
            // consulted, and the phrase is still matched by the engine's own split.
            Assert.NotEmpty(engine.Search("\"brown fox\"", new SearchOptions(Limit: 10, ParseQuerySyntax: false)));
        });
    }

    [SkippableFact]
    public void Clear_EmptiesTheCorpusAndTheNextSearchRebuildsTheSnapshot()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("clear", engine =>
        {
            engine.Index(Corpus);
            Assert.NotEmpty(engine.Search("quick"));

            engine.Clear();

            Assert.Empty(engine.Search("quick"));
        });
    }

    [Fact]
    public void Constructor_RefusesInvalidRankingParameters()
    {
        const string connection = "Host=localhost;Database=unused";

        Assert.Throws<ArgumentException>(() => new PostgresBm25SearchEngine(
            connection, new PostgresBm25Options { K1 = -1 }, autoCreateSchema: false));

        Assert.Throws<ArgumentException>(() => new PostgresBm25SearchEngine(
            connection, new PostgresBm25Options { B = 1.5 }, autoCreateSchema: false));

        Assert.Throws<ArgumentException>(() => new PostgresBm25SearchEngine(
            connection, new PostgresBm25Options { CandidatePool = -1 }, autoCreateSchema: false));
    }

    [SkippableFact]
    public void Search_RecordsTheGateAndTheScoreStage()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("trace", engine =>
        {
            engine.Index(Corpus);

            var trace = new SearchTrace();
            var results = engine.Search("quick brown", new SearchOptions(Limit: 10, Trace: trace));

            TraceStep gate = trace.Steps.Single(step => step.Stage == TraceStage.Gate);
            Assert.Equal(string.Empty, gate.DocumentId);
            Assert.True(double.IsNaN(gate.Before));
            Assert.Contains("rare-term gate", gate.Detail!, StringComparison.Ordinal);
            Assert.Contains("candidates", gate.Detail!, StringComparison.Ordinal);

            var scored = trace.Steps.Where(step => step.Stage == TraceStage.Score).ToList();
            Assert.Equal(results.Count, scored.Count);
            Assert.All(scored, step => Assert.Equal("Postgres BM25", step.Detail));
        });
    }

    [SkippableFact]
    public void Search_RecordsWhyTheGateWasNotApplied()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("tracenotgate", engine =>
        {
            engine.Index(Corpus);

            var trace = new SearchTrace();
            engine.Search("quick brown", new SearchOptions(Limit: 10, Trace: trace));

            TraceStep gate = trace.Steps.Single(step => step.Stage == TraceStage.Gate);
            Assert.Contains("gate disabled", gate.Detail!, StringComparison.Ordinal);
        }, options => options with { RareTermGate = false });
    }
}
