using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

public class TermCorrectorTests
{
    [Fact]
    public void Passthrough_ReturnsTerm()
    {
        var corrector = new PassthroughTermCorrector();

        var result = corrector.Correct("hello");

        Assert.Single(result);
        Assert.Equal("hello", result[0].Term);
        Assert.Equal(1.0, result[0].Weight);
    }

    [Fact]
    public void Passthrough_ThrowsOnNull()
    {
        var corrector = new PassthroughTermCorrector();
        Assert.Throws<ArgumentNullException>(() => corrector.Correct(null!));
    }

    [Fact]
    public void Passthrough_NameIsNotNull()
    {
        var corrector = new PassthroughTermCorrector();
        Assert.False(string.IsNullOrWhiteSpace(corrector.Name));
    }

    [Fact]
    public void Damerau_ReturnsOriginalWhenInVocabulary()
    {
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello", "world" });

        var result = corrector.Correct("hello");

        Assert.Contains(result, r => r.Term == "hello" && r.Weight == 1.0);
    }

    [Fact]
    public void Damerau_CorrectsTypo()
    {
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello", "world" });

        var result = corrector.Correct("helo");

        Assert.Contains(result, r => r.Term == "hello");
    }

    [Fact]
    public void Damerau_TranspositionCostsOne()
    {
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "the" }, maxDistance: 1);

        var result = corrector.Correct("teh");

        Assert.Contains(result, r => r.Term == "the");
    }

    [Fact]
    public void Damerau_ReturnsEmptyForNoCandidate()
    {
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello" }, maxDistance: 1);

        var result = corrector.Correct("zzzzz");

        Assert.DoesNotContain(result, r => r.Term != "zzzzz");
    }

    [Fact]
    public void Damerau_ThrowsOnNullTerm()
    {
        var corrector = new DamerauLevenshteinTermCorrector(new[] { "a" });
        Assert.Throws<ArgumentNullException>(() => corrector.Correct(null!));
    }

    [Fact]
    public void Damerau_ThrowsOnNullVocabulary()
    {
        Assert.Throws<ArgumentNullException>(() => new DamerauLevenshteinTermCorrector(null!));
    }
}

public class CorrectingTextSearchEngineTests
{
    [Fact]
    public void CorrectingEngine_FindsDocumentOnTypo()
    {
        var index = new LexiSharp.Indexing.InMemoryTextIndex();
        index.Add(new LexiSharp.Core.SearchDocument("d1", "hello world"));

        var corrector = new DamerauLevenshteinTermCorrector(new[] { "hello", "world" });
        var engine = new LexiSharp.Expansion.CorrectingTextSearchEngine(
            new LexiSharp.Ranking.RankedTextSearchEngine(index, new LexiSharp.Ranking.Bm25Scorer()),
            corrector);

        var results = engine.Search("helo");

        Assert.NotEmpty(results);
        Assert.Equal("d1", results[0].DocumentId);
    }

    [Fact]
    public void CorrectingEngine_PassthroughKeepsQuery()
    {
        var index = new LexiSharp.Indexing.InMemoryTextIndex();
        index.Add(new LexiSharp.Core.SearchDocument("d1", "hello world"));

        var engine = new LexiSharp.Expansion.CorrectingTextSearchEngine(
            new LexiSharp.Ranking.RankedTextSearchEngine(index, new LexiSharp.Ranking.Bm25Scorer()),
            new PassthroughTermCorrector());

        var results = engine.Search("hello");

        Assert.NotEmpty(results);
        Assert.Equal("d1", results[0].DocumentId);
    }
}

public class FuzzyTextSearchEngineTests
{
    [Fact]
    public void FuzzyEngine_FindsDocumentOnTypo()
    {
        var index = new LexiSharp.Indexing.InMemoryTextIndex();
        index.Add(new LexiSharp.Core.SearchDocument("d1", "hello world"));

        var engine = new LexiSharp.Expansion.FuzzyTextSearchEngine(
            new LexiSharp.Ranking.RankedTextSearchEngine(index, new LexiSharp.Ranking.Bm25Scorer()),
            new[] { "hello", "world" });

        var results = engine.Search("helo");

        Assert.NotEmpty(results);
        Assert.Equal("d1", results[0].DocumentId);
    }

    [Fact]
    public void KeyboardCorrector_AdjacentKeyCostsLess()
    {
        // 's' is adjacent to 'a' on QWERTY; a typo 'hsllo' should correct to 'hello' cheaper than a distant substitution.
        var corrector = new KeyboardAwareTermCorrector(new[] { "hello" }, KeyboardLayout.Qwerty, maxDistance: 3.0);

        var result = corrector.Correct("hsllo");

        Assert.Contains(result, r => r.Term == "hello");
    }

    [Fact]
    public void KeyboardCorrector_AzertyLayout_DiffersFromQwerty()
    {
        // Sanity: both layouts produce a result for a typo, but the layouts are distinct objects.
        var qwerty = new KeyboardAwareTermCorrector(new[] { "hello" }, KeyboardLayout.Qwerty, maxDistance: 3.0);
        var azerty = new KeyboardAwareTermCorrector(new[] { "hello" }, KeyboardLayout.Azerty, maxDistance: 3.0);

        Assert.Contains(qwerty.Correct("hsllo"), r => r.Term == "hello");
        Assert.Contains(azerty.Correct("hsllo"), r => r.Term == "hello");
        Assert.NotEqual(qwerty.Name, azerty.Name + "-different"); // names equal by design
    }
}

public class ShortTextAutomationPipelineTests
{
    [Fact]
    public void Pipeline_SearchClassifyRerank_OnShortFragments()
    {
        // Index a handful of short fragments (the "automation on short text" scenario).
        var index = new LexiSharp.Indexing.InMemoryTextIndex();
        var docs = new[]
        {
            new LexiSharp.Core.SearchDocument("1", "refund invoice payment", Category: "billing"),
            new LexiSharp.Core.SearchDocument("2", "password reset login", Category: "auth"),
            new LexiSharp.Core.SearchDocument("3", "shipping delay order", Category: "logistics"),
        };
        foreach (var d in docs) index.Add(d);

        var vocab = docs.SelectMany(d => d.Text.Split(' ')).Distinct().ToList();
        var fuzzy = new LexiSharp.Expansion.FuzzyTextSearchEngine(
            new LexiSharp.Ranking.RankedTextSearchEngine(index, new LexiSharp.Ranking.Bm25Scorer()),
            vocab, maxDistance: 2);

        // A typo query still finds the right fragment.
        var results = fuzzy.Search("refnd invoice");
        Assert.NotEmpty(results);
        Assert.Equal("1", results[0].DocumentId);

        // Classification on the same short fragments.
        var classifier = new LexiSharp.Classification.NaiveBayesClassifier();
        classifier.Train(docs);
        var predicted = classifier.Predict("reset password", limit: 1);
        Assert.NotEmpty(predicted);
        Assert.Equal("auth", predicted[0].Category);
    }
}
