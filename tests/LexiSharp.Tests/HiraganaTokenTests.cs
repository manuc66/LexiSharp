using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Under <see cref="WordSegmentation.UnicodeWordBoundaries"/> a Hiragana character is a term of its
/// own, while a Katakana run stays one term.
/// </summary>
/// <remarks>
/// The reference's grammar has one state per script: <c>&lt;HIRAGANA&gt;</c> emits a single
/// Hiragana character, <c>&lt;KATAKANA&gt;</c> a sequence. Both were measured against Lucene
/// 10.5.2's <c>EnglishAnalyzer</c>: <c>ひらがな</c> is four tokens, <c>カタカナ</c> is one, and
/// <c>ひらカナ</c> is three.
/// <para>
/// The classes also decide joins. Hiragana and Katakana are not <c>ALetter</c> in the reference's
/// word-break data, so the separators that join two letters do not join them: <c>q·ひら</c>,
/// <c>q·カナ</c> and <c>カ·ナ</c> are all split.
/// </para>
/// </remarks>
public class HiraganaTokenTests
{
    private static Tokenizer Segmenter() => new(new TokenizerOptions
    {
        KeepSingleCharTerms = true,
        FoldDiacritics = false,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
    });

    [Fact]
    public void EachHiraganaCharacterIsATerm()
    {
        Assert.Equal(["ひ", "ら", "が", "な"], Segmenter().Tokenize("ひらがな"));
        Assert.Equal(["き", "ょ", "う"], Segmenter().Tokenize("きょう"));
        Assert.Equal(["ゝ", "ゞ"], Segmenter().Tokenize("ゝゞ"));
    }

    [Fact]
    public void AKatakanaRunIsOneTerm()
    {
        Assert.Equal(["カタカナ"], Segmenter().Tokenize("カタカナ"));
        Assert.Equal(["カタカナー"], Segmenter().Tokenize("カタカナー"));
        Assert.Equal(["ｶﾀｶﾅ"], Segmenter().Tokenize("ｶﾀｶﾅ"));
    }

    [Fact]
    public void AMixedKanaRunSplitsOnlyAtTheHiragana()
    {
        Assert.Equal(["ひ", "ら", "カナ"], Segmenter().Tokenize("ひらカナ"));
        Assert.Equal(["カナ", "ひ", "ら"], Segmenter().Tokenize("カナひら"));
    }

    [Fact]
    public void TheProlongedSoundMarkIsKatakanaAndStandsAloneNextToHiragana()
    {
        Assert.Equal(["ひ", "ら", "ー"], Segmenter().Tokenize("ひらー"));
        Assert.Equal(["ー", "ひ", "ら"], Segmenter().Tokenize("ーひら"));
    }

    [Fact]
    public void ACombiningMarkStaysInItsHiraganaTerm()
    {
        Assert.Equal(["が"], Segmenter().Tokenize("が"));
    }

    [Fact]
    public void ASeparatorThatJoinsTwoLettersDoesNotJoinKana()
    {
        Assert.Equal(["q", "ひ", "ら"], Segmenter().Tokenize("q·ひら"));
        Assert.Equal(["q", "カナ"], Segmenter().Tokenize("q·カナ"));
        Assert.Equal(["カ", "ナ"], Segmenter().Tokenize("カ·ナ"));
    }
}
