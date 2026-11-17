using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Under <see cref="WordSegmentation.UnicodeWordBoundaries"/> a Han ideograph is a term of its own:
/// <c>理财</c> is two terms, not one.
/// </summary>
/// <remarks>
/// The reference tokenizer's <c>IDEOGRAPHIC</c> rule emits a single CJK ideograph per token, so a
/// run of them is not a word. FiQA holds nine ideographs, and joining them lost six terms and
/// invented a seventh.
/// <para>
/// The rule is the script with its word letters removed, not a range: U+3005 and U+303B (the
/// ideographic iteration marks) are <c>Script=Han</c> and <c>Word_Break=ALetter</c>, so the
/// reference keeps them inside an alphanumeric run — <c>a々b</c> is one token — and this library's
/// letter scan already did the same. Only the ideographs that are not word letters split.
/// </para>
/// </remarks>
public class IdeographicTokenTests
{
    private static Tokenizer Segmenter() => new(new TokenizerOptions
    {
        KeepSingleCharTerms = true,
        FoldDiacritics = false,
        WordSegmentation = WordSegmentation.UnicodeWordBoundaries,
    });

    [Fact]
    public void EachIdeographIsATerm()
    {
        Assert.Equal(["理", "财"], Segmenter().Tokenize("理财"));
        Assert.Equal(["雙", "元", "貨", "幣", "產", "品"], Segmenter().Tokenize("雙元貨幣產品"));
    }

    [Fact]
    public void AnIdeographEndsTheRunBesideIt()
    {
        Assert.Equal(["6", "尺"], Segmenter().Tokenize("6尺"));
        Assert.Equal(["abc", "尺", "def"], Segmenter().Tokenize("abc尺def"));
        Assert.Equal(["尺", "abc"], Segmenter().Tokenize("尺abc"));
    }

    [Fact]
    public void RepeatedIdeographsAreOneTermEach()
    {
        Assert.Equal(["尺", "尺"], Segmenter().Tokenize("尺尺"));
    }

    /// <summary>
    /// The exceptions that keep this from being a script test: iteration marks are word letters in
    /// the reference's word-break data, and an alphanumeric run keeps them.
    /// </summary>
    [Fact]
    public void TheIdeographicIterationMarksStayInTheWord()
    {
        Assert.Equal(["a々b"], Segmenter().Tokenize("a々b"));
        Assert.Equal(["a〻b"], Segmenter().Tokenize("a〻b"));
    }

    [Fact]
    public void TheIdeographicNumberZeroIsATerm()
    {
        Assert.Equal(["〇"], Segmenter().Tokenize("〇"));
    }

    /// <summary>
    /// The control: the rule is about ideographs only. A Katakana run was one token before this
    /// change and has to stay one.
    /// </summary>
    [Fact]
    public void KatakanaIsStillOneRun()
    {
        Assert.Equal(["カタカナ"], Segmenter().Tokenize("カタカナ"));
    }
}
