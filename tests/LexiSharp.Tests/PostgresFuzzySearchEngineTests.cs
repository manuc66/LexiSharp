using LexiSharp.Core;
using LexiSharp.Postgres;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

public class PostgresFuzzySearchEngineTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static void SkipIfUnavailable(bool requireFuzzystrmatch = false)
    {
        if (ConnectionString is null)
            Skip.If(true, "POSTGRES_TEST_CONNECTION not set.");

        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pg_available_extensions WHERE name IN ('pg_trgm', 'fuzzystrmatch')";

        var available = new HashSet<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            available.Add(reader.GetString(0));

        if (!available.Contains("pg_trgm"))
            Skip.If(true, "pg_trgm extension not available.");

        if (requireFuzzystrmatch && !available.Contains("fuzzystrmatch"))
            Skip.If(true, "fuzzystrmatch extension not available.");
    }

    private static PostgresFuzzySearchEngine NewEngine(
        PostgresFuzzyOptions? options = null,
        string table = "lexisharp_fuzzy_tests")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var opts = options ?? new PostgresFuzzyOptions();
        return new PostgresFuzzySearchEngine(
            ConnectionString!,
            opts with { Table = $"{table}_{suffix}" });
    }

    private static void Run(
        Action<PostgresFuzzySearchEngine> action,
        PostgresFuzzyOptions? options = null,
        bool requireFuzzystrmatch = false)
    {
        SkipIfUnavailable(requireFuzzystrmatch);

        using var engine = NewEngine(options);

        try
        {
            action(engine);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    private static SearchDocument Doc(string id, string text) => new(id, text);

    [SkippableFact]
    public void EnsureSchema_CreatesTrgmAndMetaphoneIndexes()
    {
        Run(engine =>
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT indexname FROM pg_indexes WHERE tablename = @t";
            command.Parameters.AddWithValue("t", engine.TestTableName);

            var indexes = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                indexes.Add(reader.GetString(0));

            Assert.Contains(indexes, name => name.Contains("_content_trgm_gist"));
            Assert.Contains(indexes, name => name.Contains("_content_trgm_gin"));
            Assert.Contains(indexes, name => name.Contains("_tsv_gin"));
        });
    }

    [SkippableFact]
    public void EnsureSchema_IncludePhonetic_CreatesMetaphoneIndex()
    {
        Run(
            engine =>
            {
                using var connection = new NpgsqlConnection(ConnectionString);
                connection.Open();

                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE tablename = @t AND indexname LIKE '%_metaphone_idx'";
                command.Parameters.AddWithValue("t", engine.TestTableName);

                Assert.Equal(1L, (long)command.ExecuteScalar()!);
            },
            options: new PostgresFuzzyOptions { IncludePhonetic = true },
            requireFuzzystrmatch: true);
    }

    [SkippableFact]
    public void NearestMode_ReturnsClosestLabelFirst()
    {
        Run(engine =>
        {
            engine.Add(Doc("a", "classic rock"));
            engine.Add(Doc("b", "classical music"));
            engine.Add(Doc("c", "hip hop beats"));

            var results = engine.Search("classic");

            Assert.Contains("a", results.Select(r => r.DocumentId));
            Assert.Contains("b", results.Select(r => r.DocumentId));
            Assert.Equal("a", results[0].DocumentId);
            Assert.Equal("classic rock", results[0].Document.Text);

            Assert.All(results, r => Assert.InRange(r.Score, 0, 1));
        });
    }

    [SkippableFact]
    public void NearestMode_RoundsTripFieldsAndCategory()
    {
        Run(engine =>
        {
            engine.Add(new SearchDocument("a", "north wind",
                new Dictionary<string, string> { ["dir"] = "n" }, "weather"));

            var results = engine.Search("north winds");

            var hit = results.Single(r => r.DocumentId == "a");
            Assert.Equal("weather", hit.Document.Category);
            Assert.Equal("n", hit.Document.Fields!["dir"]);
        });
    }

    [SkippableFact]
    public void SimilarityMode_HonorsThreshold_AndDropsUnrelated()
    {
        Run(
            engine =>
            {
                engine.Add(Doc("a", "quik fox"));
                engine.Add(Doc("b", "slow turtle"));

                var results = engine.Search("quick fox");

                Assert.Single(results);
                Assert.Equal("a", results[0].DocumentId);
                Assert.Equal("quik fox", results[0].Document.Text);
            },
            options: new PostgresFuzzyOptions { SearchMode = TrgmSearchMode.Similarity });
    }

    [SkippableFact]
    public void SimilarityMode_HighThreshold_KeepsOnlyExactMatch()
    {
        Run(
            engine =>
            {
                engine.Add(Doc("a", "hello world"));
                engine.Add(Doc("b", "goodbye moon"));

                var results = engine.Search("hello world");

                Assert.Single(results);
                Assert.Equal("a", results[0].DocumentId);
                Assert.Equal(1.0, results[0].Score, 4);
            },
            options: new PostgresFuzzyOptions { SearchMode = TrgmSearchMode.Similarity, SimilarityThreshold = 0.99 });
    }

    [SkippableFact]
    public void NearestMode_LevenshteinRefinement_FiltersEditDistance()
    {
        Run(
            engine =>
            {
                engine.Add(Doc("a", "kitten"));
                engine.Add(Doc("b", "sitting"));
                engine.Add(Doc("c", "horse"));

                var results = engine.Search("kitten");

                Assert.Single(results);
                Assert.Equal("a", results[0].DocumentId);
            },
            options: new PostgresFuzzyOptions
            {
                SearchMode = TrgmSearchMode.Nearest,
                UseLevenshteinRefinement = true,
                MaxLevenshteinDistance = 1,
            },
            requireFuzzystrmatch: true);
    }

    [SkippableFact]
    public void SimilarityMode_Phonetic_MatchesSameSoundDifferentSpelling()
    {
        Run(
            engine =>
            {
                engine.Add(Doc("a", "john"));
                engine.Add(Doc("b", "erik"));

                var results = engine.Search("jon");

                Assert.Contains("a", results.Select(r => r.DocumentId));
                Assert.DoesNotContain("b", results.Select(r => r.DocumentId));
            },
            options: new PostgresFuzzyOptions
            {
                SearchMode = TrgmSearchMode.Similarity,
                IncludePhonetic = true,
            },
            requireFuzzystrmatch: true);
    }

    [SkippableFact]
    public void Search_Offset_PaginatesTheRanking()
    {
        Run(engine =>
        {
            // Every document shares trigrams with the query (all keep positive scores); the
            // Nearest mode's `id ASC` tiebreak makes the ordering stable across requests.
            engine.Add(Doc("a", "classic"));
            engine.Add(Doc("b", "classics"));
            engine.Add(Doc("c", "classical"));
            engine.Add(Doc("d", "classical music"));
            engine.Add(Doc("e", "classical music pieces"));

            var all = engine.Search("classic", new SearchOptions(Limit: 10));
            Assert.Equal(5, all.Count);

            var page1 = engine.Search("classic", new SearchOptions(Limit: 2, Offset: 0));
            var page2 = engine.Search("classic", new SearchOptions(Limit: 2, Offset: 2));
            var page3 = engine.Search("classic", new SearchOptions(Limit: 2, Offset: 4));

            Assert.Equal(2, page1.Count);
            Assert.Equal(2, page2.Count);
            Assert.Single(page3);
            Assert.Equal(
                all.Select(r => r.DocumentId),
                page1.Concat(page2).Concat(page3).Select(r => r.DocumentId));

            Assert.Empty(engine.Search("classic", new SearchOptions(Limit: 2, Offset: 5)));
            Assert.Empty(engine.Search("classic", new SearchOptions(Limit: 2, Offset: -1)));
        });
    }

    [SkippableFact]
    public void RemoveAndClear_AreReflectedInSearch()
    {
        Run(engine =>
        {
            engine.Add(Doc("a", "timber"));
            engine.Add(Doc("b", "timber wolf"));

            engine.Remove("a");
            Assert.DoesNotContain("timber", engine.Search("timber").Select(r => r.Document.Text));

            engine.Clear();
            Assert.Empty(engine.Search("timber"));
        });
    }
}