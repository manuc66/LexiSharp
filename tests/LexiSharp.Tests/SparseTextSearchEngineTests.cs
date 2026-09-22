using LexiSharp.Core;
using LexiSharp.Indexing;
using Xunit;

namespace LexiSharp.Tests;

public class SparseTextSearchEngineTests
{
    private sealed class StubSparseProvider : ISparseEmbeddingProvider
    {
        private readonly Func<string, IReadOnlyDictionary<string, float>> _lookup;

        public StubSparseProvider(Func<string, IReadOnlyDictionary<string, float>> lookup)
            => _lookup = lookup;

        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult(_lookup(text));
    }

    private static SearchDocument Doc(string id, string text) => new(id, text);

    private static readonly IReadOnlyDictionary<string, float> Empty =
        new Dictionary<string, float>();

    private static readonly string[] C2C1Ids = new[] { "c2", "c1" };
    private static readonly string[] ABIds = new[] { "a", "b" };

    // Learned-weigghts corpus: strong overlap on "cat", weaker on "dog", nothing on "fish".
    private static IReadOnlyDictionary<string, float> Embed(string text) => text switch
    {
        "c1" => new Dictionary<string, float> { ["cat"] = 1f, ["dog"] = 0.5f },
        "c2" => new Dictionary<string, float> { ["cat"] = 2f },
        "c3" => new Dictionary<string, float> { ["fish"] = 1.5f },
        "kept" => new Dictionary<string, float> { ["wipe"] = 1f },
        "q-cat" => new Dictionary<string, float> { ["cat"] = 1f },
        "q-dog" => new Dictionary<string, float> { ["dog"] = 1f },
        "q-fish" => new Dictionary<string, float> { ["fish"] = 1f },
        "q-absent" => new Dictionary<string, float> { ["absent"] = 1f },
        "q-null" => Empty,
        "q-wipe" => new Dictionary<string, float> { ["wipe"] = 1f },
        _ => Empty,
    };

