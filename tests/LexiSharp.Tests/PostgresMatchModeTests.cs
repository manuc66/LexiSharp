using LexiSharp.Core;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="PostgresIndexOptions.MatchMode"/>, against a real server. The default (every term)
/// is what the engine always did; <see cref="PostgresMatchMode.AnyTerm"/> changes which documents
/// are candidates, and these tests are what keeps it from changing anything else.
/// </summary>
public class PostgresMatchModeTests
{
    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static string Table(string suffix) => $"lexisharp_match_{suffix}_{Guid.NewGuid():N}"[..44];

    private static PostgresTextSearchEngine NewEngine(PostgresMatchMode mode, string suffix)
    {
        return new PostgresTextSearchEngine(
            ConnectionString!,
            new PostgresIndexOptions { Table = Table(suffix), MatchMode = mode });
    }

    private static void Run(PostgresMatchMode mode, string suffix, Action<PostgresTextSearchEngine> body)
    {
        using var engine = NewEngine(mode, suffix);

        try
        {
            body(engine);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "neural machine translation"),
        new("d2", "pasta recipe with tomato"),
    ];

    [SkippableFact]
    public void AllTerms_RequiresEveryTerm()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(PostgresMatchMode.AllTerms, "all", engine =>
        {
            engine.Index(Corpus);

            Assert.Empty(engine.Search("neural tomato"));
            Assert.Single(engine.Search("neural translation"));
        });
    }

    [SkippableFact]
    public void AnyTerm_MatchesADocumentHoldingOneOfTheTerms()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(PostgresMatchMode.AnyTerm, "any", engine =>
        {
            engine.Index(Corpus);

            var results = engine.Search("neural tomato", new SearchOptions(Limit: 10));

            Assert.Equal(["d1", "d2"], results.Select(result => result.DocumentId).Order().ToArray());
        });
    }

    [SkippableFact]
    public void AnyTerm_StillHonoursAQuotedPhraseAsAGate()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(PostgresMatchMode.AnyTerm, "phrase", engine =>
        {
            engine.Index(Corpus);

            // The phrase is a hard gate in both modes; the free term beside it only scores, so a
            // document holding only the free term ("tomato") does not become a result.
            var mixed = engine.Search("\"machine translation\" tomato", new SearchOptions(Limit: 10));

            Assert.Equal("d1", Assert.Single(mixed).DocumentId);
            Assert.Empty(engine.Search("\"machine learning\" tomato"));
            Assert.Equal("d1", engine.Search("\"machine translation\"").Single().DocumentId);
        });
    }

    [SkippableFact]
    public void AnyTerm_IsAccentInsensitiveAndCaseInsensitive()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(PostgresMatchMode.AnyTerm, "accents", engine =>
        {
            engine.Add(new SearchDocument("cafe", "un café à Paris"));

            Assert.Equal("cafe", engine.Search("CAFE").Single().DocumentId);
        });
    }

    [SkippableFact]
    public void AnyTerm_OnAQueryOfStopWordsReturnsNothing()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = new PostgresTextSearchEngine(
            ConnectionString!,
            new PostgresIndexOptions
            {
                Table = Table("stop"),
                MatchMode = PostgresMatchMode.AnyTerm,
                TextSearchConfig = "english",
            });

        try
        {
            engine.Add(new SearchDocument("d1", "the quick brown fox"));

            // All stop words: the tsquery is NULL rather than an error, and nothing matches.
            Assert.Empty(engine.Search("the of and"));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void AnyTerm_KeepsTheRankingMonotoneInTermOverlap()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(PostgresMatchMode.AnyTerm, "rank", engine =>
        {
            engine.Index(
            [
                new("one", "neural related"),
                new("two", "neural translation"),
                new("three", "unrelated words entirely"),
            ]);

            var results = engine.Search("neural translation", new SearchOptions(Limit: 3));

            // Same length, so coverage decides: the document matching both terms outranks the one
            // matching a single term, and the one matching neither is absent.
            Assert.Equal("two", results[0].DocumentId);
            Assert.Equal("one", results[1].DocumentId);
            Assert.DoesNotContain(results, result => result.DocumentId == "three");
        });
    }
}
