using LexiSharp.Highlighting;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class TextHighlighterTests
{
    private const string Text = "The quick brown fox jumps over the lazy dog";

    private const string PaddedText = "one two three four five six seven eight nine ten";

    [Fact]
    public void MatchRanges_FindsEachOccurrenceInReadingOrder()
    {
        var ranges = TextHighlighter.MatchRanges(Text, new[] { "quick", "lazy" }, Tokenizer.Default);

        Assert.Equal(new Range[] { new(4, 9), new(35, 39) }, ranges);
    }

    [Fact]
    public void MatchRanges_IsCaseAndAccentInsensitive()
    {
        var ranges = TextHighlighter.MatchRanges(
            "Le Café est déjà ouvert", new[] { "cafe" }, Tokenizer.Default);

        Assert.Equal(new Range[] { new(3, 7) }, ranges);
    }

    [Fact]
    public void MatchRanges_NoTermsOrNoMatch_ReturnsEmpty()
    {
        Assert.Empty(TextHighlighter.MatchRanges(Text, Array.Empty<string>(), Tokenizer.Default));
        Assert.Empty(TextHighlighter.MatchRanges(Text, new[] { "zebra" }, Tokenizer.Default));
    }

    [Fact]
    public void MatchRanges_MergesOverlappingNgramSpans()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { NGramMax = 2 });

        var ranges = TextHighlighter.MatchRanges(
            "machine learning rocks",
            new[] { "machine", "machine learning" },
            tokenizer);

        // Unigram [0,7) and bigram [0,16) collapse into the bigram's region.
        Assert.Equal(new Range[] { new(0, 16) }, ranges);
    }

    [Fact]
    public void HighlightFull_WrapsEachMatch()
    {
        var result = TextHighlighter.HighlightFull(Text, new[] { "quick", "dog" }, Tokenizer.Default);

        Assert.Equal("The <em>quick</em> brown fox jumps over the lazy <em>dog</em>", result);
    }

    [Fact]
    public void HighlightFull_KeepsTheSourceCharactersOfAccentedMatches()
    {
        var result = TextHighlighter.HighlightFull(
            "Le Café est déjà ouvert", new[] { "cafe" }, Tokenizer.Default);

        Assert.Equal("Le <em>Café</em> est déjà ouvert", result);
    }

    [Fact]
    public void HighlightFull_HonorsCustomTags()
    {
        var result = TextHighlighter.HighlightFull(
            Text,
            new[] { "fox" },
            Tokenizer.Default,
            new HighlightOptions { PreTag = "[[", PostTag = "]]" });

        Assert.Equal("The quick brown [[fox]] jumps over the lazy dog", result);
    }

    [Fact]
    public void HighlightFull_NoMatch_ReturnsTextUnchanged()
    {
        var result = TextHighlighter.HighlightFull(Text, new[] { "zebra" }, Tokenizer.Default);

        Assert.Equal(Text, result);
    }

    [Fact]
    public void Highlight_ClustersMatchesIntoPaddedWordSnappedSnippets()
    {
        var snippets = TextHighlighter.Highlight(
            PaddedText,
            new[] { "one", "three", "ten" },
            Tokenizer.Default,
            new HighlightOptions { Padding = 6, MaxSnippets = 3 });

        // "one"+"three" are close (one cluster), "ten" is far (second cluster). Padding snaps
        // outward to word boundaries: the first window runs to the end of "five", the second
        // starts at the beginning of "eight".
        Assert.Equal(2, snippets.Count);

        Assert.Equal(0, snippets[0].Start);
        Assert.Equal(23, snippets[0].Length);
        Assert.Equal(23, snippets[0].End);
        Assert.Equal("<em>one</em> two <em>three</em> four five", snippets[0].Text);

        Assert.Equal(34, snippets[1].Start);
        Assert.Equal(14, snippets[1].Length);
        Assert.Equal("eight nine <em>ten</em>", snippets[1].Text);
    }

    [Fact]
    public void Highlight_RespectsMaxSnippets()
    {
        var snippets = TextHighlighter.Highlight(
            PaddedText,
            new[] { "one", "three", "ten" },
            Tokenizer.Default,
            new HighlightOptions { Padding = 6, MaxSnippets = 1 });

        var single = Assert.Single(snippets);
        Assert.Equal(0, single.Start);
        Assert.Equal("<em>one</em> two <em>three</em> four five", single.Text);
    }

    [Fact]
    public void Highlight_MaxSnippetsZeroOrNoMatch_ReturnsNothing()
    {
        Assert.Empty(TextHighlighter.Highlight(
            Text, new[] { "quick" }, Tokenizer.Default, new HighlightOptions { MaxSnippets = 0 }));

        Assert.Empty(TextHighlighter.Highlight(Text, new[] { "zebra" }, Tokenizer.Default));
    }

    [Fact]
    public void Highlight_NullArguments_Throw()
    {
        Assert.Throws<ArgumentNullException>(() =>
            TextHighlighter.MatchRanges(null!, new[] { "a" }, Tokenizer.Default));
        Assert.Throws<ArgumentNullException>(() =>
            TextHighlighter.MatchRanges(Text, null!, Tokenizer.Default));
        Assert.Throws<ArgumentNullException>(() =>
            TextHighlighter.MatchRanges(Text, new[] { "a" }, null!));
        Assert.Throws<ArgumentNullException>(() =>
            TextHighlighter.HighlightFull(null!, new[] { "a" }, Tokenizer.Default));
        Assert.Throws<ArgumentNullException>(() =>
            TextHighlighter.Highlight(null!, new[] { "a" }, Tokenizer.Default));
    }
}
