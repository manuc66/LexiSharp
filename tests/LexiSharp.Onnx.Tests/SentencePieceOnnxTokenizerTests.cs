using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

public sealed class SentencePieceOnnxTokenizerTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static SentencePieceOnnxTokenizer Tokenizer(SentencePieceOnnxTokenizerOptions? options = null) =>
        new(Fixture("tiny-sp.model"), options);

    [Fact]
    public void Encode_ReturnsIdsInsideTheTrainedVocabulary()
    {
        OnnxTokenizedText encoded = Tokenizer().Encode("hello world", 16);

        Assert.InRange(encoded.Length, 2, 16);
        Assert.All(encoded.Ids.ToArray(), id => Assert.InRange(id, 0, 31));
        Assert.False(encoded.HasTypeIds);
    }

    [Fact]
    public void Encode_AppliesTheIdMap()
    {
        OnnxTokenizedText plain = Tokenizer().Encode("search engine", 16);
        OnnxTokenizedText shifted = Tokenizer(new SentencePieceOnnxTokenizerOptions { IdMap = id => id + 1 })
            .Encode("search engine", 16);

        Assert.Equal(plain.Ids.ToArray().Select(id => id + 1), shifted.Ids.ToArray());
    }

    [Fact]
    public void Encode_AddsTheSentenceMarkersWhenAsked()
    {
        OnnxTokenizedText encoded = Tokenizer(new SentencePieceOnnxTokenizerOptions
        {
            AddBeginningOfSentence = true,
            AddEndOfSentence = true,
        }).Encode("hello world", 16);

        Assert.Equal(2, encoded.Ids.Span[0]);
        Assert.Equal(3, encoded.Ids.Span[^1]);
    }

    [Fact]
    public void Encode_TruncatesToTheCap()
    {
        OnnxTokenizedText encoded = Tokenizer().Encode("hello world search engine", 3);

        Assert.Equal(3, encoded.Length);
    }

    [Fact]
    public void Encode_PrependsAndAppendsTheConfiguredIds()
    {
        OnnxTokenizedText encoded = Tokenizer(new SentencePieceOnnxTokenizerOptions
        {
            PrefixId = 0,
            SuffixId = 2,
        }).Encode("hello world search engine", 4);

        Assert.Equal(4, encoded.Length);
        Assert.Equal(0, encoded.Ids.Span[0]);
        Assert.Equal(2, encoded.Ids.Span[^1]);
    }

    [Fact]
    public void Encode_RejectsMappedIdsOutsideTheGraphVocabulary()
    {
        var tokenizer = Tokenizer(new SentencePieceOnnxTokenizerOptions
        {
            GraphVocabularySize = 32,
            IdMap = id => id + 100,
        });

        Assert.Throws<InvalidOperationException>(() => tokenizer.Encode("hello world", 16));
    }

    [Fact]
    public void Encode_RejectsACapBelowTheConfiguredSpecialTokens()
    {
        var tokenizer = Tokenizer(new SentencePieceOnnxTokenizerOptions { PrefixId = 0, SuffixId = 2 });

        Assert.Throws<ArgumentOutOfRangeException>(() => tokenizer.Encode("hello", 1));
    }

    [Fact]
    public void Fingerprint_IsExposed()
    {
        Assert.False(string.IsNullOrEmpty(Tokenizer().Fingerprint));
    }

    [Fact]
    public void Encode_ExposesTheUnknownId()
    {
        Assert.InRange(Tokenizer().UnknownId, 0, 31);
    }

    [Fact]
    public void EncodePair_WithoutASeparatorSaysSo()
    {
        Assert.Throws<NotSupportedException>(() => Tokenizer().EncodePair("hello", "world", 16, 8));
    }

    [Fact]
    public void EncodePair_WithASeparatorMarksTheDocumentSegment()
    {
        OnnxTokenizedText encoded = Tokenizer(new SentencePieceOnnxTokenizerOptions { PairSeparatorId = 3 })
            .EncodePair("hello", "world", 16, 8);

        int separator = Array.IndexOf(encoded.Ids.ToArray(), 3);

        Assert.InRange(separator, 1, encoded.Length - 2);
        Assert.All(encoded.TypeIds.ToArray().Take(separator + 1), type => Assert.Equal(0, type));
        Assert.All(encoded.TypeIds.ToArray().Skip(separator + 1), type => Assert.Equal(1, type));
    }
}
