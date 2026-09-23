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

    // Column-aware: the query is encoded differently per column, so the resulting ranking is only
    // correct if the engine passes the column on every call. The column-blind overload is a bug
    // here on purpose, to fail loudly if the engine ever falls back to it.
    private sealed class ColumnAwareStubEmbeddingProvider : IColumnAwareEmbeddingProvider
    {
        private readonly bool _titleUsesFirstAxis;
        private readonly IReadOnlyDictionary<string, float[]> _passages;

        public ColumnAwareStubEmbeddingProvider(bool titleUsesFirstAxis, IReadOnlyDictionary<string, float[]> passages)
        {
            _titleUsesFirstAxis = titleUsesFirstAxis;
            _passages = passages;
        }

        public int Dimension => 2;

        public List<(string Text, EmbeddingUse Use, string Column)> Calls { get; } = new();

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The engine must pass the embedding column.");

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(string text, EmbeddingUse use, string column, CancellationToken cancellationToken = default)
        {
            Calls.Add((text, use, column));

            bool firstAxis = (column == "title") == _titleUsesFirstAxis;
            float[] vector = use == EmbeddingUse.Query
                ? (firstAxis ? new[] { 1f, 0f } : new[] { 0f, 1f })
                : _passages[text];

            return Task.FromResult((ReadOnlyMemory<float>)vector);
        }
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

    private static readonly string[] GreenRedIds = new[] { "green", "red" };
    private static readonly string[] L2ResultIds = new[] { "e1", "e3", "e2" };
    private static readonly float[] RedAppleVector = new[] { 1f, 0f };
    private static readonly float[] BlueSkyVector = new[] { 0f, 1f };
    private static readonly string[] AlphaBravoCharlieIds = new[] { "alpha", "bravo", "charlie" };
    private static readonly string[] TitleColumn = new[] { "title" };
    private static readonly string[] D1OnlyIds = new[] { "d1" };
    private static readonly string[] DescriptionColumn = new[] { "description" };
    private static readonly string[] D2OnlyIds = new[] { "d2" };
    private static readonly string[] D1D2Ids = new[] { "d1", "d2" };
    private static readonly string[] NopeColumn = new[] { "nope" };

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
                GreenRedIds.OrderBy(x => x),
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

            Assert.Equal(L2ResultIds, results.Select(r => r.DocumentId).ToArray());
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
            ["red apple"] = RedAppleVector,
            ["blue sky"] = BlueSkyVector,
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
    public void Search_Offset_PaginatesTheRanking()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        // All five vectors point in different directions from the query, so the cosine scores
        // (and the ranking) are strictly decreasing: pages are stable across requests.
        var vectors = new Dictionary<string, float[]>
        {
            ["q"] = new[] { 1f, 0f },
            ["a"] = new[] { 1f, 0f },
            ["b"] = new[] { 1f, 1f },
            ["c"] = new[] { 1f, 2f },
            ["d"] = new[] { 1f, 3f },
            ["e"] = new[] { 1f, 4f },
        };

        using var engine = NewEngine(
            new StubEmbeddingProvider(vectors),
            new PostgresVectorOptions { Dimension = 2 });

        try
        {
            foreach (string id in new[] { "a", "b", "c", "d", "e" })
                engine.Add(Doc(id, id));

            var all = engine.Search("q", new SearchOptions(Limit: 10));
            Assert.Equal(5, all.Count);
            Assert.Equal(new[] { "a", "b", "c", "d", "e" }, all.Select(r => r.DocumentId).ToArray());

            var page1 = engine.Search("q", new SearchOptions(Limit: 2, Offset: 0));
            var page2 = engine.Search("q", new SearchOptions(Limit: 2, Offset: 2));
            var page3 = engine.Search("q", new SearchOptions(Limit: 2, Offset: 4));

            Assert.Equal(new[] { "a", "b" }, page1.Select(r => r.DocumentId).ToArray());
            Assert.Equal(new[] { "c", "d" }, page2.Select(r => r.DocumentId).ToArray());
            Assert.Equal(new[] { "e" }, page3.Select(r => r.DocumentId).ToArray());

            Assert.Empty(engine.Search("q", new SearchOptions(Limit: 2, Offset: 5)));
            Assert.Empty(engine.Search("q", new SearchOptions(Limit: 2, Offset: -1)));
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
                AlphaBravoCharlieIds,
                engine.ListDocumentIds().ToArray());
            Assert.Equal(
                AlphaBravoCharlieIds,
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
            var byTitle = engine.SearchWithColumns("alpha one", TitleColumn);
            Assert.Equal(D1OnlyIds, byTitle.Select(r => r.DocumentId).ToArray());

            var byDescription = engine.SearchWithColumns("alpha one", DescriptionColumn);
            Assert.Equal(D2OnlyIds, byDescription.Select(r => r.DocumentId).ToArray());

            // Searching without column selection OR-fuses every configured column by best similarity.
            var all = engine.Search("alpha one");
            Assert.Equal(
                D1D2Ids.OrderBy(x => x),
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
            Assert.Throws<ArgumentException>(() => engine.SearchWithColumns("alpha one", NopeColumn));
            Assert.Throws<ArgumentException>(() => engine.SearchWithColumns("alpha one", Array.Empty<string>()));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    private static readonly IReadOnlyDictionary<string, float[]> ColumnAwarePassages =
        new Dictionary<string, float[]>
        {
            ["alpha one"] = new[] { 1f, 0f },
            ["bravo two"] = new[] { 0f, 1f },
        };

    // d1: title "alpha one", description "bravo two"; d2 is the mirror.
    private static IEnumerable<SearchDocument> ColumnAwareDocuments() => new[]
    {
        new SearchDocument("d1", "unrelated body",
            TextFields: new Dictionary<string, string> { ["title"] = "alpha one", ["description"] = "bravo two" }),
        new SearchDocument("d2", "unrelated body",
            TextFields: new Dictionary<string, string> { ["title"] = "bravo two", ["description"] = "alpha one" }),
    };

    [SkippableFact]
    public void EmbeddingColumns_ColumnAwareProvider_EncodesQueryPerColumn()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        // Query "alpha one" encoded as (1,0) on the title channel and (0,1) on the description
        // channel: d1 matches on both. The provider records the column it was called with.
        var titleFirst = new ColumnAwareStubEmbeddingProvider(titleUsesFirstAxis: true, ColumnAwarePassages);

        using (var engine = NewEngine(titleFirst, ColumnOptions()))
        {
            try
            {
                engine.Index(ColumnAwareDocuments());

                Assert.Equal(D1OnlyIds, engine.Search("alpha one").Select(r => r.DocumentId).ToArray());

                // The column is passed on both sides: query (once per column) and passage.
                Assert.Contains(titleFirst.Calls, c => c == ("alpha one", EmbeddingUse.Query, "title"));
                Assert.Contains(titleFirst.Calls, c => c == ("alpha one", EmbeddingUse.Query, "description"));
                Assert.Contains(titleFirst.Calls, c => c == ("alpha one", EmbeddingUse.Passage, "title"));
            }
            finally
            {
                engine.DropSchema();
            }
        }

        // Same corpus and query, only the per-column query encoding flips: d2 wins instead of d1.
        var descriptionFirst = new ColumnAwareStubEmbeddingProvider(titleUsesFirstAxis: false, ColumnAwarePassages);

        using (var engine = NewEngine(descriptionFirst, ColumnOptions()))
        {
            try
            {
                engine.Index(ColumnAwareDocuments());

                Assert.Equal(D2OnlyIds, engine.Search("alpha one").Select(r => r.DocumentId).ToArray());
            }
            finally
            {
                engine.DropSchema();
            }
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

    [SkippableFact]
    public void Search_FiltersGateResults()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");

        Run(engine =>
        {
            engine.Add(new SearchDocument("red", "red apple",
                new Dictionary<string, string> { ["kind"] = "fruit" }));
            engine.Add(new SearchDocument("green", "green apple",
                new Dictionary<string, string> { ["kind"] = "veg" }));

            var filtered = engine.Search("red or green", new SearchOptions(Limit: 10,
                Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "fruit") }));

            Assert.Equal(new[] { "red" }, filtered.Select(r => r.DocumentId).ToArray());

            Assert.Empty(engine.Search("red or green", new SearchOptions(Limit: 10,
                Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "nope") })));
        });
    }
}