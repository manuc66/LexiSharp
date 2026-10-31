using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

public sealed class OnnxTextEncoderTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static BertWordPieceTokenizer Tokenizer() => new(Fixture("vocab.txt"));

    private static OnnxTextEncoderOptions Options(string model = "tiny-encoder.onnx") =>
        new()
        {
            ModelPath = Fixture(model),
            Tokenizer = Tokenizer(),
        };

    private static float[] Row(int id) =>
        Enumerable.Range(0, 8).Select(d => (float)((id * 10) + d)).ToArray();

    private static float[] Mean(params float[][] rows)
    {
        float[] result = new float[rows[0].Length];

        foreach (float[] row in rows)
        {
            for (int i = 0; i < row.Length; i++)
                result[i] += row[i];
        }

        for (int i = 0; i < result.Length; i++)
            result[i] /= rows.Length;

        return result;
    }

    private static float[] Normalize(float[] vector)
    {
        double sum = vector.Sum(value => (double)value * value);
        float length = (float)Math.Sqrt(sum);

        return vector.Select(value => value / length).ToArray();
    }

    private static void AssertClose(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual, float tolerance = 1e-5f)
    {
        Assert.Equal(expected.Length, actual.Length);

        for (int i = 0; i < expected.Length; i++)
            Assert.InRange(Math.Abs(expected[i] - actual[i]), 0, tolerance);
    }

    [Fact]
    public void Encoder_ReadsTheHiddenDimensionFromTheGraph()
    {
        using var encoder = new OnnxTextEncoder(Options());

        Assert.Equal(8, encoder.Dimension);
    }

    [Fact]
    public void Encoder_ReturnsOneNormalizedVector()
    {
        using var encoder = new OnnxTextEncoder(Options());

        float[] values = encoder.GetTextEmbedding("hello world").ToArray();
        double norm = Math.Sqrt(values.Sum(value => (double)value * value));

        Assert.Equal(8, values.Length);
        Assert.Equal(1.0, norm, 5);
    }

    [Fact]
    public void Encoder_IsDeterministic()
    {
        using var encoder = new OnnxTextEncoder(Options());

        Assert.Equal(
            encoder.GetTextEmbedding("search engine").ToArray(),
            encoder.GetTextEmbedding("search engine").ToArray());
    }

    [Fact]
    public void Encoder_AppliesTheRolePrefix()
    {
        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            QueryPrefix = "search ",
            PassagePrefix = "document ",
            Pooling = OnnxPooling.Mean,
            Normalize = false,
        });

        Assert.NotEqual(
            encoder.GetTextEmbedding("engine", EmbeddingUse.Query).ToArray(),
            encoder.GetTextEmbedding("engine", EmbeddingUse.Passage).ToArray());
    }

    [Fact]
    public void Encoder_PoolingChangesTheVector()
    {
        using var cls = new OnnxTextEncoder(Options());
        using var mean = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            Pooling = OnnxPooling.Mean,
        });

        Assert.NotEqual(
            cls.GetTextEmbedding("hello world").ToArray(),
            mean.GetTextEmbedding("hello world").ToArray());
    }

    [Fact]
    public async Task Encoder_AsyncOverloadMatchesTheSyncOne()
    {
        using var encoder = new OnnxTextEncoder(Options());
        ReadOnlyMemory<float> async = await encoder.GetTextEmbeddingAsync("rank fusion", EmbeddingUse.Passage);

        Assert.Equal(encoder.GetTextEmbedding("rank fusion").ToArray(), async.ToArray());
    }

    [Fact]
    public async Task Encoder_AsyncBatchMatchesTheSyncOne()
    {
        using var encoder = new OnnxTextEncoder(Options());
        string[] texts = ["hello world", "search engine", "rank fusion"];
        IReadOnlyList<ReadOnlyMemory<float>> async = await encoder.GetTextEmbeddingsAsync(texts, EmbeddingUse.Passage);

        IReadOnlyList<ReadOnlyMemory<float>> sync = encoder.GetTextEmbeddings(texts);

        for (int i = 0; i < texts.Length; i++)
            AssertClose(sync[i].Span, async[i].Span);
    }

    [Fact]
    public void Encoder_BatchMatchesSingleCalls()
    {
        using var encoder = new OnnxTextEncoder(Options());
        string[] texts = ["hello world", "search engine document", "rank fusion"];

        IReadOnlyList<ReadOnlyMemory<float>> batch = encoder.GetTextEmbeddings(texts);

        for (int i = 0; i < texts.Length; i++)
            AssertClose(encoder.GetTextEmbedding(texts[i]).Span, batch[i].Span);
    }

    [Fact]
    public void Encoder_AcceptsInt32Inputs()
    {
        using var int64 = new OnnxTextEncoder(Options());
        using var int32 = new OnnxTextEncoder(Options("tiny-encoder-int32.onnx"));

        AssertClose(
            int64.GetTextEmbedding("search engine").Span,
            int32.GetTextEmbedding("search engine").Span);
    }

    [Fact]
    public void Encoder_AcceptsFloat16Outputs()
    {
        using var fp32 = new OnnxTextEncoder(Options());
        using var fp16 = new OnnxTextEncoder(Options("tiny-encoder-fp16.onnx"));

        Assert.Equal(8, fp16.Dimension);
        AssertClose(
            fp32.GetTextEmbedding("search engine").Span,
            fp16.GetTextEmbedding("search engine").Span,
            tolerance: 1e-3f);
    }

    [Fact]
    public void Encoder_AcceptsAnAlreadyPooledOutput()
    {
        using var encoder = new OnnxTextEncoder(Options("tiny-encoder-pooled.onnx"));

        float[] values = encoder.GetTextEmbedding("hello world").ToArray();
        double norm = Math.Sqrt(values.Sum(value => (double)value * value));

        Assert.Equal(8, encoder.Dimension);
        Assert.Equal(1.0, norm, 5);
        Assert.False(values.SequenceEqual(encoder.GetTextEmbedding("search engine").ToArray()));
    }

    [Fact]
    public void Encoder_RejectsAnUnsupportedOutputType()
    {
        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => new OnnxTextEncoder(Options("tiny-encoder-int32-output.onnx")));

        Assert.Contains("Int32", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Encoder_InvokesTheSessionConfiguration()
    {
        bool configured = false;

        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            ConfigureSession = options =>
            {
                configured = true;
                options.GraphOptimizationLevel = Microsoft.ML.OnnxRuntime.GraphOptimizationLevel.ORT_ENABLE_ALL;
            },
        });

        Assert.True(configured);
        Assert.Equal(8, encoder.Dimension);
    }

    [Fact]
    public void Encoder_EnforcesTheInputCharacterCap()
    {
        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            MaxInputCharacters = 5,
        });

        Assert.Throws<ArgumentException>(() => encoder.GetTextEmbedding("hello world"));
        Assert.Throws<ArgumentException>(() => encoder.GetTextEmbeddings(["hello world"]));
    }

    [Fact]
    public void Encoder_PoolingAndNormalizationMatchTheHandComputedVector()
    {
        using var cls = new OnnxTextEncoder(Options("tiny-encoder-known.onnx"));
        using var mean = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder-known.onnx"),
            Tokenizer = Tokenizer(),
            Pooling = OnnxPooling.Mean,
        });

        // The known graph's row for id i is [i*10, i*10+1, ..., i*10+7]. "hello world" is
        // [CLS]=2, hello=4, world=5, [SEP]=3.
        AssertClose(Normalize(Row(2)), cls.GetTextEmbedding("hello world").Span);
        AssertClose(Normalize(Mean(Row(2), Row(4), Row(5), Row(3))), mean.GetTextEmbedding("hello world").Span);
    }

    [Fact]
    public void Encoder_ChunkingDoesNotChangeTheVectors()
    {
        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            MaxBatchSize = 2,
        });

        string[] texts = ["hello world", "search engine", "rank fusion", "dense retrieval", "document query"];
        IReadOnlyList<ReadOnlyMemory<float>> batch = encoder.GetTextEmbeddings(texts);

        for (int i = 0; i < texts.Length; i++)
            AssertClose(encoder.GetTextEmbedding(texts[i]).Span, batch[i].Span);
    }

    [Fact]
    public void Encoder_RejectsANonPositiveBatchSize()
    {
        Assert.Throws<ArgumentException>(() => new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            MaxBatchSize = 0,
        }));
    }

    [Fact]
    public async Task Encoder_DoesNotCrashWhenDisposedDuringUse()
    {
        var encoder = new OnnxTextEncoder(Options());

        Task[] workers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 20; i++)
                {
                    try
                    {
                        encoder.GetTextEmbedding("hello world");
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                }
            }))
            .ToArray();

        encoder.Dispose();
        await Task.WhenAll(workers);

        Assert.Throws<ObjectDisposedException>(() => encoder.GetTextEmbedding("hello"));
    }

    [Fact]
    public void Encoder_ObservesAPreCanceledToken()
    {
        using var encoder = new OnnxTextEncoder(Options());

        Assert.Throws<OperationCanceledException>(
            () => encoder.GetTextEmbedding("hello world", EmbeddingUse.Passage, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Encoder_WarmupDoesNotChangeResults()
    {
        using var encoder = new OnnxTextEncoder(Options());
        float[] before = encoder.GetTextEmbedding("hello world").ToArray();

        encoder.Warmup();

        AssertClose(before, encoder.GetTextEmbedding("hello world").Span);
    }

    [Fact]
    public void Encoder_SignatureIsStableForTheSamePipeline()
    {
        using var first = new OnnxTextEncoder(Options());
        using var second = new OnnxTextEncoder(Options());

        Assert.Equal(first.EmbeddingSignature, second.EmbeddingSignature);
        Assert.False(string.IsNullOrEmpty(first.EmbeddingSignature));
    }

    [Fact]
    public void Encoder_SignatureChangesWithPreprocessing()
    {
        using var cls = new OnnxTextEncoder(Options());
        using var mean = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            Pooling = OnnxPooling.Mean,
        });

        Assert.NotEqual(cls.EmbeddingSignature, mean.EmbeddingSignature);
    }

    [Fact]
    public void Encoder_SignatureRefusesAnIndexFromAnotherPipeline()
    {
        using var cls = new OnnxTextEncoder(Options());
        using var mean = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Fixture("tiny-encoder.onnx"),
            Tokenizer = Tokenizer(),
            Pooling = OnnxPooling.Mean,
        });

        var source = new InMemoryVectorSearchEngine(cls);
        source.Index([new SearchDocument("1", "hello world")]);
        var target = new InMemoryVectorSearchEngine(mean);

        Assert.Throws<InvalidOperationException>(() => target.ImportSnapshot(source.ExportSnapshot()));
    }

    [Fact]
    public void Encoder_ThrowsAfterDispose()
    {
        var encoder = new OnnxTextEncoder(Options());
        encoder.Dispose();

        Assert.Throws<ObjectDisposedException>(() => encoder.GetTextEmbedding("hello"));
    }
}
