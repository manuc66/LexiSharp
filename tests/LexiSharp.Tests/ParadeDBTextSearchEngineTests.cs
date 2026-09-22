using LexiSharp.Core;
using LexiSharp.ParadeDB;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

public class ParadeDBTextSearchEngineTests
{
    private static readonly string[] DenseScarceSingleIds = new[] { "dense", "scarce", "single" };
    private static readonly string[] CatFoxIds = new[] { "cat", "fox" };
    private static readonly string[] ABCIds = new[] { "a", "b", "c" };

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static ParadeDBTextSearchEngine NewEngine(string table = "lexisharp_paradedb_tests")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new ParadeDBTextSearchEngine(
            ConnectionString!,
            new ParadeDBOptions { Table = $"{table}_{suffix}" });
    }

    private static bool ParadeDBAvailable(string connectionString)
    {
        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = 'pg_search'";
            return (long)command.ExecuteScalar()! > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void SkipIfUnavailable()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        if (ConnectionString is not null && !ParadeDBAvailable(ConnectionString))
            Skip.If(true, "pg_search extension not available on the target instance.");
    }

    private static SearchDocument Doc(string id, string text, string? category = null) =>
        new(id, text, null, category);

    [SkippableFact]
    public void EnsureSchema_CreatesParadeDBIndexAndTable()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT count(*) FROM pg_indexes
                WHERE schemaname = 'public' AND tablename = @t AND indexdef LIKE '%USING paradedb%';
                """;
            command.Parameters.AddWithValue("t", engine.TestTableName);
            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Search_RanksRepeatedTermDocumentFirst()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(Doc("scarce", "the fox"));
            engine.Add(Doc("dense", "fox fox fox"));
            engine.Add(Doc("single", "the quick brown fox"));

            var results = engine.Search("fox");

            Assert.Equal(DenseScarceSingleIds, results.Select(r => r.DocumentId).ToArray());
            Assert.All(results, r => Assert.True(r.Score > 0, $"score for {r.DocumentId} should be positive"));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Search_Disjunction_ReturnsAnySingleTermMatches()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(Doc("fox", "a fox runs in the night"));
            engine.Add(Doc("cat", "a cat purrs in the sun"));
            engine.Add(Doc("none", "the dog sleeps"));

            var results = engine.Search("fox cat");

            Assert.Equal(CatFoxIds.OrderBy(x => x), results.Select(r => r.DocumentId).OrderBy(x => x));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Search_AccentInsensitive_WithDefaultAsciiFolding()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(Doc("accent", "un élève résumé"));
            engine.Add(Doc("plain", "a plain resume"));

            var accented = engine.Search("resume", new SearchOptions(Limit: 10));
            var plain = engine.Search("eleve", new SearchOptions(Limit: 10));

            Assert.Contains("accent", accented.Select(r => r.DocumentId));
            Assert.Contains("accent", plain.Select(r => r.DocumentId));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Search_EqualScores_TieBreakById_IsDeterministic()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(Doc("b", "identical content"));
            engine.Add(Doc("a", "identical content"));
            engine.Add(Doc("c", "identical content"));

            var results = engine.Search("identical content");

            Assert.Equal(ABCIds, results.Select(r => r.DocumentId).ToArray());
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Add_AndSearch_RoundTripFieldsAndCategory()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(new SearchDocument(
                "a",
                "the quick brown fox",
                new Dictionary<string, string> { ["kind"] = "fable" },
                "fable"));

            var results = engine.Search("fox");

            Assert.Single(results);
            Assert.Equal("fable", results[0].Document.Category);
            Assert.Equal("fable", results[0].Document.Fields!["kind"]);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Remove_And_Clear_DeleteDocuments()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(Doc("a", "fox"));
            engine.Add(Doc("b", "fox"));

            engine.Remove("a");
            Assert.Single(engine.Search("fox"));

            engine.Clear();
            Assert.Empty(engine.Search("fox"));
        }
        finally
        {
            engine.DropSchema();
        }
    }
}