using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Pins how a term repeated in the query reaches the scorer through the public search path.
/// </summary>
/// <remarks>
/// <para>
/// This is a regression guard for a defect that was never caught, because nothing tested it: the
/// engine deduplicated the query before handing it to <see cref="IQueryPlannableScorer"/>, and
/// <c>Bm25Scorer.Terms</c> short-circuits on a list it can prove is already distinct. So
/// <see cref="QueryTermWeighting.QueryFrequency"/> was wired to a value the engine had already
/// normalised away, and two different configurations of a public constructor produced bit-identical
/// rankings. Measured through the shipped evaluation harness on 1,406 ArguAna queries, nDCG@10 was
/// 0.285 either way, on every metric.
/// </para>
/// <para>
/// The engine keeps its deduplicated list for the two decisions that need it —
/// <c>SumDocumentFrequencies</c> and candidate enumeration — because a query repeating a term
/// thirty times would otherwise claim thirty times the reach and flip the scoring path for a query
/// matching the same documents. The two lists answer different questions; the tests below pin both
/// halves.
/// </para>
/// </remarks>
public class QueryTermWeightingTests
{
    /// <summary>
    /// The defect itself. Before the fix these two rankings were identical for every query,
    /// which is what made the setting look plausible and left it unnoticed.
    /// </summary>
    [Fact]
    public void QueryFrequency_ChangesTheRanking_ThroughTheEngine()
    {
        var index = Corpus();

        var distinct = Search(index, QueryTermWeighting.Distinct, "quick quick brown");
        var frequency = Search(index, QueryTermWeighting.QueryFrequency, "quick quick brown");

        Assert.NotEmpty(distinct);
        Assert.NotEmpty(frequency);

        Assert.False(
            Scores(distinct).SequenceEqual(Scores(frequency)),
            "QueryTermWeighting had no observable effect through RankedTextSearchEngine: a repeated " +
            "query term counted once either way");
    }

    /// <summary>
    /// The default path must be untouched by the fix. <c>Distinct</c> is the documented default and
    /// a query with no repeated term must score exactly as it did before, bit for bit.
    /// </summary>
    [Fact]
    public void ADistinctQuery_ScoresIdenticallyUnderBothSettings()
    {
        var index = Corpus();

        var distinct = Search(index, QueryTermWeighting.Distinct, "quick brown");
        var frequency = Search(index, QueryTermWeighting.QueryFrequency, "quick brown");

        Assert.Equal(distinct.Count, frequency.Count);
        Assert.Equal(
            distinct.Select(hit => hit.Document.Id),
            frequency.Select(hit => hit.Document.Id));
        Assert.Equal(Scores(distinct), Scores(frequency));
    }

    /// <summary>
    /// Not passing the argument at all must still mean <see cref="QueryTermWeighting.Distinct"/>.
    /// </summary>
    [Fact]
    public void TheParameterlessConstructor_BehavesAsDistinct()
    {
        var index = Corpus();
        var options = new SearchOptions(Limit: 10);

        var byDefault = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search("quick quick brown", options);
        var explicitDistinct = Search(index, QueryTermWeighting.Distinct, "quick quick brown");

        Assert.Equal(Scores(explicitDistinct), Scores(byDefault));
    }

    /// <summary>
    /// A query that repeats a term heavily must still return the same documents, in the same order,
    /// as the deduplicated query, under the default. This is the user-visible half of the
    /// invariant: the engine's own reach calculation counts each term once, so repetition must not
    /// change which documents are scored or how they rank.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(40)]
    public void Repetition_UnderTheDefault_DoesNotChangeTheResultSet(int repetitions)
    {
        var index = Corpus();
        string query = string.Join(' ', Enumerable.Repeat("quick", repetitions).Append("brown"));

        var repeated = Search(index, QueryTermWeighting.Distinct, query);
        var deduplicated = Search(index, QueryTermWeighting.Distinct, "quick brown");

        Assert.Equal(deduplicated, repeated);
    }

