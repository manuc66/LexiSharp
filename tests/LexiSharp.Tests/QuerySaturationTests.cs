using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The query-side half of BM25's saturation: a repeated term is worth more than a single one, but
/// not in proportion to how many times it was written.
/// </summary>
/// <remarks>
/// The unit tests pin the curve; the engine tests pin that the curve is what actually reaches a
/// score, because a weighter that computes the right weights and a decorator that applies them
/// differently is a bug neither half would show alone.
/// </remarks>
public class QuerySaturationTests
{
    private static IReadOnlyDictionary<string, double> Weigh(params string[] terms) =>
        new SaturatingQueryTermWeighter().Weigh(terms);

    [Fact]
    public void Weigh_ReturnsNothingWhenNoTermRepeats()
    {
        // An empty map means "every weight is 1" by contract, and it is what keeps the weighter free
        // on the queries that are the overwhelming majority: no scorer copy, no allocation of a map.
        Assert.Empty(Weigh("alpha", "beta", "gamma"));
    }

    [Fact]
    public void Weigh_SaturatesARepeatedTerm()
    {
        var weights = Weigh("alpha", "alpha", "beta");

        // (k3 + 1) · 2 / (k3 + 2) = 1.8 at k3 = 8, normalized by the heaviest term.
        Assert.Equal(1.0, weights["alpha"]);
        Assert.Equal(1.0 / 1.8, weights["beta"], 12);
    }

    [Fact]
    public void Weigh_ConvergesToTheSaturationCeiling()
    {
        // Ten occurrences weigh 5× one at k3 = 8 — not 10×, and the ratio cannot pass 9× however
        // many times the word is written.
        Assert.Equal(1.0 / 5.0, Weigh(Enumerable.Repeat("alpha", 10).Append("beta").ToArray())["beta"], 12);

        var weights = Weigh(Enumerable.Repeat("alpha", 1_000).Append("beta").ToArray());
        Assert.Equal(1.0 / ((8.0 + 1.0) * 1_000 / (8.0 + 1_000)), weights["beta"], 12);
        Assert.True(weights["beta"] > 1.0 / 9.0);
    }

    [Fact]
    public void Weigh_WithAZeroConstantIsTheNoOp()
    {
        // k3 = 0 returns 1 for every term whatever its count: the control a measurement needs, on
        // the same code path, one number away from the treatment.
        var weighter = new SaturatingQueryTermWeighter(k3: 0);

        Assert.Empty(weighter.Weigh(["alpha", "alpha", "beta"]));
        Assert.Equal(0, weighter.Saturation);
    }

    [Fact]
    public void Weigh_NormalizesSoTheHeaviestTermCarriesOne()
    {
        var weights = Weigh("a", "a", "a", "b", "b", "c");

        Assert.Equal(1.0, weights["a"]);
        Assert.Equal((9.0 * 2 / 10) / (9.0 * 3 / 11), weights["b"], 12);
        Assert.Equal(1.0 / (9.0 * 3 / 11), weights["c"], 12);
        Assert.All(weights.Values, weight => Assert.InRange(weight, 1.0 / 9.0, 1.0));
    }

    [Fact]
    public void Weigh_IsEmptyForAnEmptyQuery()
    {
        Assert.Empty(new SaturatingQueryTermWeighter().Weigh([]));
    }

    [Fact]
    public void Weigh_RejectsANullTermList()
    {
        Assert.Throws<ArgumentNullException>(() => new SaturatingQueryTermWeighter().Weigh(null!));
    }

    [Theory]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Constructor_RejectsAnInvalidConstant(double k3)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SaturatingQueryTermWeighter(k3));
    }

    [Fact]
    public void Engine_LeavesAnEmphasizedTermsDocumentsScoreUntouched()
    {
        // The emphasized term is the query's heaviest, so its weight is 1 and its documents score
        // exactly as they do without the weighter — the effect is on the terms it outranks, not on it.
        var index = CreateIndex();
        double plain = Score(Plain(index), "alpha", "d1");

        double saturated = Score(
            Saturating(index, new SaturatingQueryTermWeighter()),
            string.Join(' ', Enumerable.Repeat("alpha", 10)) + " beta",
            "d1");

        Assert.Equal(plain, saturated, 12);
    }

    [Fact]
    public void Engine_DownWeightsATermWrittenOnceAgainstOneWrittenTenTimes()
    {
        var index = CreateIndex();
        double plain = Score(Plain(index), "alpha beta", "d2");

        double saturated = Score(
            Saturating(index, new SaturatingQueryTermWeighter()),
            string.Join(' ', Enumerable.Repeat("alpha", 10)) + " beta",
            "d2");

        Assert.Equal(plain / 5.0, saturated, 12);
    }

    [Fact]
    public void Engine_SaturatesWhereQueryFrequencyWouldBeLinear()
    {
        // The setting this weighter replaces: QueryFrequency multiplies a term's idf by how often it
        // was written, so ten occurrences pay ten times. The two must not be combined, and the
        // difference is the point of the class.
        var index = CreateIndex();
        string query = string.Join(' ', Enumerable.Repeat("alpha", 10)) + " beta";

        double plain = Score(Plain(index), "alpha beta", "d1");
        double linear = Score(
            new RankedTextSearchEngine(index, new Bm25Scorer(queryTermWeighting: QueryTermWeighting.QueryFrequency)),
            query,
            "d1");

        Assert.Equal(plain * 10.0, linear, 6);
        Assert.Equal(plain, Score(Saturating(index, new SaturatingQueryTermWeighter()), query, "d1"), 12);
    }

    private static InMemoryTextIndex CreateIndex()
    {
        var index = new InMemoryTextIndex();
        index.Add(new SearchDocument("d1", string.Join(' ', Enumerable.Repeat("alpha", 10))));
        index.Add(new SearchDocument("d2", "beta"));
        index.Add(new SearchDocument("d3", "gamma delta epsilon"));
        return index;
    }

    /// <summary>The plain BM25 pipeline, for the comparison each test makes.</summary>
    private static ITextSearchEngine Plain(ITextIndex index) =>
        new RankedTextSearchEngine(index, new Bm25Scorer());

    /// <summary>BM25 with the weighter applied per query through its public decorator.</summary>
    private static ITextSearchEngine Saturating(ITextIndex index, IQueryTermWeighter weighter) =>
        new ReweightingTextSearchEngine(index, new Bm25Scorer(), weighter, Tokenizer.Default);

    private static double Score(ITextSearchEngine engine, string query, string documentId) =>
        engine.Search(query).Single(result => result.DocumentId == documentId).Score;
}
