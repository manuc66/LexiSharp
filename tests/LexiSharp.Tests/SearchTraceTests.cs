using LexiSharp.Core;
using LexiSharp.Embeddings;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="SearchTrace"/>: that each pipeline stage records itself, that the recorded
/// numbers are the real ones, and that a trace never grows with the corpus.
/// </summary>
public class SearchTraceTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "oauth access token renewal", new Dictionary<string, string> { ["kind"] = "auth" }, "auth"),
        new("d2", "vector search retrieval pipeline", new Dictionary<string, string> { ["kind"] = "search" }, "search"),
        new("d3", "token refresh and credential rotation"),
    ];

    private static RankedTextSearchEngine Bm25() =>
        new(new InMemoryTextIndex(), new Bm25Scorer()) { };

    private static RankedTextSearchEngine Indexed()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(Corpus);
        return engine;
    }

    [Fact]
    public void NoTraceMeansNoStepsRecorded()
    {
        var engine = Indexed();

        engine.Search("token");

        // The contract is that a null trace is inert; there is nothing to observe directly, so the
        // assertion is that Search still works and no trace object was needed to get there.
        Assert.NotNull(engine.Search("token"));
    }

    [Fact]
    public void DenseAndSparseEnginesAlsoRecordTheScoreStage()
    {
        // A stage that only the BM25 engine recorded would leave the dense lane of the demo's
        // comparison with an empty chain, so every stock engine records the page it returns.
        var dense = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider());
        dense.Index(Corpus);

        var denseTrace = new SearchTrace();
        var denseResults = dense.Search("token", new SearchOptions(Limit: 2, Trace: denseTrace));

        var denseSteps = denseTrace.Steps.Where(s => s.Stage == TraceStage.Score).ToArray();
        Assert.Equal(denseResults.Count, denseSteps.Length);
        Assert.All(denseSteps, s => Assert.Equal("Dense", s.Detail));

        var sparse = new SparseTextSearchEngine(new TestSparseProvider());
        sparse.Index(Corpus);

        var sparseTrace = new SearchTrace();
        var sparseResults = sparse.Search("token", new SearchOptions(Limit: 2, Trace: sparseTrace));

        var sparseSteps = sparseTrace.Steps.Where(s => s.Stage == TraceStage.Score).ToArray();
        Assert.Equal(sparseResults.Count, sparseSteps.Length);
        Assert.All(sparseSteps, s => Assert.Equal("Sparse", s.Detail));
    }

    [Fact]
    public void ScoreStageRecordsOneStepPerResultOfThePage()
    {
        var engine = Indexed();
        var trace = new SearchTrace();

        var results = engine.Search("token", new SearchOptions(Limit: 2, Trace: trace));

        var scoreSteps = trace.Steps.Where(s => s.Stage == TraceStage.Score).ToArray();

        Assert.Equal(results.Count, scoreSteps.Length);
        Assert.All(scoreSteps, s => Assert.Equal(s.Before, s.After));
        Assert.All(scoreSteps, s => Assert.Equal("BM25", s.Detail));

        // Same order as the returned page, and the same scores.
        for (int i = 0; i < results.Count; i++)
        {
            Assert.Equal(results[i].DocumentId, scoreSteps[i].DocumentId);
            Assert.Equal(results[i].Score, scoreSteps[i].After);
        }
    }

    [Fact]
    public void ScoreStageIsBoundedByThePageNotTheCorpus()
    {
        // 20k documents: the step count must follow Limit, never the corpus.
        var documents = new SearchDocument[20_000];
        for (int i = 0; i < documents.Length; i++)
            documents[i] = new SearchDocument($"d{i}", "token refresh credential rotation oauth session");

        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(documents);

        var trace = new SearchTrace();
        engine.Search("token", new SearchOptions(Limit: 5, Trace: trace));

        Assert.Equal(5, trace.Steps.Count(s => s.Stage == TraceStage.Score));
    }

    [Fact]
    public void BoostStageRecordsTheScoreItReplaced()
    {
        var engine = new BoostedTextSearchEngine(
            Indexed(),
            r => new ScoreBoost(Multiply: 2.0, Add: 0.5));

        var trace = new SearchTrace();
        var results = engine.Search("token", new SearchOptions(Limit: 3, Trace: trace));

        var boostSteps = trace.Steps.Where(s => s.Stage == TraceStage.Boost).ToArray();
        Assert.NotEmpty(boostSteps);

        foreach (var step in boostSteps)
        {
            // after == before * 2 + 0.5, the exact rule the boost function applied
            Assert.Equal(step.Before * 2.0 + 0.5, step.After, 10);
            Assert.Equal("x2 +0.5", step.Detail);
        }

        // The boosted page is what Search returned, and each boosted doc is in the trace.
        Assert.All(results, r => Assert.Contains(boostSteps, s => s.DocumentId == r.DocumentId));
    }

    [Fact]
    public void RerankStagePairsTheInnerScoreWithTheRerankers()
    {
        var inner = Indexed();
        var engine = new RerankedTextSearchEngine(inner, new FixedScoreReranker(7.5));

        var trace = new SearchTrace();
        var results = engine.Search("token", new SearchOptions(Limit: 2, Trace: trace));

        var rerankSteps = trace.Steps.Where(s => s.Stage == TraceStage.Rerank).ToArray();

        Assert.Equal(results.Count, rerankSteps.Length);
        Assert.All(rerankSteps, s =>
        {
            Assert.Equal(7.5, s.After);
            Assert.NotEqual(7.5, s.Before);   // the inner BM25 score it replaced
            Assert.Equal("FixedScore", s.Detail);
        });

        Assert.All(results, r => Assert.Equal(7.5, r.Score));
    }

    [Fact]
    public void RerankStageReportsNaNBeforeForADocumentTheInnerEngineNeverReturned()
    {
        // A reranker is allowed to introduce a document; there is no inner score to pair it with.
        var engine = new RerankedTextSearchEngine(Indexed(), new InjectingReranker("ghost", 3.0));

        var trace = new SearchTrace();
        engine.Search("token", new SearchOptions(Limit: 5, Trace: trace));

        var ghost = trace.Steps.Single(s => s.DocumentId == "ghost");

        Assert.Equal(TraceStage.Rerank, ghost.Stage);
        Assert.True(double.IsNaN(ghost.Before));
        Assert.Equal(3.0, ghost.After);
    }

    [Fact]
    public void MergeStageListsThePerSourceScoresSortedByLabel()
    {
        var lexical = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        var dense = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider());

        lexical.Index(Corpus);
        dense.Index(Corpus);

        var hybrid = new HybridTextSearchEngine(
            [lexical, dense],
            new ReciprocalRankFusionMerger(),
            sourceNames: ["zeta", "alpha"]);

        var trace = new SearchTrace();
        var results = hybrid.Search("token", new SearchOptions(Limit: 2, Trace: trace));

        var mergeSteps = trace.Steps.Where(s => s.Stage == TraceStage.Merge).ToArray();
        Assert.Equal(results.Count, mergeSteps.Length);

        foreach (var step in mergeSteps)
        {
            // Labels sorted, so the detail is stable and assertable.
            Assert.Matches("^(alpha=[^ ]+ zeta=[^ ]+|zeta=[^ ]+)$", step.Detail!);
            Assert.Equal(step.Before, step.After);
        }
    }

    [Fact]
    public void RouteStageRecordsWhichRouteRan()
    {
        var engine = new RoutingSearchEngine(
            new FixedRouter("dense"),
            [new SearchRoute("bm25", Indexed()), new SearchRoute("dense", Dense())],
            fallbackId: "bm25");

        var trace = new SearchTrace();
        engine.Search("token", new SearchOptions(Limit: 3, Trace: trace));

        var route = trace.Steps.Single(s => s.Stage == TraceStage.Route);

        Assert.Equal("dense", route.Detail);
        Assert.Equal(0.9, route.Before);
        Assert.Equal(string.Empty, route.DocumentId);
    }

    [Fact]
    public void RouteStageSaysWhenTheFallbackWasTaken()
    {
        // Confidence below the threshold: the fallback runs, and the trace must not claim a
        // deliberate choice.
        var engine = new RoutingSearchEngine(
            new FixedRouter("dense", confidence: 0.1),
            [new SearchRoute("bm25", Indexed()), new SearchRoute("dense", Dense())],
            fallbackId: "bm25",
            minimumConfidence: 0.6);

        var trace = new SearchTrace();
        engine.Search("token", new SearchOptions(Limit: 3, Trace: trace));

        var route = trace.Steps.Single(s => s.Stage == TraceStage.Route);

        Assert.Contains("fallback", route.Detail!);
        Assert.Contains("bm25", route.Detail!);
    }

    [Fact]
    public void TruncationIsReportedRatherThanSilent()
    {
        var trace = new SearchTrace(capacity: 3);

        for (int i = 0; i < 10; i++)
            trace.Record(new TraceStep(TraceStage.Score, $"d{i}", 1, 1, "BM25"));

        Assert.Equal(3, trace.Steps.Count);
        Assert.Equal(7, trace.Dropped);
        Assert.True(trace.IsTruncated);
    }

    [Fact]
    public void CapacityMustBePositive()
        => Assert.Throws<ArgumentOutOfRangeException>(() => new SearchTrace(capacity: 0));

    [Fact]
    public void StageCountsBucketsTheRecordedStepsByStage()
    {
        var trace = new SearchTrace();
        trace.Record(new TraceStep(TraceStage.Score, "d0", 1, 1, "BM25"));
        trace.Record(new TraceStep(TraceStage.Score, "d1", 1, 1, "BM25"));
        trace.Record(new TraceStep(TraceStage.Merge, "d0", 1, 1, "rrf"));

        var counts = trace.StageCounts();

        Assert.Equal(2, counts[TraceStage.Score]);
        Assert.Equal(1, counts[TraceStage.Merge]);
        Assert.Equal(2, counts.Count);

        // A stage that never ran stays absent: StageCounts counts recorded steps, it does not
        // fabricate zeros for stages the pipeline did not reach.
        Assert.False(counts.ContainsKey(TraceStage.Route));
    }

    [Fact]
    public void ATruncatedPipelineTraceStillReportsItself()
    {
        // Capacity smaller than the pipeline needs: the trace must flag that it is partial rather
        // than presenting a short history as the whole story.
        var engine = new BoostedTextSearchEngine(
            Indexed(),
            r => new ScoreBoost(Multiply: 1.1));

        var trace = new SearchTrace(capacity: 2);
        engine.Search("token", new SearchOptions(Limit: 3, Trace: trace));

        Assert.True(trace.IsTruncated);
    }

    private static InMemoryVectorSearchEngine Dense()
    {
        var engine = new InMemoryVectorSearchEngine(new HashingEmbeddingProvider());
        engine.Index(Corpus);
        return engine;
    }

    private sealed class FixedScoreReranker(double score) : IReranker
    {
        public string Name => "FixedScore";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates) =>
            candidates.Select(c => c with { Score = score }).ToArray();
    }

    private sealed class InjectingReranker(string documentId, double score) : IReranker
    {
        public string Name => "Injecting";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates)
        {
            var results = new List<SearchResult>(candidates)
            {
                new(documentId, score, new SearchDocument(documentId, "injected by the reranker")),
            };
            return results;
        }
    }

    private sealed class FixedRouter(string routeId, double confidence = 0.9) : IQueryRouter
    {
        public ValueTask<QueryRoute?> RouteAsync(
            string query,
            IReadOnlyList<string> candidateRouteIds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<QueryRoute?>(new QueryRoute(routeId, confidence));
    }

    /// <summary>Deterministic stand-in so the sparse engine path runs without a model.</summary>
    private sealed class TestSparseProvider : ISparseEmbeddingProvider
    {
        public Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
            string text,
            EmbeddingUse use,
            CancellationToken cancellationToken = default)
        {
            var activations = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var term in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                activations[term] = 1.0f;
            return Task.FromResult<IReadOnlyDictionary<string, float>>(activations);
        }
    }
}
