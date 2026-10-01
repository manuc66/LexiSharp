using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="TokenizerOptions.WordJoiners"/> and <see cref="TokenizerOptions.FoldDiacritics"/>.
/// </summary>
/// <remarks>
/// Both change what a term <i>is</i>, and a term is the unit every document frequency, every idf and
/// therefore every score is computed from. They are opt-in because a library has no business
/// deciding that <c>don't</c> is one word rather than two, or that <c>café</c> and <c>cafe</c> are the
/// same term — but a caller comparing against a system that decided differently has to be able to
/// decide the same way, or the comparison is not a comparison of ranking.
/// </remarks>
public class WordJoinerTests
{
    private static Tokenizer Joiner(string joiners) => new(new TokenizerOptions
    {
        KeepSingleCharTerms = true,
        WordJoiners = joiners,
    });

    /// <summary>The dot and the apostrophe, which is what the published vocabulary actually contains.</summary>
    private const string Reference = ".,\u2019'";

    [Fact]
    public void TheDefaultJoinsNothing()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true });

        Assert.Equal(["don", "t"], tokenizer.Tokenize("don't"));
        Assert.Equal(["data", "worldbank", "org"], tokenizer.Tokenize("data.worldbank.org"));
    }

    [Fact]
    public void AnInteriorJoinerKeepsTheWordWhole()
    {
        var tokenizer = Joiner(Reference);

        Assert.Equal(["don't"], tokenizer.Tokenize("don't"));
        Assert.Equal(["data.worldbank.org"], tokenizer.Tokenize("data.worldbank.org"));
        Assert.Equal(["1,000km2"], tokenizer.Tokenize("1,000km2"));
    }

    [Fact]
    public void ATrailingJoinerIsPunctuationNotPartOfTheWord()
    {
        var tokenizer = Joiner(Reference);

        Assert.Equal(["hello"], tokenizer.Tokenize("hello."));
        Assert.Equal(["hello"], tokenizer.Tokenize("hello,"));
    }

    [Fact]
    public void ALeadingJoinerIsPunctuationNotPartOfTheWord()
    {
        var tokenizer = Joiner(Reference);

        Assert.Equal(["world"], tokenizer.Tokenize(".world"));
    }

    /// <summary>
    /// The case that rules out the tempting shortcut. Trimming joiners off each end of a bulk-scanned
    /// run would make the run <c>hello.</c> and the run <c>,world</c>, and the second one starts with a
    /// comma followed by a word — so a naive joiner check would produce <c>hello,world</c>. Neither
    /// separator is interior: a comma follows the period, and a period precedes the comma.
    /// </summary>
    [Fact]
    public void AJoinerFollowedByAJoinerJoinsNothing()
    {
        var tokenizer = Joiner(Reference);

        Assert.Equal(["hello", "world"], tokenizer.Tokenize("hello.,world"));
        Assert.Equal(["hello", "world"], tokenizer.Tokenize("hello,.world"));
        Assert.Equal(["hello", "world"], tokenizer.Tokenize("hello,.world"));
    }

    [Fact]
    public void JoinersJoinRepeatedly()
    {
        var tokenizer = Joiner(Reference);

        Assert.Equal(["a.b.c"], tokenizer.Tokenize("a.b.c"));
    }

    [Fact]
    public void ADiacriticIsAWordCharacterAndDoesNotStopAJoiner()
    {
        // A diacritic belongs to the letter it decorates, so the dot in \u00e9j\u00e0.vu sits between two
        // word characters and joins. The token comes out folded because folding is on by default,
        // which is why the same input is asserted twice under the two settings.
        var joining = Joiner(Reference);
        var joiningUnfolded = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            WordJoiners = Reference,
            FoldDiacritics = false,
        });

        Assert.Equal(["deja.vu"], joining.Tokenize("d\u00e9j\u00e0.vu"));
        Assert.Equal(["d\u00e9j\u00e0.vu"], joiningUnfolded.Tokenize("d\u00e9j\u00e0.vu"));
    }

    [Fact]
    public void ANonAsciiJoinerWorksWhenConfigured()
    {
        // U+00AD SOFT HYPHEN is in the published vocabulary and U+2011 is not. Configuring the first
        // must not quietly enable the second: the configuration decides, not the character class.
        var softHyphen = Joiner("\u00AD");
        var asciiJoinersOnly = Joiner(Reference);

        Assert.Equal(["co\u00ADoperate"], softHyphen.Tokenize("co\u00ADoperate"));
        Assert.Equal(["co", "operate"], softHyphen.Tokenize("co operate"));
        Assert.Equal(["co", "operate"], asciiJoinersOnly.Tokenize("co\u00ADoperate"));
        Assert.Equal(["co", "operate"], asciiJoinersOnly.Tokenize("co\u2011operate"));
    }

    [Fact]
    public void JoinerConfigurationChangesNothingForTextWithoutJoiners()
    {
        var plain = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true });
        var joining = Joiner(Reference);

        const string Text = "the quick brown fox jumps over the lazy dog";

        Assert.Equal(plain.Tokenize(Text), joining.Tokenize(Text));
    }
}

/// <summary><see cref="TokenizerOptions.FoldDiacritics"/>.</summary>
public class FoldDiacriticsTests
{
    [Fact]
    public void FoldingIsOnByDefault()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true });

        Assert.True(TokenizerOptions.Default.FoldDiacritics);
        Assert.Equal(["cafe"], tokenizer.Tokenize("café"));
    }

    [Fact]
    public void TurningFoldingOffKeepsTheAccents()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            FoldDiacritics = false,
        });

        Assert.Equal(["café"], tokenizer.Tokenize("café"));
        Assert.Equal(["cafe"], tokenizer.Tokenize("cafe"));
    }

    /// <summary>
    /// Turning folding off must still lowercase: an index that is case-sensitive and one that folds
    /// accents are two different choices, but a case-sensitive index is not a thing this library has.
    /// </summary>
    [Fact]
    public void TurningFoldingOffStillLowercases()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            FoldDiacritics = false,
        });

        Assert.Equal(["café"], tokenizer.Tokenize("CAFÉ"));
    }

    [Fact]
    public void FoldingOffSeparatesSpellingsThatFoldingWouldMerge()
    {
        var folded = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true });
        var unfolded = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            FoldDiacritics = false,
        });

        Assert.Equal(["resume"], folded.Tokenize("résumé"));
        Assert.Equal(["résumé"], unfolded.Tokenize("résumé"));
    }
}