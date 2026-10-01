using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="TokenizerOptions.StripPossessives"/>, and the pipeline position it has to sit at.
/// </summary>
/// <remarks>
/// The published algorithm removes a possessive as its first step, and this stemmer does not — which
/// is why <c>Adam's</c> would become <c>adam'</c>, a term that cannot match anything indexed without the
/// apostrophe. That was measured against the reference implementation's own stemmer, not inferred:
/// asked directly, it answers <c>Adam'</c> too, and the possessive removal happens one stage earlier in
/// its pipeline. So this is a tokenizer option, and the stage it sits at is the part worth testing.
/// </remarks>
public class PorterPossessiveTests
{
    // The apostrophe has to join before a word containing one exists at all: with the default
    // segmentation every non-word character ends a word, so `it's` is two tokens and the possessive
    // is not one of them. That is the configuration the measured reference analysis uses.
    private static Tokenizer Keeping() => new(new TokenizerOptions
    {
        Stemmer = PorterStemmer.Default,
        KeepSingleCharTerms = true,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
    });

    private static Tokenizer Stripping() => new(new TokenizerOptions
    {
        Stemmer = PorterStemmer.Default,
        KeepSingleCharTerms = true,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
        StripPossessives = true,
    });

    [Fact]
    public void TheDefaultLeavesPossessivesAlone()
    {
        Assert.False(new TokenizerOptions().StripPossessives);
        Assert.Equal("adam'", TokenizeFirst(Keeping(), "adam's"));
        Assert.Equal("cat'", TokenizeFirst(Keeping(), "cat's"));
        Assert.Equal("adam'", TokenizeFirst(Keeping(), "Adam's"));
    }

    [Fact]
    public void ATrailingPossessiveIsRemovedBeforeTheAlgorithmRuns()
    {
        // Stemming first and trimming afterwards would give `cat` from `cat's` but `adam'` from
        // `Adam's`, because the algorithm does not touch a word ending in a quote. Order matters.
        Assert.Equal("adam", TokenizeFirst(Stripping(), "adam's"));
        Assert.Equal("cat", TokenizeFirst(Stripping(), "cat's"));
        Assert.Equal("adam", TokenizeFirst(Stripping(), "Adam's"));
        // `children` is left alone by both: the reference implementation answers `children` too,
        // so trimming the possessive is the only difference here and not a different plural rule.
        Assert.Equal("children", TokenizeFirst(Stripping(), "children's"));
        Assert.Equal("nation", TokenizeFirst(Stripping(), "nation's"));
    }

    [Fact]
    public void TheCurlyApostropheCountsToo()
    {
        Assert.Equal("adam", TokenizeFirst(Stripping(), "adam’s"));
    }

    /// <summary>
    /// Only the two-character suffix. These three are the cases where trimming would be wrong, and the
    /// measured reference answers for all of them confirms it.
    /// </summary>
    [Fact]
    public void ContractionsAndInteriorApostrophesAreLeftAlone()
    {
        foreach (Tokenizer tokenizer in new[] { Keeping(), Stripping() })
        {
            Assert.Equal("aren't", TokenizeFirst(tokenizer, "aren't"));
            Assert.Equal("ba'ath", TokenizeFirst(tokenizer, "ba'ath"));
            Assert.Equal("don't", TokenizeFirst(tokenizer, "don't"));
            Assert.Equal("o'brien", TokenizeFirst(tokenizer, "O'Brien"));
        }
    }

    [Fact]
    public void AWordEndingInSButNotPossessiveIsUnchanged()
    {
        foreach (Tokenizer tokenizer in new[] { Keeping(), Stripping() })
        {
            Assert.Equal("run", TokenizeFirst(tokenizer, "runs"));
        }
    }

