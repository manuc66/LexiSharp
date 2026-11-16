using LexiSharp.Postgres;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The <c>tsvector</c> text reader the BM25 engine scores from. No server: the input is the exact
/// string <c>tsvector_out</c> produces, and the expected numbers are what BM25's <c>tf</c> and
/// <c>|d|</c> have to be for that vector.
/// </summary>
public class PostgresTsvTextTests
{
    [Fact]
    public void Parse_CountsEveryPositionAsOneOccurrence()
    {
        var (terms, length) = PostgresTsvText.Parse("'fat':2,4 'cat':3 'rat':5A");

        Assert.Equal(2, terms["fat"]);
        Assert.Equal(1, terms["cat"]);
        Assert.Equal(1, terms["rat"]);
        Assert.Equal(4, length);
    }

    [Fact]
    public void Parse_TreatsWeightLettersAsPartOfThePosition()
    {
        var (terms, length) = PostgresTsvText.Parse("'a':1A,2B 'b':3C 'c':4D");

        Assert.Equal(2, terms["a"]);
        Assert.Equal(1, terms["b"]);
        Assert.Equal(1, terms["c"]);
        Assert.Equal(4, length);
    }

    [Fact]
    public void Parse_ReadsALexemeWithNoPositionsAsOneOccurrence()
    {
        // A vector written with strip(): presence without positions. One is the only count the
        // vector can support, and reading it as zero would make the term disappear from the score.
        var (terms, length) = PostgresTsvText.Parse("'quick' 'brown'");

        Assert.Equal(1, terms["quick"]);
        Assert.Equal(1, terms["brown"]);
        Assert.Equal(2, length);
    }

    [Fact]
    public void Parse_UnescapesADoubledQuoteInsideALexeme()
    {
        var (terms, _) = PostgresTsvText.Parse("'o''brien':1 's':2");

        Assert.Equal(1, terms["o'brien"]);
        Assert.Equal(1, terms["s"]);
    }

    [Fact]
    public void Parse_ReadsALexemeContainingASpace()
    {
        // A custom dictionary can emit a multi-word lexeme, and the positions still count.
        var (terms, length) = PostgresTsvText.Parse("'new york':1,7 'city':2");

        Assert.Equal(2, terms["new york"]);
        Assert.Equal(1, terms["city"]);
        Assert.Equal(3, length);
    }

    [Fact]
    public void Parse_ReturnsNothingForAnEmptyVector()
    {
        var (terms, length) = PostgresTsvText.Parse(string.Empty);

        Assert.Empty(terms);
        Assert.Equal(0, length);
    }

    [Fact]
    public void Parse_UsesTheOrdinalComparer()
    {
        // 'A' and 'a' are different lexemes to tsvector and must stay different to the scorer.
        var (terms, _) = PostgresTsvText.Parse("'A':1 'a':2");

        Assert.Equal(2, terms.Count);
        Assert.Equal(1, terms["A"]);
        Assert.Equal(1, terms["a"]);
    }

    [Fact]
    public void Format_RoundTripsThroughParse()
    {
        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["quick"] = 3,
            ["brown"] = 1,
            ["o'brien"] = 2,
        };

        var (terms, length) = PostgresTsvText.Parse(PostgresTsvText.Format(frequencies));

        Assert.Equal(frequencies, terms);
        Assert.Equal(6, length);
    }
}
