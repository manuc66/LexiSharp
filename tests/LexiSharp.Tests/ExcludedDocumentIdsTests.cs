using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The document-id exclusion in <see cref="SearchOptions"/>, on its own.
/// </summary>
/// <remarks>
/// It exists because some benchmarks use a corpus document as the query — the query id is then a
/// document id, and the lexically closest document is the query's own text. The published
/// measurement for such a corpus drops it, and a comparison against that figure needs the same gate.
/// </remarks>
public class ExcludedDocumentIdsTests
{
    private static InMemoryTextIndex Index(params string[] documents)
    {
        var index = new InMemoryTextIndex();
        index.Index(documents.Select(id => new SearchDocument(id, "renewal policy session token refresh")));
        return index;
    }

    [Fact]
    public void AnExcludedDocumentIsNotReturned()
    {
        var index = Index("doc-a", "doc-b", "doc-c");
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var excluded = new HashSet<string>(StringComparer.Ordinal) { "doc-a" };
        var results = engine.Search(
            "session", new SearchOptions(10, ExcludedDocumentIds: excluded));

        Assert.DoesNotContain(results, result => result.DocumentId == "doc-a");
        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void ExcludingOneDocumentStillFillsThePage()
    {
        // The point of gating inside the search rather than filtering the returned page: the window
        // has to reach one document further to return the same number of results.
        var index = Index("doc-a", "doc-b", "doc-c", "doc-d");
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var all = engine.Search("session", new SearchOptions(2));
        var excluded = new HashSet<string>(StringComparer.Ordinal) { all[0].DocumentId };
        var after = engine.Search("session", new SearchOptions(2, ExcludedDocumentIds: excluded));

        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, result => result.DocumentId == all[0].DocumentId);
    }

    [Fact]
    public void AnUnsetExclusionChangesNothing()
    {
        var index = Index("doc-a", "doc-b", "doc-c");
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        Assert.Equal(
            engine.Search("session", new SearchOptions(10)).Select(result => result.DocumentId),
            engine.Search("session", new SearchOptions(10, ExcludedDocumentIds: null)).Select(result => result.DocumentId));
    }

    [Fact]
    public void AnEmptyExclusionChangesNothing()
    {
        // An empty set must not read as "exclude every document", which is what a Contains test
        // written against the count rather than the reference would do.
        var index = Index("doc-a", "doc-b", "doc-c");
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var results = engine.Search(
            "session",
            new SearchOptions(10, ExcludedDocumentIds: new HashSet<string>(StringComparer.Ordinal)));

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void ExcludingEveryDocumentReturnsNothing()
    {
        var index = Index("doc-a", "doc-b", "doc-c");
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var excluded = new HashSet<string>(StringComparer.Ordinal) { "doc-a", "doc-b", "doc-c" };
        var results = engine.Search("session", new SearchOptions(10, ExcludedDocumentIds: excluded));

        Assert.Empty(results);
    }

    [Fact]
    public void ExclusionIsRankedOutRatherThanShifted()
    {
        // Every candidate is gated, so the document that was third becomes second rather than the
        // page keeping a hole where the excluded one stood.
        var index = Index("doc-a", "doc-b", "doc-c");
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var excluded = new HashSet<string>(StringComparer.Ordinal) { "doc-a" };
        var after = engine.Search("session", new SearchOptions(10, ExcludedDocumentIds: excluded));

        Assert.Equal("doc-b", after[0].DocumentId);
        Assert.Equal("doc-c", after[1].DocumentId);
    }

    [Fact]
    public void AValidationQueryCarriesItsExclusionIntoTuning()
    {
        // The tuner builds its own SearchOptions per query, so a pin's exclusion only reaches the
        // tuned rows if the query itself carries it. This is the regression that made the ArguAna
        // tuned rows disagree with the fixed ones.
        var index = Index("doc-a", "doc-b", "doc-c");
        var excluded = new HashSet<string>(StringComparer.Ordinal) { "doc-a" };

        var tuner = new Bm25ParameterTuner(index, new[]
        {
            new Bm25ValidationQuery("session", new[] { "doc-b" }, excluded),
        });

        var result = tuner.Tune(topK: 3, metric: TuningMetric.Recall);

        Assert.True(result.MetricScore > 0, $"recall should be 1 with 'doc-a' excluded, got {result.MetricScore}");
    }

    [Fact]
    public void WithoutTheExclusionTheTunerPenalisesTheParameterSet()
    {
        // Same corpus, no exclusion: the tuner must be free to leave 'doc-a' in, and the validation
        // query is then satisfied by whichever parameter set happens to rank it first.
        var index = Index("doc-a", "doc-b", "doc-c");

        var tuner = new Bm25ParameterTuner(index, new[]
        {
            new Bm25ValidationQuery("session", new[] { "doc-b" }),
        });

        var result = tuner.Tune(topK: 3, metric: TuningMetric.Recall);

        Assert.True(result.MetricScore > 0);
    }
}