    /// <summary>
    /// A short word is trimmed like any other, and the result is returned whole rather than stemmed
    /// further.
    /// </summary>
    /// <remarks>
    /// There is no length floor in front of this rule, and that is measured: asked about every length
    /// from one up, the reference gives <c>z's</c> → <c>z</c> and <c>zz's</c> → <c>zz</c>. So the
    /// possessive goes before anything asks how long the result is. A floor here would leave <c>t's</c>
    /// as <c>t'</c> — a term no query contains, which is what this index was doing four times over in a
    /// corpus of 23 895 terms.
    /// </remarks>
    [Fact]
    public void AWordShorterThanTheStemmingMinimumIsStillTrimmed()
    {
        Assert.Equal("x", TokenizeFirst(Stripping(), "x's"));
        Assert.Equal("z", TokenizeFirst(Stripping(), "z's"));
        Assert.Equal("zz", TokenizeFirst(Stripping(), "zz's"));

        // `as` is below the minimum, so the stemmer returns it whole instead of mutilating it further.
        Assert.Equal("as", TokenizeFirst(Stripping(), "as's"));

        // A control that exercises the algorithm rather than this rule.
        Assert.Equal("cat", TokenizeFirst(Stripping(), "cats"));
    }

    /// <summary>
    /// The end-to-end contract: a tokenizer that joins the apostrophe and a stemmer that trims the
    /// possessive put <c>Adam's</c> where the reference index puts it.
    /// </summary>
    [Fact]
    public void TokenizerAndStemmerTogetherProduceTheMeasuredResult()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
            Stemmer = PorterStemmer.Default,
            StripPossessives = true,
        });

        Assert.Equal("adam", TokenizeFirst(tokenizer, "Adam's"));
        Assert.Equal("adam", TokenizeFirst(tokenizer, "Adam’s"));
        Assert.Equal("ba’ath", TokenizeFirst(tokenizer, "ba’ath"));
        Assert.Equal("aren’t", TokenizeFirst(tokenizer, "aren’t"));
    }

    /// <summary>
    /// The reason this is a tokenizer option and not a stemmer one.
    /// </summary>
    /// <remarks>
    /// A stop word list is consulted between the tokenizer and the stemmer, so trimming the possessive
    /// afterwards consults it on <c>it's</c> rather than on <c>it</c> and lets through a term the
    /// caller asked to remove. Measured on BEIR ArguAna, that indexed an <c>it</c> 3 501 times where
    /// the reference has 3 134, and indexed a <c>that</c> and a <c>there</c> the reference does not
    /// have at all. The reference's <c>it</c> comes from <c>its</c>, which is not a possessive and is
    /// not on the list — which is why both of those counts are exact rather than approximate.
    /// </remarks>
    [Fact]
    public void ThePossessiveIsTrimmedBeforeTheStopWordListIsConsulted()
    {
        var trimming = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            RemoveStopWords = true,
            StopWords = StopWords.EnglishFunction,
            WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
            Stemmer = PorterStemmer.Default,
            StripPossessives = true,
        });

        var keeping = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            RemoveStopWords = true,
            StopWords = StopWords.EnglishFunction,
            WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
            Stemmer = PorterStemmer.Default,
        });

        // `it's` is a stop word once the possessive is gone, so it disappears. Without the trim it
        // survives the test as `it's` and is indexed as `it`.
        Assert.Equal(["cat"], trimming.Tokenize("it's a cat"));

        // Without the trim the apostrophe is still there when the list is consulted, so the token is
        // `it'` — not on the list, and not the term the reference indexes either.
        Assert.Equal(["it'", "cat"], keeping.Tokenize("it's a cat"));

        // `its` is not a possessive: it passes the list and only then becomes `it`. That is where the
        // reference's `it` comes from, and it is why trimming does not remove stop words here.
        Assert.Equal("it", TokenizeFirst(trimming, "its"));
        Assert.Equal("it", TokenizeFirst(keeping, "its"));

        // The two words that proved the order: absent from the reference index entirely.
        Assert.Equal(["fact"], trimming.Tokenize("that's a fact"));
        Assert.Equal(["fact"], trimming.Tokenize("there's a fact"));
        Assert.Equal(["that'", "fact"], keeping.Tokenize("that's a fact"));
        Assert.Equal(["there'", "fact"], keeping.Tokenize("there's a fact"));
    }

    /// <summary>The single term a one-word input produces, so the assertions below read as claims about terms.</summary>
    private static string TokenizeFirst(Tokenizer tokenizer, string input)
    {
        var terms = tokenizer.Tokenize(input);

        return terms.Count == 1 ? terms[0] : string.Join("|", terms);
    }
}
