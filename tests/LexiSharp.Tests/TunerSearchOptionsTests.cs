using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The optional <see cref="SearchOptions"/> template on the four parameter tuners.
/// </summary>
/// <remarks>
/// The template exists because a tuner that scores its candidates with the library defaults and a
/// harness that reports with other options are not measuring the same reading of a query. The
/// property that matters most is the default: <c>null</c> has to be the same run the tuners performed
/// before the parameter existed, because every published tuned row was fitted through it.
/// </remarks>
public class TunerSearchOptionsTests
{
    private static InMemoryTextIndex SyntaxFreeIndex()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument(
                "a", "alpha beta",
                TextFields: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "alpha" }),
            new SearchDocument(
                "b", "beta gamma",
                TextFields: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "beta" }),
            new SearchDocument(
                "c", "alpha gamma",
                TextFields: new Dictionary<string, string>(StringComparer.Ordinal) { ["title"] = "gamma" }),
        ]);

        return index;
    }

    private static Bm25ValidationQuery[] SyntaxFreeQueries() =>
    [
        new("alpha", ["a", "c"]),
        new("gamma", ["b", "c"]),
    ];

    private static SearchOptions LiteralQueries() => new(ParseQuerySyntax: false);

    private static void AssertSameScores<T>(
        IEnumerable<T> expected,
        IEnumerable<T> actual,
        Func<T, (double K1, double B, double MetricScore)> shape)
    {
        Assert.Equal(expected.Select(shape), actual.Select(shape));
    }

    /// <summary>
    /// On a corpus whose queries carry no syntax, the default and an explicit literal-reading
    /// template are the same search: the same grid, point for point, and the same winner.
    /// </summary>
    [Fact]
    public void DefaultOptionsAndAnExplicitTemplateAgreeOnASyntaxFreeCorpus()
    {
        var index = SyntaxFreeIndex();
        var validation = SyntaxFreeQueries();

        var plain = new Bm25ParameterTuner(index, validation)
            .Tune(k1Values: [0.5, 1.5], bValues: [0.0, 0.5, 1.0], topK: 2);
        var plainLiteral = new Bm25ParameterTuner(index, validation, searchOptions: LiteralQueries())
            .Tune(k1Values: [0.5, 1.5], bValues: [0.0, 0.5, 1.0], topK: 2);

        Assert.Equal(plain.Parameters, plainLiteral.Parameters);
        AssertSameScores(plain.Grid, plainLiteral.Grid, point => (point.K1, point.B, point.MetricScore));

        var plus = new Bm25PlusParameterTuner(index, validation)
            .Tune(k1Values: [0.5, 1.5], bValues: [0.0, 0.5], deltaValues: [0.0, 0.5], topK: 2);
        var plusLiteral = new Bm25PlusParameterTuner(index, validation, searchOptions: LiteralQueries())
            .Tune(k1Values: [0.5, 1.5], bValues: [0.0, 0.5], deltaValues: [0.0, 0.5], topK: 2);

        Assert.Equal(plus.Grid, plusLiteral.Grid);

        var l = new Bm25LParameterTuner(index, validation)
            .Tune(k1Values: [0.5, 1.5], bValues: [0.0, 0.5], deltaValues: [0.0, 0.5], topK: 2);
        var lLiteral = new Bm25LParameterTuner(index, validation, searchOptions: LiteralQueries())
            .Tune(k1Values: [0.5, 1.5], bValues: [0.0, 0.5], deltaValues: [0.0, 0.5], topK: 2);

        Assert.Equal(l.Grid, lLiteral.Grid);

        var f = new Bm25FParameterTuner(index, validation)
            .Tune(
                k1Values: [0.5, 1.5],
                bValues: [0.0, 0.5],
                weightedFields: ["title"],
                weightValues: [1.0, 2.0],
                topK: 2);
        var fLiteral = new Bm25FParameterTuner(index, validation, searchOptions: LiteralQueries())
            .Tune(
                k1Values: [0.5, 1.5],
                bValues: [0.0, 0.5],
                weightedFields: ["title"],
                weightValues: [1.0, 2.0],
                topK: 2);

        Assert.Equal(f.Parameters.K1, fLiteral.Parameters.K1, 12);
        Assert.Equal(f.Parameters.B, fLiteral.Parameters.B, 12);
        AssertSameScores(f.Grid, fLiteral.Grid, point => (point.K1, point.B, point.MetricScore));
    }

    /// <summary>
    /// A quoted query is the case the two readings disagree on: the default reads it as a phrase that
    /// only the adjacent document satisfies, and the literal reading matches both documents on the
    /// words, so the parameter that puts the relevant one first becomes part of what is fitted.
    /// </summary>
    [Fact]
    public void AQuotedQueryIsAPhraseForTheDefaultAndTextForTheTemplate()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("adjacent", "the new york office opened"),
            // Both words, neither next to the other: the phrase gate rejects it, the literal reading
            // scores it on a higher term frequency. The filler is what lets length normalization
            // hand the relevant document the top slot at b = 1.
            new SearchDocument("apart", "new new zulu zulu zulu zulu zulu zulu zulu zulu york york"),
        ]);

        var validation = new[] { new Bm25ValidationQuery("\"new york\"", ["adjacent"]) };

        var parsed = new Bm25ParameterTuner(index, validation)
            .Tune(k1Values: [0.5], bValues: [0.0, 1.0], topK: 1);
        var literal = new Bm25ParameterTuner(index, validation, searchOptions: LiteralQueries())
            .Tune(k1Values: [0.5], bValues: [0.0, 1.0], topK: 1);

        // The parsed run can only retrieve the adjacent document, whatever the parameters.
        Assert.All(parsed.Grid, point => Assert.Equal(1, point.MetricScore, 12));

        // Without the phrase gate the repeated document wins where length is not normalized, so the
        // same grid now contains a point that ranks the relevant document second.
        Assert.Equal(0, literal.Grid.Single(point => point.B == 0.0).MetricScore, 12);
        Assert.Equal(1, literal.Grid.Single(point => point.B == 1.0).MetricScore, 12);

        Assert.Equal(0.0, parsed.Parameters.B, 12);
        Assert.Equal(1.0, literal.Parameters.B, 12);
    }

    /// <summary>
    /// The template supplies the reading, not the depth or the exclusions: those still come from the
    /// tuning call and from each validation query, as they did when the tuner built its options itself.
    /// </summary>
    [Fact]
    public void TheTemplateIsOverriddenForDepthAndPerQueryExclusions()
    {
        var index = new InMemoryTextIndex();
        index.Index(
        [
            new SearchDocument("one", "alpha"),
            new SearchDocument("two", "alpha"),
            new SearchDocument("three", "alpha beta"),
        ]);

        var validation = new[] { new Bm25ValidationQuery("alpha", ["one", "two"]) };

        // Both fields would break the score if they survived: a depth of one truncates the relevant
        // pair, and excluding "one" drops half of it.
        var template = new SearchOptions(
            1,
            ExcludedDocumentIds: new HashSet<string>(StringComparer.Ordinal) { "one" });

        var plain = new Bm25ParameterTuner(index, validation, searchOptions: template)
            .Tune(k1Values: [1.0], bValues: [0.5], topK: 2);
        var plus = new Bm25PlusParameterTuner(index, validation, searchOptions: template)
            .Tune(k1Values: [1.0], bValues: [0.5], deltaValues: [0.0], topK: 2);

        Assert.Equal(1, plain.MetricScore, 12);
        Assert.Equal(1, plus.MetricScore, 12);
    }
}
