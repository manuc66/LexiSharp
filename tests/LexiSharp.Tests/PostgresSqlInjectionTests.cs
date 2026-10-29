using LexiSharp.Core;
using LexiSharp.ParadeDB;
using LexiSharp.Postgres;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Every value a caller supplies — a document's text, a query, a filter's field and value — reaches
/// PostgreSQL as a parameter, never as statement text. Each test carries a payload that would end the
/// statement if that stopped being true, and then asks the table whether it is still there.
/// </summary>
/// <remarks>
/// <para>
/// Identifiers are a different contract, because they cannot be parameters: <c>SafeIdentifier</c>
/// restricts them to <c>[A-Za-z0-9_]</c> and quotes them, and <c>PostgresIdentifierValidationTests</c>
/// pins the refusal. This file is about values.
/// </para>
/// <para>
/// The payload names the test's own generated table, so an injection that worked would drop it and
/// the count that follows would throw rather than pass quietly.
/// </para>
/// </remarks>
public class PostgresSqlInjectionTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static string Table(string suffix) => $"lexisharp_inject_{suffix}_{Guid.NewGuid():N}"[..44];

    private static string Payload(string table) => $"x'; DROP TABLE public.{table}; --";

    private static void SkipIfNoServer() =>
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

    private static void AssertTableStillHasOneRow(string table)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM public.\"{table}\"";

        Assert.Equal(1L, (long)command.ExecuteScalar()!);
    }

    [SkippableFact]
    public void TextEngine_StoresADocumentCarryingSqlAsText()
    {
        SkipIfNoServer();

        string table = Table("text");
        using var engine = new PostgresTextSearchEngine(
            ConnectionString!, new PostgresIndexOptions { Table = table });

        try
        {
            string payload = Payload(table);
            engine.Add(new SearchDocument("d1", payload + " common"));

            var results = engine.Search("common", new SearchOptions(Limit: 10));

            Assert.Equal("d1", Assert.Single(results).DocumentId);
            AssertTableStillHasOneRow(table);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void TextEngine_TreatsTheQueryAsAValue()
    {
        SkipIfNoServer();

        string table = Table("query");
        using var engine = new PostgresTextSearchEngine(
            ConnectionString!, new PostgresIndexOptions { Table = table });

        try
        {
            string payload = Payload(table);
            engine.Add(new SearchDocument("d1", "common term"));

            engine.Search(payload, new SearchOptions(Limit: 10));

            AssertTableStillHasOneRow(table);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void TextEngine_TreatsAFilterFieldAndValueAsParameters()
    {
        SkipIfNoServer();

        string table = Table("filter");
        using var engine = new PostgresTextSearchEngine(
            ConnectionString!, new PostgresIndexOptions { Table = table });

        try
        {
            string payload = Payload(table);
            engine.Add(new SearchDocument(
                "d1", "common term", new Dictionary<string, string> { ["kind"] = "a" }));

            var option = new SearchOptions(
                Limit: 10,
                Filters: [new MetadataFilter($"kind{payload}", MetadataFilterOperator.Equal, payload)]);

            engine.Search("common", option);

            AssertTableStillHasOneRow(table);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void FuzzyEngine_TreatsTheQueryAsAValue()
    {
        SkipIfNoServer();
        Skip.If(!ExtensionAvailable("pg_trgm"), "pg_trgm extension unavailable.");

        string table = Table("fuzzy");
        using var engine = new PostgresFuzzySearchEngine(
            ConnectionString!, new PostgresFuzzyOptions { Table = table });

        try
        {
            string payload = Payload(table);
            engine.Add(new SearchDocument("d1", "common term"));

            engine.Search(payload, new SearchOptions(Limit: 10));

            AssertTableStillHasOneRow(table);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void ParadeDBEngine_TreatsTheQueryAndAPhraseAsValues()
    {
        SkipIfNoServer();
        Skip.If(!ExtensionAvailable("pg_search"), "pg_search extension unavailable.");

        string table = Table("parade");
        using var engine = new ParadeDBTextSearchEngine(
            ConnectionString!, new ParadeDBOptions { Table = table });

        try
        {
            string payload = Payload(table);
            engine.Add(new SearchDocument("d1", "common term"));

            // The unquoted path reaches `|||`; the quoted one reaches `###` with the phrase as a
            // parameter. Both are values, so neither can end the statement.
            engine.Search(payload, new SearchOptions(Limit: 10));
            engine.Search($"\"{payload}\"", new SearchOptions(Limit: 10));

            AssertTableStillHasOneRow(table);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    private static bool ExtensionAvailable(string name)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = @name";
        command.Parameters.AddWithValue("name", name);

        return (long)command.ExecuteScalar()! > 0;
    }
}
