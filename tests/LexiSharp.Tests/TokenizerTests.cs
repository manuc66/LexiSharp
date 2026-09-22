using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class TokenizerTests
{
    private static readonly string[] LowercaseTokens = { "hello", "world", "this", "is", "net" };
    private static readonly string[] DiacriticFreeTokens = { "cafe", "deja", "vu", "facade", "nono" };
    private static readonly string[] SingleWordToken = { "word" };
    private static readonly string[] SingleCharKeptTokens = { "a", "b", "c", "word" };
    private static readonly string[] StopWordFreeTokens = { "quick", "brown", "fox", "lazy", "dog" };
    private static readonly string[] CustomStopWordTokens = { "baz", "qux" };
    private static readonly string[] BigramTokens = { "machine", "learning", "rocks", "machine learning", "learning rocks" };
    private static readonly string[] StemmedTokens = { "jump", "jump", "cat", "dog" };

    [Fact]
    public void Tokenize_LowercasesAndSplitsOnPunctuation()
    {
        var tokens = Tokenizer.Default.Tokenize("Hello, World! This is .NET C#.");

        // "C#" -> "c", discarded because single-character terms are dropped by default.
        Assert.Equal(LowercaseTokens, tokens);
    }

    [Fact]
    public void Tokenize_RemovesDiacritics()
    {
        var tokens = Tokenizer.Default.Tokenize("Café déjà vu façade ñoño");

        Assert.Equal(DiacriticFreeTokens, tokens);
    }

    [Fact]
    public void Tokenize_DropsSingleCharacterTokensByDefault()
    {
        var tokens = Tokenizer.Default.Tokenize("a b c word");

        Assert.Equal(SingleWordToken, tokens);
    }

    [Fact]
    public void Tokenize_KeepsSingleCharacterTokensWhenConfigured()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { KeepSingleCharTerms = true });

        var tokens = tokenizer.Tokenize("a b c word");

        Assert.Equal(SingleCharKeptTokens, tokens);
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

        Assert.Equal(StopWordFreeTokens, tokens);
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

        Assert.Equal(CustomStopWordTokens, tokens);
    }

    [Fact]
    public void Tokenize_GeneratesBigramsWhenConfigured()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { NGramMax = 2 });

        var tokens = tokenizer.Tokenize("machine learning rocks");

        Assert.Equal(
            BigramTokens,
            tokens);
    }

    [Fact]
    public void Tokenize_AppliesConsumerStemmerWhenConfigured()
    {
        var tokenizer = new Tokenizer(new TokenizerOptions { Stemmer = new SuffixStrippingStemmer() });

        var tokens = tokenizer.Tokenize("jumping jumped cats dogs");

        Assert.Equal(StemmedTokens, tokens);
    }

    [Fact]
    public void Normalize_HandlesMixedCaseWithAccents()
    {
        Assert.Equal("resume", Tokenizer.Normalize("Résumé"));
        Assert.Equal("e", Tokenizer.Normalize("É"));
        Assert.Equal("æ", Tokenizer.Normalize("Æ")); // ligature, left as-is by NFKD
    }
}