using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
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
    public void Search_InsertionOrder_BreaksTiesByTheOrderDocumentsWereAdded()
    {
        // The first-added document is reached by the *second* query term, so neither the id order
        // nor the order the accumulator happens to touch the candidates can produce this page.
        var engine = new SparseTextSearchEngine(new StubSparseProvider(t => t switch
        {
            "first" => new Dictionary<string, float> { ["dog"] = 1f },
            "second" => new Dictionary<string, float> { ["cat"] = 1f },
            "q" => new Dictionary<string, float> { ["cat"] = 1f, ["dog"] = 1f },
            _ => Empty,
        }));

        // Ids sort the other way round, so the two tie-breaks cannot both produce this page.
        engine.Add(Doc("z-first", "first"));
        engine.Add(Doc("a-second", "second"));

        var results = engine.Search("q", new SearchOptions(Limit: 10, TieBreak: TieBreak.InsertionOrder));

        Assert.Equal(new[] { "z-first", "a-second" }, results.Select(r => r.DocumentId).ToArray());
        Assert.All(results, r => Assert.Equal(1.0, r.Score, 1e-9));
    }

    [Fact]
    public void Search_InsertionOrder_DoesNotDependOnTheOrderTheQueryTermsAreRead()
    {
        // Each document reaches the tie through a different term. A tie-break that recorded the
        // order the accumulator first touched the candidates would follow the query's term order
        // and hand the two spellings of the same query two different pages.
        var engine = new SparseTextSearchEngine(new StubSparseProvider(t => t switch
        {
            "first" => new Dictionary<string, float> { ["t2"] = 1f },
            "second" => new Dictionary<string, float> { ["t1"] = 1f },
            "q-12" => new Dictionary<string, float> { ["t1"] = 1f, ["t2"] = 1f },
            "q-21" => new Dictionary<string, float> { ["t2"] = 1f, ["t1"] = 1f },
            _ => Empty,
        }));

        engine.Add(Doc("z-first", "first"));
        engine.Add(Doc("a-second", "second"));

        var forwards = engine.Search("q-12", new SearchOptions(Limit: 10, TieBreak: TieBreak.InsertionOrder));
        var backwards = engine.Search("q-21", new SearchOptions(Limit: 10, TieBreak: TieBreak.InsertionOrder));

        // "z-first" was added first despite its id sorting last.
        Assert.Equal(new[] { "z-first", "a-second" }, forwards.Select(r => r.DocumentId).ToArray());
        Assert.Equal(
            forwards.Select(r => r.DocumentId),
            backwards.Select(r => r.DocumentId));
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
    public void AddAfterRemove_ReusesTheFreedOrdinalWithoutResurrectingItsPostings()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(t => t switch
        {
            "removed" => new Dictionary<string, float> { ["gone"] = 1f },
            "kept" => new Dictionary<string, float> { ["kept"] = 1f },
            "added" => new Dictionary<string, float> { ["fresh"] = 1f },
            "q-gone" => new Dictionary<string, float> { ["gone"] = 1f },
            "q-kept" => new Dictionary<string, float> { ["kept"] = 1f },
            "q-fresh" => new Dictionary<string, float> { ["fresh"] = 1f },
            _ => Empty,
        }));

        engine.Add(Doc("r", "removed")); // ordinal 0
        engine.Add(Doc("k", "kept"));    // ordinal 1
        engine.Remove("r");
        engine.Add(Doc("n", "added"));   // takes the freed ordinal 0

        // The reused slot carries the new document, not the one it replaced: the term the removed
        // document posted reaches nothing, and each surviving term reaches exactly its document.
        Assert.Empty(engine.Search("q-gone"));
        Assert.Equal(new[] { "n" }, engine.Search("q-fresh").Select(r => r.DocumentId).ToArray());
        Assert.Equal(new[] { "k" }, engine.Search("q-kept").Select(r => r.DocumentId).ToArray());
        Assert.Equal(2, engine.Count);
    }

    /// <summary>
    /// Removing most of a posting list compacts it, and a document added afterwards still reaches
    /// the term: the tombstoned entries are gone, not merely hidden from scoring.
    /// </summary>
    [Fact]
    public void Remove_CompactsAHeavilyTombstonedListAndKeepsAppendsWorking()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(text => text switch
        {
            "shared" => new Dictionary<string, float> { ["shared"] = 1f },
            "q" => new Dictionary<string, float> { ["shared"] = 1f },
            _ => Empty,
        }));

        foreach (string id in new[] { "d1", "d2", "d3", "d4" })
            engine.Add(Doc(id, "shared"));

        engine.Remove("d1");
        engine.Remove("d2");
        engine.Remove("d3"); // live count is one quarter of the array: compaction

        engine.Add(Doc("n", "shared")); // reuses a freed ordinal, appends after the compacted array

        Assert.Equal(new[] { "d4", "n" }, engine.Search("q").Select(r => r.DocumentId).Order().ToArray());

        engine.Remove("n");

        Assert.Equal(new[] { "d4" }, engine.Search("q").Select(r => r.DocumentId).ToArray());
        Assert.Equal(1, engine.VocabularySize);
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

    /// <summary>
    /// Import counts the postings in a first pass to size each list once, and accepts a lazy
    /// source by materializing it rather than requiring a list.
    /// </summary>
    [Fact]
    public void Import_AcceptsALazySequenceAndStillScores()
    {
        var engine = new SparseTextSearchEngine(new StubSparseProvider(Embed));

        engine.Import(new[]
        {
            new SparseIndexEntry(Doc("c1", "c1"), new Dictionary<string, float> { ["cat"] = 1f }),
            new SparseIndexEntry(Doc("c2", "c2"), new Dictionary<string, float> { ["cat"] = 2f }),
        }.Select(entry => entry));

        var results = engine.Search("q-cat");

        Assert.Equal(["c2", "c1"], results.Select(result => result.DocumentId));
        Assert.Equal(1, engine.VocabularySize);
    }

    /// <summary>
    /// Import takes a copy of the vector, so a caller that keeps and mutates its dictionary after
    /// the call cannot change what the engine scores or exports.
    /// </summary>
    [Fact]
    public void Import_CopiesTheVectorSoLaterMutationDoesNotReachTheEngine()
    {
        var weights = new Dictionary<string, float> { ["apple"] = 1f };
        var engine = new SparseTextSearchEngine(new StubSparseProvider(_ => Empty));

        engine.Import([new SparseIndexEntry(Doc("1", "doc"), weights)]);

        weights["apple"] = 9f;
        weights["pear"] = 1f;

        IReadOnlyDictionary<string, float> exported = Assert.Single(engine.Export()).Weights;

        Assert.Equal(1f, exported["apple"]);
        Assert.False(exported.ContainsKey("pear"));
        Assert.Equal(1, engine.VocabularySize);
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