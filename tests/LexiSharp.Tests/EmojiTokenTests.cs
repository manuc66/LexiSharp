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
/// surrogate halves, and a sequence is one term too: a ZWJ family, a flag pair, a skin-tone
/// modifier, a keycap and a tag sequence were each measured against the reference's tokenizer.
/// A sequence's token absorbs the <c>Extend</c>/<c>Format</c> characters after it, including a
/// <c>ZWJ</c> that joins nothing.
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

        // The family is one term: the surrogate pairs are not split, and neither are the ZWJ
        // elements. Measured against the reference tokenizer, which emits the whole sequence.
        Assert.Equal(["👨‍👩‍👧‍👦", "🤔"], terms);
    }

    [Theory]
    [InlineData("👨‍👩‍👧‍👦")]
    [InlineData("🇫🇷")]
    [InlineData("👍🏽")]
    [InlineData("1️⃣")]
    [InlineData("#️⃣")]
    [InlineData("🏳️‍🌈")]
    [InlineData("🏴\U000E0067\U000E0062\U000E0073\U000E0063\U000E0074\U000E007F")]
    public void AnEmojiSequenceIsOneTerm(string sequence)
    {
        Assert.Equal([sequence], Segmenter().Tokenize(sequence));
    }

    [Fact]
    public void TwoSequencesAreTwoTerms()
    {
        Assert.Equal(["🇫🇷", "🇩🇪"], Segmenter().Tokenize("🇫🇷🇩🇪"));
        Assert.Equal(["#️⃣", "*️⃣"], Segmenter().Tokenize("#️⃣*️⃣"));
        Assert.Equal(["👍🏽", "👍🏿"], Segmenter().Tokenize("👍🏽👍🏿"));
    }

    [Fact]
    public void ALoneRegionalIndicatorIsNotATerm()
    {
        Assert.Empty(Segmenter().Tokenize("🇫"));
        Assert.Equal(["z"], Segmenter().Tokenize("🇫z"));
    }

    [Fact]
    public void AZwjThatJoinsNothingStaysInTheToken()
    {
        Assert.Equal(["👨‍", "z"], Segmenter().Tokenize("👨‍z"));
    }

    [Fact]
    public void AnEmojiSequenceAbsorbsTheVariationSelector()
    {
        Assert.Equal(["©️"], Segmenter().Tokenize("©️"));
        Assert.Equal(["❤️"], Segmenter().Tokenize("❤️"));
        Assert.Equal(["🇫🇷️"], Segmenter().Tokenize("🇫🇷️"));
    }

    [Fact]
    public void AModifierWithoutABaseIsItsOwnTerm()
    {
        Assert.Equal(["🏽"], Segmenter().Tokenize("🏽"));
    }

    [Fact]
    public void AZwjSequenceKeepsTheModifierWithItsBase()
    {
        Assert.Equal(["👩🏽‍🚀"], Segmenter().Tokenize("👩🏽‍🚀"));
    }

    [Fact]
    public void AKeycapWithoutTheSelectorIsStillOneTerm()
    {
        Assert.Equal(["#⃣"], Segmenter().Tokenize("#⃣"));
    }

    [Fact]
    public void AHashAloneOrWithOnlyASelectorIsNotATerm()
    {
        Assert.Empty(Segmenter().Tokenize("#"));
        Assert.Empty(Segmenter().Tokenize("#️"));
        Assert.Equal(["z"], Segmenter().Tokenize("#z"));
    }

    [Fact]
    public void AnEmojiBesideAWordDoesNotJoinIt()
    {
        Assert.Equal(["a", "👨‍👩", "z"], Segmenter().Tokenize("a👨‍👩z"));
    }

    [Fact]
    public void AConfiguredSymbolAbsorbsTheSelectorAfterIt()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            FoldDiacritics = false,
            WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
            SymbolTerms = "©®™↔",
        });

        Assert.Equal(["↔️"], tokenizer.Tokenize("↔️"));
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
