using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using LexiSharp.Linguistics;
using LexiSharp.Sources;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The score-structure diagnostic behind the per-query report. These decide whether a verdict can be
/// believed: a tie reported on a deterministic ordering, a tie past the cut treated as if it changed
/// the page, or a single document called degenerate each produce a confident wrong answer — and a
/// confident wrong answer is the whole product here.
/// </summary>
/// <remarks>
/// The scores here are literal <see cref="double"/> values rather than computed ones wherever the
/// point is a verdict, so that a tie in the test is a tie in the engine's own comparison. The one
/// test that computes a score exists to pin the opposite: that the diagnostic does not invent a
/// tolerance the sorter does not have.
/// </remarks>
public class QueryDiscriminationTests
{
    [Fact]
    public void AQueryThatReturnedNothingIsARecallFailureNotADegenerateRanking()
    {
        DiscriminationProfile profile = Measure([], cutoff: 10);

        Assert.Equal(DiscriminationVerdict.NoResults, profile.Verdict);
        Assert.False(profile.CutStraddled);
        Assert.False(profile.PageTied);
        Assert.Equal(0, profile.ScoreRange);
    }

    [Fact]
    public void ASingleDocumentIsNotDegenerateThereIsNothingToOrder()
    {
        DiscriminationProfile profile = Measure([("a", 4.0)], cutoff: 10);

        Assert.Equal(DiscriminationVerdict.SingleResult, profile.Verdict);
        Assert.True(profile.PageMembershipDetermined);
        Assert.True(profile.PageOrderDetermined);
    }

    [Fact]
    public void AConstantScoreIsNoDiscriminationBecauseTheTieBreakDecidedEverything()
    {
        DiscriminationProfile profile = Measure([("a", 1.0), ("b", 1.0), ("c", 1.0)], cutoff: 2);

        Assert.Equal(DiscriminationVerdict.NoDiscrimination, profile.Verdict);
        Assert.Equal(1, profile.DistinctScores);
        Assert.Equal(3, profile.LargestClass);
        Assert.False(profile.PageMembershipDetermined);
        Assert.False(profile.PageOrderDetermined);
    }

    [Fact]
    public void ATieCrossingTheCutMakesThePagesMembershipArbitrary()
    {
        // Page of two: "a" is in, and "b" and "c" are tied for the place that is left.
        DiscriminationProfile profile = Measure([("a", 3.0), ("b", 2.0), ("c", 2.0)], cutoff: 2);

        Assert.Equal(DiscriminationVerdict.TiedAtCut, profile.Verdict);
        Assert.True(profile.CutStraddled);
        Assert.False(profile.PageMembershipDetermined);
        Assert.False(profile.PageOrderDetermined);
    }

    [Fact]
    public void ATieInsideThePageLeavesMembershipDeterminedAndOnlyTheOrderArbitrary()
    {
        // Page of three holds the whole tie; nothing is competing to get in.
        DiscriminationProfile profile = Measure([("a", 3.0), ("b", 2.0), ("c", 2.0), ("d", 1.0)], cutoff: 3);

        Assert.Equal(DiscriminationVerdict.TiedWithinPage, profile.Verdict);
        Assert.False(profile.CutStraddled);
        Assert.True(profile.PageMembershipDetermined);
        Assert.False(profile.PageOrderDetermined);
    }

    [Fact]
    public void ATieEntirelyPastTheCutDoesNotChangeThePage()
    {
        DiscriminationProfile profile =
            Measure([("a", 3.0), ("b", 2.0), ("c", 1.0), ("d", 1.0)], cutoff: 2);

        Assert.Equal(DiscriminationVerdict.Determined, profile.Verdict);
        Assert.True(profile.PageMembershipDetermined);
        Assert.True(profile.PageOrderDetermined);

        // The tie still exists, and a caller looking at index breadth should see it.
        Assert.Equal(2, profile.LargestClass);
        Assert.Equal(2, profile.DistinctInPage);
    }

    [Fact]
    public void AListThatEndsAtTheCutIsUnknownUnlessTheCallerSaysItIsWhole()
    {
        // Two results at a depth of two: complete when the index held no more, truncated when the
        // engine stopped at the page. The measurement cannot tell, so the caller declares it.
        var ranked = Rank([("a", 3.0), ("b", 2.0)]);

        DiscriminationProfile truncated = QueryDiscrimination.Measure("q", ranked, 2);
        DiscriminationProfile whole = QueryDiscrimination.Measure("q", ranked, 2, complete: true);

        Assert.Equal(DiscriminationVerdict.CutUnobserved, truncated.Verdict);
        Assert.True(truncated.TruncatedAtCut);
        Assert.False(truncated.PageMembershipDetermined);
        Assert.True(truncated.PageOrderDetermined);

        Assert.Equal(DiscriminationVerdict.Determined, whole.Verdict);
        Assert.False(whole.TruncatedAtCut);
        Assert.True(whole.PageMembershipDetermined);
    }

