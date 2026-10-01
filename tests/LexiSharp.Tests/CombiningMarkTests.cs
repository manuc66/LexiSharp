using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A combining mark continues the word it follows and cannot open one.
/// </summary>
/// <remarks>
/// Measured against the reference implementation's tokenizer, over the four positions a mark can occupy.
/// The asymmetry is the content of the rule: <c>a◌̇b</c> is one token and <c>a◌̇</c> is one token, but
/// <c>◌̇ab</c> is just <c>ab</c>, and a lone mark between spaces produces nothing. A mark modifies the
/// character it follows, so it extends a word and cannot begin one.
/// <para>
/// The effect on the corpora in this repository is small and stated rather than implied: ArguAna holds no
/// combining mark at all, SciFact none either, NFCorpus four characters out of 5,779,318. It is worth
/// having because a scanner that accepts only letters and digits splits one word into two, and a word is
/// the unit everything downstream counts.
/// </para>
/// </remarks>
public class CombiningMarkTests
{
    private static Tokenizer Tokenizing(WordSegmentation segmentation = WordSegmentation.UnicodeWordBoundaries) =>
        new(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            FoldDiacritics = false,
            WordSegmentation = segmentation,
        });

    private const string Dot = "̇";    // U+0307, combining dot above
    private const string Breve = "̆";  // U+0306, combining breve
    private const string Bar = "ͣ";   // U+0363, combining latin small letter a

    [Theory]
    [InlineData(WordSegmentation.UnicodeWordBoundaries)]
    [InlineData(WordSegmentation.Flat)]
    public void AMarkBetweenLettersDoesNotEndTheWord(WordSegmentation segmentation)
    {
        var tokenizer = Tokenizing(segmentation);

        Assert.Equal(["a" + Dot + "b"], tokenizer.Tokenize("a" + Dot + "b"));
        Assert.Equal(["a" + Breve + "b"], tokenizer.Tokenize("a" + Breve + "b"));
        Assert.Equal(["a" + Bar + "b"], tokenizer.Tokenize("a" + Bar + "b"));
    }

    [Fact]
    public void AMarkAtTheEndOfAWordStillBelongsToIt()
    {
        Assert.Equal(["a" + Dot], Tokenizing().Tokenize("a" + Dot));
        Assert.Equal(["celi" + Dot + "l"], Tokenizing().Tokenize("celi" + Dot + "l"));
    }

    /// <summary>
    /// The half of the rule that is easy to get backwards: a mark cannot <b>open</b> a token. It is not a
    /// word character, so leading one is dropped rather than attached.
    /// </summary>
    [Fact]
    public void AMarkCannotOpenAWord()
    {
        Assert.Equal(["ab"], Tokenizing().Tokenize(Dot + "ab"));
        Assert.Equal(["ab"], Tokenizing().Tokenize(Dot + Dot + "ab"));
        Assert.Empty(Tokenizing().Tokenize(Dot));
    }

    /// <summary>
    /// A mark followed by punctuation is still part of the word, and the punctuation is not: the word ends
    /// where the mark ends. Measured on <c>a◌̇-</c>, which yields <c>a◌̇</c>.
    /// </summary>
    [Fact]
    public void AMarkDoesNotMakePunctuationPartOfTheWord()
    {
        Assert.Equal(["a" + Dot], Tokenizing().Tokenize("a" + Dot + "-"));
        Assert.Equal(["9" + Dot + "9"], Tokenizing().Tokenize("9" + Dot + "9"));
    }

    /// <summary>
    /// The control that shows this is about marks and not about non-ASCII in general: a precomposed letter
    /// was one token before any of this, and has to stay one.
    /// </summary>
    [Fact]
    public void PrecomposedLettersAreUnaffected()
    {
        var tokenizer = Tokenizing();

        Assert.Equal(["école"], tokenizer.Tokenize("école"));
        Assert.Equal(["क़"], tokenizer.Tokenize("क़"));
        Assert.Equal(["ẋ"], tokenizer.Tokenize("ẋ"));
    }
}