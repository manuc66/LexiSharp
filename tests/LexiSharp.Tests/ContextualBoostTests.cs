using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// What the caller hands to a contextual boost. A composite rather than a payload library could
/// know the concepts in: adding a concept is a new property on the caller's own type, never a
/// change to anything here.
/// </summary>
internal sealed record CallerContext(string User, DateTimeOffset Now, string? ExperimentArm = null);

public class ContextualBoostTests
{
    /// <summary>
    /// A base engine that returns a fixed page, so a test states the ranking it is boosting
    /// instead of arranging a corpus to produce one.
    /// </summary>
    private sealed class FixedEngine : ITextSearchEngine
    {
        private readonly IReadOnlyList<SearchResult> _results;

        public FixedEngine(params (string Id, double Score)[] results) =>
            _results = results
                .Select(r => new SearchResult(r.Id, r.Score, new SearchDocument(r.Id, $"text of {r.Id}")))
                .ToList();

        public IReadOnlyList<SearchResult> LastResults { get; private set; } = Array.Empty<SearchResult>();

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public bool Remove(string documentId) => false;

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
        {
            options ??= SearchOptions.Default;
            LastResults = _results
                .OrderByDescending(r => r.Score)
                .ThenBy(r => r.DocumentId, StringComparer.Ordinal)
                .Skip(options.Offset)
                .Take(options.Limit)
                .ToList();
            return LastResults;
        }
    }

