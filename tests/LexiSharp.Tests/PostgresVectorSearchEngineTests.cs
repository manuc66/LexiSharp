using LexiSharp.Core;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

public class PostgresVectorSearchEngineTests
{
    private const double Cos45 = 0.707106781;

    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        private readonly IReadOnlyDictionary<string, float[]> _vectors;

        public StubEmbeddingProvider(IReadOnlyDictionary<string, float[]> vectors)
        {
            _vectors = vectors;
            Dimension = vectors.Values.First().Length;
        }

        public int Dimension { get; }

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, CancellationToken cancellationToken = default) =>
            _vectors.TryGetValue(text, out var vector)
                ? Task.FromResult((ReadOnlyMemory<float>)vector)
                : throw new KeyNotFoundException($"No embedding configured for '{text}'.");
    }

    private static readonly IReadOnlyDictionary<string, float[]> CosineVectors =
        new Dictionary<string, float[]>
        {
            ["red apple"] = new[] { 1f, 0f, 0f, 0f },
            ["green apple"] = new[] { 0f, 1f, 0f, 0f },
            ["blue sky"] = new[] { 0f, 0f, 1f, 0f },
            ["red or green"] = new[] { 1f, 1f, 0f, 0f },
            ["pure red"] = new[] { 1f, 0f, 0f, 0f },
        };

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static PostgresVectorSearchEngine NewEngine(
        IEmbeddingProvider embeddings,
        PostgresVectorOptions? options = null,
        string table = "lexisharp_vector_tests")
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var opts = options ?? new PostgresVectorOptions() with { Dimension = embeddings.Dimension };
        return new PostgresVectorSearchEngine(
            ConnectionString!,
            embeddings,
            opts with { Table = $"{table}_{suffix}" });
    }

    private static void Run(Action<PostgresVectorSearchEngine> action)
    {
        using var engine = NewEngine(new StubEmbeddingProvider(CosineVectors));

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
    public void EnsureSchema_CreatesVectorColumnAndHnswIndex()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            var table = engine.TestTableName;
            command.CommandText = "SELECT indexname FROM pg_indexes WHERE tablename = @t";
            command.Parameters.AddWithValue("t", table);

            var indexes = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                indexes.Add(reader.GetString(0));

            Assert.Contains(indexes, name => name.Contains("_embedding_hnsw"));
            Assert.Contains(indexes, name => name.Contains("_tsv_gin"));
        });
    }

    [SkippableFact]
    public void Search_OrdersByCosineSimilarity_AndSkipsOrthogonal()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(Doc("red", "red apple"));
            engine.Add(Doc("green", "green apple"));
            engine.Add(Doc("blue", "blue sky"));

            var near = engine.Search("red or green");

            Assert.Equal(
                new[] { "green", "red" }.OrderBy(x => x),
                near.Select(r => r.DocumentId).OrderBy(x => x));
            Assert.All(near, r => Assert.Equal(Cos45, r.Score, 4));

            var exact = engine.Search("pure red");
            Assert.Single(exact);
            Assert.Equal("red", exact[0].DocumentId);
            Assert.Equal(1.0, exact[0].Score, 6);
        });
    }

    [SkippableFact]
    public void Search_L2Distance_OrdersByProximity()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        var l2 = new Dictionary<string, float[]>
        {
            ["e1"] = new[] { 1f, 0f },
            ["e2 element"] = new[] { 0f, 2f },
            ["e3 element"] = new[] { 0f, 1f },
            ["q"] = new[] { 1f, 0f },
        };

        using var engine = NewEngine(
            new StubEmbeddingProvider(l2),
            new PostgresVectorOptions { Dimension = 2, Distance = VectorDistance.L2, IvfLists = 10 });

        try
        {
            engine.Add(Doc("e1", "e1"));
            engine.Add(Doc("e2", "e2 element"));
            engine.Add(Doc("e3", "e3 element"));

            var results = engine.Search("q");

            Assert.Equal(new[] { "e1", "e3", "e2" }, results.Select(r => r.DocumentId).ToArray());
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Add_AndSearch_RoundTripFieldsAndCategory()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(new SearchDocument("a", "red apple",
                new Dictionary<string, string> { ["kind"] = "fruit" }, "food"));

            var results = engine.SearchAsync("pure red").GetAwaiter().GetResult();

            Assert.Single(results);
            Assert.Equal("food", results[0].Document.Category);
            Assert.Equal("fruit", results[0].Document.Fields!["kind"]);
        });
    }

    [SkippableFact]
    public void AddAsync_DimensionMismatch_Throws()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine(
            new StubEmbeddingProvider(CosineVectors),
            new PostgresVectorOptions { Dimension = 3 });

        try
        {
            Assert.Throws<InvalidOperationException>(() => engine.Add(Doc("a", "red apple")));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void IvfFlat_EnsureSchemaAfterRows_CreatesIndex()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine(
            new StubEmbeddingProvider(CosineVectors),
            new PostgresVectorOptions { IndexMethod = VectorIndexMethod.IvfFlat, IvfLists = 4, Dimension = 4 });

        try
        {
            engine.Add(Doc("a", "red apple"));
            engine.Add(Doc("b", "green apple"));

            // First run skipped the IVFFlat index (table was empty).
            engine.EnsureSchema();

            using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM pg_indexes WHERE tablename = @t AND indexname LIKE '%_embedding_ivfflat'";
            command.Parameters.AddWithValue("t", engine.TestTableName);

            Assert.Equal(1L, (long)command.ExecuteScalar()!);
        }
        finally
        {
            engine.DropSchema();
        }
    }
}