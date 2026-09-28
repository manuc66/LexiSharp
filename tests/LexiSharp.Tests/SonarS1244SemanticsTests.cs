using LexiSharp.Benchmarking;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

// CA1861 ("prefer a static readonly field over a constant array argument") is suppressed on
// the lines below. Its premise is a call repeated with the same literal, allocating each
// time. These are one-shot fixtures, and the literal belongs beside the assertion that reads
// it -- hoisting it into a field saves nothing that is measured, and moves the data away
// from the test that fails on it.

namespace LexiSharp.Tests;

/// <summary>
/// The three places where a <c>double</c> is compared with <c>==</c> or <c>!=</c> against a literal,
/// each carrying a <c>NOSONAR:S1244</c>. Sonar is right that this is usually a bug; these are the
/// cases where it is the requirement, and this file exists so that a future editor who "fixes" one
/// of them finds a red test instead of only a suppressed warning.
/// </summary>
/// <remarks>
/// The property under test in all three is the same: <b>an exact comparison must stay exact</b>. Each
/// site is a classifier, not a measurement — binary versus graded, tie versus genuine re-ordering,
/// equal score versus a score that differs in the last bits. A tolerance would make each answer
/// depend on an arbitrary epsilon and would blur precisely the distinction the caller relies on.
/// </remarks>
public class SonarS1244SemanticsTests
{
    /// <summary>
    /// <c>BenchmarkQuery.IsGraded</c>: "are these judgments graded, or all-binary?" Gains are read
    /// from a qrels file, so 1.0 is exactly representable and the question has an exact answer.
    /// </summary>
    [Fact]
    public void IsGradedClassifiesExactlyRatherThanWithinATolerance()
    {
        // A hair above 1.0. Under an epsilon this would be reported as binary, and a report would
        // then treat a graded nDCG as comparable with a binary one -- the exact confusion the
        // property exists to prevent.
        var barelyGraded = new BenchmarkQuery("q1", "query", new Dictionary<string, double>
        {
            ["a"] = 1.0 + 1e-9,
        });

        Assert.True(barelyGraded.IsGraded);

        // And the other direction: gains that are all exactly 1.0 must not read as graded.
        var binary = new BenchmarkQuery("q2", "query", new Dictionary<string, double>
        {
            ["a"] = 1.0,
            ["b"] = 1.0,
        });

        Assert.False(binary.IsGraded);
    }

    /// <summary>
    /// <c>BenchmarkQueryResult.TiesWithNeighbour</c>: did a document move only because it was tied?
    /// A swap between equal scores is a tie; a swap between scores differing in the last bits is a
    /// real change and must be reported as one.
    /// </summary>
    [Fact]
    public void ATieIsBitwiseEqualityAndALastBitDifferenceIsNotATie()
    {
        // Two documents whose scores differ by one unit in the last place. The golden master calls
        // this a genuine re-ordering, and that is the behaviour worth protecting: an epsilon here
        // would have excused the unstable-PMI-sort defect that e81c02c fixed.
        var almostTied = new BenchmarkQueryResult(
            "q1",
            "query",
            Metrics(0.5),
            new[] { "a", "b" }, // NOSONAR:CA1861
            null,
            new[] { 1.0000000000000002, 1.0 }); // NOSONAR:CA1861

        Assert.False(almostTied.TiesWithNeighbour(0));
        Assert.False(almostTied.TiesWithNeighbour(1));

        // Exactly equal, as produced by two documents with identical text and length.
        var tied = new BenchmarkQueryResult(
            "q2",
            "query",
            Metrics(1.0),
            new[] { "a", "b" }, // NOSONAR:CA1861
            null,
            new[] { 2.5, 2.5 }); // NOSONAR:CA1861

        Assert.True(tied.TiesWithNeighbour(0));
        Assert.True(tied.TiesWithNeighbour(1));

        // An unknown score is not evidence of a tie.
        var unknown = new BenchmarkQueryResult("q3", "query", Metrics(1.0), new[] { "a" }, null); // NOSONAR:CA1861

        Assert.False(unknown.TiesWithNeighbour(0));
    }

    private static BenchmarkMetrics Metrics(double value) => new(value, value, value, value, value, value);

    /// <summary>
    /// <c>RankedTextSearchEngine</c>: equal scores are ordered by document id, and scores that
    /// differ are ordered by the score, however small the difference.
    /// </summary>
    [Fact]
    public void EqualScoresRankByDocumentIdButNearEqualScoresKeepTheirRanking()
    {
        // zulu outscores alpha, and also sorts later alphabetically. So the two orderings disagree,
        // which is what gives the assertion teeth: a tolerance in the tie test would call the pair
        // equal and impose the id order (alpha, zulu), discarding the score the scorer returned.
        // This is the shape of the case the golden master refused to excuse as a tie
        // ("re-ordered X and Y without a tie"), and the reason ScoresAreTied is not a comparison
        // against a tolerance.
        Assert.Equal(["zulu", "alpha"], Ids(Rank(zuluScore: 1.0 + 5e-13)));

        // The score branch is checked before the tie branch, so a difference of any size wins over
        // the id -- including one unit in the last place.
        Assert.Equal(["zulu", "alpha"], Ids(Rank(zuluScore: 1.0 + 2.220446049250313E-16)));

        // Only genuinely equal scores fall through to the id, which is what makes the order total
        // and independent of how the corpus was enumerated.
        Assert.Equal(["alpha", "zulu"], Ids(Rank(zuluScore: 1.0)));
    }

    private static string[] Ids(IReadOnlyList<SearchResult> results) =>
        results.Select(result => result.DocumentId).ToArray();

    /// <summary>Ranks two documents whose scores differ by <paramref name="zuluScore"/> - 1.0.</summary>
    private static IReadOnlyList<SearchResult> Rank(double zuluScore)
    {
        var index = new InMemoryTextIndex();

        // "zulu" is inserted first on purpose, so an enumeration-order tie-break would put it ahead
        // of "alpha" and the assertion would pass for the wrong reason.
        index.Index(
        [
            new SearchDocument("zulu", "term statistics"),
            new SearchDocument("alpha", "term statistics"),
        ]);

        return new RankedTextSearchEngine(index, new TwoDocumentScorer(zuluScore)).Search("term");
    }

    /// <summary>
    /// A scorer giving "zulu" the requested score and "alpha" exactly 1.0. Stands in for a real
    /// BM25 pair, which is what the engine sees; the point under test is the engine's ordering, not
    /// the arithmetic that produced the scores.
    /// </summary>
    private sealed class TwoDocumentScorer(double zuluScore) : ITextScorer
    {
        public string Name => "TwoDocument";

        public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index) =>
            documentId == "zulu" ? zuluScore : 1.0;
    }
}