    private static SparseTextSearchEngine EngineOfSnapshot()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(Embed));
        engine.Index(new[] { Doc("c1", "c1"), Doc("c2", "c2"), Doc("c3", "c3") });
        return engine;
    }

    [Fact]
    public void Search_ScoresBySparseDotProduct()
    {
        var engine = EngineOfSnapshot();

        // score(c1) = 1·1 = 1 (cat term), score(c2) = 1·2 = 2 (cat term).
        var results = engine.Search("q-cat");

        Assert.Equal(C2C1Ids, results.Select(r => r.DocumentId).ToArray());
        Assert.Equal(2.0, results[0].Score, 1e-9);
        Assert.Equal(1.0, results[1].Score, 1e-9);

        // "dog" only fires for c1: dot product surfaces a weaker overlap too.
        var dogResults = engine.Search("q-dog");
        Assert.Single(dogResults, r => r.DocumentId == "c1" && Math.Abs(r.Score - 0.5) < 1e-9);
    }

    [Fact]
    public void Search_NoTermOverlap_YieldsNoMatch()
    {
        var engine = EngineOfSnapshot();

        Assert.Empty(engine.Search("q-absent"));
    }

    [Fact]
    public void Search_RespectsLimit()
    {
        var engine = EngineOfSnapshot();

        Assert.Single(engine.Search("q-cat", new SearchOptions(Limit: 1)));
    }

    [Fact]
    public void Search_Offset_SkipsTopResults()
    {
        var engine = EngineOfSnapshot();

        var all = engine.Search("q-cat", new SearchOptions(Limit: 10));
        Assert.Equal(C2C1Ids, all.Select(r => r.DocumentId).ToArray());

        var page = engine.Search("q-cat", new SearchOptions(Limit: 1, Offset: 1));
        Assert.Single(page, r => r.DocumentId == "c1");

        Assert.Empty(engine.Search("q-cat", new SearchOptions(Limit: 1, Offset: 5)));
        Assert.Empty(engine.Search("q-cat", new SearchOptions(Limit: 1, Offset: -1)));
    }

    [Fact]
    public void Search_RespectsMinimumScore()
    {
        var engine = EngineOfSnapshot();

        var results = engine.Search("q-cat", new SearchOptions(Limit: 10, MinimumScore: 2.0));

        Assert.Single(results, r => r.DocumentId == "c2");
    }

    [Fact]
    public void Search_RespectsFilters()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(Embed));
        engine.Index(new[]
        {
            Doc("c1", "c1") with { Fields = new Dictionary<string, string> { ["topic"] = "animals" } },
            Doc("c2", "c2") with { Fields = new Dictionary<string, string> { ["topic"] = "animals" } },
            Doc("c3", "c3"),
        });

        var filter = new MetadataFilter("topic", MetadataFilterOperator.Equal, "animals");
        var results = engine.Search("q-cat", new SearchOptions(Limit: 10, Filters: new[] { filter }));

        Assert.Equal(C2C1Ids, results.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void Search_EmptyQuery_YieldsNothing()
    {
        var engine = EngineOfSnapshot();

        Assert.Empty(engine.Search(""));
        Assert.Empty(engine.Search("   "));
        Assert.Empty(engine.Search("q-null"));
    }

    [Fact]
    public void Search_DeterministicTieBreak()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(t => t switch
        {
            "a" => new Dictionary<string, float> { ["cat"] = 2f },
            "b" => new Dictionary<string, float> { ["cat"] = 1f, ["dog"] = 1f },
            "q" => new Dictionary<string, float> { ["cat"] = 1f, ["dog"] = 1f },
            _ => Empty,
        }));
        engine.Add(Doc("b", "b"));
        engine.Add(Doc("a", "a"));

        // Both docs reach 2 = (1·2) vs (1·1 + 1·1): ties resolve by document id.
        var results = engine.Search("q");

        Assert.Equal(ABIds, results.Select(r => r.DocumentId).ToArray());
        Assert.All(results, r => Assert.Equal(2.0, r.Score, 1e-9));
    }

    [Fact]
    public void Add_ReplacesExistingDocument()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(t => t switch
        {
            "kept" => new Dictionary<string, float> { ["wipe"] = 1f },
            "doc" => new Dictionary<string, float> { ["cat"] = 1f },
            "q-wipe" => new Dictionary<string, float> { ["wipe"] = 1f },
            _ => Empty,
        }));

        engine.Add(Doc("id", "doc"));
        Assert.Equal("doc", engine.Documents.Single().Text);

        engine.Add(Doc("id", "kept"));
        Assert.Single(engine.Documents);
        Assert.Single(engine.Search("q-wipe", new SearchOptions(Limit: 10)), r => r.DocumentId == "id");
    }

    [Fact]
    public void Remove_DeletesDocumentAndPrunesEmptyPostings()
    {
        var engine = EngineOfSnapshot();
        Assert.Equal(3, engine.Count);

        engine.Remove("c1");

        Assert.Equal(2, engine.Count);
        Assert.DoesNotContain("c1", engine.Documents.Select(d => d.Id));
        Assert.Equal(2, engine.VocabularySize); // dog posting only came from c1 → term dropped

        // After c1 is gone, only c2 matches "cat".
        var results = engine.Search("q-cat");
        Assert.Single(results, r => r.DocumentId == "c2");
    }

    [Fact]
    public void Clear_RemovesEverything()
    {
        var engine = EngineOfSnapshot();

        engine.Clear();

        Assert.Empty(engine.Documents);
        Assert.Equal(0, engine.VocabularySize);
        Assert.Empty(engine.Search("q-cat"));
    }

    [Fact]
    public void NonPositiveAndNonFiniteWeights_AreTreatedAsAbsent()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(t => t switch
        {
            "doc" => new Dictionary<string, float>
            {
                ["good"] = 1f,
                ["zero"] = 0f,
                ["negative"] = -2f,
                ["nan"] = float.NaN,
                ["inf"] = float.PositiveInfinity,
            },
            "q" => new Dictionary<string, float> { ["good"] = 1f, ["zero"] = 0f },
            _ => Empty,
        }));

        engine.Add(Doc("id", "doc"));

        Assert.Single(engine.Search("q"));
        Assert.Equal(1, engine.VocabularySize); // only "good" survived
    }

    [Fact]
    public async Task SearchAsync_MatchesSyncSearch()
    {
        var engine = EngineOfSnapshot();

        var sync = engine.Search("q-cat");
        var async = await engine.SearchAsync("q-cat");

        Assert.Equal(sync.Select(r => r.DocumentId), async.Select(r => r.DocumentId));
        Assert.Equal(sync.Select(r => r.Score), async.Select(r => r.Score));
    }

    [Fact]
    public async Task AddAsync_MatchesSyncAdd()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(Embed));

        await engine.AddAsync(Doc("c1", "c1"));

        Assert.Single(engine.Documents);
    }

    [Fact]
    public void Validation_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new SparseTextSearchEngine(null!));

        var engine = new SparseTextSearchEngine(new StubSparseProvider(Embed));
        Assert.Throws<ArgumentNullException>(() => engine.Add(null!));
        Assert.Throws<ArgumentNullException>(() => engine.Search(null!));
        Assert.Throws<ArgumentNullException>(() => engine.Index(null!));
        Assert.Throws<ArgumentNullException>(() => engine.Remove(null!));
    }

    [Fact]
    public void EmbeddingUse_IsReportedToTheProvider()
    {
        var provider = new RecordingSparseProvider(Embed);
        var engine = new SparseTextSearchEngine(provider);
        engine.Add(Doc("c1", "c1"));
        engine.Search("q-cat");

        Assert.Equal(new[] { EmbeddingUse.Passage, EmbeddingUse.Query }, provider.Uses.ToArray());
    }

    private sealed class RecordingSparseProvider : ISparseEmbeddingProvider
    {
        private readonly StubSparseProvider _inner;

        public RecordingSparseProvider(Func<string, IReadOnlyDictionary<string, float>> lookup)
            => _inner = new StubSparseProvider(lookup);

        public List<EmbeddingUse> Uses { get; } = new();

        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
        {
            Uses.Add(use);
            return _inner.GetSparseEmbeddingAsync(text, use, cancellationToken);
        }
    }
}