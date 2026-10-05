using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the per-query cost sheet: that it stays out of the way when unattached, that it bounds
/// itself, and — the part that can go quietly wrong — that the count it reports is the work paid
/// for rather than the hits that survived.
/// </summary>
/// <remarks>
/// Two scoring paths produce that count, and they disagree about what a candidate is. The
/// term-at-a-time pass scores exactly the documents the accumulator recorded — the ones sharing at
/// least one query term — while the per-document loop either scores a candidate set or scans the
/// corpus, and scores every document it walks past, including the ones that come back zero. So the
/// same query on two corpus shapes yields two different honest counts, and a test that only pins
/// one path would let the other drift. Both are exercised here, each from a fixture built to reach
/// the path it claims to test.
/// </remarks>
public class SearchCostsTests
{
    /// <summary>
    /// Six documents, half of them carrying the query term.
    /// </summary>
    /// <remarks>
    /// The shape is load-bearing in two ways. <c>SumDocumentFrequencies</c> returns 3 for a corpus
    /// of 6, and 3/6 is not below the 0.5 the engine wants to take the candidate path, so it walks
    /// the corpus and scores all six — three of which match. And 3 is below the accumulation
    /// floor, so the term-at-a-time pass declines first and never gets to have its own opinion.
    /// </remarks>
    private static InMemoryTextIndex CreateFullScanCorpus()
    {
        var index = new InMemoryTextIndex();

        for (int i = 0; i < 3; i++)
            index.Add(new SearchDocument($"match-{i:D2}", "zebra crosses the plain"));

        for (int i = 0; i < 3; i++)
            index.Add(new SearchDocument($"other-{i:D2}", "marmot watches the plain"));

        return index;
    }

    /// <summary>
    /// Twenty documents, nineteen of them carrying the query term.
    /// </summary>
    /// <remarks>
    /// Nineteen is above both thresholds the fast path has to clear — the accumulation floor of
    /// <c>max(8, ordinalSpace / 20_000)</c>, which is 8 here — and the 0.5 fraction at which the
    /// engine stops walking the corpus. The one document without the term is what makes the two
    /// paths tell each other apart: the accumulator never sees it, so it is recorded 19, where a
    /// corpus scan would have recorded 20.
    /// </remarks>
    private static InMemoryTextIndex CreateAccumulatingCorpus()
    {
        var index = new InMemoryTextIndex();

        for (int i = 0; i < 19; i++)
            index.Add(new SearchDocument($"match-{i:D2}", "zebra crosses the plain"));

        index.Add(new SearchDocument("absent-00", "marmot watches the plain"));

        return index;
    }

    private static RankedTextSearchEngine CreateEngine(InMemoryTextIndex index) =>
        new(index, new Bm25Scorer());

    [Fact]
    public void SearchOptions_WithoutASheet_CostsNothing()
    {
        Assert.Null(SearchOptions.Default.Costs);
    }

