using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="TokenizerOptions.FoldTurkishDottedI"/>, on its own.
/// </summary>
/// <remarks>
/// The scope was measured, not assumed. The reference implementation's lowercasing was dumped over the
/// eight ranges a European corpus contains — 505 code points whose lowercase differs from themselves —
/// and compared against .NET's invariant casing. Exactly one position disagrees: U+0130, which the
/// reference sends to U+0069 U+0307, the complete Unicode mapping, while
/// <see cref="string.ToLowerInvariant"/> leaves the letter alone. That is why this is one character and
/// not a rule about Turkish casing in general.
/// </remarks>
public class TurkishDottedITests
{
    private static Tokenizer Tokenizing(bool fold) => new(new TokenizerOptions
    {
        KeepSingleCharTerms = true,
        FoldDiacritics = false,
        FoldTurkishDottedI = fold,
    });

    [Fact]
    public void TheDefaultLeavesTheLetterAlone()
    {
        Assert.False(new TokenizerOptions().FoldTurkishDottedI);

        // Invariant casing is the correct answer for a Turkish index, where `İ` and `i` are two letters.
        Assert.Equal(["İstanbul"], Tokenizing(fold: false).Tokenize("İstanbul"));
        Assert.Equal(["İ"], Tokenizing(fold: false).Tokenize("İ"));
    }

    /// <summary>
    /// The measured rule: U+0069, one character — the Unicode <b>simple</b> mapping.
    /// </summary>
    /// <remarks>
    /// The full mapping would send U+0130 to U+0069 U+0307, adding a combining dot above, and that is
    /// the version of this rule that was implemented first and then measured away: the reference index
    /// holds a five-character <c>celil</c>, and handing the reference analyzer a six-character
    /// <c>celi̇l</c> returns the dot intact, so its pipeline never produces one.
    /// </remarks>
    [Fact]
    public void TheFoldIsTheSimpleMappingAndNotTheCompleteOne()
    {
        var tokenizer = Tokenizing(fold: true);

        Assert.Equal(["istanbul"], tokenizer.Tokenize("İstanbul"));
        Assert.Equal(["i"], tokenizer.Tokenize("İ"));
        Assert.Equal(["celil"], tokenizer.Tokenize("celİl"));
        Assert.Equal(["iki"], tokenizer.Tokenize("İKİ"));
        Assert.Equal(["izmir"], tokenizer.Tokenize("İzmir"));
    }

    /// <summary>
    /// A term that already carries the combining dot keeps it: the option replaces U+0130, and does not
    /// strip marks. The reference behaves the same way — <c>celi̇l</c> comes back as <c>celi̇l</c>, six
    /// characters — which is what rules out the complete mapping as the thing being implemented here.
    /// </summary>
    [Fact]
    public void ACombiningDotThatIsAlreadyThereIsLeftAlone()
    {
        var tokenizer = Tokenizing(fold: true);

        Assert.Equal(["celi̇l"], tokenizer.Tokenize("celi̇l"));
        Assert.Equal(["i̇"], tokenizer.Tokenize("i̇"));
    }

    /// <summary>
    /// The dotless <c>ı</c> is a different letter and stays a different letter. Folding only the dotted
    /// capital is what makes this a two-way distinction rather than a blanket lowercase.
    /// </summary>
    [Fact]
    public void TheDotlessLowercaseIIsNotTouched()
    {
        foreach (bool fold in new[] { false, true })
        {
            var tokenizer = Tokenizing(fold);
            Assert.Equal(["ı"], tokenizer.Tokenize("ı"));
            Assert.Equal(["I".ToLowerInvariant()], tokenizer.Tokenize("I"));
        }
    }

    /// <summary>
    /// Every other capital agrees between the two casings, so the option must leave them alone. Without
    /// this the option could quietly become "lowercase differently" rather than "fold one character".
    /// </summary>
    [Theory]
    [InlineData("À")]
    [InlineData("Ç")]
    [InlineData("É")]
    [InlineData("Ö")]
    [InlineData("ẞ")]
    [InlineData("Ǆ")]
    [InlineData("Σ")]
    public void OtherCapitalsAreUnaffectedByTheOption(string capital)
    {
        Assert.Equal(
            Tokenizing(fold: false).Tokenize(capital),
            Tokenizing(fold: true).Tokenize(capital));
    }

    /// <summary>
    /// The case that started this: one term in a corpus of 23,895, differing only in this letter. With the
    /// option the vocabulary matches the reference index term for term.
    /// </summary>
    [Fact]
    public void TheTermThatDifferedFromTheReferenceNowMatches()
    {
        // Their index holds `celil`; ours held `celİl`. One occurrence, one document, no frequency moved.
        Assert.Equal(["celil"], Tokenizing(fold: true).Tokenize("celİl"));
        Assert.Equal(["celİl"], Tokenizing(fold: false).Tokenize("celİl"));
    }

    /// <summary>
    /// <see cref="TokenizerOptions.FoldDiacritics"/> composes with this rather than excluding it, and the
    /// two agree on this character either way — which is why the option exists: the reference keeps its
    /// diacritics, so a caller reproducing it needs this without giving up folding.
    /// </summary>
    [Fact]
    public void FoldingDiacriticsOnTopOfThisRemovesTheDotAgain()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            KeepSingleCharTerms = true,
            FoldDiacritics = true,
            FoldTurkishDottedI = true,
        });

        Assert.Equal(["celil"], tokenizer.Tokenize("celİl"));
        Assert.Equal(["istanbul"], tokenizer.Tokenize("İstanbul"));
    }
}