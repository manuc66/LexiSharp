using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Under <see cref="WordSegmentation.UnicodeWordBoundaries"/> a format character does not end the
/// word it sits in, and does not open one of its own.
/// </summary>
/// <remarks>
/// The reference's word-break data classes a byte-order mark, a left-to-right mark and a soft hyphen
/// as <c>Format</c>, which a word continues through. FiQA holds <c>199</c> + U+FEFF + <c>0</c> and
/// <c>bank</c> + U+200E, both one term in the reference and two here before this rule.
/// <para>
/// The exception is U+200B, the zero-width space: its word-break class is <c>Other</c>, and the
/// reference splits a word around it. Treating every <c>Format</c> character the same would merge
/// 145 of FiQA's word boundaries.
/// </para>
/// </remarks>
public class FormatCharacterTests
{
    private const string Bom = "\uFEFF";
    private const string LeftToRightMark = "\u200E";
    private const string ZeroWidthSpace = "\u200B";
    private const string SoftHyphen = "\u00AD";

    private static Tokenizer Segmenter() => new(new TokenizerOptions
    {
        KeepSingleCharTerms = true,
        FoldDiacritics = false,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
    });

    [Fact]
    public void AFormatCharacterInsideAWordStaysInTheTerm()
    {
        Assert.Equal(["199" + Bom + "0"], Segmenter().Tokenize("199" + Bom + "0"));
        Assert.Equal(["bank" + LeftToRightMark], Segmenter().Tokenize("bank" + LeftToRightMark));
        Assert.Equal(["x" + LeftToRightMark + "y"], Segmenter().Tokenize("x" + LeftToRightMark + "y"));
    }

    [Fact]
    public void AFormatCharacterCannotOpenAWord()
    {
        Assert.Equal(["ab"], Segmenter().Tokenize(Bom + "ab"));
        Assert.Equal(["ab"], Segmenter().Tokenize(LeftToRightMark + "ab"));
        Assert.Empty(Segmenter().Tokenize(Bom));
    }

    [Fact]
    public void AFormatCharacterDoesNotChangeTheWordClassForASeparator()
    {
        Assert.Equal(["bank" + LeftToRightMark + ".foo"], Segmenter().Tokenize("bank" + LeftToRightMark + ".foo"));
    }

    /// <summary>
    /// The zero-width space is <c>Format</c> by category and <c>Other</c> by word-break class, and
    /// the reference splits on it. This is the pin that keeps the rule from being a category test.
    /// </summary>
    [Fact]
    public void AZeroWidthSpaceStillSplits()
    {
        Assert.Equal(["a", "b"], Segmenter().Tokenize("a" + ZeroWidthSpace + "b"));
        Assert.Equal(["a", "b", "c"], Segmenter().Tokenize("a" + ZeroWidthSpace + "b" + ZeroWidthSpace + "c"));
    }

    /// <summary>
    /// The soft hyphen was already a joiner before this rule, and it still is; this checks that the
    /// format-character rule did not change what the soft hyphen does at the end of a word.
    /// </summary>
    [Fact]
    public void ASoftHyphenStillJoinsAndStaysAtTheEnd()
    {
        Assert.Equal(["co" + SoftHyphen + "operate"], Segmenter().Tokenize("co" + SoftHyphen + "operate"));
        Assert.Equal(["bank" + SoftHyphen], Segmenter().Tokenize("bank" + SoftHyphen));
    }
}
