using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class InMemoryVectorSearchEngineTests
{
    private static readonly SearchDocument Auth = new("auth", "oauth access token refresh session");
    private static readonly SearchDocument Search = new("search", "bm25 ranking inverted index");
    private static readonly SearchDocument Db = new("db", "postgres vector index query");
    private static readonly SearchDocument Weather = new("weather", "weather forecast sunny afternoon");

    private static SearchDocument[] Corpus => new[] { Auth, Search, Db, Weather };

    private static InMemoryVectorSearchEngine NewEngine(out HashingEmbeddingProvider provider)
    {
        provider = new HashingEmbeddingProvider(256);
        return new InMemoryVectorSearchEngine(provider);
    }

    [Fact]
    public void Constructor_NullProvider_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new InMemoryVectorSearchEngine(null!));
    }

    [Fact]
    public void Count_Dimension_And_Documents_ReflectState()
    {
        var engine = NewEngine(out var provider);

        Assert.Equal(256, engine.Dimension);
        Assert.Equal(provider.Dimension, engine.Dimension);
        Assert.Empty(engine.Documents);

        engine.Add(Auth);

        Assert.Equal(1, engine.Count);
        Assert.Single(engine.Documents);
    }

    [Fact]
    public void Index_ReplacesPreviousContent()
    {
        var engine = NewEngine(out _);

        engine.Add(Auth);
        engine.Index(new[] { Search, Db });

        Assert.Equal(2, engine.Count);
        Assert.DoesNotContain(engine.Documents, document => document.Id == "auth");
    }

    [Fact]
    public void Add_SameId_ReplacesDocument()
    {
        var engine = NewEngine(out _);

        engine.Add(Auth);
        engine.Add(new SearchDocument("auth", "completely different content here"));

        Assert.Equal(1, engine.Count);
        Assert.Equal("completely different content here", engine.Documents.Single().Text);
    }

    [Fact]
    public void Add_NullDocument_Throws()
    {
        var engine = NewEngine(out _);
        Assert.Throws<ArgumentNullException>(() => engine.Add(null!));
    }

    [Fact]
    public void Add_ProviderDimensionMismatch_Throws()
    {
        var engine = new InMemoryVectorSearchEngine(new FixedProvider(dimension: 4, length: _ => 2));
        Assert.Throws<InvalidOperationException>(() => engine.Add(Auth));
    }

    [Fact]
    public void Remove_ExistingAndMissing()
    {
        var engine = NewEngine(out _);
        engine.Add(Auth);
        engine.Add(Search);

        engine.Remove("auth");
        engine.Remove("absent");

        Assert.Equal(1, engine.Count);
        Assert.DoesNotContain(engine.Documents, document => document.Id == "auth");
    }

    [Fact]
    public void Remove_Null_Throws()
    {
        var engine = NewEngine(out _);
        Assert.Throws<ArgumentNullException>(() => engine.Remove(null!));
    }

    [Fact]
    public void Clear_DropsEverything()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        engine.Clear();

        Assert.Equal(0, engine.Count);
        Assert.Empty(engine.Documents);
    }

    [Fact]
    public void Search_RanksSharedVocabularyFirst()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        var results = engine.Search("oauth token refresh", new SearchOptions(Limit: 5));

        Assert.Equal("auth", results[0].DocumentId);
        Assert.True(results[0].Score > 0);
    }

    [Fact]
    public void Search_EmptyIndex_ReturnsEmpty()
    {
        var engine = NewEngine(out _);
        Assert.Empty(engine.Search("anything"));
    }

    [Fact]
    public void Search_BlankQuery_ReturnsEmpty()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        Assert.Empty(engine.Search("   "));
    }

    [Fact]
    public void Search_EmptyOptions_ReturnsEmpty()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        Assert.Empty(engine.Search("oauth", new SearchOptions(Limit: 0)));
    }

    [Fact]
    public void Search_NullQuery_Throws()
    {
        var engine = NewEngine(out _);
        Assert.Throws<ArgumentNullException>(() => engine.Search(null!));
    }

    [Fact]
    public void Search_RejectsUnsupportedSyntax()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        Assert.Equal(QueryFeature.None, engine.SupportedQueryFeatures);
        Assert.Throws<NotSupportedException>(() => engine.Search("token*"));
    }

    [Fact]
    public void Search_HonorsLimitAndOffset()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        var all = engine.Search("index query", new SearchOptions(Limit: 10));
        Assert.True(all.Count >= 2);

        var limited = engine.Search("index query", new SearchOptions(Limit: 1));
        Assert.Single(limited);

        var offset = engine.Search("index query", new SearchOptions(Limit: 1, Offset: 1));
        Assert.Single(offset);
        Assert.Equal(all[1].DocumentId, offset[0].DocumentId);
    }

    [Fact]
    public void Search_AppliesMetadataFilter()
    {
        var engine = NewEngine(out _);
        engine.Index(new[]
        {
            new SearchDocument("a", "oauth token", new Dictionary<string, string> { ["kind"] = "article" }),
            new SearchDocument("b", "oauth token", new Dictionary<string, string> { ["kind"] = "talk" }),
        });

        var results = engine.Search("oauth token", new SearchOptions(
            Limit: 10,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "article") }));

        Assert.Single(results, result => result.DocumentId == "a");
    }

    [Fact]
    public void Search_AppliesMinimumScore()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        var all = engine.Search("index query", new SearchOptions(Limit: 10));
        Assert.True(all.Count >= 2);

        double threshold = all[0].Score;

        var filtered = engine.Search("index query", new SearchOptions(Limit: 10, MinimumScore: threshold));

        Assert.All(filtered, result => Assert.True(result.Score >= threshold));
        Assert.True(filtered.Count < all.Count);
    }

    [Fact]
    public void Search_QueryDimensionMismatch_Throws()
    {
        // Passage vectors match the declared dimension (Add succeeds), query vectors do not.
        var engine = new InMemoryVectorSearchEngine(
            new FixedProvider(dimension: 4, length: use => use == EmbeddingUse.Query ? 2 : 4));

        engine.Add(Auth);

        Assert.Throws<InvalidOperationException>(() => engine.Search("oauth"));
    }

    [Fact]
    public void EstimateCandidateCount_EmptyRequestIsZero_ElseCorpusSize()
    {
        var engine = NewEngine(out _);
        engine.Index(Corpus);

        Assert.Equal(0, engine.EstimateCandidateCount("oauth", new SearchOptions(Limit: 0)));
        Assert.Equal(engine.Count, engine.EstimateCandidateCount("oauth", SearchOptions.Default));
    }

    [Fact]
    public void EstimateCandidateCount_NullOptions_Throws()
    {
        var engine = NewEngine(out _);
        Assert.Throws<ArgumentNullException>(() => engine.EstimateCandidateCount("oauth", null!));
    }

    [Fact]
    public void Export_Import_RoundTripsWithoutReEmbedding()
    {
        var provider = new CountingProvider();
        var engine = new InMemoryVectorSearchEngine(provider);
        engine.Index(Corpus);

        var entries = engine.Export();
        Assert.Equal(engine.Count, entries.Count);

        int callsAfterIndex = provider.Calls;
        Assert.Equal(Corpus.Length, callsAfterIndex);

        var reloaded = new InMemoryVectorSearchEngine(provider);
        reloaded.Import(entries);

        // Import restores the stored vectors: the provider is not called again.
        Assert.Equal(callsAfterIndex, provider.Calls);
        Assert.Equal(engine.Count, reloaded.Count);

        var results = reloaded.Search("oauth token refresh", new SearchOptions(Limit: 5));
        Assert.Equal(callsAfterIndex + 1, provider.Calls);
        Assert.Equal("auth", results[0].DocumentId);
    }

    [Fact]
    public void Import_NullEntries_Throws()
    {
        var engine = NewEngine(out _);
        Assert.Throws<ArgumentNullException>(() => engine.Import(null!));
    }

    [Fact]
    public void Import_NullEntry_Throws()
    {
        var engine = NewEngine(out _);
        Assert.Throws<ArgumentNullException>(() => engine.Import(new VectorIndexEntry[] { null! }));
    }

    [Fact]
    public void Import_EntryWithNullDocument_Throws()
    {
        var engine = NewEngine(out _);
        var entry = new VectorIndexEntry(null!, new float[256]);
        Assert.Throws<ArgumentNullException>(() => engine.Import(new[] { entry }));
    }

    [Fact]
    public void Hybrid_WithLexicalEngine_FusesByRrf()
    {
        var tokenizer = Tokenizer.Default;
        var lexicalIndex = new InMemoryTextIndex(tokenizer);
        lexicalIndex.Index(Corpus);
        var lexical = new RankedTextSearchEngine(lexicalIndex, new Bm25Scorer(), tokenizer);

        var vector = NewEngine(out _);
        vector.Index(Corpus);

        var hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[] { lexical, vector },
            new ReciprocalRankFusionMerger(),
            sourceNames: new[] { "lexical", "vector" });

        var results = hybrid.Search("oauth token refresh", new SearchOptions(Limit: 3));

        Assert.Equal("auth", results[0].DocumentId);
    }

    /// <summary>An <see cref="IEmbeddingProvider"/> with a fixed dimension and per-role length.</summary>
    private sealed class FixedProvider : IEmbeddingProvider
    {
        private readonly Func<EmbeddingUse, int> _length;

        public FixedProvider(int dimension, Func<EmbeddingUse, int> length)
        {
            Dimension = dimension;
            _length = length;
        }

        public int Dimension { get; }

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReadOnlyMemory<float>>(new float[_length(use)]);
    }

    /// <summary>Wraps the hashing provider and counts how many texts were embedded.</summary>
    private sealed class CountingProvider : IEmbeddingProvider
    {
        private readonly HashingEmbeddingProvider _inner = new(64);

        public int Calls { get; private set; }

        public int Dimension => _inner.Dimension;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return _inner.GetTextEmbeddingAsync(text, use, cancellationToken);
        }
    }
}