    [Fact]
    public void Constructor_WithANonPositiveCapacity_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchCosts(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SearchCosts(-1));
    }

    [Fact]
    public void Record_BeyondTheCapacity_DropsAndSaysSo()
    {
        var costs = new SearchCosts(capacity: 1);

        costs.Record(new SearchCostStage("score", 1, 0, 0.1));
        costs.Record(new SearchCostStage("retrieve", 2, 0, 0.2));
        costs.Record(new SearchCostStage("rerank", 3, 0, 0.3));

        SearchCostStage only = Assert.Single(costs.Stages);
        Assert.Equal("score", only.Stage);
        Assert.Equal(2, costs.Dropped);
        Assert.True(costs.IsTruncated);
    }

    [Fact]
    public void AddTokens_SumsTheCountsAndRefusesANegativeOne()
    {
        var costs = new SearchCosts();

        costs.AddTokens(3);
        costs.AddTokens(4);

        Assert.Equal(7, costs.Tokens);
        Assert.Throws<ArgumentOutOfRangeException>(() => costs.AddTokens(-1));
    }

    [Fact]
    public void Scoring_CostsTheTokensTheQueryWasParsedInto()
    {
        var costs = new SearchCosts();
        var engine = CreateEngine(CreateFullScanCorpus());

        engine.Search("zebra plain", new SearchOptions(Limit: 10, Costs: costs));

        Assert.Equal(2, costs.Tokens);
    }

    [Fact]
    public void Scoring_RecordsTheDocumentsItScored_NotTheOnesThatMatched()
    {
        var costs = new SearchCosts();
        var engine = CreateEngine(CreateFullScanCorpus());

        var results = engine.Search("zebra", new SearchOptions(Limit: 10, Costs: costs));

        // Three documents match and three do not; the three that scored zero were still paid for.
        Assert.Equal(3, results.Count);
        Assert.Equal(6, Assert.Single(costs.Stages).ItemCount);
    }

    [Fact]
    public void Scoring_OnTheAccumulatingPath_CountsTheDocumentsTheAccumulatorRecorded()
    {
        var costs = new SearchCosts();
        var engine = CreateEngine(CreateAccumulatingCorpus());

        engine.Search("zebra", new SearchOptions(Limit: 10, Costs: costs));

        // Nineteen of the twenty documents carry the term, and the pass scores exactly those. A
        // corpus scan would have said twenty, which is what makes this assert the path rather than
        // the arithmetic.
        Assert.Equal(19, Assert.Single(costs.Stages).ItemCount);
    }

    [Fact]
    public void Scoring_AQueryThatMatchesNothing_StillRecordsWhatItCost()
    {
        var costs = new SearchCosts();
        var engine = CreateEngine(CreateFullScanCorpus());

        var results = engine.Search("aardvark", new SearchOptions(Limit: 10, Costs: costs));

        Assert.Empty(results);
        Assert.Equal(1, costs.Tokens);

        SearchCostStage stage = Assert.Single(costs.Stages);
        Assert.Equal("score", stage.Stage);
        Assert.Equal(0, stage.ItemCount);
    }

    [Fact]
    public void Scoring_AttachingASheetDoesNotChangeWhatTheSearchReturns()
    {
        var engine = CreateEngine(CreateFullScanCorpus());
        var query = "zebra plain";

        var plain = engine.Search(query, new SearchOptions(Limit: 10));
        var costed = engine.Search(query, new SearchOptions(Limit: 10, Costs: new SearchCosts()));

        Assert.Equal(plain.Select(x => x.DocumentId), costed.Select(x => x.DocumentId));
        Assert.Equal(plain.Select(x => x.Score), costed.Select(x => x.Score));
    }

    [Fact]
    public void Scoring_WithoutASheet_RecordsNothing()
    {
        var engine = CreateEngine(CreateFullScanCorpus());
        var options = new SearchOptions(Limit: 10);

        var results = engine.Search("zebra plain", options);

        // Nothing to read back is the claim, so what is asserted is that there is no sheet to read
        // and that its absence cost the search nothing: all six documents come back, every one of
        // them carrying "plain".
        Assert.Null(options.Costs);
        Assert.Equal(6, results.Count);
    }

    [Fact]
    public void Reranking_CostsTheInnerScoringTheRetrievalAndTheRerank()
    {
        var costs = new SearchCosts();
        var inner = CreateEngine(CreateAccumulatingCorpus());
        var engine = new RerankedTextSearchEngine(inner, new DoublingReranker(), maxCandidates: 50);

        engine.Search("zebra", new SearchOptions(Limit: 5, Costs: costs));

        Assert.Equal(
            ["score", "retrieve", "rerank:double"],
            costs.Stages.Select(x => x.Stage));

        // The inner search is asked for the full candidate depth so the reranker has room, which is
        // why the retrieval row and the scoring row do not carry the same number of documents.
        SearchCostStage retrieve = costs.Stages[1];
        SearchCostStage rerank = costs.Stages[2];

        Assert.Equal(19, retrieve.ItemCount);
        Assert.Equal(19, rerank.ItemCount);
    }

    /// <summary>A reranker that only rescales, so a test can tell it apart from retrieval.</summary>
    private sealed class DoublingReranker : IReranker
    {
        public string Name => "double";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates) =>
            candidates.Select(x => x with { Score = x.Score * 2 }).ToList();
    }
}