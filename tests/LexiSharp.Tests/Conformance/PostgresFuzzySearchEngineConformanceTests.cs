using LexiSharp.Core;
using LexiSharp.Postgres;
using Npgsql;

namespace LexiSharp.Tests.Conformance;

public class PostgresFuzzySearchEngineConformanceTests : SearchEngineConformanceTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static readonly Lazy<bool> TrgmAvailable = new(() =>
    {
        if (ConnectionString is null)
            return false;

        try
        {
            using var connection = new NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = 'pg_trgm'";
            return (long)command.ExecuteScalar()! > 0;
        }
        catch
        {
            return false;
        }
    });

    protected override bool IsUnavailable => ConnectionString is null || !TrgmAvailable.Value;

    protected override string UnavailableReason =>
        ConnectionString is null ? "POSTGRES_TEST_CONNECTION not set." : "pg_trgm extension not available.";

    protected override ITextSearchEngine CreateEngine()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new PostgresFuzzySearchEngine(
            ConnectionString!,
            new PostgresFuzzyOptions { Table = $"lexisharp_conformance_{suffix}" });
    }

    protected override void DestroyEngine(ITextSearchEngine engine)
    {
        var fuzzy = (PostgresFuzzySearchEngine)engine;

        try
        {
            fuzzy.DropSchema();
        }
        finally
        {
            fuzzy.Dispose();
        }
    }
}
