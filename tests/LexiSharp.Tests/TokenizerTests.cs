using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class TokenizerTests
{
    [Fact]
    public void Tokenize_LowercasesAndSplitsOnPunctuation()
    {
        var tokens = Tokenizer.Default.Tokenize("Hello, World! This is .NET C#.");

        // "C#" -> "c", discarded because single-character terms are dropped by default.
        Assert.Equal(new[] { "hello", "world", "this", "is", "net" }, tokens);
    }

    [Fact]
    public void Tokenize_RemovesDiacritics()
    {
        var tokens = Tokenizer.Default.Tokenize("Café déjà vu façade ñoño");

        Assert.Equal(new[] { "cafe", "deja", "vu", "facade", "nono" }, tokens);
    }

    [Fact]
    public void Tokenize_DropsSingleCharacterTokensByDefault()
    {
        var tokens = Tokenizer.Default.Tokenize("a b c word");

        Assert.Equal(new[] { "word" }, tokens);
    }

    [Fact]
    public void Tokenize_KeepsSingleCharacterTokensWhenConfigured()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true });

        var tokens = tokenizer.Tokenize("a b c word");

        Assert.Equal(new[] { "a", "b", "c", "word" }, tokens);
    }

    [Fact]
    public void Tokenize_EmptyAndNullInputProduceNoTokens()
    {
        Assert.Empty(Tokenizer.Default.Tokenize(""));
        Assert.Empty(Tokenizer.Default.Tokenize(null!));
        Assert.Empty(Tokenizer.Default.Tokenize("   ,,, , !!! "));
    }

    [Fact]
    public void Tokenize_RemovesStopWordsWhenEnabled()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { RemoveStopWords = true });

        var tokens = tokenizer.Tokenize("the quick brown fox and the lazy dog");

        Assert.Equal(new[] { "quick", "brown", "fox", "lazy", "dog" }, tokens);
    }

    [Fact]
    public void Tokenize_UsesCustomStopWords()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions
        {
            RemoveStopWords = true,
            StopWords = StopWords.Create("foo", "bar"),
        });

        var tokens = tokenizer.Tokenize("foo baz bar qux");

        Assert.Equal(new[] { "baz", "qux" }, tokens);
    }

    [Fact]
    public void Tokenize_GeneratesBigramsWhenConfigured()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { NGramMax = 2 });

        var tokens = tokenizer.Tokenize("machine learning rocks");

        Assert.Equal(
            new[] { "machine", "learning", "rocks", "machine learning", "learning rocks" },
            tokens);
    }

    [Fact]
    public void Tokenize_AppliesConsumerStemmerWhenConfigured()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new SuffixStrippingStemmer() });

        var tokens = tokenizer.Tokenize("jumping jumped cats dogs");

        Assert.Equal(new[] { "jump", "jump", "cat", "dog" }, tokens);
    }

    [Fact]
    public void Normalize_HandlesMixedCaseWithAccents()
    {
        Assert.Equal("resume", Tokenizer.Normalize("Résumé"));
        Assert.Equal("e", Tokenizer.Normalize("É"));
        Assert.Equal("æ", Tokenizer.Normalize("Æ")); // ligature, left as-is by NFKD
    }
}