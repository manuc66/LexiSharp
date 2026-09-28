using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class SpanTokenizerTests
{
    private static readonly string[] Samples =
    {
        "plain ascii text with punctuation, and some 1234567890 numbers!",
        "C'est déjà vu - l'été à Paris…",
        "Straße Ärger über Öl und so.",
        "hello 🚀 world 🌍 and 🐍",
        "привет мир как дела хорошо",
        "v1.2.3 beta2 rc-7",
        "foo—bar «quoted» “double” …more",
        "  leading and trailing  ",
        "中文测试与word混合mixed文本",
        "word́with́combininǵmarks",
        "Cats, dogs. Fish; birds! Pigs?",
        "one\ttwo\nthree\r\nfour",
        "double  space  after   words",
        "étude éléphant été été",
        "foo_bar-baz.qux/quux",
    };

    [Fact]
    public void TokenizeWithSpans_ReportsSourcePositions()
    {
        var spans = Tokenizer.Default.TokenizeWithSpans("Hello, World! This is .NET.");

        Assert.Equal(
            new[]
            {
                (Term: "hello", Start: 0, Length: 5),
                (Term: "world", Start: 7, Length: 5),
                (Term: "this", Start: 14, Length: 4),
                (Term: "is", Start: 19, Length: 2),
                (Term: "net", Start: 23, Length: 3),
            },
            spans.Select(s => (s.Term, s.Start, s.Length)));
    }

    [Fact]
    public void TokenizeWithSpans_NullAndEmpty_ProduceNoSpans()
    {
        Assert.Empty(Tokenizer.Default.TokenizeWithSpans(null!));
        Assert.Empty(Tokenizer.Default.TokenizeWithSpans(string.Empty));
        Assert.Empty(Tokenizer.Default.TokenizeWithSpans("   ,,, , !!! "));
    }

    [Fact]
    public void TokenizeWithSpans_SpansAreSortedAndNonOverlapping()
    {
        foreach (var sample in Samples)
        {
            var spans = Tokenizer.Default.TokenizeWithSpans(sample);

            for (int i = 1; i < spans.Count; i++)
            {
                Assert.True(
                    spans[i - 1].End <= spans[i].Start,
                    $"overlap at '{sample}': {spans[i - 1]} vs {spans[i]}");
            }
        }
    }

    [Fact]
    public void TokenizeWithSpans_NormalizedSliceEqualsTerm()
    {
        foreach (var sample in Samples)
        {
            foreach (var span in Tokenizer.Default.TokenizeWithSpans(sample))
            {
                var slice = sample.Substring(span.Start, span.Length);
                Assert.Equal(span.Term, Tokenizer.Normalize(slice));
            }
        }
    }

    [Fact]
    public void TokenizeWithSpans_StemmedSliceStemsToTerm()
    {
        var stemmer = new SuffixStrippingStemmer();
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = stemmer });
        const string text = "jumping cats jumped dogs";

        var spans = tokenizer.TokenizeWithSpans(text);
        Assert.Equal(new[] { "jump", "cat", "jump", "dog" }, spans.Select(s => s.Term));

        foreach (var span in spans)
        {
            // The source slice normalizes first, then stems — same order as the pipeline.
            var raw = text.Substring(span.Start, span.Length);
            Assert.Equal(span.Term, stemmer.Stem(Tokenizer.Normalize(raw)));
        }
    }

    [Fact]
    public void TokenizeWithSpans_StopWordsLeaveNoSpan()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { RemoveStopWords = true });

        var spans = tokenizer.TokenizeWithSpans("the quick brown fox");

        Assert.Equal(new[] { "quick", "brown", "fox" }, spans.Select(s => s.Term));
        Assert.Equal(4, spans[0].Start); // "the" (0..3) left no hole in the offsets
        Assert.Equal(5, spans[0].Length);
    }

    [Fact]
    public void TokenizeWithSpans_NgramSpansCoverTheWholePhraseRegion()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { NGramMax = 2 });

        var spans = tokenizer.TokenizeWithSpans("machine learning rocks");

        Assert.Equal(
            new[]
            {
                new TokenSpan("machine", 0, 7),
                new TokenSpan("learning", 8, 8),
                new TokenSpan("rocks", 17, 5),
                new TokenSpan("machine learning", 0, 16),
                new TokenSpan("learning rocks", 8, 14),
            },
            spans);
    }

    [Fact]
    public void TokenizeWithSpans_DropsSingleCharacterTermsByDefault()
    {
        var spans = Tokenizer.Default.TokenizeWithSpans("a b c word");

        var single = Assert.Single(spans);
        Assert.Equal("word", single.Term);
        Assert.Equal(6, single.Start);
        Assert.Equal(4, single.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("keep-single")]
    [InlineData("ngrams")]
    [InlineData("stopwords")]
    [InlineData("stem")]
    public void TokenizeWithSpans_ProjectsExactlyToTokenize(string? mode)
    {
        TokenizerOptions? options = mode switch
        {
            "keep-single" => new TokenizerOptions { KeepSingleCharTerms = true },
            "ngrams" => new TokenizerOptions { NGramMax = 2 },
            "stopwords" => new TokenizerOptions { RemoveStopWords = true },
            "stem" => new TokenizerOptions { Stemmer = new SuffixStrippingStemmer() },
            _ => null,
        };
        var sut = new Tokenizer(options);

        foreach (var sample in Samples)
        {
            Assert.Equal(
                sut.Tokenize(sample),
                sut.TokenizeWithSpans(sample).Select(s => s.Term));
        }
    }
}
