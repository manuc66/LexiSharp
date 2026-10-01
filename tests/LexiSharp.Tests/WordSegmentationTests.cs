using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="WordSegmentation.UnicodeWordBoundaries"/>, on its own.
/// </summary>
/// <remarks>
/// Every expectation here was established by tokenizing the two sides with a separator between them
/// against an implementation that segments on these boundaries — not read out of a specification and
/// not assumed. The rule is not "these characters join words": a comma joins two digits and splits two
/// letters, a colon does the reverse, and a full stop or an apostrophe does both. A flat character
/// set cannot express that, and getting one word in a million wrong still moves document frequencies,
/// which moves every score.
/// </remarks>
public class WordSegmentationTests
{
    private static Tokenizer Segmenter() => new(new TokenizerOptions
    {
        KeepSingleCharTerms = true,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
    });

    private static IReadOnlyList<string> Joined(string left, string separator, string right) =>
        Segmenter().Tokenize(left + separator + right);

    private static void AssertSingle(string left, string separator, string right)
    {
        IReadOnlyList<string> terms = Joined(left, separator, right);

        Assert.True(
            terms.Count == 1,
            $"'{left}{separator}{right}' gave {terms.Count} terms ({string.Join("|", terms)}), expected one.");
    }

    private static void AssertSplit(string left, string separator, string right)
    {
        IReadOnlyList<string> terms = Joined(left, separator, right);

        Assert.True(
            terms.Count == 2,
            $"'{left}{separator}{right}' gave {terms.Count} terms ({string.Join("|", terms)}), expected two.");
    }

    // Between digits: comma and semicolon join, full stop joins, colon splits.

    [Fact]
    public void BetweenDigitsACommaSemicolonAndFullStopJoin()
    {
        AssertSingle("1", ",", "2");
        AssertSingle("12", ";", "34");
        AssertSingle("100", ".", "5");
    }

    [Fact]
    public void BetweenDigitsAColonDoesNotJoin()
    {
        AssertSplit("01", ":", "12");
    }

    // Between letters: colon joins, comma and semicolon split.

    [Fact]
    public void BetweenLettersAColonJoins()
    {
        AssertSingle("cost", ":", "benefit");
    }

    [Fact]
    public void BetweenLettersACommaAndSemicolonDoNotJoin()
    {
        AssertSplit("alpha", ",", "beta");
        AssertSplit("alpha", ";", "beta");
    }

    [Fact]
    public void BetweenLettersAFullStopAndApostrophesJoin()
    {
        AssertSingle("data", ".", "worldbank");
        AssertSingle("don", "'", "t");
        AssertSingle("don", "’", "t");
    }

    // Across the classes, nothing joins except the unconditional pair.

    [Fact]
    public void AcrossDigitsAndLettersNothingJoins()
    {
        foreach (string separator in new[] { ".", ",", ":", ";", "'", "’" })
        {
            AssertSplit("1", separator, "accept");
            AssertSplit("accept", separator, "1");
        }
    }

    [Fact]
    public void TheMeasuredCaseThatMotivatedThisIsNowRight()
    {
        // A file name: digits, a dot, letters. The reference index holds `0335204279` and `pdf` as two
        // terms; a flat joiner set holds `0335204279.pdf`, which changes that file's document
        // frequency and therefore every score that weights it.
        Assert.Equal(["0335204279", "pdf"], Segmenter().Tokenize("0335204279.pdf"));
    }

    [Fact]
    public void AndTheNumericOneIsStillRight()
    {
        // The same reference index holds `1,2,3` as one term.
        Assert.Equal(["lives", "1,2,3"], Segmenter().Tokenize("lives.1,2,3"));
    }

    [Fact]
    public void UnderscoreAndSoftHyphenJoinAnything()
    {
        AssertSingle("1", "_", "2");
        AssertSingle("1", "_", "b");
        AssertSingle("a", "_", "1");
        AssertSingle("co", "­", "operate");
    }

    [Fact]
    public void TheHyphenNeverJoins()
    {
        AssertSplit("environment", "-", "friendly");
        AssertSplit("1", "-", "2");
        AssertSplit("1013", "-", "1014");
    }

    [Fact]
    public void SeparatorsOutsideTheMeasuredSetNeverJoin()
    {
        foreach (string separator in new[] { "/", "@", "&", "+", "!", "?", "#", "%" })
        {
            AssertSplit("ab", separator, "cd");
        }
    }

    [Fact]
    public void AJoinerAtEitherEndOfTheTextDoesNotJoin()
    {
        Assert.Equal(["ab"], Segmenter().Tokenize(".ab"));
        Assert.Equal(["ab"], Segmenter().Tokenize("ab."));
        Assert.Equal(["ab", "cd"], Segmenter().Tokenize("ab,cd"));
    }

    /// <summary>
    /// The flat set is unchanged: this option is additive, and a caller who configured explicit
    /// joiners keeps exactly what they asked for.
    /// </summary>
    [Fact]
    public void TheFlatSetIsUnaffectedByTheOption()
    {
        var flat = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true, WordJoiners = ".," });

        Assert.Equal(["1,2"], flat.Tokenize("1,2"));
        Assert.Equal(["0335204279.pdf"], flat.Tokenize("0335204279.pdf"));
    }

    [Fact]
    public void FlatIsTheDefault()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true });

        Assert.Equal(WordSegmentation.Flat, TokenizerOptions.Default.WordSegmentation);
        Assert.Equal(["1", "2", "3"], tokenizer.Tokenize("1,2,3"));
    }
}