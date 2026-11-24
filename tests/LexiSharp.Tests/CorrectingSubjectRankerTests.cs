using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Expansion;
using Xunit;

namespace LexiSharp.Tests;

public class CorrectingSubjectRankerTests
{
    [Fact]
    public void CorrectsUnknownTerm()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingSubjectRanker(inner, corrector, new[] { "hello" });

        var results = ranker.Predict("helo", limit: 3);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void KnownTerm_Unchanged()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" });
        var ranker = new CorrectingSubjectRanker(inner, corrector, new[] { "hello" });

        var results = ranker.Predict("hello", limit: 3);
        Assert.NotEmpty(results);
    }
}
