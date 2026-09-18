using LexiSharp.Similarity;
using Xunit;

namespace LexiSharp.Tests;

public class LexicalSimilarityTests
{
    [Fact]
    public void Jaccard_IdenticalTexts_ArePerfectlySimilar()
    {
        Assert.Equal(1, LexicalSimilarity.Jaccard("apple pie recipe", "apple pie recipe"), 6);
    }

    [Fact]
    public void Jaccard_DisjointTexts_ScoreZero()
    {
        Assert.Equal(0, LexicalSimilarity.Jaccard("apple pie recipe", "kubernetes cluster"), 6);
    }

    [Fact]
    public void Jaccard_IgnoresCaseDiacriticsAndRepetition()
    {
        // Normalization is the library's own: "Café" == "cafe", repeated tokens count once.
        Assert.Equal(1, LexicalSimilarity.Jaccard("Café crème", "cafe CAFÉ crème"), 6);
    }

    [Fact]
    public void Jaccard_PartialOverlap()
    {
        // Tokens: {search, engine} vs {search, ranker} → 1 shared / 3 union.
        Assert.Equal(1.0 / 3.0, LexicalSimilarity.Jaccard("search engine", "search ranker"), 6);
    }

    [Fact]
    public void SorensenDice_IsMoreForgivingThanJaccard_OnSmallOverlaps()
    {
        double jaccard = LexicalSimilarity.Jaccard("search engine", "search ranker");
        double dice = LexicalSimilarity.SorensenDice("search engine", "search ranker");

        // 2·1/(2+2) = 0.5 > 1/3.
        Assert.Equal(0.5, dice, 6);
        Assert.True(dice > jaccard);
    }

    [Fact]
    public void Trigram_NearDuplicatesScoreHigh()
    {
        double score = LexicalSimilarity.Trigram("kubernetes cluster", "kubernetes clusters");

        Assert.True(score > 0.8, $"expected > 0.8 but got {score}");
    }

    [Fact]
    public void Trigram_DistantTextsScoreLow()
    {
        double score = LexicalSimilarity.Trigram("kubernetes cluster", "banana smoothie");

        Assert.True(score < 0.3, $"expected < 0.3 but got {score}");
    }

    [Fact]
    public void Trigram_IsCaseAndAccentInsensitive()
    {
        Assert.Equal(1, LexicalSimilarity.Trigram("Café Résumé", "cafe resume"), 6);
    }

    [Fact]
    public void Trigram_PaddingLetsBoundariesCount()
    {
        // "ab" vs "ab c": without padding the two trigram sets would be empty; with padding,
        // "  a" and "ab " etc. give the pair a clearly imperfect score.
        double score = LexicalSimilarity.Trigram("ab", "ab c");

        Assert.True(score > 0 && score < 1, $"expected (0, 1) but got {score}");
    }

    [Fact]
    public void EmptyInputs_AreHandledSymmetrically()
    {
        Assert.Equal(1, LexicalSimilarity.Jaccard("", ""), 6);
        Assert.Equal(0, LexicalSimilarity.Jaccard("", "word"), 6);
        Assert.Equal(1, LexicalSimilarity.SorensenDice("", ""), 6);
        Assert.Equal(0, LexicalSimilarity.SorensenDice("word", ""), 6);
        Assert.Equal(1, LexicalSimilarity.Trigram("", ""), 6);
        Assert.Equal(0, LexicalSimilarity.Trigram("", "word"), 6);
    }

    [Fact]
    public void Levenshtein_IdenticalStrings_AreAtDistanceZero()
    {
        Assert.Equal(0, LevenshteinDistance.Distance("kitten", "kitten"));
    }

    [Fact]
    public void Levenshtein_ClassicSubstitutions()
    {
        Assert.Equal(3, LevenshteinDistance.Distance("kitten", "sitting"));
    }

    [Fact]
    public void Levenshtein_IsSymmetric_AndHandlesEmptyInputs()
    {
        Assert.Equal(LevenshteinDistance.Distance("abc", "ax"),
            LevenshteinDistance.Distance("ax", "abc"));
        Assert.Equal(4, LevenshteinDistance.Distance("", "abcd"));
        Assert.Equal(0, LevenshteinDistance.Distance("", ""));
    }

    [Fact]
    public void Levenshtein_PureInsertionAndDeletion()
    {
        Assert.Equal(2, LevenshteinDistance.Distance("abc", "abcde"));
        Assert.Equal(2, LevenshteinDistance.Distance("abcde", "abc"));
    }
}
