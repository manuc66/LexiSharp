using LexiSharp.Core;
using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

public class PostgresSparseSearchEngineTests
{
    private sealed class StubSparseProvider : ISparseEmbeddingProvider
    {
        private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> _vectors;

        public StubSparseProvider(IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> vectors)
        {
            _vectors = vectors;
        }

        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult(_vectors.TryGetValue(text, out var vector)
                ? vector
                : throw new KeyNotFoundException($"No sparse embedding configured for '{text}'."));
    }

    private static readonly IReadOnlyDictionary<string, int> Vocabulary =
        new Dictionary<string, int>
        {
            ["apple"] = 0, ["red"] = 1, ["green"] = 2, ["banana"] = 3, ["bread"] = 4, ["blue"] = 5,
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, float>> Docs =
        new Dictionary<string, IReadOnlyDictionary<string, float>>
        {
            ["red apple"] = new Dictionary<string, float> { ["apple"] = 2f, ["red"] = 1f },
            ["green apple"] = new Dictionary<string, float> { ["apple"] = 2f, ["green"] = 1f },
            ["blue sky"] = new Dictionary<string, float> { ["blue"] = 2f }, // "sky" outside the vocabulary
        };

    private static readonly string[] RedGreenIds = new[] { "red", "green" };
    private static readonly string[] E2E1E3Ids = new[] { "e2", "e1", "e3" };

    private static string? ConnectionString =>
        Environment.GetEnvironmentVariable("POSTGRES_TEST_CONNECTION");

    private static bool VectorExtensionAvailable()
    {
        using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_extension WHERE extname = 'vector'";
        return command.ExecuteScalar() is not null;
    }

    private static PostgresSparseSearchEngine NewEngine(
        ISparseEmbeddingProvider? provider = null,
        PostgresSparseOptions? options = null)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var opts = options ?? new PostgresSparseOptions();
        return new PostgresSparseSearchEngine(
            ConnectionString!,
            provider ?? new StubSparseProvider(Docs),
            opts with { Vocabulary = opts.Vocabulary.Count == 0 ? Vocabulary : opts.Vocabulary, Table = $"lexisharp_sparse_tests_{suffix}" });
    }

    private static void Run(Action<PostgresSparseSearchEngine> action)
    {
        using var engine = NewEngine();

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
    public void EnsureSchema_CreatesSparseColumnAndHnswIndex()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");
        Skip.If(!VectorExtensionAvailable(), "vector extension unavailable.");

        Run(engine =>
        {
            using var connection = new Npgsql.NpgsqlConnection(ConnectionString);
            connection.Open();

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT indexname FROM pg_indexes WHERE tablename = @t";
            command.Parameters.AddWithValue("t", engine.TestTableName);

            var indexes = new List<string>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                indexes.Add(reader.GetString(0));

            Assert.Contains(indexes, name => name.Contains("_sparse_hnsw"));
            Assert.Contains(indexes, name => name.Contains("_tsv_gin"));
        });
    }

    [SkippableFact]
    public void Search_InnerProduct_RanksByDotProductAndSkipsOrthogonal()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");
        Skip.If(!VectorExtensionAvailable(), "vector extension unavailable.");

