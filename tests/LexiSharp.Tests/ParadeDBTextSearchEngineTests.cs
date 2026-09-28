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
    public void Search_Offset_PaginatesTheRanking()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(Doc("dense", "fox fox fox"));
            engine.Add(Doc("scarce", "the fox"));
            engine.Add(Doc("single", "the quick brown fox"));
            engine.Add(Doc("more", "the quick brown fox jumps"));
            engine.Add(Doc("most", "the quick brown fox jumps over"));

            var all = engine.Search("fox", new SearchOptions(Limit: 10));
            Assert.Equal(5, all.Count);

            var page1 = engine.Search("fox", new SearchOptions(Limit: 2, Offset: 0));
            var page2 = engine.Search("fox", new SearchOptions(Limit: 2, Offset: 2));
            var page3 = engine.Search("fox", new SearchOptions(Limit: 2, Offset: 4));

            Assert.Equal(2, page1.Count);
            Assert.Equal(2, page2.Count);
            Assert.Single(page3);
            Assert.Equal(
                all.Select(r => r.DocumentId),
                page1.Concat(page2).Concat(page3).Select(r => r.DocumentId));

            Assert.Empty(engine.Search("fox", new SearchOptions(Limit: 2, Offset: 5)));
            Assert.Empty(engine.Search("fox", new SearchOptions(Limit: 2, Offset: -1)));
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

    [SkippableFact]
    public void Search_PhraseQuery_RequiresConsecutiveTerms()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            // ||| tokenizes quotes into a plain disjunction — only the ### path enforces
            // consecutive positions on this backend.
            engine.Add(Doc("adjacent", "running shoes are great"));
            engine.Add(Doc("reversed", "shoes running are great"));
            engine.Add(Doc("gapped", "running with shoes are great"));

            var results = engine.Search("\"running shoes\"", new SearchOptions(Limit: 10));

            var hit = Assert.Single(results);
            Assert.Equal("adjacent", hit.DocumentId);
            Assert.True(hit.Score > 0);

            // Free text around a phrase never hard-filters (stock parity): an absent free
            // term must not hide the phrase match.
            var withFree = engine.Search("absent \"running shoes\"", new SearchOptions(Limit: 10));

            var freeHit = Assert.Single(withFree);
            Assert.Equal("adjacent", freeHit.DocumentId);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Search_MultiplePhrases_AreAnded()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(Doc("both", "deep learning and machine learning"));
            engine.Add(Doc("first-only", "deep learning beats statistics"));
            engine.Add(Doc("second-only", "machine learning beats statistics"));

            var results = engine.Search("\"deep learning\" \"machine learning\"", new SearchOptions(Limit: 10));

            var hit = Assert.Single(results);
            Assert.Equal("both", hit.DocumentId);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Search_FiltersGateResults()
    {
        SkipIfUnavailable();

        using var engine = NewEngine();

        try
        {
            engine.Add(new SearchDocument("red", "red apple",
                new Dictionary<string, string> { ["kind"] = "fruit" }));
            engine.Add(new SearchDocument("green", "green apple",
                new Dictionary<string, string> { ["kind"] = "veg" }));

            var filtered = engine.Search("apple", new SearchOptions(Limit: 10,
                Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "fruit") }));

            Assert.Equal(new[] { "red" }, filtered.Select(r => r.DocumentId).ToArray());

            Assert.Empty(engine.Search("apple", new SearchOptions(Limit: 10,
                Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "nope") })));
        }
        finally
        {
            engine.DropSchema();
        }
    }
}