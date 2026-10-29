using LexiSharp.Core;
using LexiSharp.ParadeDB;
using LexiSharp.Postgres;
using Npgsql;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// One contract, every SQL engine: scores that are exactly equal are ordered by the document id,
/// ordinally — the page the in-memory engines return under the default
/// <see cref="TieBreak.DocumentId"/>.
/// </summary>
/// <remarks>
/// <para>
/// The corpus is inserted in reverse id order and every document is identical, so all scores are
/// equal and the order a scan returns without a secondary sort is the reverse of the page these
/// tests ask for. A statement that orders on the score alone fails here rather than passing by luck.
/// </para>
/// <para>
/// These run against a real server and auto-skip without <c>POSTGRES_TEST_CONNECTION</c>, like the
/// other backend suites.
/// </para>
/// </remarks>
public class PostgresTieBreakTests
{
    private const string Query = "shared common term";

    private static readonly string[] OrdinalIds = ["d1", "d2", "d3", "d4"];

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static string Table(string suffix) => $"lexisharp_tie_{suffix}_{Guid.NewGuid():N}"[..44];

    private static IReadOnlyList<SearchDocument> ReversedCorpus() =>
        OrdinalIds.Reverse().Select(id => new SearchDocument(id, Query)).ToArray();

    private static void AssertOrdinalPage(IReadOnlyList<SearchResult> results) =>
        Assert.Equal(OrdinalIds, results.Select(r => r.DocumentId).ToArray());

    private static bool ExtensionAvailable(string name)
    {
        using var connection = new NpgsqlConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_available_extensions WHERE name = @name";
        command.Parameters.AddWithValue("name", name);

        return (long)command.ExecuteScalar()! > 0;
    }

    private static void SkipIfNoServer() =>
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

