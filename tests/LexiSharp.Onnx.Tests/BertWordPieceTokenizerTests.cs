using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Onnx.Tests;

public sealed class BertWordPieceTokenizerTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    private static BertWordPieceTokenizer Tokenizer() => new(Fixture("vocab.txt"));

    [Fact]
    public void WordPiece_SplitsIntoContinuations()
    {
        Assert.Equal(new[] { 6, 16 }, Tokenizer().WordPieceIds("searching"));
    }

    [Fact]
    public void Encode_WrapsWithSpecialTokens()
    {
        OnnxTokenizedText encoded = Tokenizer().Encode("hello world", 8);

        Assert.Equal(new[] { 2, 4, 5, 3 }, encoded.Ids.ToArray());
        Assert.False(encoded.HasTypeIds);
    }

    [Fact]
    public void Encode_TruncatesToTheCap()
    {
        OnnxTokenizedText encoded = Tokenizer().Encode("hello world hello world hello", 4);

        Assert.Equal(4, encoded.Length);
        Assert.Equal(2, encoded.Ids.Span[0]);
        Assert.Equal(3, encoded.Ids.Span[^1]);
    }

    [Fact]
    public void EncodePair_MarksTheDocumentSegment()
    {
        OnnxTokenizedText encoded = Tokenizer().EncodePair("search", "engine", 8, 4);

        Assert.Equal(new[] { 2, 6, 3, 7, 3 }, encoded.Ids.ToArray());
        Assert.Equal(new[] { 0, 0, 0, 1, 1 }, encoded.TypeIds.ToArray());
    }

    [Fact]
    public void EncodePair_TruncatesTheDocumentFromItsStart()
    {
        OnnxTokenizedText encoded = Tokenizer().EncodePair("search", "hello world hello world", 6, 2);

        Assert.Equal(6, encoded.Length);
        Assert.Equal(3, encoded.Ids.Span[^1]);
    }

    [Fact]
    public void Encode_RejectsACapThatCannotHoldTheSpecialTokens()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Tokenizer().Encode("hello", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Tokenizer().EncodePair("a", "b", 2, 1));
    }

    [Fact]
    public void Fingerprint_IsStableAndExposed()
    {
        Assert.Equal(Tokenizer().Fingerprint, Tokenizer().Fingerprint);
        Assert.False(string.IsNullOrEmpty(Tokenizer().Fingerprint));
    }

    [Fact]
    public void TheDefaultTokenizerFingerprintIsNull()
    {
        IOnnxTokenizer custom = new MinimalTokenizer();

        Assert.Null(custom.Fingerprint);
    }

    private sealed class MinimalTokenizer : IOnnxTokenizer
    {
        public OnnxTokenizedText Encode(string text, int maxSequenceLength) => new([1]);

        public OnnxTokenizedText EncodePair(string query, string document, int maxSequenceLength, int maxQueryTokens) => new([1]);
    }

    [Fact]
    public void Constructor_RejectsAVocabularyWithoutTheSpecialTokens()
    {
        Assert.Throws<InvalidOperationException>(() => new BertWordPieceTokenizer(["hello", "world"]));
    }

    [Fact]
    public void CasedOption_KeepsTheOriginalCase()
    {
        var cased = new BertWordPieceTokenizer(
            Fixture("vocab.txt"),
            new BertWordPieceOptions { LowerCase = false });

        Assert.Equal(new[] { 1 }, cased.WordPieceIds("Hello"));
        Assert.Equal(new[] { 4 }, Tokenizer().WordPieceIds("Hello"));
    }

    [Fact]
    public void FoldAccentsOption_ControlsDiacriticStripping()
    {
        var literal = new BertWordPieceTokenizer(
            Fixture("vocab.txt"),
            new BertWordPieceOptions { FoldAccents = false });

        Assert.Equal(new[] { 4 }, Tokenizer().WordPieceIds("Héllo"));
        Assert.Equal(new[] { 1 }, literal.WordPieceIds("Héllo"));
    }
}
