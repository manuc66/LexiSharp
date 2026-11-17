using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Under <see cref="WordSegmentation.UnicodeWordBoundaries"/> an emoji code point is a term of its
/// own, and a symbol that is not emoji is not.
/// </summary>
/// <remarks>
/// The rule is the <c>Extended_Pictographic</c> property, not the <c>OtherSymbol</c> category, and
/// the difference is not academic: FiQA's index holds <c>✔</c>, <c>♦</c>, <c>☏</c> and the emoji,
/// while it holds no <c>°</c>, <c>■</c> or <c>№</c> at all. A rule written on the category emits
/// 143 terms the reference does not have.
/// <para>
/// The code point is emitted whole, so a supplementary-plane emoji is one term rather than two
/// surrogate halves. Multi-code-point emoji sequences — a ZWJ family, a flag pair — are not
/// reproduced; see the remark on <c>WordBreakClasses</c> and the plan's residual note.
/// </para>
/// </remarks>
public class EmojiTokenTests
{
    private static Tokenizer Segmenter() => new(new TokenizerOptions
    {
        KeepSingleCharTerms = true,
        FoldDiacritics = false,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
    });

    [Theory]
    [InlineData("✔")]
    [InlineData("♦")]
    [InlineData("☏")]
    [InlineData("©")]
    [InlineData("®")]
    [InlineData("™")]
    [InlineData("↔")]
    [InlineData("🤔")]
    [InlineData("😂")]
    [InlineData("👻")]
    public void AnEmojiCodePointIsATerm(string symbol)
    {
        Assert.Equal([symbol], Segmenter().Tokenize(symbol));
    }

    [Fact]
    public void ASupplementaryEmojiIsOneTermNotTwoSurrogateHalves()
    {
        IReadOnlyList<string> terms = Segmenter().Tokenize("👨‍👩‍👧‍👦 🤔");

        // The ZWJ family is four emoji code points plus three joiners and becomes four terms; the
        // point of the assertion is that the surrogate pairs are not split.
        Assert.Equal(["👨", "👩", "👧", "👦", "🤔"], terms);
    }

    [Fact]
    public void AnEmojiDoesNotExtendTheWordBesideIt()
    {
        Assert.Equal(["aspirin", "®"], Segmenter().Tokenize("Aspirin®"));
        Assert.Equal(["a", "🤔", "b"], Segmenter().Tokenize("a🤔b"));
        Assert.Equal(["2024", "©"], Segmenter().Tokenize("2024©"));
    }

    [Fact]
    public void RepeatedEmojiAreOneTermEach()
    {
        Assert.Equal(["🤔", "🤔", "🤔"], Segmenter().Tokenize("🤔🤔🤔"));
        Assert.Equal(["✔", "✔"], Segmenter().Tokenize("✔ ✔"));
    }

    /// <summary>
    /// The other half of the rule: the category is not enough. These characters are
    /// <c>OtherSymbol</c> and the reference drops them.
    /// </summary>
    [Theory]
    [InlineData("°")]
    [InlineData("¦")]
    [InlineData("●")]
    [InlineData("■")]
    [InlineData("▲")]
    [InlineData("№")]
    [InlineData("℅")]
    [InlineData("│")]
    public void ANonEmojiSymbolIsNotATerm(string symbol)
    {
        Assert.Empty(Segmenter().Tokenize(symbol));
        Assert.Equal(["a", "b"], Segmenter().Tokenize("a" + symbol + "b"));
    }
}
