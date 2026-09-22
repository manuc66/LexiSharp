using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The three-way topology from the sparse-learned design: a lexical BM25 engine, a learned-sparse
/// (SPLADE-style) engine and optionally a dense engine, merged through Reciprocal Rank Fusion —
/// each engine's scores have their own scale, so RRF must be the bridge.
/// </summary>
public class HybridSparseMergingTests
{
    private static readonly string[] SharedHitIds = new[] { "1", "3" };

    private static SearchDocument Doc(string id, string text) => new(id, text);

    private static RankedTextSearchEngine LexicalEngine(IEnumerable<SearchDocument> docs)
    {
        var index = new InMemoryTextIndex();
        index.Index(docs);
        return new RankedTextSearchEngine(index, new Bm25Scorer());
    }

    private static SparseTextSearchEngine SparseEngine(IReadOnlyDictionary<string, float> queryVector)
    {
        var provider = new StubSparseProvider(text => text switch
        {
            // Documents: the corpus is embedded by the (consumer-provided) model.
            "doc1 apple pie" => new Dictionary<string, float> { ["apple"] = 1.5f, ["pie"] = 1f },
            "doc2 banana bread" => new Dictionary<string, float> { ["banana"] = 1.5f, ["bread"] = 1f },
            "doc3 apple tart" => new Dictionary<string, float> { ["apple"] = 1.5f, ["tart"] = 1f },
            _ => queryVector,
        });

        return new SparseTextSearchEngine(provider);
    }

    [Fact]
    public void Rrf_MergesLexicalAndSparse_SoSharedHitsOutrankOneSidedHits()
    {
        var docs = new[] { Doc("1", "doc1 apple pie"), Doc("2", "doc2 banana bread"), Doc("3", "doc3 apple tart") };

        // The query activates "apple" for both engines; "banana bread" only in the sparse vector.
        var hybrid = new HybridTextSearchEngine(
            new ITextSearchEngine[]
            {
                LexicalEngine(docs),
                SparseEngine(new Dictionary<string, float> { ["apple"] = 1f, ["banana"] = 1f, ["bread"] = 1f }),
            },
            new ReciprocalRankFusionMerger(),
            minCandidatesPerEngine: 50);
        hybrid.Index(docs);

        var results = hybrid.Search("apple");

        var order = results.Select(r => r.DocumentId).ToList();

        // "1" and "3" match "apple" lexically AND sparsely → double RRF contribution;
        // "2" only matches the sparse engine's banana/bread activations.
        Assert.Equal(SharedHitIds, order.Take(2).OrderBy(x => x).ToArray());
        Assert.Equal("2", order[^1]);
    }

    [Fact]
    public void Rrf_KeepsSparseOnlyHits_ThatLexicalRescoringWouldDrop()
    {
        var docs = new[] { Doc("1", "doc1 apple pie"), Doc("2", "doc2 banana bread"), Doc("3", "doc3 apple tart") };

        var sparseOnly = new Dictionary<string, float> { ["banana"] = 1f, ["bread"] = 1f };

        // Default RerankingResultMerger re-scores the union with BM25: "2" shares no term with
        // "apple", so score 0 → dropped. RRF keeps it because the sparse engine returned it.
        var rescoring = new HybridTextSearchEngine(
            new ITextSearchEngine[]
            {
                LexicalEngine(docs),
                SparseEngine(sparseOnly),
            },
            new RerankingResultMerger(),
            minCandidatesPerEngine: 50);
        rescoring.Index(docs);

        var rrf = new HybridTextSearchEngine(
            new ITextSearchEngine[]
            {
                LexicalEngine(docs),
                SparseEngine(sparseOnly),
            },
            new ReciprocalRankFusionMerger(),
            minCandidatesPerEngine: 50);
        rrf.Index(docs);

        Assert.DoesNotContain(rescoring.Search("apple"), r => r.DocumentId == "2");
        Assert.Contains(rrf.Search("apple"), r => r.DocumentId == "2");
    }

    private sealed class StubSparseProvider : ISparseEmbeddingProvider
    {
        private readonly Func<string, IReadOnlyDictionary<string, float>> _lookup;

        public StubSparseProvider(Func<string, IReadOnlyDictionary<string, float>> lookup)
            => _lookup = lookup;

        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
            => Task.FromResult(_lookup(text));
    }
}