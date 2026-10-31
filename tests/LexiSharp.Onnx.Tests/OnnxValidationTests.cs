using LexiSharp.Core;
using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

public sealed class OnnxValidationTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static BertWordPieceTokenizer Tokenizer() => new(Fixture("vocab.txt"));

    private static OnnxTextEncoderOptions EncoderVariant(
        string model = "tiny-encoder.onnx",
        string inputIds = "input_ids",
        string mask = "attention_mask",
        string output = "last_hidden_state",
        int maxSequence = 512,
        int maxInput = 1_000_000,
        int maxBatch = 32) =>
        new()
        {
            ModelPath = Fixture(model),
            Tokenizer = Tokenizer(),
            InputIdsName = inputIds,
            AttentionMaskName = mask,
            OutputName = output,
            MaxSequenceLength = maxSequence,
            MaxInputCharacters = maxInput,
            MaxBatchSize = maxBatch,
        };

    private static OnnxCrossEncoderOptions ScorerVariant(
        string model = "tiny-cross-encoder.onnx",
        string inputIds = "input_ids",
        string mask = "attention_mask",
        string output = "logits",
        int maxSequence = 512,
        int maxQuery = 128,
        int maxInput = 1_000_000,
        int maxBatch = 32,
        int scoreIndex = 0,
        OnnxScoreTransform transform = OnnxScoreTransform.None) =>
        new()
        {
            ModelPath = Fixture(model),
            Tokenizer = Tokenizer(),
            InputIdsName = inputIds,
            AttentionMaskName = mask,
            OutputName = output,
            MaxSequenceLength = maxSequence,
            MaxQueryTokens = maxQuery,
            MaxInputCharacters = maxInput,
            MaxBatchSize = maxBatch,
            ScoreIndex = scoreIndex,
            ScoreTransform = transform,
        };

    [Fact]
    public void Encoder_RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxTextEncoder(null!));
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(new OnnxTextEncoderOptions { ModelPath = " ", Tokenizer = Tokenizer() }));
        Assert.Throws<ArgumentNullException>(() => new OnnxTextEncoder(new OnnxTextEncoderOptions { ModelPath = Fixture("tiny-encoder.onnx"), Tokenizer = null! }));
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(EncoderVariant(maxSequence: 1)));
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(EncoderVariant(maxInput: -1)));
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(EncoderVariant(maxBatch: 0)));
    }

    [Fact]
    public void Encoder_RejectsTensorNamesTheGraphDoesNotDeclare()
    {
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(EncoderVariant(inputIds: "nope")));
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(EncoderVariant(mask: "nope")));
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(EncoderVariant(output: "nope")));
    }

    [Fact]
    public void Encoder_RejectsUnsupportedTensorTypesAndRanks()
    {
        Assert.Throws<NotSupportedException>(() => new OnnxTextEncoder(EncoderVariant("tiny-encoder-int8-input.onnx")));
        Assert.Throws<NotSupportedException>(() => new OnnxTextEncoder(EncoderVariant("tiny-encoder-mixed-mask.onnx")));
        Assert.Throws<NotSupportedException>(() => new OnnxTextEncoder(EncoderVariant("tiny-encoder-int32-output.onnx")));
        Assert.Throws<NotSupportedException>(() => new OnnxTextEncoder(EncoderVariant("tiny-encoder-rank4.onnx")));
    }

    [Fact]
    public void Encoder_ResolvesADynamicHiddenDimension()
    {
        using var encoder = new OnnxTextEncoder(EncoderVariant("tiny-encoder-dynamic.onnx"));

        Assert.Equal(8, encoder.Dimension);
    }

    [Fact]
    public void Encoder_RejectsNullAndEmptyInputs()
    {
        using var encoder = new OnnxTextEncoder(EncoderVariant());

        Assert.Throws<ArgumentNullException>(() => encoder.GetTextEmbedding(null!));
        Assert.Throws<ArgumentNullException>(() => encoder.GetTextEmbeddings(null!));
        Assert.Throws<ArgumentException>(() => encoder.GetTextEmbeddings([null!]));
        Assert.Empty(encoder.GetTextEmbeddings([]));
    }

    [Fact]
    public void Encoder_DisposeIsIdempotent()
    {
        var encoder = new OnnxTextEncoder(EncoderVariant());

        encoder.Dispose();
        encoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => encoder.GetTextEmbeddings(["hello"]));
    }

    [Fact]
    public void Encoder_SignatureChangesWithTheModelFile()
    {
        using var first = new OnnxTextEncoder(EncoderVariant("tiny-encoder.onnx"));
        using var second = new OnnxTextEncoder(EncoderVariant("tiny-encoder-known.onnx"));

        Assert.NotEqual(first.EmbeddingSignature, second.EmbeddingSignature);
    }

    [Fact]
    public void Scorer_RejectsInvalidOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new OnnxCrossEncoderScorer(null!));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions { ModelPath = " ", Tokenizer = Tokenizer() }));
        Assert.Throws<ArgumentNullException>(() => new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions { ModelPath = Fixture("tiny-cross-encoder.onnx"), Tokenizer = null! }));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(maxSequence: 2)));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(maxQuery: 0)));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(maxInput: -1)));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(maxBatch: 0)));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(scoreIndex: -1)));
    }

    [Fact]
    public void Scorer_RejectsTensorNamesTheGraphDoesNotDeclare()
    {
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(inputIds: "nope")));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(mask: "nope")));
        Assert.Throws<ArgumentException>(() => new OnnxCrossEncoderScorer(ScorerVariant(output: "nope")));
    }

    [Fact]
    public void Scorer_RejectsUnsupportedTensorTypesAndRanks()
    {
        Assert.Throws<NotSupportedException>(() => new OnnxCrossEncoderScorer(ScorerVariant("tiny-cross-encoder-int8-input.onnx")));
        Assert.Throws<NotSupportedException>(() => new OnnxCrossEncoderScorer(ScorerVariant("tiny-cross-encoder-int32-output.onnx")));
        Assert.Throws<NotSupportedException>(() => new OnnxCrossEncoderScorer(ScorerVariant("tiny-cross-encoder-rank3.onnx")));
    }

    [Fact]
    public void Scorer_HandlesRankOneOutputAndRejectsTheImpossibleOptions()
    {
        using var scorer = new OnnxCrossEncoderScorer(ScorerVariant("tiny-cross-encoder-rank1.onnx"));
        var document = new SearchDocument("1", "hello world");

        Assert.True(double.IsFinite(scorer.Score("search", document)));

        using var indexed = new OnnxCrossEncoderScorer(ScorerVariant("tiny-cross-encoder-rank1.onnx", scoreIndex: 1));
        Assert.Throws<InvalidOperationException>(() => indexed.Score("search", document));

        using var softmax = new OnnxCrossEncoderScorer(ScorerVariant("tiny-cross-encoder-rank1.onnx", transform: OnnxScoreTransform.Softmax));
        Assert.Throws<InvalidOperationException>(() => softmax.Score("search", document));
    }

    [Fact]
    public void Scorer_RejectsNullAndEmptyInputs()
    {
        using var scorer = new OnnxCrossEncoderScorer(ScorerVariant());

        Assert.Throws<ArgumentNullException>(() => scorer.ScoreBatch(null!, []));
        Assert.Throws<ArgumentNullException>(() => scorer.ScoreBatch("search", null!));
        Assert.Throws<ArgumentException>(() => scorer.ScoreBatch("search", [null!]));
        Assert.Empty(scorer.ScoreBatch("search", []));
    }

    [Fact]
    public void Scorer_EnforcesTheInputCharacterCap()
    {
        using var scorer = new OnnxCrossEncoderScorer(ScorerVariant(maxInput: 3));

        Assert.Throws<ArgumentException>(
            () => scorer.ScoreBatch("long query", [new SearchDocument("1", "abc")]));
        Assert.Throws<ArgumentException>(
            () => scorer.ScoreBatch("abc", [new SearchDocument("1", "too long")]));
    }

    [Fact]
    public void Scorer_SigmoidMapsTheLogit()
    {
        using var raw = new OnnxCrossEncoderScorer(ScorerVariant());
        using var sigmoid = new OnnxCrossEncoderScorer(ScorerVariant(transform: OnnxScoreTransform.Sigmoid));
        var document = new SearchDocument("1", "hello world");

        double expected = 1.0 / (1.0 + Math.Exp(-raw.Score("search", document)));

        Assert.Equal(expected, sigmoid.Score("search", document), precision: 6);
    }

    [Fact]
    public void Scorer_DisposeIsIdempotentAndBlocksBatchUse()
    {
        var scorer = new OnnxCrossEncoderScorer(ScorerVariant());

        scorer.Dispose();
        scorer.Dispose();

        Assert.Throws<ObjectDisposedException>(
            () => scorer.ScoreBatch("search", [new SearchDocument("1", "hello")]));
    }

    [Fact]
    public void BatchedReranker_ValidatesItsArgumentsAndHandlesAnEmptyList()
    {
        using var scorer = new OnnxCrossEncoderScorer(ScorerVariant());

        Assert.Throws<ArgumentNullException>(() => new BatchedCrossEncoderReranker(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BatchedCrossEncoderReranker(scorer, limit: 0));

        var reranker = new BatchedCrossEncoderReranker(scorer);
        Assert.Empty(reranker.Rerank("search", []));
        Assert.Contains("batched", reranker.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void BatchedReranker_DropsEverythingBelowTheMinimum()
    {
        using var scorer = new OnnxCrossEncoderScorer(ScorerVariant());
        var reranker = new BatchedCrossEncoderReranker(scorer, minimumScore: double.PositiveInfinity);

        IReadOnlyList<SearchResult> reranked = reranker.Rerank(
            "search",
            [new SearchResult("1", 1, new SearchDocument("1", "hello world"))]);

        Assert.Empty(reranked);
    }
}
