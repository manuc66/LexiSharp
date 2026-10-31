using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

/// <summary>
/// Opt-in compatibility checks against a real export. They are skipped without
/// <c>LEXISHARP_ONNX_MODEL_DIR</c>, so CI never downloads a checkpoint; run
/// <c>fixtures/fetch-minilm.sh</c> and point the variable at its output to execute them.
/// </summary>
public sealed class RealModelTests
{
    private static string? ModelDirectory => Environment.GetEnvironmentVariable("LEXISHARP_ONNX_MODEL_DIR");

    [SkippableFact]
    public void MiniLm_EncodesWithMeanPoolingAndNormalization()
    {
        Skip.If(string.IsNullOrWhiteSpace(ModelDirectory), "LEXISHARP_ONNX_MODEL_DIR is not set.");

        string model = Path.Combine(ModelDirectory!, "model.onnx");
        string vocabulary = Path.Combine(ModelDirectory!, "vocab.txt");

        Skip.If(!File.Exists(model), $"No model.onnx under {ModelDirectory}.");
        Skip.If(!File.Exists(vocabulary), $"No vocab.txt under {ModelDirectory}.");

        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = model,
            Tokenizer = new BertWordPieceTokenizer(vocabulary),
            Pooling = OnnxPooling.Mean,
            MaxSequenceLength = 256,
        });

        float[] vector = encoder.GetTextEmbedding("hello world").ToArray();
        double norm = Math.Sqrt(vector.Sum(value => (double)value * value));

        Assert.Equal(384, encoder.Dimension);
        Assert.Equal(1.0, norm, 4);

        string[] texts = ["hello world", "dense retrieval"];
        IReadOnlyList<ReadOnlyMemory<float>> batch = encoder.GetTextEmbeddings(texts);

        Assert.Equal(2, batch.Count);

        for (int i = 0; i < texts.Length; i++)
            Assert.Equal(encoder.GetTextEmbedding(texts[i]).ToArray(), batch[i].ToArray());
    }

    private static (string Text, float[] Vector) Reference(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", name);
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;

        string text = root.GetProperty("text").GetString()!;
        float[] vector = root.GetProperty("vector")
            .EnumerateArray()
            .Select(value => value.GetSingle())
            .ToArray();

        return (text, vector);
    }

    private static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        Assert.Equal(left.Length, right.Length);

        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (int i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }

        return dot / Math.Sqrt(leftNorm * rightNorm);
    }

    [SkippableFact]
    public void MiniLm_MatchesTheIndependentReferenceVector()
    {
        Skip.If(string.IsNullOrWhiteSpace(ModelDirectory), "LEXISHARP_ONNX_MODEL_DIR is not set.");

        string model = Path.Combine(ModelDirectory!, "model.onnx");
        string vocabulary = Path.Combine(ModelDirectory!, "vocab.txt");

        Skip.If(!File.Exists(model) || !File.Exists(vocabulary), "The MiniLM export is incomplete.");

        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = model,
            Tokenizer = new BertWordPieceTokenizer(vocabulary),
            Pooling = OnnxPooling.Mean,
            MaxSequenceLength = 256,
        });

        (string text, float[] reference) = Reference("reference-minilm.json");

        Assert.True(
            Cosine(encoder.GetTextEmbedding(text).Span, reference) > 0.9999,
            "The package embedding does not match the independent tokenizers+ORT reference.");
    }

    [SkippableFact]
    public void E5_MatchesTheIndependentReferenceVector()
    {
        string? directory = Environment.GetEnvironmentVariable("LEXISHARP_ONNX_E5_DIR");
        Skip.If(string.IsNullOrWhiteSpace(directory), "LEXISHARP_ONNX_E5_DIR is not set.");

        string model = Path.Combine(directory!, "model.onnx");
        string sentencePiece = Path.Combine(directory!, "sentencepiece.bpe.model");
        Skip.If(!File.Exists(model) || !File.Exists(sentencePiece), "The E5 export is incomplete.");

        int unknown = new SentencePieceOnnxTokenizer(sentencePiece).UnknownId;

        var tokenizer = new SentencePieceOnnxTokenizer(sentencePiece, new SentencePieceOnnxTokenizerOptions
        {
            IdMap = id => id == unknown ? 3 : id + 1,
            PrefixId = 0,
            SuffixId = 2,
        });

        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = model,
            Tokenizer = tokenizer,
            Pooling = OnnxPooling.Mean,
            MaxSequenceLength = 512,
            QueryPrefix = "query: ",
        });

        (string text, float[] reference) = Reference("reference-e5.json");

        Assert.True(
            Cosine(encoder.GetTextEmbedding(text, EmbeddingUse.Query).Span, reference) > 0.9999,
            "The package embedding does not match the independent tokenizers+ORT reference.");
    }

    [SkippableFact]
    public void Gpt2_TokenizerJsonMatchesTheReferenceIds()
    {
        string? path = Environment.GetEnvironmentVariable("LEXISHARP_ONNX_GPT2_TOKENIZER");
        Skip.If(string.IsNullOrWhiteSpace(path), "LEXISHARP_ONNX_GPT2_TOKENIZER is not set.");
        Skip.If(!File.Exists(path), $"No tokenizer.json at {path}.");

        var tokenizer = new BpeOnnxTokenizer(path!);

        Assert.Equal(
            new[] { 31373, 995 },
            tokenizer.Encode("hello world", 64).Ids.ToArray());

        Assert.Equal(
            new[] { 18243, 3113, 11, 15715, 45069, 0 },
            tokenizer.Encode("Search engine, dense retrieval!", 64).Ids.ToArray());
    }

    [SkippableFact]
    public void E5_EncodesWithSentencePieceAndTheExportIdMapping()
    {
        string? directory = Environment.GetEnvironmentVariable("LEXISHARP_ONNX_E5_DIR");
        Skip.If(string.IsNullOrWhiteSpace(directory), "LEXISHARP_ONNX_E5_DIR is not set.");

        string model = Path.Combine(directory!, "model.onnx");
        string sentencePiece = Path.Combine(directory!, "sentencepiece.bpe.model");

        Skip.If(!File.Exists(model), $"No model.onnx under {directory}.");
        Skip.If(!File.Exists(sentencePiece), $"No sentencepiece.bpe.model under {directory}.");

        int unknown = new SentencePieceOnnxTokenizer(sentencePiece).UnknownId;

        var tokenizer = new SentencePieceOnnxTokenizer(sentencePiece, new SentencePieceOnnxTokenizerOptions
        {
            // The Xenova export inserts <s>, <pad>, </s>, <unk> where the SentencePiece model has
            // three specials, so content ids shift by one and the unknown id moves to 3.
            IdMap = id => id == unknown ? 3 : id + 1,
            PrefixId = 0,
            SuffixId = 2,
        });

        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = model,
            Tokenizer = tokenizer,
            Pooling = OnnxPooling.Mean,
            MaxSequenceLength = 512,
            QueryPrefix = "query: ",
            PassagePrefix = "passage: ",
        });

        float[] query = encoder.GetTextEmbedding("search engine", EmbeddingUse.Query).ToArray();
        float[] passage = encoder.GetTextEmbedding("search engine", EmbeddingUse.Passage).ToArray();
        double norm = Math.Sqrt(query.Sum(value => (double)value * value));

        Assert.Equal(384, encoder.Dimension);
        Assert.Equal(1.0, norm, 4);
        Assert.False(query.SequenceEqual(passage));

        string[] texts = ["search engine", "dense retrieval"];
        IReadOnlyList<ReadOnlyMemory<float>> batch = encoder.GetTextEmbeddings(texts, EmbeddingUse.Passage);

        for (int i = 0; i < texts.Length; i++)
            Assert.Equal(encoder.GetTextEmbedding(texts[i], EmbeddingUse.Passage).ToArray(), batch[i].ToArray());
    }
}
