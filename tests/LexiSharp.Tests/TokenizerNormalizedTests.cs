using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The span-shaped tokenization must produce exactly the terms the string-shaped one produces, and
/// must decline rather than guess when it cannot.
/// </summary>
/// <remarks>
/// The point of the span path is that a term whose normalized form the source already holds is
/// returned as a slice of it, with no string built. That makes the two paths two implementations of
/// one contract, which is what these tests hold: same terms, same order, same characters — and a
/// <c>-1</c> rather than a term the caller could not resolve.
/// </remarks>
public class TokenizerNormalizedTests
{
    private static NormalizedTerm[] Buffer(string text) => new NormalizedTerm[Math.Max(1, text.Length)];

    [Theory]
    [InlineData("search engine")]
    [InlineData("Search Engine")]
    [InlineData("café résumé")]
    [InlineData("MIXED café 42")]
    [InlineData("don't stop")]
    [InlineData("environment-friendly")]
    [InlineData("one")]
    [InlineData("a b")]
    [InlineData("")]
    public void TheNormalizedTermsAreTheTokenizedTerms(string text)
    {
        var tokenizer = new Tokenizer();
        var expected = tokenizer.Tokenize(text);

        var destination = Buffer(text);
        int count = tokenizer.TokenizeNormalized(text, destination);

        Assert.Equal(expected.Count, count);

        for (int i = 0; i < count; i++)
            Assert.Equal(expected[i], destination[i].View(text).ToString());
    }

    [Fact]
    public void ALowercaseAsciiTermIsItsOwnNormalForm()
    {
        const string Text = "search engine";
        var destination = Buffer(Text);

        int count = new Tokenizer().TokenizeNormalized(Text, destination);

        Assert.Equal(2, count);
        Assert.Null(destination[0].Materialized);
        Assert.Null(destination[1].Materialized);
        Assert.Equal("search", destination[0].View(Text).ToString());
        Assert.Equal("engine", destination[1].View(Text).ToString());
    }

    [Fact]
    public void ASlicePointsAtTheTermItCameFrom()
    {
        const string Text = "zulu alpha";

        var destination = Buffer(Text);
        int count = new Tokenizer().TokenizeNormalized(Text, destination);

        Assert.Equal(2, count);
        Assert.Equal((0, 4), (destination[0].Start, destination[0].Length));
        Assert.Equal((5, 5), (destination[1].Start, destination[1].Length));
    }

    [Fact]
    public void ATermThatHadToBeBuiltIsMaterialized()
    {
        const string Text = "Résumé";

        var destination = Buffer(Text);
        int count = new Tokenizer().TokenizeNormalized(Text, destination);

        Assert.Equal(1, count);
        Assert.Equal("resume", destination[0].Materialized);
    }

    [Fact]
    public void ADestinationTooSmallDeclines()
    {
        int count = new Tokenizer().TokenizeNormalized("search engine", new NormalizedTerm[1]);

        Assert.Equal(-1, count);
    }

    [Fact]
    public void AnAnalysisThatReplacesTermsDeclines()
    {
        const string Text = "running quickly";

        // A stemmer replaces the term, a stop-word list is asked a question a span cannot answer, an
        // n-gram pass renumbers the terms, and the possessive trim shortens one. Each of those
        // materializes, so each declines rather than handing back a slice that is not the term.
        var stemmed = new Tokenizer(new TokenizerOptions { Stemmer = new PorterStemmer() });
        var filtered = new Tokenizer(new TokenizerOptions { RemoveStopWords = true });
        var ngrams = new Tokenizer(new TokenizerOptions { NGramMax = 2 });
        var possessives = new Tokenizer(new TokenizerOptions { StripPossessives = true });

        Assert.Equal(-1, stemmed.TokenizeNormalized(Text, Buffer(Text)));
        Assert.Equal(-1, filtered.TokenizeNormalized(Text, Buffer(Text)));
        Assert.Equal(-1, ngrams.TokenizeNormalized(Text, Buffer(Text)));
        Assert.Equal(-1, possessives.TokenizeNormalized("don's", Buffer("don's")));
    }

    [Fact]
    public void ADefaultTokenizerKeepsTheFlatSegmentationRules()
    {
        // The span path shares the scan, so the terms a separator produces are the string path's:
        // a comma between digits joins, an apostrophe inside a word class joins, and a hyphen splits.
        var tokenizer = new Tokenizer();
        string[] texts = ["1,000", "don't", "environment-friendly"];

        foreach (string text in texts)
        {
            var expected = tokenizer.Tokenize(text);
            var destination = Buffer(text);
            int count = tokenizer.TokenizeNormalized(text, destination);

            Assert.Equal(expected.Count, count);

            for (int i = 0; i < count; i++)
                Assert.Equal(expected[i], destination[i].View(text).ToString());
        }
    }
}
