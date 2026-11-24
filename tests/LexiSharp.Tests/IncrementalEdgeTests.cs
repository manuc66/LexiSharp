using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class IncrementalEdgeTests
{
    [Fact]
    public void NaiveBayes_LearnThenUnlearn_RestoresPrediction()
    {
        var nb = new NaiveBayesClassifier();
        nb.Train(new[] { new SearchDocument("1", "hello", Category: "A") });

        var before = nb.Predict("hello", limit: 3);

        nb.Learn(new SearchDocument("2", "world", Category: "B"));
        nb.Unlearn(new SearchDocument("2", "world", Category: "B"));

        var after = nb.Predict("hello", limit: 3);
        Assert.Equal(before.Count, after.Count);
        if (before.Count > 0 && after.Count > 0)
        {
            Assert.Equal(before[0].Category, after[0].Category);
        }
    }

    [Fact]
    public void NaiveBayes_UnlearnUnknown_DoesNotCrash()
    {
        var nb = new NaiveBayesClassifier();
        nb.Train(new[] { new SearchDocument("1", "hello", Category: "A") });
        nb.Unlearn(new SearchDocument("999", "ghost", Category: "Z"));
        Assert.NotNull(nb.Predict("hello", limit: 3));
    }

    [Fact]
    public void Tokenizer_LongString_DoesNotCrash()
    {
        var tokenizer = Tokenizer.Default;
        var longText = new string('a', 10000);
        var tokens = tokenizer.Tokenize(longText);
        Assert.NotNull(tokens);
    }
}
