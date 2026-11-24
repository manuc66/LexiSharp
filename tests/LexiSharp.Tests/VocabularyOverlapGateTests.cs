using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

public class VocabularyOverlapGateTests
{
    private static readonly SearchDocument[] Docs =
    {
        new("1", "alpha", Category: "A"),
        new("2", "beta", Category: "B"),
    };

    [Fact]
    public void FiltersCategoriesWithoutOverlap()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Docs);
        var vocab = new Dictionary<string, HashSet<string>>
        {
            ["A"] = new HashSet<string> { "alpha" },
            ["B"] = new HashSet<string> { "beta" },
        };
        var gate = new VocabularyOverlapGate(inner, Tokenizer.Default, vocab);

        var results = gate.Predict("alpha", limit: 5);
        Assert.All(results, r => Assert.Equal("A", r.Category));
    }

    [Fact]
    public void EmptyText_ReturnsEmpty()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(Docs);
        var vocab = new Dictionary<string, HashSet<string>>
        {
            ["A"] = new HashSet<string> { "alpha" },
        };
        var gate = new VocabularyOverlapGate(inner, Tokenizer.Default, vocab);

        Assert.Empty(gate.Predict("", limit: 5));
    }
}

public class RecordingClassifierTests
{
    [Fact]
    public void RecordsLastPrediction()
    {
        var inner = new NaiveBayesClassifier();
        inner.Train(new[] { new SearchDocument("1", "alpha", Category: "A") });
        var recorder = new RecordingClassifier(inner);

        _ = recorder.Predict("alpha", limit: 3);
        Assert.NotEmpty(recorder.Last);
    }
}