    private static readonly DateTimeOffset Noon = new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Boost_ReceivesTheQueryAsItWasIssued()
    {
        string? seen = null;
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10)),
            (context, cand) =>
            {
                seen = context.Query;
                return ScoreBoost.None();
            });

        engine.Search("the original query", null, new CallerContext("ana", Noon));

        // Before tokenization: a boost that matches on phrasing has to see the phrasing.
        Assert.Equal("the original query", seen);
    }

    [Fact]
    public void Boost_ReceivesThePreBoostRanking()
    {
        IReadOnlyList<SearchResult>? seen = null;
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10), ("b", 9)),
            (context, cand) =>
            {
                seen = context.Results;
                return ScoreBoost.None();
            });

        engine.Search("q", null, new CallerContext("ana", Noon));

        Assert.Equal(new[] { "a", "b" }, seen!.Select(r => r.DocumentId));

        // The base scores, not the boosted ones — the whole point of being able to decline.
        Assert.Equal(new[] { 10.0, 9.0 }, seen!.Select(r => r.Score));
    }

    [Fact]
    public void Boost_ReceivesTheCallerPayload()
    {
        CallerContext? seen = null;
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10)),
            (context, cand) =>
            {
                seen = context.Payload;
                return ScoreBoost.None();
            });

        var payload = new CallerContext("ana", Noon, "arm-b");
        engine.Search("q", null, payload);

        Assert.Same(payload, seen);
    }

    [Fact]
    public void EmptyInnerResultSet_NeverReachesTheBoost()
    {
        // With no candidate there is nothing to boost, so the boost is not called at all — which
        // is also why the boost cannot observe TopConfidence on an empty ranking, and why
        // TopConfidence is documented as 0 rather than as something to read there.
        int calls = 0;
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(),
            (_, cand) =>
            {
                calls++;
                return ScoreBoost.None();
            });

        Assert.Empty(engine.Search("q", null, new CallerContext("ana", Noon)));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void DeclinedBoost_LeavesAClearRankingIntact()
    {
        // 100 against 10 is a clear winner, so a boost that only fires under doubt stays out of
        // it. This is the regression the gate exists to prevent: a correction applied to a
        // ranking that was already right.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 100), ("b", 10)),
            (context, cand) =>
                context.TopConfidence < 0.2 ? new ScoreBoost(Add: 500) : ScoreBoost.None());

        var results = engine.Search("q", null, new CallerContext("ana", Noon));

        Assert.Equal(new[] { 100.0, 10.0 }, results.Select(r => r.Score).OrderByDescending(s => s));
    }

    [Fact]
    public void Boost_FiresOnAnAmbiguousRanking()
    {
        // Same boost, same threshold, ranking the base engine could not resolve: 100 against
        // 99.9 reads as a near-tie, which is exactly when a correction is worth applying.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            (context, candidate) =>
                context.TopConfidence < 0.2 && candidate.DocumentId == "b"
                    ? new ScoreBoost(Add: 500)
                    : ScoreBoost.None());

        var results = engine.Search("q", null, new CallerContext("ana", Noon));

        Assert.Equal("b", results[0].DocumentId);
    }

    [Fact]
    public void WinnerMargin_ReadsAnExactTieAsNoConfidenceAtAll()
    {
        // Worth pinning because it is the one case where "ambiguous" and "certain" are easy to
        // confuse: WinnerMargin is the gap to the follower, so two equal scores give 0 — maximum
        // uncertainty — not 1. A gate written as "confidence above 0.5" and one written as
        // "confidence below 0.5" behave oppositely on a tie, and only one of them is right.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 100), ("b", 100)),
            (context, cand) => ScoreBoost.None());

        var results = engine.Search("q", null, new CallerContext("ana", Noon));

        Assert.Equal(2, results.Count);
        Assert.Equal(100, results[0].Score);
    }

    [Fact]
    public void Context_IsBuiltOncePerSearchSoTheBaseRankingDoesNotMoveUnderTheBoost()
    {
        int builds = 0;
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10), ("b", 9), ("c", 8)),
            (context, cand) =>
            {
                builds++;
                return ScoreBoost.None();
            });

        engine.Search("q", null, new CallerContext("ana", Noon));

        Assert.Equal(3, builds);
    }

    [Fact]
    public void Boost_CanDependOnThePayloadPerCandidate()
    {
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10), ("b", 9)),
            (context, candidate) =>
                context.Payload.ExperimentArm == "arm-b" && candidate.DocumentId == "b"
                    ? new ScoreBoost(Add: 50)
                    : ScoreBoost.None());

        // Two candidates, both kept, so a missing result means the document was dropped rather
        // than merely ranked second.
        var inArm = engine.Search("q", null, new CallerContext("ana", Noon, "arm-b"));
        var outArm = engine.Search("q", null, new CallerContext("ana", Noon, "arm-a"));

        Assert.Equal(2, inArm.Count);
        Assert.Equal("b", inArm[0].DocumentId);
        Assert.Equal("a", outArm[0].DocumentId);
    }

    [Fact]
    public void Search_WithoutAPayload_LeavesEveryCandidateAtItsBaseScore()
    {
        // Reachable through ITextSearchEngine, so a pipeline unaware of the capability runs the
        // engine — without the state it was given the means to use. Payload is null here, which
        // is unambiguous because TPayload is constrained to a reference type.
        ITextSearchEngine engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            (context, candidate) =>
                context.Payload is not null ? new ScoreBoost(Add: 500) : ScoreBoost.None());

        var results = engine.Search("q");

        Assert.Equal(new[] { 100.0, 99.9 }, results.Select(r => r.Score).OrderByDescending(s => s));
    }

    [Fact]
    public void PayloadlessOverload_ReportsThePayloadAsNull()
    {
        CallerContext? seen = null;
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10)),
            (context, cand) =>
            {
                seen = context.Payload;
                return ScoreBoost.None();
            });

        engine.Search("q");

        // The check the type constraint exists to make safe: with a reference-type payload this
        // reads null rather than a value type's zero value.
        Assert.Null(seen);
    }

    [Fact]
    public void PayloadSupplied_TellsNoPayloadFromANullPayload()
    {
        // The two are different situations and Payload alone cannot distinguish them, which is
        // what made a null check the wrong test.
        var supplied = new List<bool>();
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10)),
            (context, cand) =>
            {
                supplied.Add(context.PayloadSupplied);
                return ScoreBoost.None();
            });

        // No payload offered.
        engine.Search("q");
        Assert.All(supplied, s => Assert.False(s));

        supplied.Clear();

        // A payload offered, and null.
        engine.Search("q", null, null!);
        Assert.All(supplied, s => Assert.True(s));

        supplied.Clear();

        // A payload offered, and real.
        engine.Search("q", null, new CallerContext("ana", Noon));
        Assert.All(supplied, s => Assert.True(s));
    }

    [Fact]
    public void RequirePayload_MakesAPayloadlessSearchThrow()
    {
        // The regression this prevents: a per-user correction silently ceasing to apply, with
        // results that still look plausible and nothing to indicate the boost stopped running.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 100), ("b", 10)),
            (context, cand) => context.Payload!.ExperimentArm == "arm-b"
                ? new ScoreBoost(Add: 50)
                : ScoreBoost.None(),
            requirePayload: true);

        ITextSearchEngine unaware = engine;

        var error = Assert.Throws<InvalidOperationException>(() => unaware.Search("q"));
        Assert.Contains("requirePayload", error.Message);
        Assert.Contains(nameof(IContextualSearchEngine<CallerContext>), error.Message);

        // The capability interface still serves it. The payload is offered, and requirePayload
        // only ever rejects an absent one — it is not a non-null contract, so a null payload is
        // the caller's to handle.
        Assert.NotEmpty(engine.Search("q", null, new CallerContext("ana", Noon)));
    }

    [Fact]
    public void RequirePayload_DefaultsToOff_SoARankingOnlyBoostKeepsWorking()
    {
        // A boost that reads nothing but the ranking must survive the plain ITextSearchEngine
        // surface; making it opt-in is what keeps that true.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            (context, cand) => context.TopConfidence < 0.2 && cand.DocumentId == "b"
                ? new ScoreBoost(Add: 500)
                : ScoreBoost.None());

        ITextSearchEngine unaware = engine;
        var results = unaware.Search("q");

        // No payload to gate on, and none needed.
        Assert.Equal("b", results[0].DocumentId);
    }

    [Fact]
    public void RequirePayload_ThrowsBeforeRetrievingFromTheInnerEngine()
    {
        // Failing here rather than after the inner engine ran keeps the cost of a misrouted call
        // at zero and makes the mistake obvious in a stack trace.
        var inner = new FixedEngine(("a", 10));
        var engine = new BoostedTextSearchEngine<CallerContext>(
            inner,
            (context, cand) => ScoreBoost.None(),
            requirePayload: true);

        Assert.Throws<InvalidOperationException>(() => ((ITextSearchEngine)engine).Search("q"));

        Assert.Empty(inner.LastResults);
    }

    [Fact]
    public void RequirePayload_DoesNotDisturbAnEmptyRequest()
    {
        // A request that cannot produce results is not a payload mistake, and throwing there
        // would turn an empty page into a failure.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10)),
            (context, cand) => ScoreBoost.None(),
            requirePayload: true);

        Assert.Empty(engine.Search("q", new SearchOptions(Limit: 0)));
        Assert.Empty(engine.Search("q", new SearchOptions(Offset: -1)));
    }

    [Fact]
    public void Confidences_AreComputedLazilyAndOnlyOnce()
    {
        var arrays = new List<IReadOnlyList<double>>();
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10), ("b", 9), ("c", 8)),
            (context, cand) =>
            {
                arrays.Add(context.Confidences);
                _ = context.TopConfidence;
                return ScoreBoost.None();
            });

        engine.Search("q", null, new CallerContext("ana", Noon));

        // One array for three candidates: the confidence walk is per search, not per candidate.
        Assert.Equal(3, arrays.Count);
        Assert.All(arrays, a => Assert.Same(arrays[0], a));
    }

    [Fact]
    public void Confidences_AreNotComputedByABoostThatNeverReadsThem()
    {
        // The candidate-only engine discards the context entirely, so deriving its confidences
        // eagerly would be work nobody can observe. Readable through the allocation a search
        // performs, since the walk is what allocates.
        static long BytesPerSearch(ITextSearchEngine e)
        {
            e.Search("q", new SearchOptions(10));
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) e.Search("q", new SearchOptions(10));
            return (GC.GetAllocatedBytesForCurrentThread() - before) / 100;
        }

        var candidates = Enumerable.Range(0, 50).Select(i => (Id: $"d{i}", Score: (double)i)).ToArray();

        var reads = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(candidates),
            (context, cand) =>
            {
                _ = context.Confidences;
                return ScoreBoost.None();
            });

        var ignores = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(candidates),
            (context, cand) =>
            {
                _ = context.Payload;
                return ScoreBoost.None();
            });

        // Both take the same path apart from whether the confidences are read; if the read is
        // lazy, the two allocations differ by at most the 50-element array itself.
        long withRead = BytesPerSearch(reads);
        long withoutRead = BytesPerSearch(ignores);

        Assert.True(
            withoutRead < withRead,
            $"expected the unread path ({withoutRead} B) to allocate less than the read path ({withRead} B)");
    }

    [Fact]
    public void Confidences_AgreeWithScoreConfidenceOnTheSameResults()
    {
        IReadOnlyList<double>? seen = null;
        var candidates = new (string Id, double Score)[] { ("a", 100.0), ("b", 40.0), ("c", 39.0) };
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(candidates),
            (context, cand) =>
            {
                seen = context.Confidences;
                return ScoreBoost.None();
            });

        engine.Search("q", null, new CallerContext("ana", Noon));

        var expected = ScoreConfidence.Compute(
            candidates
                .Select(c => new SearchResult(c.Id, c.Score, new SearchDocument(c.Id, "t")))
                .ToList());

        Assert.Equal(expected, seen);
    }

    [Fact]
    public void DefaultScoreBoost_ExcludesEveryDocument()
    {
        // The trap ScoreBoost.None() exists to name, pinned from the boost side: what a boost
        // that returns default actually does to the page.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 100), ("b", 99.9)),
            (_, cand) => default);

        Assert.Empty(engine.Search("q", null, new CallerContext("ana", Noon)));
    }

    [Fact]
    public void TiedScores_AreBrokenByOrdinalDocumentId_NotByCulture()
    {
        // The id break this decorator used to do was ThenBy(x => x.DocumentId) with no comparer,
        // which is Comparer<string>.Default — culture-sensitive. Ordinally 'B' (0x42) sorts
        // before 'a' (0x61); a culture-aware comparison puts "a" first. TieBreak.DocumentId
        // documents ordinal, and TopRankedWindow compares ordinally, so a decorated ranking
        // disagreeing with the ranking it decorates is the bug being pinned here.
        //
        // The stub hands them over in the opposite order, so it is the decorator's sort that
        // decides and not the fixture's — FixedEngine sorts by id and would have proved nothing.
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new TieOrderingEngine(("a", 10), ("B", 10)),
            (context, cand) => ScoreBoost.None());

        var page = engine.Search("q", null, new CallerContext("ana", Noon));

        Assert.Equal(new[] { "B", "a" }, page.Select(r => r.DocumentId));
    }

    [Fact]
    public void TiedScores_HonourInsertionOrder_WhenAskedForIt()
    {
        // Neither decorator observed SearchOptions.TieBreak before: both hardcoded a document-id
        // ordering, so a caller reproducing an insertion-ordered system had it silently dropped
        // the moment they wrapped the engine in a decorator.
        //
        // The stub returns ties in the order given, deliberately — FixedEngine sorts by id, and a
        // fixture that had already sorted would make insertion order and id order the same thing
        // and test neither.
        var baseEngine = new TieOrderingEngine(("b", 10), ("a", 10));

        var byId = new BoostedTextSearchEngine<CallerContext>(
            baseEngine, (context, cand) => ScoreBoost.None())
            .Search("q", new SearchOptions(TieBreak: TieBreak.DocumentId), new CallerContext("ana", Noon));

        var byInsertion = new BoostedTextSearchEngine<CallerContext>(
            new TieOrderingEngine(("b", 10), ("a", 10)), (context, cand) => ScoreBoost.None())
            .Search("q", new SearchOptions(TieBreak: TieBreak.InsertionOrder), new CallerContext("ana", Noon));

        Assert.Equal(new[] { "a", "b" }, byId.Select(r => r.DocumentId));
        Assert.Equal(new[] { "b", "a" }, byInsertion.Select(r => r.DocumentId));
    }

    /// <summary>
    /// Returns ties exactly as given: the precondition a test of tie-breaking needs, and the one
    /// FixedEngine cannot provide because it sorts by id.
    /// </summary>
    private sealed class TieOrderingEngine : ITextSearchEngine
    {
        private readonly IReadOnlyList<SearchResult> _results;

        public TieOrderingEngine(params (string Id, double Score)[] results) =>
            _results = results
                .Select(r => new SearchResult(r.Id, r.Score, new SearchDocument(r.Id, $"text of {r.Id}")))
                .ToList();

        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public bool Remove(string documentId) => false;

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            options is { IsEmpty: false } ? _results : Array.Empty<SearchResult>();
    }

    [Fact]
    public void NegativeFactor_IsStillRejected()
    {
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 10)),
            (_, cand) => new ScoreBoost(Multiply: -2));

        var error = Assert.Throws<ArgumentException>(() => engine.Search("q", null, new CallerContext("ana", Noon)));

        Assert.Contains("invert", error.Message);
    }

    [Fact]
    public void NonGenericBoost_RemainsUnchangedByTheGenericOneExisting()
    {
        // The candidate-only form is now a forwarder. It must keep behaving as it did: no payload,
        // no context, same ordering, and still accepting a bare double as a factor.
        var engine = new BoostedTextSearchEngine(
            new FixedEngine(("a", 10), ("b", 5)),
            result => result.DocumentId == "b" ? 3.0 : ScoreBoost.None());

        var results = engine.Search("q");

        Assert.Equal(new[] { "b", "a" }, results.Select(r => r.DocumentId));
    }

    [Fact]
    public void Calibrator_FitsTheAbstentionTheContextGateReads()
    {
        // The two pieces together, which is what the gate is for: a fitted probability of being
        // right, read from the top score the context exposes.
        var pairs = new List<(double Score, bool IsCorrect)>();
        for (int i = 0; i < 40; i++)
        {
            pairs.Add((i, false));
            pairs.Add((100 + i, true));
        }

        var calibrated = CalibratedScoreConfidence.Fit(pairs);
        var engine = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 20), ("b", 1)),
            (context, candidate) =>
                calibrated.ShouldAbstain(context.TopScore) && candidate.DocumentId == "b"
                    ? new ScoreBoost(Add: 100)
                    : ScoreBoost.None());

        var unsure = engine.Search("q", null, new CallerContext("ana", Noon));
        Assert.Equal("b", unsure[0].DocumentId);

        // A clear top result is not uncertain, so the correction stays out.
        var sure = new BoostedTextSearchEngine<CallerContext>(
            new FixedEngine(("a", 139), ("b", 138)),
            (context, candidate) =>
                calibrated.ShouldAbstain(context.TopScore) && candidate.DocumentId == "b"
                    ? new ScoreBoost(Add: 100)
                    : ScoreBoost.None());

        var decided = sure.Search("q", null, new CallerContext("ana", Noon));
        Assert.Equal("a", decided[0].DocumentId);
    }
}
