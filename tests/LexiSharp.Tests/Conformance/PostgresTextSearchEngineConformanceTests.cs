using LexiSharp.Core;
using LexiSharp.Postgres;

namespace LexiSharp.Tests.Conformance;

public class PostgresTextSearchEngineConformanceTests : SearchEngineConformanceTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    protected override EngineCapabilities Capabilities => new(Phrases: true);

    protected override bool IsUnavailable => ConnectionString is null;

    protected override string UnavailableReason => "POSTGRES_TEST_CONNECTION not set.";

    protected override ITextSearchEngine CreateEngine()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new PostgresTextSearchEngine(
            ConnectionString!,
            new PostgresIndexOptions { Table = $"lexisharp_conformance_{suffix}" });
    }

    protected override void DestroyEngine(ITextSearchEngine engine)
    {
        var postgres = (PostgresTextSearchEngine)engine;

        try
        {
            postgres.DropSchema();
        }
        finally
        {
            postgres.Dispose();
        }
    }
}
