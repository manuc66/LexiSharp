using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The four parity rules added for <see cref="WordSegmentation.UnicodeWordBoundaries"/> — emoji and
/// ideographs as terms, combining and format characters inside a word, and the 255-unit chop — do
/// not reach the other analyses.
/// </summary>
/// <remarks>
/// This is the regression pin for the scope of the change: <c>default</c>, <c>porter</c>,
/// <c>english</c> and <c>identifiers</c> are the analyses every published figure and every caller
/// outside the uax29 comparison uses, and each keeps the behavior it had. The joining path is
/// shared with the identifiers analysis, which is why the chop is gated on the mode rather than on
/// the scan.
/// </remarks>
public class AnalysisScopeTests
{
    public static TheoryData<string> Analyses => new() { "default", "porter", "english", "identifiers" };

    private static Tokenizer Build(string analyzer) => analyzer switch
    {
        "default" => new Tokenizer(TokenizerOptions.Default),
        "porter" => new Tokenizer(new TokenizerOptions
        {
            Stemmer = new PorterStemmer(),
            RemoveStopWords = true,
        }),
        "english" => new Tokenizer(new TokenizerOptions
        {
            Stemmer = new PorterStemmer(),
            RemoveStopWords = true,
            StopWords = StopWords.EnglishFunction,
            KeepSingleCharTerms = true,
        }),
        "identifiers" => new Tokenizer(new TokenizerOptions
        {
            WordJoiners = "-_.:/",
            KeepSingleCharTerms = true,
            FoldDiacritics = false,
        }),
        _ => throw new ArgumentOutOfRangeException(nameof(analyzer)),
    };

    [Theory]
    [MemberData(nameof(Analyses))]
    public void AnEmojiIsNotATermOutsideTheMode(string analyzer)
    {
        Tokenizer tokenizer = Build(analyzer);

        Assert.Equal(["ab", "cd"], tokenizer.Tokenize("ab🤔cd"));
        Assert.Empty(tokenizer.Tokenize("✔"));
    }

    [Theory]
    [MemberData(nameof(Analyses))]
    public void AnIdeographicRunStaysOneWordOutsideTheMode(string analyzer)
    {
        Assert.Equal(["理财"], Build(analyzer).Tokenize("理财"));
    }

    [Theory]
    [MemberData(nameof(Analyses))]
    public void AFormatCharacterStillSplitsOutsideTheMode(string analyzer)
    {
        // The ASCII number survives every analysis; the `0` after the byte-order mark is kept by
        // `english` and `identifiers`, which keep single-character terms, and dropped by the two
        // that do not.
        string[] expected = analyzer is "english" or "identifiers" ? ["199", "0"] : ["199"];

        Assert.Equal(expected, Build(analyzer).Tokenize("199\uFEFF0"));
    }

    [Theory]
    [MemberData(nameof(Analyses))]
    public void ANarrowNoBreakSpaceStillSplitsOutsideTheMode(string analyzer)
    {
        Assert.Equal(["ab", "cd"], Build(analyzer).Tokenize("ab\u202Fcd"));
    }

    [Theory]
    [MemberData(nameof(Analyses))]
    public void ALongTokenIsNotChoppedOutsideTheMode(string analyzer)
    {
        Assert.Equal([new string('x', 468)], Build(analyzer).Tokenize(new string('x', 468)));
    }
}
