using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="Bm25FScorer"/>: the field weighting it exists for, the term-overlap contract
/// its candidate fast path relies on, and the properties that must hold whatever the weights are.
/// </summary>
public class Bm25FScorerTests
{
    /// <summary>
    /// Two documents whose bodies are long and whose titles are short, both mentioning the query
    /// term in different places. D1 has it in its title, D2 only in its body.
    /// </summary>
    private static InMemoryTextIndex TitlesAndBodies()
    {
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument(
                "d1",
                "the body is long and rambles on about many unrelated subjects at considerable length",
                TextFields: new Dictionary<string, string> { ["title"] = "ranking" }),
            new SearchDocument(
                "d2",
                "the body is long and rambles on about many unrelated subjects at considerable length",
                TextFields: new Dictionary<string, string> { ["title"] = "indexing" }),
        });

        return index;
    }

    // ---- the reason the scorer exists ------------------------------------------------------------

    [Fact]
    public void WeightingATitleMovesItsDocumentUp()
    {
        var index = TitlesAndBodies();
        var weighted = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 3.0 });
        var neutral = new Bm25FScorer();

        double weightedTitleHit = weighted.Score("d1", ["ranking"], index);
        double weightedBodyHit = weighted.Score("d2", ["ranking"], index);

        // The term is in d1's title and d2's body, over equally long bodies: weighting the title
        // must lift d1 above d2.
        Assert.True(weightedTitleHit > weightedBodyHit,
            $"title hit {weightedTitleHit} should outrank body hit {weightedBodyHit}");

        // With neutral weights d1 still matches through its title, and d2 does not match at all:
        // its title is "indexing" and neither body mentions the term.
        Assert.True(neutral.Score("d1", ["ranking"], index) > 0);
        Assert.Equal(0, neutral.Score("d2", ["ranking"], index));
    }

    [Fact]
    public void AZeroWeightRemovesAFieldFromTheRanking()
    {
        // Built here rather than reusing the shared corpus: this needs the term in a body as well,
        // so that ignoring the title leaves exactly one document still matching.
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument(
                "in-title",
                "an unrelated body of text",
                TextFields: new Dictionary<string, string> { ["title"] = "ranking" }),
            new SearchDocument(
                "in-body",
                "a body that happens to discuss ranking at some length",
                TextFields: new Dictionary<string, string> { ["title"] = "something else" }),
        });

        var titleBlind = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 0.0 });

        Assert.Equal(0, titleBlind.Score("in-title", ["ranking"], index));
        Assert.True(titleBlind.Score("in-body", ["ranking"], index) > 0);

        // And with the title weighted, both match — the field is not the term's only route.
        var neutral = new Bm25FScorer();

        Assert.True(neutral.Score("in-title", ["ranking"], index) > 0);
        Assert.True(neutral.Score("in-body", ["ranking"], index) > 0);
    }

    [Fact]
    public void AnUnconfiguredFieldIsNotSilentlyIgnored()
    {
        var index = TitlesAndBodies();

        // Naming one field must leave the others participating: reading a missing key as a zero
        // weight would make the scorer rank nothing.
        var partial = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 2.0 });

        Assert.True(partial.Score("d1", ["ranking"], index) > 0);
        Assert.True(new Bm25FScorer().Score("d1", ["ranking"], index) > 0);
    }

    [Fact]
    public void AFieldAbsentFromTheWeightsKeepsANeutralWeight()
    {
        var index = TitlesAndBodies();

        // Naming a field the corpus does not have must not silently drop the real ones.
        var scorer = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["nonexistent"] = 5.0 });

        Assert.Equal(
            new Bm25FScorer().Score("d1", ["ranking"], index),
            scorer.Score("d1", ["ranking"], index),
            12);
    }

    [Fact]
    public void ABodyTheTermNeverAppearsInDoesNotAffectItsScore()
    {
        // Identical titles, wildly different bodies. The term lives only in the title, so BM25F's
        // length term — taken over the fields that contain the term — is the same for both.
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument(
                "short",
                "brief",
                TextFields: new Dictionary<string, string> { ["title"] = "obsidian guide" }),
            new SearchDocument(
                "long",
                "a considerably longer body about something else entirely, dragging on and on",
                TextFields: new Dictionary<string, string> { ["title"] = "obsidian guide" }),
        });

        var scorer = new Bm25FScorer();

        Assert.Equal(
            scorer.Score("short", ["obsidian"], index),
            scorer.Score("long", ["obsidian"], index),
            12);

        // Plain BM25 over the flattened text is the contrast: it normalizes by the whole document,
        // so the long body does drag the same term down. If this ever equals BM25F's answer, the
        // field-aware length term has stopped doing anything.
        var bm25 = new Bm25Scorer();

        Assert.NotEqual(
            bm25.Score("short", ["obsidian"], index),
            bm25.Score("long", ["obsidian"], index),
            12);
    }

    // ---- the ITermOverlapScorer contract ----------------------------------------------------------

    [Fact]
    public void ADocumentSharingNoQueryTermScoresExactlyZero()
    {
        var index = TitlesAndBodies();
        var scorer = new Bm25FScorer();

        // The candidate-generation fast path depends on this being exactly 0, not merely small.
        Assert.Equal(0, scorer.Score("d1", ["absent"], index));
        Assert.Equal(0, scorer.Score("d2", ["absent"], index));

        Assert.IsAssignableFrom<ITermOverlapScorer>(scorer);
    }

    [Fact]
    public void APlannedSearchScoresIdenticallyToAnUnplannedOne()
    {
        var index = TitlesAndBodies();
        var scorer = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 2.5 });
        var plan = ((IQueryPlannableScorer)scorer).CreatePlan(["ranking", "absent"], index);

        foreach (string id in new[] { "d1", "d2" })
        {
            Assert.Equal(
                scorer.Score(id, ["ranking", "absent"], index),
                plan.Score(id),
                15);
        }
    }

    // ---- a single-field corpus still works --------------------------------------------------------

    [Fact]
    public void ItWorksOnASingleFieldIndexAndRanksLikeAnyOtherScorer()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[]
        {
            new SearchDocument("1", "the quick brown fox"),
            new SearchDocument("2", "the lazy brown dog"),
        });

        var scorer = new Bm25FScorer();
        var engine = new RankedTextSearchEngine(index, scorer);

        var results = engine.Search("brown");

        Assert.Equal(2, results.Count);
        Assert.All(results, result => Assert.True(result.Score > 0));
    }

    [Fact]
    public void AnEmptyIndexAndAnUnknownDocumentScoreZero()
    {
        var index = TitlesAndBodies();
        var scorer = new Bm25FScorer();

        Assert.Equal(0, scorer.Score("does-not-exist", ["ranking"], index));

        var empty = new InMemoryTextIndex();
        Assert.Equal(0, scorer.Score("whatever", ["ranking"], empty));
    }

    // ---- explainability ---------------------------------------------------------------------------

    [Fact]
    public void TheExplanationSumsBackToTheScore()
    {
        var index = TitlesAndBodies();
        var scorer = new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = 2.0 });
        var terms = new[] { "ranking", "body" };

        foreach (string id in new[] { "d1", "d2" })
        {
            var explanation = scorer.Explain(id, terms, index);

            Assert.Equal(scorer.Score(id, terms, index), explanation.TotalScore, 12);
            Assert.Equal(
                explanation.TotalScore,
                explanation.Terms.Sum(term => term.Score),
                12);
        }
    }

    [Fact]
    public void TheExplanationReportsTheWeightedFrequencyAndTheParameters()
    {
        var index = TitlesAndBodies();
        var scorer = new Bm25FScorer(1.4, 0.6, new Dictionary<string, double> { ["title"] = 2.0 });

        var explanation = scorer.Explain("d1", ["ranking"], index);

        var term = Assert.Single(explanation.Terms);

        // The title holds the term once and is exactly the corpus average length, so its length
        // correction is 1 and the weighted frequency is simply the weight.
        Assert.Equal(2.0, term.TermFrequency, 6);
        Assert.Equal(1, term.DocumentFrequency);

        Assert.Equal("BM25F", explanation.Algorithm);
        Assert.Equal(1.4, explanation.Parameters["k1"]);
        Assert.Equal(0.6, explanation.Parameters["b"]);
        Assert.Equal(2.0, explanation.Parameters["weight.title"]);
    }

    [Fact]
    public void AnAbsentTermIsOmittedFromTheExplanation()
    {
        var index = TitlesAndBodies();

        var explanation = new Bm25FScorer().Explain("d1", ["ranking", "absent"], index);

        Assert.Equal("ranking", Assert.Single(explanation.Terms).Term);
    }

    // ---- the index capability it depends on -------------------------------------------------------

    [Fact]
    public void AnIndexWithoutFieldStatisticsIsRefusedByName()
    {
        var index = new InMemoryTextIndex();
        index.Index(new[] { new SearchDocument("1", "a plain single-field document") });

        // InMemoryTextIndex always tracks fields, so the refusal is asserted through the interface's
        // own default: a scorer handed an index whose HasFieldStatistics is false.
        var flat = new NoFieldStatisticsAdapter(index);
        var exception = Assert.Throws<NotSupportedException>(
            () => new Bm25FScorer().Score("1", ["plain"], flat));

        Assert.Contains("BM25F", exception.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(NoFieldStatisticsAdapter), exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParameterValidationRejectsNonsensicalValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25FScorer(k1: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Bm25FScorer(b: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Bm25FScorer(fieldWeights: new Dictionary<string, double> { ["title"] = -1 }));
        Assert.Throws<ArgumentException>(
            () => new Bm25FScorer(fieldWeights: new Dictionary<string, double> { [" "] = 1 }));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new Bm25FScorer(new Bm25FParameters(double.NaN, 0.5)));

        foreach (double bad in new[] { -1.0, double.NaN, double.PositiveInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(
                () => Bm25FParameters.Balanced.WithWeight("title", bad));
    }

    [Fact]
    public void TheParametersRecordCanCarryAWeight()
    {
        var parameters = Bm25FParameters.Balanced.WithWeight("title", 4.0).WithWeight("body", 0.5);

        var index = TitlesAndBodies();
        var fromParameters = new Bm25FScorer(parameters);
        var direct = new Bm25FScorer(
            Bm25FParameters.Balanced.K1,
            Bm25FParameters.Balanced.B,
            new Dictionary<string, double> { ["title"] = 4.0, ["body"] = 0.5 });

        Assert.Equal(
            direct.Score("d1", ["ranking"], index),
            fromParameters.Score("d1", ["ranking"], index),
            12);
    }

    /// <summary>Delegates everything but reports no per-field statistics, like a flat index.</summary>
    private sealed class NoFieldStatisticsAdapter(ITextIndex inner) : ITextIndex
    {
        public IReadOnlyCollection<SearchDocument> Documents => inner.Documents;

        public int Count => inner.Count;

        public double AverageDocumentLength => inner.AverageDocumentLength;

        public int VocabularySize => inner.VocabularySize;

        public long CorpusTokenCount => inner.CorpusTokenCount;

        public void Index(IEnumerable<SearchDocument> documents) => inner.Index(documents);

        public void Add(SearchDocument document) => inner.Add(document);

        public bool Remove(string documentId) => inner.Remove(documentId);

        public void Clear() => inner.Clear();

        public bool Contains(string documentId) => inner.Contains(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => inner.GetTerms(documentId);

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
            inner.GetTermPositions(documentId, term);

        public int DocumentFrequency(string term) => inner.DocumentFrequency(term);

        public int CorpusFrequency(string term) => inner.CorpusFrequency(term);

        public int TermFrequency(string documentId, string term) => inner.TermFrequency(documentId, term);

        public int DocumentLength(string documentId) => inner.DocumentLength(documentId);

        public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document) =>
            inner.TryGetDocument(documentId, out document);
    }
}
