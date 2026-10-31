using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

public sealed class OnnxCrossEncoderScorerTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static BertWordPieceTokenizer Tokenizer() => new(Fixture("vocab.txt"));

    private static OnnxCrossEncoderScorer Scorer(string model = "tiny-cross-encoder.onnx") =>
        new(new OnnxCrossEncoderOptions
        {
            ModelPath = Fixture(model),
            Tokenizer = Tokenizer(),
        });

    private static OnnxCrossEncoderScorer ScorerWith(string model, int scoreIndex) =>
        new(new OnnxCrossEncoderOptions
        {
            ModelPath = Fixture(model),
            Tokenizer = Tokenizer(),
            ScoreIndex = scoreIndex,
        });

    private static SearchDocument[] Documents() =>
    [
        new("1", "hello world"),
        new("2", "dense retrieval"),
        new("3", "search engine"),
    ];

    [Fact]
    public void Score_DependsOnTheDocument()
    {
        using var scorer = Scorer();

        double first = scorer.Score("search", new SearchDocument("1", "hello world"));
        double second = scorer.Score("search", new SearchDocument("2", "dense retrieval"));

        Assert.True(double.IsFinite(first));
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Score_IsDeterministic()
    {
        using var scorer = Scorer();
        var document = new SearchDocument("1", "rank fusion");

        Assert.Equal(scorer.Score("search", document), scorer.Score("search", document));
    }

    [Fact]
    public void ScoreBatch_MatchesIndividualCalls()
    {
        using var scorer = Scorer();
        SearchDocument[] documents = Documents();

        IReadOnlyList<double> batch = scorer.ScoreBatch("search", documents);
        double[] single = documents.Select(document => scorer.Score("search", document)).ToArray();

        Assert.Equal(single, batch);
    }

    [Fact]
    public void Scorer_AcceptsInt32Inputs()
    {
        using var int32 = Scorer("tiny-cross-encoder-int32.onnx");

        Assert.Equal(
            Scorer().Score("search", new SearchDocument("1", "hello world")),
            int32.Score("search", new SearchDocument("1", "hello world")),
            precision: 5);
    }

    [Fact]
    public void Scorer_AcceptsFloat16Outputs()
    {
        using var fp16 = Scorer("tiny-cross-encoder-fp16.onnx");

        Assert.Equal(
            Scorer().Score("search", new SearchDocument("1", "hello world")),
            fp16.Score("search", new SearchDocument("1", "hello world")),
            precision: 2);
    }

    [Fact]
    public void ScoreIndex_SelectsTheConfiguredLabel()
    {
        double[] raw = new double[3];

        for (int index = 0; index < raw.Length; index++)
        {
            using var scorer = ScorerWith("tiny-cross-encoder-3logits.onnx", index);
            raw[index] = scorer.Score("search", new SearchDocument("1", "hello world"));
        }

        using var selected = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
        {
            ModelPath = Fixture("tiny-cross-encoder-3logits.onnx"),
            Tokenizer = Tokenizer(),
            ScoreIndex = 2,
        });

        Assert.Equal(raw[2], selected.Score("search", new SearchDocument("1", "hello world")));
    }

    [Fact]
    public void Softmax_ReturnsTheSelectedLabelProbability()
    {
        double[] raw = Enumerable.Range(0, 3)
            .Select(index => ScorerWith("tiny-cross-encoder-3logits.onnx", index).Score("search", new SearchDocument("1", "hello world")))
            .ToArray();

        using var softmax = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
        {
            ModelPath = Fixture("tiny-cross-encoder-3logits.onnx"),
            Tokenizer = Tokenizer(),
            ScoreIndex = 2,
            ScoreTransform = OnnxScoreTransform.Softmax,
        });

        double expected = Math.Exp(raw[2]) / raw.Sum(value => Math.Exp(value));

        Assert.Equal(expected, softmax.Score("search", new SearchDocument("1", "hello world")), precision: 5);
    }

    [Fact]
    public void ScoreIndexOutOfRange_SaysSo()
    {
        using var scorer = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
        {
            ModelPath = Fixture("tiny-cross-encoder-3logits.onnx"),
            Tokenizer = Tokenizer(),
            ScoreIndex = 9,
        });

        Assert.Throws<InvalidOperationException>(
            () => scorer.Score("search", new SearchDocument("1", "hello world")));
    }

    [Fact]
    public void Scorer_ReportsItsConfiguredName()
    {
        using var scorer = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
        {
            ModelPath = Fixture("tiny-cross-encoder.onnx"),
            Tokenizer = Tokenizer(),
            Name = "tiny-cross-encoder",
        });

        Assert.Equal("tiny-cross-encoder", scorer.Name);
    }

    [Fact]
    public void Scorer_DrivesTheHybridReranker()
    {
        using var scorer = Scorer();
        var reranker = new CrossEncoderReranker(scorer);
        var candidates = new[]
        {
            new SearchResult("1", 1.0, new SearchDocument("1", "hello world")),
            new SearchResult("2", 0.5, new SearchDocument("2", "dense retrieval")),
        };

        IReadOnlyList<SearchResult> reranked = reranker.Rerank("search", candidates);

        Assert.Equal(2, reranked.Count);
        Assert.Equal(candidates.Select(c => c.DocumentId).Order(), reranked.Select(r => r.DocumentId).Order());
        Assert.All(reranked, result => Assert.Equal(scorer.Score("search", result.Document), result.Score));
    }

    [Fact]
    public void BatchedReranker_ProducesTheSameOrder()
    {
        using var scorer = Scorer();
        var perPair = new CrossEncoderReranker(scorer);
        var batched = new BatchedCrossEncoderReranker(scorer);
        var candidates = Documents()
            .Select((document, index) => new SearchResult(document.Id, 10 - index, document))
            .ToArray();

        IReadOnlyList<SearchResult> expected = perPair.Rerank("search", candidates);
        IReadOnlyList<SearchResult> actual = batched.Rerank("search", candidates);

        Assert.Equal(expected.Select(result => result.DocumentId), actual.Select(result => result.DocumentId));
        Assert.Equal(expected.Select(result => result.Score), actual.Select(result => result.Score));
    }

    [Fact]
    public void BatchedReranker_AppliesTheLimit()
    {
        using var scorer = Scorer();
        var reranker = new BatchedCrossEncoderReranker(scorer, limit: 2);

        IReadOnlyList<SearchResult> reranked = reranker.Rerank(
            "search",
            Documents().Select(document => new SearchResult(document.Id, 1, document)).ToArray());

        Assert.Equal(2, reranked.Count);
    }

    [Fact]
    public void Scorer_BatchSizeDoesNotChangeTheScores()
    {
        using var scorer = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
        {
            ModelPath = Fixture("tiny-cross-encoder.onnx"),
            Tokenizer = Tokenizer(),
            MaxBatchSize = 2,
        });

        SearchDocument[] documents = Documents();
        IReadOnlyList<double> batch = scorer.ScoreBatch("search", documents);

        Assert.Equal(documents.Select(document => scorer.Score("search", document)).ToArray(), batch);
    }

    [Fact]
    public void Scorer_ObservesAPreCanceledToken()
    {
        using var scorer = Scorer();

        Assert.Throws<OperationCanceledException>(
            () => scorer.ScoreBatch("search", Documents(), new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Scorer_WarmupDoesNotChangeScores()
    {
        using var scorer = Scorer();
        var document = new SearchDocument("1", "hello world");
        double before = scorer.Score("search", document);

        scorer.Warmup();

        Assert.Equal(before, scorer.Score("search", document));
    }

    [Fact]
    public void Scorer_ThrowsAfterDispose()
    {
        var scorer = Scorer();
        scorer.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scorer.Score("search", new SearchDocument("1", "hello")));
    }
}
