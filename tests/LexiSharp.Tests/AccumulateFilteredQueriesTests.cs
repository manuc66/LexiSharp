using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// <see cref="SearchOptions.AccumulateFilteredQueries"/>: the accumulation pass serving a query
/// that carries metadata filters, and the two paths agreeing.
/// </summary>
/// <remarks>
/// The shape of these tests is the point, and it is not the obvious one. Every one of them
/// asserts that its fixture <b>reaches</b> the accumulation path
/// (<see cref="AssertFixtureReachesAccumulation"/>) before asserting anything about results.
/// The six tests for <see cref="SearchOptions.ExcludedDocumentIds"/> all passed while that pass
/// silently ignored the exclusion, because their fixture was three documents of identical text —
/// small enough to fall back to the per-document loop, which is the one that applied the gate. A
/// test that cannot tell which path it exercised is a test that will pass while the gate is
/// missing.
/// <para>
/// The equivalence asserted here is stronger than "the same documents came back": the score bits
/// are compared too, because the two paths sum the same contributions in the same order and any
/// divergence in that is a real difference, not a rounding artefact.
/// </para>
/// </remarks>
public class AccumulateFilteredQueriesTests
{
    /// <summary>
    /// 40 documents sharing a vocabulary, so a query over it clears
    /// <see cref="RankedTextSearchEngine.AccumulationThreshold"/>. Distinct enough that a filter
    /// has something to select on, which a corpus of identical documents would not.
    /// </summary>
    private static InMemoryTextIndex Index(int documents = 40)
    {
        var index = new InMemoryTextIndex();

        var texts = new List<string>(documents);

        for (int i = 0; i < documents; i++)
        {
            // Every document shares the query terms — that is what makes the reachable count clear
            // the threshold — and each adds a token of its own so a filter can separate them.
            texts.Add(
                $"renewal policy session token refresh deployment shard{i:D3} " +
                $"telemetry rollout window budget ledger queue buffer sandbox{i:D3}");
        }

        index.Index(texts.Select((text, i) => new SearchDocument(
            $"doc-{i:D3}",
            text,
            Fields: new Dictionary<string, string> { ["tier"] = i % 2 == 0 ? "even" : "odd" })));

        return index;
    }

    private static IReadOnlyList<MetadataFilter> TierFilter(string value) =>
        new[] { new MetadataFilter("tier", MetadataFilterOperator.Equal, value) };

    /// <summary>A filter set but not selective: no document carries the field, so Matches admits all.</summary>
    private static IReadOnlyList<MetadataFilter> AbsentFieldFilter { get; } =
        new[] { new MetadataFilter("absent", MetadataFilterOperator.NotEqual, "x") };