    [Fact]
    public void DistinctScoresAreDetermined()
    {
        DiscriminationProfile profile =
            Measure([("a", 3.0), ("b", 2.5), ("c", 1.25)], cutoff: 3);

        Assert.Equal(DiscriminationVerdict.Determined, profile.Verdict);
        Assert.Equal(3, profile.DistinctScores);
        Assert.Equal(1, profile.LargestClass);
        Assert.Equal(1.75, profile.ScoreRange, precision: 12);
    }

    [Fact]
    public void AScoreThatDiffersInTheLastBitIsNotATie()
    {
        // 0.1 + 0.2 is 0.30000000000000004 in binary floating point, not 0.3. A diagnostic that
        // rounded here would report a tie the sorter does not have, on every query.
        double computed = 0.1 + 0.2;

        DiscriminationProfile profile = Measure([("a", computed), ("b", 0.3)], cutoff: 2);

        Assert.Equal(DiscriminationVerdict.Determined, profile.Verdict);
        Assert.Equal(2, profile.DistinctScores);
    }

    [Fact]
    public void ThePageDepthIsRequiredAndMustBePositive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Measure([("a", 1.0)], cutoff: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Measure([("a", 1.0)], cutoff: -1));
        Assert.Throws<ArgumentNullException>(() => QueryDiscrimination.Measure("q", null!, 10));
        Assert.Throws<ArgumentException>(() => QueryDiscrimination.Measure(" ", Rank([("a", 1.0)]), 10));
    }

    [Fact]
    public void ExplainSaysWhatWasMeasuredWithoutSpeculating()
    {
        DiscriminationProfile flat = Measure([("a", 1.0), ("b", 1.0)], cutoff: 10);

        Assert.Contains("share a single score", flat.Explain());
        Assert.Contains("tie-break", flat.Explain());
    }

    private static DiscriminationProfile Measure((string Id, double Score)[] results, int cutoff) =>
        QueryDiscrimination.Measure("q", Rank(results), cutoff, complete: true);

    [Fact]
    public void TheReferenceCorpusHasNoUndiscriminatedQueryAndNoTieAtTheReportedCut()
    {
        // The committed corpus is curated, so this is the false-positive guard: if the diagnostic
        // fires here, on ordinary text, it would fire on live traffic for reasons that are not the
        // caller's fault. The engine is searched to the corpus size rather than to the page, because
        // a page truncated at the cut cannot show a class that extends past it.
        (ITextIndex index, Dictionary<string, string> queries) = ReferenceCorpus();
        var tokenizer = Tokenizer.Default;
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(), tokenizer);
        int depth = index.Count;

        var profiles = queries
            .Select(query => QueryDiscrimination.Measure(
                query.Key, engine.Search(query.Value, new SearchOptions(depth)), cutoff: 10))
            .ToList();

        Assert.DoesNotContain(profiles, p =>
            p.Verdict is DiscriminationVerdict.NoDiscrimination or DiscriminationVerdict.TiedAtCut);
        Assert.Contains(profiles, p => p.Verdict == DiscriminationVerdict.TiedWithinPage);
        Assert.Contains(profiles, p => p.Verdict == DiscriminationVerdict.SingleResult);
    }

    [Fact]
    public void AQueryOfOnlyProhibitedAtomsIsReportedAsNoDiscrimination()
    {
        // Documented in RankedTextSearchEngine: a query of only prohibited atoms scores every
        // surviving document 1, and the configured tie-break ranks them. This is the degenerate case
        // the diagnostic exists to surface — the whole order was the tie-break's, not the scorer's.
        (ITextIndex index, _) = ReferenceCorpus();
        var tokenizer = Tokenizer.Default;
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer(), tokenizer);

        var results = engine.Search("-zzzznotaword", new SearchOptions(index.Count));
        DiscriminationProfile profile = QueryDiscrimination.Measure("prohibited", results, cutoff: 10);

        Assert.Equal(index.Count, profile.Hits);
        Assert.Equal(DiscriminationVerdict.NoDiscrimination, profile.Verdict);
        Assert.Equal(1, profile.DistinctScores);
        Assert.Equal(index.Count, profile.LargestClass);
    }

    private static (ITextIndex Index, Dictionary<string, string> Queries) ReferenceCorpus()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "LexiSharp.slnx")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        string root = Path.Combine(directory!.FullName, "bench", "reference-corpus");

        var documents = MarkdownLoader
            .LoadDirectory(Path.Combine(root, "corpus"), new MarkdownLoadOptions { TextFieldNames = ["title"] })
            .Select(document => document.ToSearchDocument())
            .ToList();

        var index = new InMemoryTextIndex(Tokenizer.Default);
        index.Index(documents);

        var queries = JsonSerializer.Deserialize<Dictionary<string, string>>(
            File.ReadAllText(Path.Combine(root, "queries.json")))!;

        return (index, queries);
    }

    private static IReadOnlyList<SearchResult> Rank((string Id, double Score)[] results)
    {
        var ranked = new List<SearchResult>(results.Length);
        foreach ((string id, double score) in results)
            ranked.Add(new SearchResult(id, score, new SearchDocument(id, id)));

        return ranked;
    }
}
