using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

public sealed class BpeOnnxTokenizerTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static BpeOnnxTokenizer Tokenizer(BpeOnnxTokenizerOptions? options = null) =>
        new(Fixture("tiny-bpe.json"), options);

    [Fact]
    public void Encode_ReadsTheTokenizerJson()
    {
        // Ids produced by the Python `tokenizers` library that trained the fixture.
        Assert.Equal(
            new[] { 72, 69, 76, 76, 79, 221, 87, 79, 82, 76, 68 },
            Tokenizer().Encode("hello world", 32).Ids.ToArray());
    }

    [Fact]
    public void Encode_TruncatesToTheCap()
    {
        Assert.Equal(4, Tokenizer().Encode("hello world", 4).Length);
    }

    [Fact]
    public void Encode_AppliesATemplatePostProcessor()
    {
        Assert.Equal(
            new[] { 257, 72, 69, 76, 76, 79, 221, 87, 79, 82, 76, 68, 258 },
            new BpeOnnxTokenizer(Fixture("tiny-bpe-post.json")).Encode("hello world", 64).Ids.ToArray());
    }

    [Fact]
    public void EncodePair_AppliesAPairTemplate()
    {
        OnnxTokenizedText encoded = new BpeOnnxTokenizer(Fixture("tiny-bpe-post.json"))
            .EncodePair("hello", "world", 64, 32);

        Assert.Equal(
            new[] { 257, 72, 69, 76, 76, 79, 258, 258, 87, 79, 82, 76, 68, 258 },
            encoded.Ids.ToArray());
        Assert.All(encoded.TypeIds.ToArray(), type => Assert.Equal(0, type));
    }

    [Fact]
    public void Encode_RejectsACapBelowTheTemplateSpecialTokens()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new BpeOnnxTokenizer(Fixture("tiny-bpe-post.json")).Encode("hello world", 1));
    }

    [Fact]
    public void Constructor_RejectsAnUnknownPostProcessor()
    {
        Assert.Throws<NotSupportedException>(
            () => new BpeOnnxTokenizer(Fixture("tiny-bpe-unknown-post.json")));
    }

    [Fact]
    public void EncodePair_WithoutASeparatorSaysSo()
    {
        Assert.Throws<NotSupportedException>(() => Tokenizer().EncodePair("hello", "world", 16, 8));
    }

    [Fact]
    public void EncodePair_WithASeparatorMarksTheDocumentSegment()
    {
        OnnxTokenizedText encoded = Tokenizer(new BpeOnnxTokenizerOptions { PairSeparatorId = 221 })
            .EncodePair("hello", "world", 16, 8);

        int separator = Array.IndexOf(encoded.Ids.ToArray(), 221);

        Assert.InRange(separator, 1, encoded.Length - 2);
        Assert.All(encoded.TypeIds.ToArray().Take(separator + 1), type => Assert.Equal(0, type));
        Assert.All(encoded.TypeIds.ToArray().Skip(separator + 1), type => Assert.Equal(1, type));
    }

    [Fact]
    public void Encode_InfersNonByteLevelFromThePreTokenizer()
    {
        Assert.Equal(
            new[] { 49, 51 },
            new BpeOnnxTokenizer(Fixture("tiny-bpe-whitespace.json")).Encode("hello world", 64).Ids.ToArray());
    }

    [Fact]
    public void Encode_ExplicitByteLevelWins()
    {
        OnnxTokenizedText inferred = new BpeOnnxTokenizer(Fixture("tiny-bpe.json")).Encode("hello world", 64);
        OnnxTokenizedText forced = new BpeOnnxTokenizer(
            Fixture("tiny-bpe.json"),
            new BpeOnnxTokenizerOptions { ByteLevel = false }).Encode("hello world", 64);

        Assert.NotEqual(inferred.Ids.ToArray(), forced.Ids.ToArray());
    }

    [Fact]
    public void Encode_AppliesARobertaPostProcessor()
    {
        var tokenizer = new BpeOnnxTokenizer(Fixture("tiny-bpe-roberta-post.json"));

        Assert.Equal(
            new[] { 257, 72, 69, 76, 76, 79, 221, 87, 79, 82, 76, 68, 258 },
            tokenizer.Encode("hello world", 64).Ids.ToArray());

        Assert.Equal(
            new[] { 257, 72, 69, 76, 76, 79, 258, 258, 87, 79, 82, 76, 68, 258 },
            tokenizer.EncodePair("hello", "world", 64, 32).Ids.ToArray());
    }

    [Fact]
    public void Encode_AppliesABertPostProcessorWithDocumentTypes()
    {
        OnnxTokenizedText encoded = new BpeOnnxTokenizer(Fixture("tiny-bpe-bert-post.json"))
            .EncodePair("hello", "world", 64, 32);

        Assert.Equal(
            new[] { 257, 72, 69, 76, 76, 79, 258, 87, 79, 82, 76, 68, 258 },
            encoded.Ids.ToArray());
        Assert.Equal(0, encoded.TypeIds.Span[0]);
        Assert.Equal(1, encoded.TypeIds.Span[^2]);
    }

    [Fact]
    public void Encode_RejectsAMalformedPostProcessorStep()
    {
        Assert.Throws<NotSupportedException>(
            () => new BpeOnnxTokenizer(Fixture("tiny-bpe-bogus-step.json")).Encode("hello", 8));
    }

    [Fact]
    public void Encode_InfersByteLevelWithoutAPreTokenizer()
    {
        Assert.Equal(
            new[] { 257, 72, 69, 76, 76, 79, 221, 87, 79, 82, 76, 68, 258 },
            new BpeOnnxTokenizer(Fixture("tiny-bpe-no-pre.json")).Encode("hello world", 64).Ids.ToArray());
    }

    [Fact]
    public void Encode_InfersByteLevelFromASequencePreTokenizer()
    {
        Assert.Equal(
            new[] { 257, 72, 69, 76, 76, 79, 221, 87, 79, 82, 76, 68, 258 },
            new BpeOnnxTokenizer(Fixture("tiny-bpe-sequence-pre.json")).Encode("hello world", 64).Ids.ToArray());
    }

    [Fact]
    public void Constructor_RejectsAModelWithoutAVocabulary()
    {
        Assert.Throws<InvalidOperationException>(
            () => new BpeOnnxTokenizer(Fixture("tiny-bpe-no-vocab.json")));
    }

    [Fact]
    public void Constructor_RejectsAnEmptyPath()
    {
        Assert.Throws<ArgumentException>(() => new BpeOnnxTokenizer(" "));
    }

    [Fact]
    public void Fingerprint_IsExposed()
    {
        Assert.False(string.IsNullOrEmpty(new BpeOnnxTokenizer(Fixture("tiny-bpe.json")).Fingerprint));
    }

    [Fact]
    public void Constructor_RejectsANonBpeModel()
    {
        Assert.Throws<NotSupportedException>(() => new BpeOnnxTokenizer(Fixture("tiny-wordpiece-tokenizer.json")));
    }
}
