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

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default) =>
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

    [SkippableFact]
    public void Add_AndSearch_RoundTripsTextFields()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(new SearchDocument("a", "red apple",
                new Dictionary<string, string> { ["kind"] = "fruit" }, "food",
                new Dictionary<string, string> { ["summary"] = "fresh and crunchy" }));

            var results = engine.SearchAsync("pure red").GetAwaiter().GetResult();

            var result = Assert.Single(results);
            Assert.Equal("food", result.Document.Category);
            Assert.Equal("fruit", result.Document.Fields!["kind"]);
            Assert.Equal("fresh and crunchy", result.Document.TextFields!["summary"]);
        });
    }

    [SkippableFact]
    public void EmbeddingTextField_SelectsWhichTextIsEmbedded()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        var embeddings = new StubEmbeddingProvider(new Dictionary<string, float[]>
        {
            ["red apple"] = new[] { 1f, 0f },
            ["blue sky"] = new[] { 0f, 1f },
        });

        using var engine = NewEngine(
            embeddings,
            new PostgresVectorOptions { Dimension = 2, EmbeddingTextField = "title" });

        try
        {
            engine.Add(new SearchDocument("a", "ignored body text",
                TextFields: new Dictionary<string, string> { ["title"] = "red apple" }));

            // Orthogonal: the body text was NOT embedded, the title was.
            Assert.Empty(engine.Search("blue sky"));

            var hit = engine.Search("red apple");
            var result = Assert.Single(hit);
            Assert.Equal("a", result.DocumentId);
            Assert.Equal("ignored body text", result.Document.Text);
            Assert.Equal("red apple", result.Document.TextFields!["title"]);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void ListDocumentIds_ReturnsEveryStoredIdInStableOrder()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(Doc("bravo", "red apple"));
            engine.Add(Doc("alpha", "green apple"));
            engine.Add(Doc("charlie", "blue sky"));

            Assert.Equal(
                new[] { "alpha", "bravo", "charlie" },
                engine.ListDocumentIds().ToArray());
            Assert.Equal(
                new[] { "alpha", "bravo", "charlie" },
                engine.ListDocumentIds(batchSize: 1).ToArray());

            Assert.Throws<ArgumentOutOfRangeException>(() => engine.ListDocumentIds(0));
        });
    }

    [SkippableFact]
    public void Search_WithHnswEfSearch_RunsInsideALocalTransaction()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine(
            new StubEmbeddingProvider(CosineVectors),
            new PostgresVectorOptions { Dimension = 4, HnswEfSearch = 64 });

        try
        {
            engine.Add(Doc("red", "red apple"));
            engine.Add(Doc("green", "green apple"));

            var exact = engine.Search("pure red");

            var result = Assert.Single(exact);
            Assert.Equal("red", result.DocumentId);
            Assert.Equal(1.0, result.Score, 6);
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void EmbeddingUse_IsReportedToTheProvider()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        var provider = new RecordingEmbeddingProvider(CosineVectors);
        using var engine = NewEngine(provider);

        try
        {
            engine.Add(Doc("a", "red apple"));
            engine.Search("pure red");

            Assert.Equal(new[] { EmbeddingUse.Passage, EmbeddingUse.Query }, provider.Uses.ToArray());
        }
        finally
        {
            engine.DropSchema();
        }
    }

    private static readonly IReadOnlyDictionary<string, float[]> ColumnVectors =
        new Dictionary<string, float[]>
        {
            ["alpha one"] = new[] { 1f, 0f },
            ["bravo two"] = new[] { 0f, 1f },
        };

    private static PostgresVectorOptions ColumnOptions() => new()
    {
        Dimension = 2,
        EmbeddingColumns = new Dictionary<string, string>
        {
            ["title"] = "title",
            ["description"] = "description",
        },
    };

    [SkippableFact]
    public void EmbeddingColumns_EnsureSchema_CreatesOneIndexPerColumn()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine(new StubEmbeddingProvider(ColumnVectors), ColumnOptions());

        try
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

            Assert.Contains(indexes, name => name.Contains("_title_embedding_hnsw"));
            Assert.Contains(indexes, name => name.Contains("_description_embedding_hnsw"));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void EmbeddingColumns_SearchByColumn_SelectsTheRightEmbedding()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine(new StubEmbeddingProvider(ColumnVectors), ColumnOptions());

        try
        {
            engine.Add(new SearchDocument("d1", "unrelated body",
                TextFields: new Dictionary<string, string> { ["title"] = "alpha one", ["description"] = "bravo two" }));
            engine.Add(new SearchDocument("d2", "unrelated body",
                TextFields: new Dictionary<string, string> { ["title"] = "bravo two", ["description"] = "alpha one" }));

            // "alpha one" lives in d1's title and d2's description.
            var byTitle = engine.SearchWithColumns("alpha one", new[] { "title" });
            Assert.Equal(new[] { "d1" }, byTitle.Select(r => r.DocumentId).ToArray());

            var byDescription = engine.SearchWithColumns("alpha one", new[] { "description" });
            Assert.Equal(new[] { "d2" }, byDescription.Select(r => r.DocumentId).ToArray());

            // Searching without column selection OR-fuses every configured column by best similarity.
            var all = engine.Search("alpha one");
            Assert.Equal(
                new[] { "d1", "d2" }.OrderBy(x => x),
                all.Select(r => r.DocumentId).OrderBy(x => x));
            Assert.All(all, r => Assert.Equal(1.0, r.Score, 6));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void EmbeddingColumns_SearchWithDetails_ExposesPerColumnContributions()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine(new StubEmbeddingProvider(ColumnVectors), ColumnOptions());

        try
        {
            engine.Add(new SearchDocument("d1", "unrelated body",
                TextFields: new Dictionary<string, string> { ["title"] = "alpha one", ["description"] = "bravo two" }));
            engine.Add(new SearchDocument("d2", "unrelated body",
                TextFields: new Dictionary<string, string> { ["title"] = "bravo two", ["description"] = "alpha one" }));

            var details = engine.SearchWithDetails("alpha one");

            var d1 = Assert.Single(details, d => d.DocumentId == "d1");
            var titleOnly = Assert.Single(d1.Contributions.Keys);
            Assert.Equal("title", titleOnly); // orthogonal description similarity (0) is dropped
            Assert.Equal(1.0, d1.Contributions["title"], 6);

            var d2 = Assert.Single(details, d => d.DocumentId == "d2");
            var descriptionOnly = Assert.Single(d2.Contributions.Keys);
            Assert.Equal("description", descriptionOnly);
            Assert.Equal(1.0, d2.Contributions["description"], 6);

            // Details agree with the plain search on ordering.
            Assert.Equal(
                engine.Search("alpha one").Select(r => r.DocumentId).OrderBy(x => x),
                details.Select(r => r.DocumentId).OrderBy(x => x));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void SearchWithColumns_RejectsUnknownColumn()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        using var engine = NewEngine(new StubEmbeddingProvider(ColumnVectors), ColumnOptions());

        try
        {
            Assert.Throws<ArgumentException>(() => engine.SearchWithColumns("alpha one", new[] { "nope" }));
            Assert.Throws<ArgumentException>(() => engine.SearchWithColumns("alpha one", Array.Empty<string>()));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [Fact]
    public void EmbeddingColumns_And_EmbeddingTextField_AreMutuallyExclusive()
    {
        var provider = new StubEmbeddingProvider(CosineVectors);

        Assert.Throws<ArgumentException>(() => new PostgresVectorSearchEngine(
            "Host=localhost;Username=test;Password=test;Database=test",
            provider,
            new PostgresVectorOptions
            {
                Dimension = provider.Dimension,
                EmbeddingTextField = "title",
                EmbeddingColumns = new Dictionary<string, string> { ["title"] = "title" },
                AutoCreateSchema = false,
            }));
    }

    [Fact]
    public void EmbeddingColumns_RejectsUntrustedColumnNames()
    {
        var provider = new StubEmbeddingProvider(CosineVectors);

        Assert.Throws<ArgumentException>(() => new PostgresVectorSearchEngine(
            "Host=localhost;Username=test;Password=test;Database=test",
            provider,
            new PostgresVectorOptions
            {
                Dimension = provider.Dimension,
                EmbeddingColumns = new Dictionary<string, string> { ["bad;name"] = "title" },
                AutoCreateSchema = false,
            }));
    }

    private sealed class RecordingEmbeddingProvider : IEmbeddingProvider
    {
        private readonly StubEmbeddingProvider _inner;

        public RecordingEmbeddingProvider(IReadOnlyDictionary<string, float[]> vectors)
        {
            _inner = new StubEmbeddingProvider(vectors);
            Dimension = _inner.Dimension;
        }

        public List<EmbeddingUse> Uses { get; } = new();

        public int Dimension { get; }

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default)
        {
            Uses.Add(use);
            return _inner.GetTextEmbeddingAsync(text, use, cancellationToken);
        }
    }
}