    /// <summary>
    /// A term said ten times contributes ten clauses, so documents containing it must gain, and
    /// the gain must be monotone in the number of repetitions. A document that does not contain
    /// the repeated term must be untouched, because it never had a clause for it to duplicate.
    /// </summary>
    [Fact]
    public void Repetition_UnderQueryFrequency_AccumulatesPerOccurrence()
    {
        var index = Corpus();
        var options = new SearchOptions(Limit: 10);
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(queryTermWeighting: QueryTermWeighting.QueryFrequency));

        var once = engine.Search("quick brown", options);
        var twice = engine.Search("quick quick brown", options);
        var tenTimes = engine.Search(string.Join(' ', Enumerable.Repeat("quick", 10).Append("brown")), options);

        static double ScoreOf(IReadOnlyList<SearchResult> results, string id) =>
            results.Single(hit => hit.Document.Id == id).Score;

        foreach (var id in new[] { "d1", "d3" })
        {
            double onceScore = ScoreOf(once, id);
            double twiceScore = ScoreOf(twice, id);
            double tenScore = ScoreOf(tenTimes, id);

            Assert.True(twiceScore > onceScore, $"{id} should gain from the term said twice");
            Assert.True(tenScore > twiceScore, $"{id} should gain more from the term said ten times");
        }

        // d2 holds "brown" and no "quick", so it has no clause to duplicate.
        Assert.Equal(ScoreOf(once, "d2"), ScoreOf(tenTimes, "d2"));
    }

    /// <summary>
    /// The other two BM25 families carry the same parameter and the same reasoning, and were wired
    /// the same way, so the guard applies to all three.
    /// </summary>
    [Theory]
    [InlineData("bm25")]
    [InlineData("bm25+")]
    [InlineData("bm25l")]
    public void EveryScorerExposingTheSetting_IsReachableThroughTheEngine(string family)
    {
        var index = Corpus();
        var options = new SearchOptions(Limit: 10);
        var repeated = string.Join(' ', Enumerable.Repeat("quick", 5).Append("brown"));

        var distinct = new RankedTextSearchEngine(index, WithSetting(family, QueryTermWeighting.Distinct))
            .Search(repeated, options).Select(hit => hit.Score).ToArray();
        var frequency = new RankedTextSearchEngine(index, WithSetting(family, QueryTermWeighting.QueryFrequency))
            .Search(repeated, options).Select(hit => hit.Score).ToArray();

        Assert.NotEmpty(distinct);
        Assert.False(distinct.SequenceEqual(frequency), $"{family} ignored QueryTermWeighting");
    }

    /// <summary>
    /// The families are built with their default parameters, which is all this test needs: it is
    /// asking whether the setting reaches the scorer, not what the scorer does with it.
    /// </summary>
    private static ITextScorer WithSetting(string family, QueryTermWeighting weighting) => family switch
    {
        "bm25" => new Bm25Scorer(queryTermWeighting: weighting),
        "bm25+" => new Bm25PlusScorer(queryTermWeighting: weighting),
        _ => new Bm25LScorer(queryTermWeighting: weighting),
    };

    private static IReadOnlyList<SearchResult> Search(InMemoryTextIndex index, QueryTermWeighting weighting, string query) =>
        new RankedTextSearchEngine(index, new Bm25Scorer(queryTermWeighting: weighting))
            .Search(query, new SearchOptions(Limit: 10));

    private static double[] Scores(IReadOnlyList<SearchResult> results) =>
        results.Select(hit => hit.Score).ToArray();

    private static InMemoryTextIndex Corpus()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("d1", "the quick brown fox jumps over the lazy dog"));
        index.Add(new SearchDocument("d2", "a slow brown bear sleeps all day in the sun"));
        index.Add(new SearchDocument("d3", "quick quick quick brown rabbit"));
        return index;
    }
}
