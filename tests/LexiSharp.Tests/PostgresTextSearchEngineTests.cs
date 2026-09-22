using LexiSharp.Core;
using LexiSharp.Postgres;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Integration tests against a real PostgreSQL instance. They process only when
/// <c>POSTGRES_TEST_CONNECTION</c> is set to a reachable connection string
/// (e.g. <c>Host=localhost;Port=5432;Username=postgres;Password=postgres;Database=lexisharp</c>);
/// otherwise they are skipped so the suite stays green without Docker/a server.
/// </summary>
public class PostgresTextSearchEngineTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static PostgresTextSearchEngine NewEngine(string? connection = null, string table = "lexisharp_engine_tests")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var engine = new PostgresTextSearchEngine(
            connection ?? ConnectionString!,
            new PostgresIndexOptions { Table = $"{table}_{suffix}" });

        return engine;
    }

    private static void Run(Action<PostgresTextSearchEngine> action)
    {
        using var engine = NewEngine();

        try
        {
            action(engine);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void EnsureSchema_CreatesTableAndIndex()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            using var connection = new NpgsqlConnection(ConnectionString!);
            connection.Open();

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = @table_name"; // NOSONAR:S2077
                check.Parameters.AddWithValue("table_name", engine.TestTableName);
                Assert.Equal(1L, check.ExecuteScalar());
            }

            using (var check = connection.CreateCommand())
            {
                check.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE tablename = @table_name AND indexdef ILIKE '%USING gin (%tsv%'"; // NOSONAR:S2077
                check.Parameters.AddWithValue("table_name", engine.TestTableName);
                Assert.Equal(1L, check.ExecuteScalar());
            }
        });
    }

    [SkippableFact]
    public void AddAndSearch_RoundTripsDocuments()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(new SearchDocument("a", "The quick brown fox jumps over the lazy dog",
                new Dictionary<string, string> { ["kind"] = "fable" }, "fable"));
            engine.Add(new SearchDocument("b", "A quick fox, a lazy dog."));

            var results = engine.Search("quick fox", new SearchOptions(Limit: 10));

            Assert.Equal(2, results.Count);
            Assert.Equal(new[] { "a", "b" }, results.Select(r => r.DocumentId).OrderBy(x => x).ToArray());

            var fable = results.Single(r => r.DocumentId == "a");
            Assert.Equal("fable", fable.Document.Category);
            Assert.Equal("fable", fable.Document.Fields!["kind"]);
        });
    }

    [SkippableFact]
    public void Search_IsAccentInsensitive()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(new SearchDocument("cafe", "un café et une vie à Paris"));

            var results = engine.Search("cafe vie");

            Assert.Single(results);
            Assert.Equal("cafe", results[0].DocumentId);
        });
    }

    [SkippableFact]
    public void Search_WithNoMatchingTermReturnsNothing()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(new SearchDocument("a", "organic vegetables"));

            Assert.Empty(engine.Search("quantum mechanics"));
        });
    }

    [SkippableFact]
    public void Index_ReplacesContentAndRemove_Deletes()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Index(new[]
            {
                new SearchDocument("a", "first version content"),
                new SearchDocument("b", "second document"),
            });

            engine.Index(new[] { new SearchDocument("b", "replaced content") });

            Assert.Empty(engine.Search("first version"));
            Assert.Single(engine.Search("replaced"));

            engine.Remove("b");
            Assert.Empty(engine.Search("replaced"));

            engine.Clear();
            Assert.Empty(engine.Search("replaced"));
        });
    }

    [SkippableFact]
    public void Search_AppliesLimitAndMinimumScore()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            for (int i = 0; i < 5; i++)
                engine.Add(new SearchDocument($"d{i}", $"repeated term term term number {i}"));

            var limited = engine.Search("term", new SearchOptions(Limit: 2));
            Assert.Equal(2, limited.Count);

            var all = engine.Search("term term term", new SearchOptions(Limit: 10, MinimumScore: 0.0));
            Assert.Equal(5, all.Count);

            // ts_rank_cd ≈ 0.3 for every identical doc; 0.4 cuts them all, 0.1 keeps them all.
            var none = engine.Search("term term term", new SearchOptions(Limit: 10, MinimumScore: 0.4));
            Assert.Empty(none);

            var some = engine.Search("term term term", new SearchOptions(Limit: 10, MinimumScore: 0.1));
            Assert.Equal(5, some.Count);
        });
    }
}