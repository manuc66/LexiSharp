using LexiSharp.Core;
using LexiSharp.Postgres;

namespace LexiSharp.Tests.Conformance;

public class PostgresBm25SearchEngineConformanceTests : SearchEngineConformanceTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    protected override bool IsUnavailable => ConnectionString is null;

    protected override string UnavailableReason => "POSTGRES_TEST_CONNECTION not set.";

    protected override ITextSearchEngine CreateEngine()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return new PostgresBm25SearchEngine(
            ConnectionString!,
            new PostgresBm25Options { Table = $"lexisharp_bm25_conformance_{suffix}" });
    }

    protected override void DestroyEngine(ITextSearchEngine engine)
    {
        var postgres = (PostgresBm25SearchEngine)engine;

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