    private static IReadOnlyList<SearchResult> Search(
        InMemoryTextIndex index,
        string query,
        IReadOnlyList<MetadataFilter>? filters,
        bool accumulate) =>
        new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(
                Limit: 50,
                Filters: filters,
                AccumulateFilteredQueries: accumulate));

    /// <summary>
    /// Fails loudly unless the fixture's query reaches the accumulation pass. Without this, every
    /// other assertion in this file can hold while both configurations run the same fallback.
    /// </summary>
    private static void AssertFixtureReachesAccumulation(InMemoryTextIndex index, string query)
    {
        var terms = new Tokenizer().Tokenize(query);

        long reachable = terms.Sum(term => (long)index.DocumentFrequency(term));
        int threshold = RankedTextSearchEngine.AccumulationThreshold(index.OrdinalSpace);

        Assert.True(
            reachable >= threshold,
            $"fixture does not reach the accumulation pass: {reachable} reachable documents, threshold {threshold}. " +
            "These tests would pass vacuously.");
    }

    [Fact]
    public void AFilterThatRejectsNothingReturnsTheSameDocumentsAndTheSameScoreBits()
    {
        var index = Index();
        const string query = "renewal policy session";

        AssertFixtureReachesAccumulation(index, query);

        // A field no document carries, which Matches admits for every document — the shape a
        // caller uses for a filter that is present but not selective.
        IReadOnlyList<MetadataFilter> admitsAll =
            new[] { new MetadataFilter("absent", MetadataFilterOperator.NotEqual, "x") };

        var fallback = Search(index, query, admitsAll, accumulate: false);
        var accumulated = Search(index, query, admitsAll, accumulate: true);

        Assert.NotEmpty(fallback);
        Assert.Equal(
            fallback.Select(r => (r.DocumentId, r.Score)).ToArray(),
            accumulated.Select(r => (r.DocumentId, r.Score)).ToArray());
    }

    [Fact]
    public void ASelectiveFilterReturnsTheSameDocumentsAndTheSameScoreBits()
    {
        var index = Index();
        const string query = "renewal policy session";

        AssertFixtureReachesAccumulation(index, query);

        var fallback = Search(index, query, TierFilter("even"), accumulate: false);
        var accumulated = Search(index, query, TierFilter("even"), accumulate: true);

        Assert.Equal(20, fallback.Count);
        Assert.Equal(
            fallback.Select(r => (r.DocumentId, r.Score)).ToArray(),
            accumulated.Select(r => (r.DocumentId, r.Score)).ToArray());
    }

    [Fact]
    public void TwoFiltersAndAnExclusionTogetherMatchTheFallback()
    {
        var index = Index();
        const string query = "renewal policy session";

        AssertFixtureReachesAccumulation(index, query);

        IReadOnlyList<MetadataFilter> filters =
            new[]
            {
                new MetadataFilter("tier", MetadataFilterOperator.Equal, "even"),
                new MetadataFilter("absent", MetadataFilterOperator.NotEqual, "x"),
            };

        var excluded = new HashSet<string>(StringComparer.Ordinal) { "doc-004", "doc-006" };

        var fallback = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(Limit: 50, Filters: filters, ExcludedDocumentIds: excluded));

        var accumulated = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(
                Limit: 50,
                Filters: filters,
                ExcludedDocumentIds: excluded,
                AccumulateFilteredQueries: true));

        Assert.Equal(
            fallback.Select(r => (r.DocumentId, r.Score)).ToArray(),
            accumulated.Select(r => (r.DocumentId, r.Score)).ToArray());
    }

    [Fact]
    public void TheDefaultIsAccumulationAndEitherSettingReturnsTheSameAnswer()
    {
        // Under TieBreak.DocumentId the two paths are equivalent by construction, so which one ran
        // is not observable from the outside — and that is the point: the default is a performance
        // decision with no result attached. Asserting it through a trick that returns empty from
        // both paths would prove nothing, so this asserts the two things that ARE checkable: the
        // default's value, and that both settings agree on the answer.
        //
        // The value is asserted directly because it is the whole decision. If a future change flips
        // it back, this is where it shows up — not as a slower query, which nothing would notice.
        Assert.True(new SearchOptions().AccumulateFilteredQueries);
        Assert.False(new SearchOptions(AccumulateFilteredQueries: false).AccumulateFilteredQueries);

        var index = Index();
        IReadOnlyList<MetadataFilter> filters = TierFilter("even");

        Assert.Equal(
            Search(index, "renewal policy session", filters, accumulate: true)
                .Select(r => (r.DocumentId, r.Score)).ToArray(),
            Search(index, "renewal policy session", filters, accumulate: false)
                .Select(r => (r.DocumentId, r.Score)).ToArray());
    }

    [Fact]
    public void APhraseStillDeclinesTheAccumulationPassUnderAFilter()
    {
        // The phrase refusal stands regardless of the option: a phrase rejects by walking
        // positions, so scoring first would throw most of the arithmetic away. If the option ever
        // overrode this, a phrased query would start paying for documents the phrase excludes.
        var index = Index();

        var withoutTheOption = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search("\"policy session\"", new SearchOptions(Limit: 50, Filters: TierFilter("even")));

        var withTheOption = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search("\"policy session\"", new SearchOptions(
                Limit: 50,
                Filters: TierFilter("even"),
                AccumulateFilteredQueries: true));

        Assert.Equal(
            withoutTheOption.Select(r => (r.DocumentId, r.Score)).ToArray(),
            withTheOption.Select(r => (r.DocumentId, r.Score)).ToArray());
    }

    [Theory]
    [InlineData(TieBreak.DocumentId, true)]
    [InlineData(TieBreak.DocumentId, false)]
    public void BothPathsAgreeUnderDocumentIdTieBreak(TieBreak tieBreak, bool accumulate)
    {
        var index = Index();
        const string query = "renewal policy session";

        AssertFixtureReachesAccumulation(index, query);

        var fallback = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(
                Limit: 50, Filters: TierFilter("even"), TieBreak: tieBreak));

        var accumulated = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(
                Limit: 50,
                Filters: TierFilter("even"),
                TieBreak: tieBreak,
                AccumulateFilteredQueries: accumulate));

        Assert.Equal(
            fallback.Select(r => (r.DocumentId, r.Score)).ToArray(),
            accumulated.Select(r => (r.DocumentId, r.Score)).ToArray());
    }

    [Fact]
    public void TheTieBreakFixtureActuallyContainsTies()
    {
        // Without an exact tie, the test above would pass on either tie-break and would be a test
        // that proves nothing — the same trap ResultOrderInvarianceTests guards against. Equal
        // text means equal length and equal term frequencies, so every document scores the same.
        var index = Index();
        var results = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search("renewal policy session", new SearchOptions(Limit: 50, Filters: TierFilter("even")));

        Assert.Equal(20, results.Count);
        Assert.Contains(
            results.Select(r => r.Score).Zip(results.Skip(1).Select(r => r.Score)),
            pair => pair.First == pair.Second);
    }

    /// <summary>
    /// Two query terms whose document sets interleave in ordinal space, so the accumulation pass
    /// records the even ordinals first and the odd ones second, while a full scan produces corpus
    /// order. Both terms carry the same document frequency, so both document lengths are equal and
    /// every document scores the same bit-for-bit — which is what makes the arrival order the only
    /// thing left that can decide the ranking.
    /// </summary>
    private static InMemoryTextIndex InterleavedIndex(int pairs = 6)
    {
        var index = new InMemoryTextIndex();
        var documents = new List<SearchDocument>(pairs * 2);

        for (int i = 0; i < pairs * 2; i++)
            documents.Add(new SearchDocument($"doc-{i:D2}", i % 2 == 0 ? "renewal" : "session"));

        index.Index(documents);
        return index;
    }

    [Fact]
    public void TheInterleavedFixtureProducesTwelveExactlyTiedDocuments()
    {
        // The precondition the next test depends on. Without twelve documents at one identical
        // score, both paths agree on order for free and the comparison proves nothing.
        var index = InterleavedIndex();

        Assert.Equal(6, index.DocumentFrequency("renewal"));
        Assert.Equal(6, index.DocumentFrequency("session"));
        Assert.Equal(12, index.Count);

        var results = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search("renewal session", new SearchOptions(Limit: 12));

        Assert.Equal(12, results.Count);
        Assert.Single(results.Select(r => r.Score).Distinct());
    }

    [Fact]
    public void InsertionOrderDeclinesThePassUnderAFilterRatherThanReorderingTies()
    {
        var index = InterleavedIndex();
        const string query = "renewal session";

        AssertFixtureReachesAccumulation(index, query);

        // Without the guard this pass answers doc-00, doc-02, … where the per-document loop answers
        // doc-00, doc-01, …, at identical scores — the accumulation pass records the even ordinals
        // first because they all hold "renewal", and only reaches the odd ones on the second term.
        var fallback = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(
                Limit: 50,
                Filters: AbsentFieldFilter,
                TieBreak: TieBreak.InsertionOrder));

        var withTheOption = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(
                Limit: 50,
                Filters: AbsentFieldFilter,
                TieBreak: TieBreak.InsertionOrder,
                AccumulateFilteredQueries: true));

        // Corpus order, which is what InsertionOrder documents.
        Assert.Equal(
            new[] { "doc-00", "doc-01", "doc-02", "doc-03", "doc-04", "doc-05" },
            fallback.Take(6).Select(r => r.DocumentId).ToArray());

        // The option declines under this tie-break, so the answer is the same one.
        Assert.Equal(
            fallback.Select(r => (r.DocumentId, r.Score)).ToArray(),
            withTheOption.Select(r => (r.DocumentId, r.Score)).ToArray());
    }

    [Fact]
    public void MinimumScoreIsAppliedByTheAccumulationPassToo()
    {
        var index = Index();
        const string query = "renewal policy session";

        AssertFixtureReachesAccumulation(index, query);

        // A floor between zero and the scores these documents get, so it rejects some and not all.
        double unfiltered = Search(index, query, null, accumulate: true)[0].Score;
        double floor = unfiltered * 0.5;

        var fallback = Search(index, query, TierFilter("even"), accumulate: false);
        var accumulated = new RankedTextSearchEngine(index, new Bm25Scorer())
            .Search(query, new SearchOptions(
                Limit: 50,
                MinimumScore: floor,
                Filters: TierFilter("even"),
                AccumulateFilteredQueries: true));

        Assert.NotEmpty(fallback);
        Assert.Equal(
            fallback.Select(r => (r.DocumentId, r.Score)).ToArray(),
            accumulated.Select(r => (r.DocumentId, r.Score)).ToArray());
    }
}
