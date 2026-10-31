using LexiSharp.Eval;
using Xunit;

namespace LexiSharp.Tests;

public class ClassificationTuningTests
{
    private static ClassificationDocument Doc(string id, string category) =>
        new(id, "text " + id, category);

    private static IReadOnlyList<ClassificationDocument> Corpus()
    {
        var documents = new List<ClassificationDocument>();

        foreach (string category in new[] { "A", "B", "C" })
        {
            for (int i = 0; i < 5; i++)
                documents.Add(Doc($"{category}-{i}", category));
        }

        return documents;
    }

    [Fact]
    public void HoldOut_IsDeterministic()
    {
        var first = ClassificationEvaluation.HoldOut(Corpus(), validationFraction: 0.2, seed: 7);
        var second = ClassificationEvaluation.HoldOut(Corpus(), validationFraction: 0.2, seed: 7);

        Assert.Equal(
            first.Fit.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Fit.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal));
        Assert.Equal(
            first.Validation.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal),
            second.Validation.Select(document => document.Id).OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void HoldOut_IsDisjointAndCoversEveryDocument()
    {
        var corpus = Corpus();
        var split = ClassificationEvaluation.HoldOut(corpus, validationFraction: 0.2, seed: 7);

        var all = split.Fit.Concat(split.Validation).Select(document => document.Id).ToList();

        Assert.Equal(corpus.Count, all.Count);
        Assert.Equal(corpus.Count, all.Distinct(StringComparer.Ordinal).Count());
        Assert.Empty(split.Fit.Select(document => document.Id).Intersect(
            split.Validation.Select(document => document.Id), StringComparer.Ordinal));
    }

    [Fact]
    public void HoldOut_PreservesEveryCategory()
    {
        var split = ClassificationEvaluation.HoldOut(Corpus(), validationFraction: 0.2, seed: 7);

        foreach (string category in new[] { "A", "B", "C" })
        {
            Assert.Contains(split.Fit, document => document.Category == category);
            Assert.Contains(split.Validation, document => document.Category == category);
        }
    }

    [Fact]
    public void HoldOut_WithASingleDocumentCategory_KeepsItInFit()
    {
        var split = ClassificationEvaluation.HoldOut(
            new[] { Doc("solo", "S"), Doc("a", "A"), Doc("b", "A") },
            validationFraction: 0.5,
            seed: 7);

        Assert.Contains(split.Fit, document => document.Id == "solo");
        Assert.DoesNotContain(split.Validation, document => document.Id == "solo");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void HoldOut_WithAnInvalidFraction_Throws(double fraction)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ClassificationEvaluation.HoldOut(Corpus(), fraction, seed: 7));
    }

    [Fact]
    public void SelectBest_PicksTheHighestScore()
    {
        var candidates = new (string Candidate, double Score)[]
        {
            ("a", 0.5),
            ("b", 0.7),
            ("c", 0.6),
        };

        string best = ClassificationEvaluation.SelectBest(candidates, candidate => candidate);

        Assert.Equal("b", best);
    }

    [Fact]
    public void SelectBest_BreaksATieByTheOrdinalKey()
    {
        var candidates = new (string Candidate, double Score)[]
        {
            ("b", 0.7),
            ("a", 0.7),
            ("c", 0.5),
        };

        string best = ClassificationEvaluation.SelectBest(candidates, candidate => candidate);

        Assert.Equal("a", best);
    }

    [Fact]
    public void SelectBest_WithNoCandidates_Throws()
    {
        Assert.Throws<ArgumentException>(
            () => ClassificationEvaluation.SelectBest(Array.Empty<(string, double)>(), candidate => candidate));
    }
}
