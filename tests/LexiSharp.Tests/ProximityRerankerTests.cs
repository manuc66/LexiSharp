using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

// CA1859 ("use a concrete type instead of the interface") is suppressed on the lines
// below. The interface is the published return type: a List<T> or an array in its place
// would hand callers a mutable collection through a contract that says they cannot have
// one, and what it saves is a single interface dispatch per call, which no measurement in
// docs/benchmarks.md attributes time to.

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="ProximityReranker"/>. The central property is that two documents with
/// identical term frequencies but different term <i>distance</i> come out in a different order —
/// which is the whole reason a proximity stage exists, since no frequency-only scorer can tell
/// them apart.
/// </summary>
public class ProximityRerankerTests
{
    /// <summary>
    /// Two documents with the same query terms and the same counts, laid out differently:
    /// <c>adjacent</c> has them next to each other, <c>spread</c> has them across the document.
    /// </summary>
    private static InMemoryTextIndex AdjacentVersusSpread() =>
        Index(
            ("adjacent", "refresh token rotation is a security concern worth documenting"),
            ("spread", "rotation of a security token for a refresh of credentials is a concern"));

    private static InMemoryTextIndex Index(params (string Id, string Text)[] documents)
    {
        var index = new InMemoryTextIndex();
        index.Index(documents.Select(document => new SearchDocument(document.Id, document.Text)));
        return index;
    }

    private static IReadOnlyList<SearchResult> Candidates(ITextIndex index, string query, params string[] order) // NOSONAR:CA1859
    {
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        var results = engine.Search(query).ToDictionary(result => result.DocumentId, result => result);

        return order.Select(id => results[id]).ToList();
    }

    [Fact]
    public void ItPutsTheDocumentWithAdjacentTermsFirst()
    {
        var index = AdjacentVersusSpread();
        var reranker = new ProximityReranker(index);

        // 'refresh' and 'token' are adjacent in one document and far apart in the other, with the
        // same term frequencies. Plain BM25 cannot tell them apart; this must.
        var before = Candidates(index, "refresh token", "adjacent", "spread");
        var after = reranker.Rerank("refresh token", before);

        Assert.Equal(["adjacent", "spread"], before.Select(r => r.DocumentId));
        Assert.Equal("adjacent", after[0].DocumentId);
    }

    [Fact]
    public void TheFactorIsTheNormalizedWindowAndTheScoreOnlyEverDrops()
    {
        var index = AdjacentVersusSpread();
        var reranker = new ProximityReranker(index);

        var before = Candidates(index, "refresh token", "adjacent", "spread");
        var after = reranker.Rerank("refresh token", before);

        // 'adjacent': refresh at 0, token at 1 -> W = 2, n = 2, tightness 1.0, factor 1.0.
        Assert.Equal(before[0].Score, after.Single(r => r.DocumentId == "adjacent").Score, 12);

        // 'spread' has them ~7 apart, so its factor is strictly below 1 and its score strictly lower.
        var spread = after.Single(r => r.DocumentId == "spread");
        Assert.True(spread.Score < before[1].Score);
        Assert.True(spread.Score > 0, "a zero would read as 'not a match' and drop the document");
    }

    [Fact]
    public void AContiguousWindowIsLeftExactlyAlone()
    {
        var index = Index(("d1", "alpha beta gamma"));
        var reranker = new ProximityReranker(index);

        var before = Candidates(index, "alpha beta", "d1");
        var after = reranker.Rerank("alpha beta", before);

        // W = n = 2, so tightness is 1.0 and the factor is 1.0: no change at all.
        Assert.Equal(before[0].Score, after[0].Score, 12);
    }

