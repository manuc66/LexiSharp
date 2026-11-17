using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Under <see cref="WordSegmentation.UnicodeWordBoundaries"/> a token longer than the reference
/// scanner's 255-unit buffer is emitted in consecutive chunks, not dropped and not left whole.
/// </summary>
/// <remarks>
/// FiQA holds one document whose token is 468 characters; the reference index holds it as one term
/// of 255 and one of 213. The limit is the scanner's buffer width, so it is a property of the mode
/// that reproduces that scanner and not of whitespace options: the other analyses keep a long
/// identifier whole, and <see cref="AnalysisScopeTests"/> pins that.
/// </remarks>
public class LongTokenTests
{
    private static Tokenizer Segmenter(bool keepSingleCharTerms = true) => new(new TokenizerOptions
    {
        KeepSingleCharTerms = keepSingleCharTerms,
        FoldDiacritics = false,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
    });

    [Fact]
    public void ATokenOf468CharactersIsChoppedAt255()
    {
        IReadOnlyList<string> terms = Segmenter().Tokenize(new string('x', 468));

        Assert.Equal(2, terms.Count);
        Assert.Equal(new string('x', 255), terms[0]);
        Assert.Equal(new string('x', 213), terms[1]);
    }

    [Fact]
    public void ATokenOfExactly255CharactersIsOneTerm()
    {
        Assert.Equal([new string('z', 255)], Segmenter().Tokenize(new string('z', 255)));
    }

    [Fact]
    public void ATokenOf256CharactersIs255And1()
    {
        Assert.Equal([new string('y', 255), "y"], Segmenter().Tokenize(new string('y', 256)));
    }

    [Fact]
    public void ATokenOf510CharactersIsTwoChunksOf255()
    {
        Assert.Equal(
            [new string('w', 255), new string('w', 255)],
            Segmenter().Tokenize(new string('w', 510)));
    }

    /// <summary>
    /// The chunk boundary never lands inside a surrogate pair. A 258-unit word whose 255th unit is
    /// the high half of a Deseret letter is chopped at 254, so the pair moves to the second chunk
    /// whole — which is what the reference scanner does when it holds a trailing high surrogate over.
    /// </summary>
    [Fact]
    public void AChunkBoundaryDoesNotSplitASurrogatePair()
    {
        string word = new string('a', 254) + "\U00010400" + "bb";

        IReadOnlyList<string> terms = Segmenter().Tokenize(word);

        Assert.Equal(2, terms.Count);
        Assert.Equal(new string('a', 254), terms[0]);
        Assert.Equal("\U00010428bb", terms[1]);
    }

    /// <summary>
    /// Each chunk is a token in its own right, so the single-character rule applies to the tail: a
    /// 256-character token keeps a one-character tail only when single-character terms are kept.
    /// </summary>
    [Fact]
    public void TheTailChunkObeysTheSingleCharacterRule()
    {
        Assert.Equal([new string('y', 255)], Segmenter(keepSingleCharTerms: false).Tokenize(new string('y', 256)));
    }
}