        Run(engine =>
        {
            engine.Add(Doc("red", "red apple"));
            engine.Add(Doc("green", "green apple"));
            engine.Add(Doc("blue", "blue sky"));

            var results = engine.Search("red apple");

            // scores via <#>: red = 1·1 + 2·2 = 5, green = 2·2 = 4, blue = 0 (dropped).
            Assert.Equal(RedGreenIds, results.Select(r => r.DocumentId).ToArray());
            Assert.Equal(5.0, results[0].Score, 4);
            Assert.Equal(4.0, results[1].Score, 4);
        });
    }

    [SkippableFact]
    public void Search_L2Distance_OrdersByProximity()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");
        Skip.If(!VectorExtensionAvailable(), "vector extension unavailable.");

        var l2Docs = new Dictionary<string, IReadOnlyDictionary<string, float>>
        {
            ["e1"] = new Dictionary<string, float> { ["apple"] = 3f },
            ["e2"] = new Dictionary<string, float> { ["apple"] = 2f },
            ["e3"] = new Dictionary<string, float> { ["apple"] = 5f },
            // The provider is keyed by query text too: embed the query "apple".
            ["apple"] = new Dictionary<string, float> { ["apple"] = 2f },
        };

        using var engine = NewEngine(
            new StubSparseProvider(l2Docs),
            new PostgresSparseOptions { Distance = SparseDistance.L2 });

        try
        {
            engine.Add(Doc("e1", "e1"));
            engine.Add(Doc("e2", "e2"));
            engine.Add(Doc("e3", "e3"));

            var results = engine.Search("apple");

            // pgvector <-> is the euclidean distance: vs {apple:2} the distances are
            // e2 = 0, e1 = 1, e3 = 3 → similarities 1, 1/2, 1/4.
            Assert.Equal(E2E1E3Ids, results.Select(r => r.DocumentId).ToArray());
            Assert.Equal(1.0, results[0].Score, 4);
            Assert.Equal(0.5, results[1].Score, 4);
            Assert.Equal(0.25, results[2].Score, 4);
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
        Skip.If(!VectorExtensionAvailable(), "vector extension unavailable.");

        Run(engine =>
        {
            engine.Add(new SearchDocument("a", "red apple",
                new Dictionary<string, string> { ["kind"] = "fruit" }, "food"));

            var results = engine.SearchAsync("red apple").GetAwaiter().GetResult();

            Assert.Single(results);
            Assert.Equal("food", results[0].Document.Category);
            Assert.Equal("fruit", results[0].Document.Fields!["kind"]);
        });
    }

    [SkippableFact]
    public void Search_QueryTermsAllOutsideVocabulary_ReturnsEmpty()
    {
        Skip.If(ConnectionString is null, "POSTGRES_TEST_CONNECTION not set.");
        Skip.If(!VectorExtensionAvailable(), "vector extension unavailable.");

        var unknownOnly = new StubSparseProvider(
            new Dictionary<string, IReadOnlyDictionary<string, float>>
            {
                ["red apple"] = new Dictionary<string, float>(),
                ["unknown term wildcard"] = new Dictionary<string, float>
                {
                    ["unknown"] = 1f, ["term"] = 2f, ["wildcard"] = 3f,
                },
            });

        using var engine = NewEngine(unknownOnly);
        try
        {
            engine.Add(Doc("red", "red apple"));

            var results = engine.Search("unknown term wildcard");

            Assert.Empty(results);
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
        Skip.If(!VectorExtensionAvailable(), "vector extension unavailable.");

        // L2 similarities vs {apple:2}: e2 = 1, e1 = 1/2, e3 = 1/4 — strictly decreasing.
        var l2Docs = new Dictionary<string, IReadOnlyDictionary<string, float>>
        {
            ["e1"] = new Dictionary<string, float> { ["apple"] = 3f },
            ["e2"] = new Dictionary<string, float> { ["apple"] = 2f },
            ["e3"] = new Dictionary<string, float> { ["apple"] = 5f },
            ["apple"] = new Dictionary<string, float> { ["apple"] = 2f },
        };

        using var engine = NewEngine(
            new StubSparseProvider(l2Docs),
            new PostgresSparseOptions { Distance = SparseDistance.L2 });

        try
        {
            engine.Add(Doc("e1", "e1"));
            engine.Add(Doc("e2", "e2"));
            engine.Add(Doc("e3", "e3"));

            var all = engine.Search("apple", new SearchOptions(Limit: 10));
            Assert.Equal(E2E1E3Ids, all.Select(r => r.DocumentId).ToArray());

            var page1 = engine.Search("apple", new SearchOptions(Limit: 2, Offset: 0));
            var page2 = engine.Search("apple", new SearchOptions(Limit: 2, Offset: 2));

            Assert.Equal(new[] { "e2", "e1" }, page1.Select(r => r.DocumentId).ToArray());
            Assert.Equal(new[] { "e3" }, page2.Select(r => r.DocumentId).ToArray());

            Assert.Empty(engine.Search("apple", new SearchOptions(Limit: 2, Offset: 3)));
            Assert.Empty(engine.Search("apple", new SearchOptions(Limit: 2, Offset: -1)));
        }
        finally
        {
            engine.DropSchema();
        }
    }

    [SkippableFact]
    public void Constructor_EmptyVocabulary_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new PostgresSparseSearchEngine(
                ConnectionString ?? "Host=localhost;Database=none",
                new StubSparseProvider(Docs),
                new PostgresSparseOptions { Vocabulary = new Dictionary<string, int>() }));
    }
}