    [Fact]
    public void AQueryMissingATermIsNeverPenalized()
    {
        // The first-stage scorer may return a document matching only part of the query. Whether it
        // should match all of it is that scorer's call, not the reranker's.
        var index = Index(
            ("both", "alpha beta gamma"),
            ("partial", "alpha gamma and other unrelated words here"));

        var reranker = new ProximityReranker(index);
        var before = Candidates(index, "alpha beta", "partial", "both");
        var after = reranker.Rerank("alpha beta", before);

        // Neither document is penalised: 'both' has a contiguous window, and 'partial' has no
        // window at all, which is not evidence of distance. Both scores must come through intact.
        foreach (var candidate in before)
        {
            Assert.Equal(
                candidate.Score,
                after.Single(result => result.DocumentId == candidate.DocumentId).Score,
                12);
        }

        // With neither pulled down, the reranker's sort restores plain score order.
        Assert.Equal(["both", "partial"], after.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void ASingleTermQueryIsANoOp()
    {
        var index = AdjacentVersusSpread();
        var reranker = new ProximityReranker(index);

        var before = Candidates(index, "refresh", "adjacent", "spread");
        var after = reranker.Rerank("refresh", before);

        // One term has no proximity: n = W = 1 whatever the layout.
        Assert.Equal(before.Select(r => r.Score), after.Select(r => r.Score));
    }

    [Fact]
    public void StrengthZeroIsAnExactNoOp()
    {
        var index = AdjacentVersusSpread();
        var reranker = new ProximityReranker(index, strength: 0);

        var before = Candidates(index, "refresh token", "adjacent", "spread");
        var after = reranker.Rerank("refresh token", before);

        Assert.Equal(before.Select(r => r.Score), after.Select(r => r.Score));
        Assert.Equal(before.Select(r => r.DocumentId), after.Select(r => r.DocumentId));
    }

    [Fact]
    public void ASmallerStrengthPullsLess()
    {
        var index = AdjacentVersusSpread();
        var before = Candidates(index, "refresh token", "adjacent", "spread");

        double Drop(double strength) =>
            before[1].Score - new ProximityReranker(index, strength: strength)
                .Rerank("refresh token", before)
                .Single(r => r.DocumentId == "spread")
                .Score;

        Assert.True(Drop(0.25) < Drop(0.5));
        Assert.True(Drop(0.5) < Drop(1.0));
    }

    [Fact]
    public void ItWorksThroughTheRerankingEngine()
    {
        var index = AdjacentVersusSpread();
        var engine = new RerankedTextSearchEngine(
            new RankedTextSearchEngine(index, new Bm25Scorer()),
            new ProximityReranker(index),
            maxCandidates: 10);

        var results = engine.Search("refresh token");

        Assert.Equal(2, results.Count);
        Assert.Equal("adjacent", results[0].DocumentId);
        Assert.All(results, result => Assert.True(result.Score > 0));
    }

    [Fact]
    public void AFarApartMatchIsDemotedBelowAnAdjacentOne()
    {
        // The clearest statement of the feature: same terms, same counts, different distance.
        var index = Index(
            ("tight", "zebra marker together in one place"),
            ("loose", "zebra appears here and then a long while later marker shows up"));

        var before = Candidates(index, "zebra marker", "tight", "loose");
        var after = new ProximityReranker(index).Rerank("zebra marker", before);

        Assert.Equal(["tight", "loose"], after.Select(r => r.DocumentId).ToArray());
        Assert.True(after[0].Score > after[1].Score);
    }

    [Fact]
    public void RepeatedTermsUseTheirClosestOccurrence()
    {
        // 'alpha' appears twice in 'both-early' and once far away in 'both-late'. The tightest
        // window is the small one, so 'both-early' must win despite the larger term frequency.
        var index = Index(
            ("both-early", "alpha beta and again alpha much later in the document"),
            ("both-late", "alpha then a great deal of filler before beta arrives at the end"));

        var before = Candidates(index, "alpha beta", "both-early", "both-late");
        var after = new ProximityReranker(index).Rerank("alpha beta", before);

        Assert.Equal("both-early", after[0].DocumentId);
    }

    [Fact]
    public void TiesKeepTheFirstStageOrder()
    {
        // Two documents whose windows are identical must not be swapped by the reranker's own sort.
        var index = Index(
            ("first", "alpha beta gamma delta"),
            ("second", "alpha beta epsilon zeta"));

        var before = Candidates(index, "alpha beta", "first", "second");
        var after = new ProximityReranker(index).Rerank("alpha beta", before);

        // Both windows are 2 wide, so both factors are 1.0 and both scores are unchanged: the order
        // the first stage produced must survive.
        Assert.Equal(before.Select(r => r.Score), after.Select(r => r.Score));
        Assert.Equal(["first", "second"], after.Select(r => r.DocumentId).ToArray());
    }

    [Fact]
    public void TermsInDifferentFieldsCountAsDistant()
    {
        // A title match and a body match are separated by the field gap, so the window is wide and
        // the factor below 1. Intended: distance across fields is still distance.
        var index = new InMemoryTextIndex();

        index.Index(new[]
        {
            new SearchDocument("split", "a body about other things entirely",
                TextFields: new Dictionary<string, string> { ["title"] = "alpha" }),
            new SearchDocument("together", "alpha beta right here in the body",
                TextFields: new Dictionary<string, string> { ["title"] = "notes" }),
        });

        var before = Candidates(index, "alpha beta", "split", "together");
        var after = new ProximityReranker(index).Rerank("alpha beta", before);

        Assert.Equal("together", after[0].DocumentId);
    }

    [Fact]
    public void ItNeverReturnsADocumentThatWasNotACandidate()
    {
        var index = Index(("d1", "alpha beta"), ("d2", "alpha gamma"));
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var before = Candidates(index, "alpha", "d1", "d2");
        var after = new ProximityReranker(index).Rerank("alpha", before);

        Assert.Equal(before.Count, after.Count);
        Assert.Equal(
            before.Select(r => r.DocumentId).Order(StringComparer.Ordinal),
            after.Select(r => r.DocumentId).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ItDoesNotMutateTheInputList()
    {
        var index = AdjacentVersusSpread();
        var before = Candidates(index, "refresh token", "adjacent", "spread");
        var snapshot = before.Select(r => r.Score).ToArray();

        _ = new ProximityReranker(index).Rerank("refresh token", before);

        Assert.Equal(snapshot, before.Select(r => r.Score));
    }

    [Fact]
    public void TheDampPenaltyIsBoundedRegardlessOfDocumentLength()
    {
        // The defect this pins: the natural decay 1 - strength*(1 - n/W) is unbounded, so a long
        // document with its query terms far apart was multiplied by a factor approaching zero. A
        // 100-token document and a 10 000-token one were punished by ~97% and ~99.98% for the same
        // relative layout -- punishing a document for being long, when "are these near each other?"
        // is a relative question. The floor makes the penalty a preference rather than a veto.
        foreach (int length in new[] { 100, 1_000, 10_000 })
        {
            var words = Enumerable.Repeat("filler", length).ToList();
            words[length / 10] = "alpha";
            words[length - 10] = "beta";

            var index = new InMemoryTextIndex();
            index.Index([new SearchDocument("long", string.Join(' ', words))]);

            Assert.Equal(length, index.DocumentLength("long"));

            var before = Candidates(index, "alpha beta", "long");
            var after = new ProximityReranker(index, strength: 1.0).Rerank("alpha beta", before);

            // Never below the default floor of 0.5, and never above 1.
            Assert.InRange(after[0].Score / before[0].Score, 0.5, 1.0);
        }
    }

    [Fact]
    public void TheFloorIsHonouredExactly()
    {
        var words = Enumerable.Repeat("filler", 500).ToList();
        words[5] = "alpha";
        words[495] = "beta";

        var index = new InMemoryTextIndex();
        index.Index([new SearchDocument("long", string.Join(' ', words))]);

        var before = Candidates(index, "alpha beta", "long");

        // A window far wider than the term count, so the raw factor is ~0.02 and the floor decides.
        var after = new ProximityReranker(index, strength: 1.0, floor: 0.25)
            .Rerank("alpha beta", before);

        Assert.Equal(before[0].Score * 0.25, after[0].Score, 12);
    }

    [Fact]
    public void AFloorOfOneIsANoOp()
    {
        var index = AdjacentVersusSpread();
        var before = Candidates(index, "refresh token", "adjacent", "spread");

        var after = new ProximityReranker(index, strength: 1.0, floor: 1.0)
            .Rerank("refresh token", before);

        Assert.Equal(before.Select(r => r.Score), after.Select(r => r.Score));
    }

    [Fact]
    public void ArgumentValidation()
    {
        var index = AdjacentVersusSpread();

        Assert.Throws<ArgumentNullException>(() => new ProximityReranker(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProximityReranker(index, strength: -0.1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProximityReranker(index, strength: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProximityReranker(index, strength: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ProximityReranker(index, mode: (ProximityMode)42));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProximityReranker(index, floor: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProximityReranker(index, floor: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ProximityReranker(index, floor: double.NaN));

        var reranker = new ProximityReranker(index);
        var candidates = Candidates(index, "refresh token", "adjacent", "spread");

        Assert.Throws<ArgumentNullException>(() => reranker.Rerank(null!, candidates));
        Assert.Throws<ArgumentNullException>(() => reranker.Rerank("query", null!));
    }

    // ---- the boost shape ---------------------------------------------------------------------------

    [Fact]
    public void BoostRaisesScoresAndNeverLowersThem()
    {
        var index = AdjacentVersusSpread();
        var before = Candidates(index, "refresh token", "spread", "adjacent");

        var after = new ProximityReranker(index, mode: ProximityMode.Boost)
            .Rerank("refresh token", before);

        // Boost adds a positive proximity term, so every score can only go up -- including the
        // spread-out document, which the damp shape demotes.
        Assert.All(before, candidate =>
        {
            var raised = after.Single(r => r.DocumentId == candidate.DocumentId).Score;
            Assert.True(raised > candidate.Score,
                $"{candidate.DocumentId}: {raised} should exceed {candidate.Score}");
        });
    }

    [Fact]
    public void BoostAddsMoreToTheTighterDocument()
    {
        var index = AdjacentVersusSpread();
        var before = Candidates(index, "refresh token", "adjacent", "spread");

        var after = new ProximityReranker(index, mode: ProximityMode.Boost)
            .Rerank("refresh token", before);

        // The adjacent document has tightness 1.0, the spread one less, so the added term is larger
        // for the adjacent one. That is the promotion boost exists to do.
        double adjacentGain = after[0].Score - before[0].Score;
        double spreadGain = after.Single(r => r.DocumentId == "spread").Score - before[1].Score;

        Assert.True(adjacentGain > spreadGain,
            $"adjacent gain {adjacentGain} should exceed spread gain {spreadGain}");
    }

    [Fact]
    public void BoostStillLeavesTheShortAndMissingTermCasesAlone()
    {
        var index = AdjacentVersusSpread();
        var boosting = new ProximityReranker(index, mode: ProximityMode.Boost);

        // Single-term query: no proximity, no change.
        var single = Candidates(index, "refresh", "adjacent", "spread");
        Assert.Equal(
            single.Select(r => r.Score),
            boosting.Rerank("refresh", single).Select(r => r.Score));

        // A document missing a query term has no window and must not be moved.
        var partial = Index(
            ("has-both", "alpha beta gamma"),
            ("has-one", "alpha and other unrelated filler words"));
        var before = Candidates(partial, "alpha beta", "has-one", "has-both");

        foreach (var candidate in before)
        {
            Assert.Equal(
                candidate.Score,
                boosting.Rerank("alpha beta", before)
                    .Single(r => r.DocumentId == candidate.DocumentId).Score,
                12);
        }
    }

    [Fact]
    public void BoostIsANoOpAtZeroStrength()
    {
        var index = AdjacentVersusSpread();
        var before = Candidates(index, "refresh token", "adjacent", "spread");

        var after = new ProximityReranker(index, strength: 0, mode: ProximityMode.Boost)
            .Rerank("refresh token", before);

        Assert.Equal(before.Select(r => r.Score), after.Select(r => r.Score));
    }

    [Fact]
    public void TheTwoModesAreNamedDistinctly()
    {
        var index = AdjacentVersusSpread();

        Assert.Equal("Proximity (damp)", new ProximityReranker(index).Name);
        Assert.Equal("Proximity (boost)", new ProximityReranker(index, mode: ProximityMode.Boost).Name);
    }

    [Fact]
    public void AnEmptyOrSingleCandidateListIsHandled()
    {
        var index = AdjacentVersusSpread();
        var reranker = new ProximityReranker(index);

        Assert.Empty(reranker.Rerank("refresh token", []));

        var one = Candidates(index, "refresh", "adjacent");
        Assert.Single(reranker.Rerank("refresh", one));
    }
}