    [SkippableFact]
    public void TextEngine_OrdersEqualScoresByOrdinalId()
    {
        SkipIfNoServer();

        using var engine = new PostgresTextSearchEngine(
            ConnectionString!, new PostgresIndexOptions { Table = Table("text") });

        try
        {
            foreach (var document in ReversedCorpus())
                engine.Add(document);

            AssertOrdinalPage(engine.Search(Query, new SearchOptions(Limit: 10)));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    /// <summary>
    /// The database's default collation is locale-sensitive: <c>en_US.utf8</c> folds case, so it
    /// orders <c>a</c> before <c>A</c>. Ordinal order — what <see cref="TieBreak.DocumentId"/>
    /// documents and the in-memory engines use — puts <c>A</c> first. This test is what pins
    /// <c>COLLATE "C"</c> rather than a bare <c>id ASC</c>.
    /// </summary>
    [SkippableFact]
    public void TextEngine_ComparesIdsOrdinallyNotUnderTheDatabaseCollation()
    {
        SkipIfNoServer();

        using var engine = new PostgresTextSearchEngine(
            ConnectionString!, new PostgresIndexOptions { Table = Table("collation") });

        try
        {
            engine.Add(new SearchDocument("b", Query));
            engine.Add(new SearchDocument("a", Query));
            engine.Add(new SearchDocument("A", Query));

            var results = engine.Search(Query, new SearchOptions(Limit: 10));

            Assert.Equal(["A", "a", "b"], results.Select(r => r.DocumentId).ToArray());
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void SparseEngine_OrdersEqualScoresByOrdinalId()
    {
        SkipIfNoServer();
        Skip.If(!ExtensionAvailable("vector"), "vector extension unavailable.");

        var vocabulary = new Dictionary<string, int>
        {
            ["shared"] = 0,
            ["common"] = 1,
            ["term"] = 2,
        };

        var vector = (IReadOnlyDictionary<string, float>)new Dictionary<string, float>
        {
            ["shared"] = 1f,
            ["common"] = 1f,
            ["term"] = 1f,
        };

        using var engine = new PostgresSparseSearchEngine(
            ConnectionString!,
            new ConstantSparseProvider(vector),
            new PostgresSparseOptions { Table = Table("sparse"), Vocabulary = vocabulary });

        try
        {
            foreach (var document in ReversedCorpus())
                engine.Add(document);

            AssertOrdinalPage(engine.Search(Query, new SearchOptions(Limit: 10)));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void FuzzyEngine_OrdersEqualScoresByOrdinalId()
    {
        SkipIfNoServer();
        Skip.If(!ExtensionAvailable("pg_trgm"), "pg_trgm extension unavailable.");

        using var engine = new PostgresFuzzySearchEngine(
            ConnectionString!, new PostgresFuzzyOptions { Table = Table("fuzzy") });

        try
        {
            foreach (var document in ReversedCorpus())
                engine.Add(document);

            AssertOrdinalPage(engine.Search(Query, new SearchOptions(Limit: 10)));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void VectorEngine_OrdersEqualScoresByOrdinalId()
    {
        SkipIfNoServer();
        Skip.If(!ExtensionAvailable("vector"), "vector extension unavailable.");

        using var engine = new PostgresVectorSearchEngine(
            ConnectionString!,
            new ConstantEmbeddingProvider(),
            new PostgresVectorOptions { Dimension = 2, Table = Table("vector") });

        try
        {
            foreach (var document in ReversedCorpus())
                engine.Add(document);

            AssertOrdinalPage(engine.Search(Query, new SearchOptions(Limit: 10)));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    /// <summary>
    /// The multi-column engine merges one ordered list per column, so the per-column SQL tie-break
    /// cannot name the order between two documents that are equal in <em>different</em> columns.
    /// The columns are listed title-first, and the corpus gives <c>d2</c> its perfect score on the
    /// title and <c>d1</c> its perfect score on the description, so column order puts <c>d2</c>
    /// first and only the merge's ordinal comparison puts <c>d1</c> there.
    /// </summary>
    [SkippableFact]
    public void VectorEngine_OrdersEqualScoresAcrossColumnsByOrdinalId()
    {
        SkipIfNoServer();
        Skip.If(!ExtensionAvailable("vector"), "vector extension unavailable.");

        var vectors = new Dictionary<string, float[]>
        {
            ["alpha"] = [1f, 0f],
            ["bravo"] = [0f, 1f],
        };

        using var engine = new PostgresVectorSearchEngine(
            ConnectionString!,
            new StubEmbeddingProvider(vectors),
            new PostgresVectorOptions
            {
                Dimension = 2,
                Table = Table("columns"),
                EmbeddingColumns = new Dictionary<string, string>
                {
                    ["title"] = "title",
                    ["description"] = "description",
                },
            });

        try
        {
            engine.Add(new SearchDocument("d1", "unrelated body",
                TextFields: new Dictionary<string, string> { ["title"] = "bravo", ["description"] = "alpha" }));
            engine.Add(new SearchDocument("d2", "unrelated body",
                TextFields: new Dictionary<string, string> { ["title"] = "alpha", ["description"] = "bravo" }));

            var results = engine.Search("alpha", new SearchOptions(Limit: 10));

            Assert.Equal(["d1", "d2"], results.Select(r => r.DocumentId).ToArray());
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void ParadeDBEngine_OrdersEqualScoresByOrdinalId()
    {
        SkipIfNoServer();
        Skip.If(!ExtensionAvailable("pg_search"), "pg_search extension unavailable.");

        using var engine = new ParadeDBTextSearchEngine(
            ConnectionString!, new ParadeDBOptions { Table = Table("parade") });

        try
        {
            foreach (var document in ReversedCorpus())
                engine.Add(document);

            AssertOrdinalPage(engine.Search(Query, new SearchOptions(Limit: 10)));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    /// <summary>Every text gets the same vector, so every document scores identically.</summary>
    private sealed class ConstantSparseProvider(IReadOnlyDictionary<string, float> vector)
        : ISparseEmbeddingProvider
    {
        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult(vector);
    }

    /// <summary>Every text gets the same vector, so every document scores identically.</summary>
    private sealed class ConstantEmbeddingProvider : IEmbeddingProvider
    {
        private static readonly float[] Vector = [1f, 0f];

        public int Dimension => Vector.Length;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult((ReadOnlyMemory<float>)Vector);
    }

    private sealed class StubEmbeddingProvider(IReadOnlyDictionary<string, float[]> vectors) : IEmbeddingProvider
    {
        public int Dimension => vectors.Values.First().Length;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult(vectors.TryGetValue(text, out var vector)
                ? (ReadOnlyMemory<float>)vector
                : throw new KeyNotFoundException($"No embedding configured for '{text}'."));
    }
}
