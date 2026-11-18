using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Postgres;
using LexiSharp.Ranking;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="PostgresAnalysis.LexiSharp"/>: the BM25 index built from this library's own
/// tokenizer, in its own <c>tsv_bm25</c> column, beside the server <c>tsv</c> the other engines read.
/// </summary>
/// <remarks>
/// <para>
/// What is asserted is parity, not a number: on the same corpus, the same documents and the same
/// parameters, the SQL engine must score what the in-memory index scores, because both sides now
/// read the same term stream. That is the whole claim the mode makes, and it is checkable without a
/// corpus download.
/// </para>
/// <para>
/// The live tests auto-skip without <c>POSTGRES_TEST_CONNECTION</c>, like the other backend suites.
/// </para>
/// </remarks>
public class PostgresLexiSharpAnalysisTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static string Table(string suffix) => $"lexisharp_client_{suffix}_{Guid.NewGuid():N}"[..44];

    /// <summary>
    /// Plain enough that the two analyses agree where they are supposed to, and awkward enough to
    /// disagree where they are not: a possessive, a stop word, a symbol term, a joined number, an
    /// accent, and a title that is only reachable through <c>TextFields</c>.
    /// </summary>
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "The quick brown fox's jumps over the lazy dog"),
        new("d2", "quick quick brown, brown; brown 1,234 cafés"),
        new("d3", "lazy dog sleeps"),
        new("d4", "Quick thinking saves the day"),
        new("d5", "Aspirin® is a © brand"),
        new("d6", "an abstract about retrieval",
            TextFields: new Dictionary<string, string> { ["title"] = "Neural machine learning" }),
    ];

    private static PostgresBm25Options LexiSharpOptions(string table) => new()
    {
        Table = table,
        Analysis = PostgresAnalysis.LexiSharp,
    };

    private static void Run(
        string suffix,
        Action<PostgresBm25SearchEngine> body,
        Func<PostgresBm25Options, PostgresBm25Options>? configure = null)
    {
        var options = LexiSharpOptions(Table(suffix));
        using var engine = new PostgresBm25SearchEngine(
            ConnectionString!, configure is null ? options : configure(options));

        try
        {
            body(engine);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    private static bool ColumnExists(string table, string column)
    {
        using var connection = new NpgsqlConnection(ConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = @table AND column_name = @column;
            """;
        command.Parameters.AddWithValue("table", table);
        command.Parameters.AddWithValue("column", column);

        return (long)command.ExecuteScalar()! > 0;
    }

    private static bool IndexExists(string name)
    {
        using var connection = new NpgsqlConnection(ConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        command.Parameters.AddWithValue("name", "public." + SafeIdentifierQuote(name));

        return (bool)command.ExecuteScalar()!;
    }

    private static string SafeIdentifierQuote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    /// <summary>The (word, ndoc, nentry) rows of <c>ts_stat</c> over the client column.</summary>
    private static Dictionary<string, (int Documents, int Occurrences)> ReadStatistics(string table, string column)
    {
        using var connection = new NpgsqlConnection(ConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT word, ndoc, nentry FROM ts_stat('SELECT {column} FROM public.\"{table}\"')";

        var statistics = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
            statistics[reader.GetString(0)] = (reader.GetInt32(1), reader.GetInt32(2));

        return statistics;
    }

    // ---------------------------------------------------------------------------------------
    // Analysis defaults
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Strings chosen per rule of the reference configuration: stop words, possessives, the four
    /// symbol terms, the measured separators (comma between digits, colon between letters, full stop
    /// and apostrophe within a class, underscore anywhere), the narrow no-break space, the combining
    /// mark, the Turkish dotted I, and diacritics that are <b>not</b> folded.
    /// </summary>
    public static TheoryData<string> AnalysisSamples => new()
    {
        "The quick brown fox's jumps over the lazy dog",
        "it's a dog's life, isn't it",
        "1,234; 5; A:1; data.worldbank.org; snake_case; _630888; hello.,world",
        "© Aspirin® ™ ↔ 10.5.2",
        "café naïve résumé",
        "İstanbul celİl",
        "a\u0307b and \u0307alone",
        "Section §\u202F1950.6 prices",
        "  ",
        "",
        "under_score and hyphen-ate",
    };

    [Theory]
    [MemberData(nameof(AnalysisSamples))]
    public void TheDefaultAnalysisIsTheHarnessUax29Analysis(string sample)
    {
        // The harness's branch is the published reference analysis; the engine's default must be the
        // same options, or a FiQA row measured through SQL and one measured in memory would be two
        // analyses under one name. Compared as token streams rather than as option records: the
        // record carries an IStemmer instance, whose identity is not the contract.
        var harness = LexiSharp.Eval.Program.BuildAnalysis("uax29", stem: false);
        var engine = new Tokenizer(PostgresBm25Options.ReferenceAnalysisOptions);

        Assert.Equal(harness.Tokenizer.Tokenize(sample), engine.Tokenize(sample));
    }

    [Fact]
    public void TheDefaultAnalysisIsSpelledOutRatherThanInherited()
    {
        // The options the mode documents, one by one: a default that drifted with a library change
        // would move every SQL LexiSharp row without moving its name.
        TokenizerOptions options = PostgresBm25Options.ReferenceAnalysisOptions;

        Assert.IsType<PorterStemmer>(options.Stemmer);
        Assert.True(options.RemoveStopWords);
        Assert.Equal(StopWords.EnglishFunction, options.StopWords);
        Assert.True(options.KeepSingleCharTerms);
        Assert.False(options.FoldDiacritics);
        Assert.Equal(WordSegmentation.UnicodeWordBoundaries, options.WordSegmentation);
        Assert.True(options.StripPossessives);
        Assert.Equal("©®™↔", options.SymbolTerms);
        Assert.True(options.FoldTurkishDottedI);
    }

    // ---------------------------------------------------------------------------------------
    // The tsvector literal
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheLiteralWriter_GivesOnePositionPerOccurrenceInReadingOrder()
    {
        Assert.Equal("'cat':1,3 'dog':2", PostgresTsvLiteral.Build(["cat", "dog", "cat"]));
        Assert.Equal("'a':2 'b':1", PostgresTsvLiteral.Build(["b", "a"]));
        Assert.Equal("'fox':1", PostgresTsvLiteral.Build(["fox"]));
    }

    [Fact]
    public void TheLiteralWriter_DoublesQuotesAndEmptiesADocumentWithNoTerm()
    {
        Assert.Equal("'o''brien':1 's':2", PostgresTsvLiteral.Build(["o'brien", "s"]));
        Assert.Equal(string.Empty, PostgresTsvLiteral.Build([]));
        Assert.Equal(string.Empty, PostgresTsvLiteral.Build(["", ""]));
    }

    [Fact]
    public void TheLiteralWriter_StopsPositionsAtTheServerCeiling()
    {
        // A tsvector position is 16 bits: the input function clamps above 16383 and merges equal
        // positions, so the writer clamps too rather than letting the server do it silently.
        string literal = PostgresTsvLiteral.Build([.. Enumerable.Repeat("a", 16_384)]);

        Assert.StartsWith("'a':1,2,", literal, StringComparison.Ordinal);
        Assert.EndsWith(",16382,16383,16383", literal, StringComparison.Ordinal);
        Assert.DoesNotContain("16384", literal, StringComparison.Ordinal);
    }

    [Fact]
    public void TheReaderStillRecoversWhatTheWriterWrote()
    {
        var terms = new List<string> { "cat", "dog", "cat", "o'brien", "dog" };

        var (frequencies, length) = PostgresTsvText.Parse(PostgresTsvLiteral.Build(terms));

        Assert.Equal(5, length);
        Assert.Equal(2, frequencies["cat"]);
        Assert.Equal(2, frequencies["dog"]);
        Assert.Equal(1, frequencies["o'brien"]);
    }

    // ---------------------------------------------------------------------------------------
    // DDL
    // ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void LexiSharpMode_AddsTheClientColumnAndItsGinIndex_DatabaseModeDoesNot()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        string client = Table("ddl_client");

        using (var engine = new PostgresBm25SearchEngine(ConnectionString!, LexiSharpOptions(client)))
        {
            try
            {
                Assert.True(ColumnExists(client, "tsv_bm25"));
                Assert.True(ColumnExists(client, "tsv"));
                Assert.True(IndexExists($"{client}_tsv_bm25_gin"));

                // Idempotent: a second call is what a restart or a second process does.
                engine.EnsureSchema();
            }
            finally
            {
                engine.DropSchema();
            }
        }

        string database = Table("ddl_database");

        using (var engine = new PostgresBm25SearchEngine(
                   ConnectionString!, new PostgresBm25Options { Table = database }))
        {
            try
            {
                // The default mode is unchanged: no column, no index, and the shared DDL is the one
                // the lexical engine already creates.
                Assert.False(ColumnExists(database, "tsv_bm25"));
                Assert.False(IndexExists($"{database}_tsv_bm25_gin"));
                Assert.True(ColumnExists(database, "tsv"));
            }
            finally
            {
                engine.DropSchema();
            }
        }
    }

    [SkippableFact]
    public void LexiSharpMode_WritesBothVectors()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("both", engine =>
        {
            engine.Add(new SearchDocument("d1", "the quick brown fox"));

            using var connection = new NpgsqlConnection(ConnectionString!);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText =
                $"SELECT tsv_bm25::text, tsv::text FROM public.\"{engine.TestTableName}\" WHERE id = 'd1'";

            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());

            // The client vector holds the kept terms (the article is a stop word) and the server
            // vector is still written, so the lexical engine reads the same rows.
            Assert.Equal("'brown':2 'fox':3 'quick':1", reader.GetString(0));
            Assert.False(reader.IsDBNull(1));
            Assert.NotEqual(string.Empty, reader.GetString(1));
        });
    }

    // ---------------------------------------------------------------------------------------
    // Indexing, scoring and statistics
    // ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void ScoresMatchTheInMemoryIndex_TermForTerm()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("parity", engine =>
        {
            engine.Index(Corpus);

            var tokenizer = new Tokenizer(PostgresBm25Options.ReferenceAnalysisOptions);
            var index = new InMemoryTextIndex(tokenizer);
            index.Index(Corpus);

            var scorer = new Bm25Scorer(PostgresBm25Options.DefaultK1, PostgresBm25Options.DefaultB);

            foreach (string query in new[]
            {
                "quick brown",
                "cafés",
                "fox's",
                "1,234",
                "aspirin ®",
                "neural retrieval",
                "lazy dog",
                "the of and",
            })
            {
                IReadOnlyList<string> terms = tokenizer.Tokenize(query);

                var expected = index.Documents
                    .Select(document => (document.Id, Score: scorer.Score(document.Id, terms, index)))
                    .Where(candidate => candidate.Score > 0)
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.Id, StringComparer.Ordinal)
                    .ToList();

                var results = engine.Search(query, new SearchOptions(Limit: 100));

                Assert.Equal(expected.Select(candidate => candidate.Id), results.Select(result => result.DocumentId));

                for (int i = 0; i < results.Count; i++)
                    Assert.InRange(results[i].Score, expected[i].Score - 1e-9, expected[i].Score + 1e-9);
            }
        });
    }

    [SkippableFact]
    public void StatisticsReadTheClientColumn_DocumentFrequenciesAndLengthsAgreeWithTheIndex()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("statistics", engine =>
        {
            engine.Index(Corpus);

            var tokenizer = new Tokenizer(PostgresBm25Options.ReferenceAnalysisOptions);
            var index = new InMemoryTextIndex(tokenizer);
            index.Index(Corpus);

            Dictionary<string, (int Documents, int Occurrences)> statistics =
                ReadStatistics(engine.TestTableName, "tsv_bm25");

            // N and the vocabulary are corpus-level facts on both sides.
            Assert.Equal(index.Vocabulary.Count(), engine.RefreshStatistics());
            Assert.Equal(index.StatisticDocumentCount, CountNonEmptyVectors(engine.TestTableName));

            foreach (string term in index.Vocabulary)
            {
                Assert.True(statistics.TryGetValue(term, out var counts), $"Missing term '{term}'.");
                Assert.Equal(index.DocumentFrequency(term), counts.Documents);
                Assert.Equal(index.CorpusFrequency(term), counts.Occurrences);
            }
        });
    }

    private static int CountNonEmptyVectors(string table)
    {
        using var connection = new NpgsqlConnection(ConnectionString!);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count(*) FROM public.\"{table}\" WHERE tsv_bm25 IS NOT NULL AND tsv_bm25::text <> ''";

        return Convert.ToInt32(command.ExecuteScalar());
    }

    [SkippableFact]
    public void Search_HonoursAQuotedPhraseOverTheKeptPositions()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("phrase", engine =>
        {
            engine.Index(Corpus);

            // `brown fox` is adjacent in d1; `fox brown` is not.
            Assert.Equal("d1", Assert.Single(engine.Search("\"brown fox\"", new SearchOptions(Limit: 10))).DocumentId);
            Assert.Empty(engine.Search("\"fox brown\""));
            Assert.Empty(engine.Search("\"quick lazy\""));

            // A phrase whose stop word the analysis removed: the positions are compact, so the
            // words on either side of `the` are adjacent in the kept stream. This is the documented
            // difference from an engine that indexes stop-word positions — and d2, whose kept
            // stream holds `quick quick brown ...`, matches the same adjacency.
            Assert.Equal(
                ["d1", "d2"],
                engine.Search("\"quick the brown\"", new SearchOptions(Limit: 10))
                    .Select(result => result.DocumentId)
                    .Order(StringComparer.Ordinal));
        });
    }

    [SkippableFact]
    public void ARowWrittenByAnotherEngineIsAbsentUntilThisEngineRewritesIt()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        string table = Table("writer");

        try
        {
            using var bm25 = new PostgresBm25SearchEngine(ConnectionString!, LexiSharpOptions(table));
            using var lexical = new PostgresTextSearchEngine(
                ConnectionString!, new PostgresIndexOptions { Table = table });

            bm25.Add(new SearchDocument("d1", "alpha beta"));
            Assert.Equal("d1", Assert.Single(bm25.Search("alpha")).DocumentId);

            // The lexical engine writes `tsv` only: the row is in the table and in the lexical
            // index, but outside the client index until this engine writes it again.
            lexical.Add(new SearchDocument("d2", "alpha gamma"));

            Assert.Empty(bm25.Search("gamma"));
            Assert.Equal("d2", Assert.Single(lexical.Search("gamma")).DocumentId);

            bm25.Add(new SearchDocument("d2", "alpha gamma"));

            Assert.Equal("d2", Assert.Single(bm25.Search("gamma")).DocumentId);
        }
        finally
        {
            using var dropper = new PostgresBm25SearchEngine(
                ConnectionString!, LexiSharpOptions(table) with { AutoCreateSchema = false });
            dropper.DropSchema();
        }
    }

    // ---------------------------------------------------------------------------------------
    // Query-time dictionaries
    // ---------------------------------------------------------------------------------------

    [SkippableFact]
    public void QueryDictionaries_BroadenAFreeTermThroughTheServer()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        // `english_stem` folds generously → generous; the `simple` index does not, so without the
        // dictionary the query cannot find the document, and with it the expansion is the only
        // reason it can.
        Run("noexpand", engine =>
        {
            engine.Add(new SearchDocument("d1", "a generous offer"));

            Assert.Empty(engine.Search("generously"));
        }, options => options with { Analysis = PostgresAnalysis.Database, TextSearchConfig = "simple" });

        Run("expand", engine =>
        {
            engine.Add(new SearchDocument("d1", "a generous offer"));

            Assert.Equal("d1", Assert.Single(engine.Search("generously", new SearchOptions(Limit: 10))).DocumentId);
        }, options => options with
        {
            Analysis = PostgresAnalysis.Database,
            TextSearchConfig = "simple",
            QueryDictionaries = ["english_stem"],
        });
    }

    [SkippableFact]
    public void QueryDictionaries_AlsoWorkBesideTheClientAnalysis()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        // An unstemmed client index (another language's analyser would be the real case) plus the
        // server's English stemmer at query time: the document is found only through the expansion.
        Func<PostgresBm25Options, PostgresBm25Options> unstemmed = options => options with
        {
            AnalysisOptions = PostgresBm25Options.ReferenceAnalysisOptions with { Stemmer = null },
        };

        Run("clientnoexpand", engine =>
        {
            engine.Add(new SearchDocument("d1", "a generous offer"));

            Assert.Empty(engine.Search("generously"));
        }, unstemmed);

        Run("clientexpand", engine =>
        {
            engine.Add(new SearchDocument("d1", "a generous offer"));

            Assert.Equal("d1", Assert.Single(engine.Search("generously", new SearchOptions(Limit: 10))).DocumentId);
        }, options => unstemmed(options) with { QueryDictionaries = ["english_stem"] });
    }

    [SkippableFact]
    public void QueryDictionaries_LeaveAPhraseAlone()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run("phraseexpand", engine =>
        {
            engine.Add(new SearchDocument("d1", "a generous offer"));

            // The expansion is a free-term alternative. A quoted phrase is the query's contract, so
            // `"generously offered"` must still match nothing.
            Assert.Empty(engine.Search("\"generously offered\""));
        }, options => options with
        {
            Analysis = PostgresAnalysis.Database,
            QueryDictionaries = ["english_stem"],
        });
    }

    [Fact]
    public void QueryDictionariesAreRefusedUnderAllTerms()
    {
        var options = new PostgresBm25Options
        {
            MatchMode = PostgresMatchMode.AllTerms,
            QueryDictionaries = ["english_stem"],
        };

        ArgumentException error = Assert.Throws<ArgumentException>(() => new PostgresBm25SearchEngine(
            "Host=localhost;Database=unused", options, autoCreateSchema: false));

        Assert.Contains("QueryDictionaries", error.Message, StringComparison.Ordinal);
        Assert.Contains("AnyTerm", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void QueryDictionaryNamesAreValidatedLikeEveryOtherIdentifier()
    {
        Assert.Throws<ArgumentException>(() => new PostgresBm25SearchEngine(
            "Host=localhost;Database=unused",
            new PostgresBm25Options { QueryDictionaries = ["english_stem'; DROP TABLE x; --"] },
            autoCreateSchema: false));
    }

    // ---------------------------------------------------------------------------------------
    // Default mode
    // ---------------------------------------------------------------------------------------

    [Fact]
    public void TheDefaultAnalysisIsDatabase()
    {
        Assert.Equal(PostgresAnalysis.Database, new PostgresBm25Options().Analysis);
    }

    [SkippableFact]
    public void DatabaseMode_StillIndexesWithPostgreSQLsConfiguration()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        string table = Table("database");

        using var engine = new PostgresBm25SearchEngine(
            ConnectionString!,
            new PostgresBm25Options { Table = table, TextSearchConfig = "english" });

        try
        {
            engine.Add(new SearchDocument("d1", "generously jumping dogs"));

            // `english` is Snowball: generously and generous share a stem, and the article and the
            // auxiliary in the query are stop words. Behaviour this mode has always had.
            Assert.Equal("d1", Assert.Single(engine.Search("generous")).DocumentId);
            Assert.Empty(engine.Search("the of and"));

            Assert.False(ColumnExists(table, "tsv_bm25"));
        }
        finally
        {
            engine.DropSchema();
        }
    }
}
