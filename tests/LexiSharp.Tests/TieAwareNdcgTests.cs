using System;
using System.Collections.Generic;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Tie-aware nDCG. These decide whether the number can be believed: an expectation that is not the
/// mean over admissible orderings, or a bound that is not attained, is a confident wrong answer on
/// exactly the runs where ties matter.
/// </summary>
/// <remarks>
/// Two independent checks. The brute-force cases enumerate every admissible ordering and compare the
/// closed form against the enumeration. The alignment case reproduces the reference implementation of
/// Ma and Cheng (RecSys 2026) for its single-positive setting, so a divergence from the published
/// protocol shows up as a failing test rather than as a number nobody can reproduce.
/// </remarks>
public class TieAwareNdcgTests
{
    [Fact]
    public void WithoutTiesTheThreeValuesAgreeWithThePlainMetric()
    {
        var ranked = Rank([("a", 4.0), ("b", 3.0), ("c", 2.0), ("d", 1.0)]);
        var relevance = new Dictionary<string, double> { ["a"] = 3, ["b"] = 1, ["c"] = 2, ["d"] = 0 };

        TieAwareMetric metric = RetrievalMetrics.TieAwareNdcgAtK(ranked, relevance, k: 4);

        double plain = RetrievalMetrics.NdcgAtK(
            ranked.Select(r => r.DocumentId).ToList(), relevance, k: 4);

        Assert.Equal(plain, metric.Expected, precision: 12);
        Assert.Equal(plain, metric.Minimum, precision: 12);
        Assert.Equal(plain, metric.Maximum, precision: 12);
        Assert.True(metric.IsDetermined);
        Assert.Equal(0, metric.Width, precision: 12);
    }

    [Fact]
    public void TheExpectationMatchesThePublishedReferenceImplementationForOnePositiveItem()
    {
        // AlignGroup/metrics_tie_aware.py, get_ndcg_k: with exactly one positive item and no ideal
        // normalisation (IDCG = 1), the expectation is the mean discount over the positive item's
        // tie block, or over the part of it inside the page.
        var ranked = Rank([("p", 2.5), ("n1", 2.5), ("n2", 2.5), ("n4", 2.5), ("n3", 1.0)]);
        var relevance = new Dictionary<string, double> { ["p"] = 1 };

        for (int k = 1; k <= 5; k++)
        {
            TieAwareMetric metric = RetrievalMetrics.TieAwareNdcgAtK(
                ranked, relevance, k, NdcgGain.Linear);

            Assert.Equal(ReferenceGetNdcgK([2.5, 2.5, 2.5, 2.5, 1.0], positiveAt: 0, k), metric.Expected, precision: 12);
        }
    }

    [Theory]
    [MemberData(nameof(BruteForceCases))]
    public void TheClosedFormEqualsTheEnumerationOfEveryAdmissibleOrdering(
        (string Id, double Score)[] ranked,
        Dictionary<string, double> relevance,
        int k)
    {
        TieAwareMetric metric = RetrievalMetrics.TieAwareNdcgAtK(Rank(ranked), relevance, k);
        (double minimum, double maximum, double mean) = Enumerate(ranked, relevance, k);

        Assert.Equal(mean, metric.Expected, precision: 12);
        Assert.Equal(minimum, metric.Minimum, precision: 12);
        Assert.Equal(maximum, metric.Maximum, precision: 12);
    }

    public static TheoryData<(string Id, double Score)[], Dictionary<string, double>, int> BruteForceCases()
    {
        var data = new TheoryData<(string Id, double Score)[], Dictionary<string, double>, int>();

        // A tie inside the page: order is arbitrary, membership is not.
        data.Add(
            [("a", 3.0), ("b", 3.0), ("c", 2.0), ("d", 1.0)],
            new Dictionary<string, double> { ["a"] = 3, ["b"] = 0, ["c"] = 1, ["d"] = 0 },
            3);

        // A tie crossing the cut: which documents make the page is arbitrary.
        data.Add(
            [("a", 2.0), ("b", 2.0), ("c", 2.0), ("d", 1.0), ("e", 1.0)],
            new Dictionary<string, double> { ["a"] = 3, ["b"] = 0, ["c"] = 1, ["d"] = 0, ["e"] = 2 },
            2);

        // A constant score: every ordering is admissible.
        data.Add(
            [("a", 1.0), ("b", 1.0), ("c", 1.0), ("d", 1.0)],
            new Dictionary<string, double> { ["a"] = 3, ["b"] = 1, ["c"] = 0, ["d"] = 0 },
            2);

        // A run of four inside the page, with graded gains that make the bounds strict.
        data.Add(
            [("a", 5.0), ("b", 4.0), ("c", 4.0), ("d", 4.0), ("e", 4.0), ("f", 1.0)],
            new Dictionary<string, double> { ["a"] = 3, ["b"] = 2, ["c"] = 1, ["d"] = 0, ["e"] = 3, ["f"] = 1 },
            5);

        return data;
    }

    [Fact]
    public void ATruncatedPageWouldNarrowTheIntervalAndIsTheReasonToPassTheWholeList()
    {
        // "b" and "c" are tied for the last place of a two-document page. Their documents sit past
        // the cut, so a caller that kept only the page would never see that the last place was a coin
        // toss.
        var full = Rank([("a", 3.0), ("b", 2.0), ("c", 2.0)]);
        var page = Rank([("a", 3.0), ("b", 2.0)]);
        var relevance = new Dictionary<string, double> { ["a"] = 3, ["b"] = 2, ["c"] = 0 };

        TieAwareMetric fromFullList = RetrievalMetrics.TieAwareNdcgAtK(full, relevance, k: 2);
        TieAwareMetric fromPage = RetrievalMetrics.TieAwareNdcgAtK(page, relevance, k: 2);

        Assert.False(fromFullList.IsDetermined);
        Assert.True(fromPage.IsDetermined);
    }

    [Fact]
    public void RepeatedIdsCountOnceAtTheirFirstRank()
    {
        var ranked = Rank([("a", 3.0), ("a", 1.0), ("b", 1.0)]);
        var relevance = new Dictionary<string, double> { ["a"] = 3 };

        TieAwareMetric metric = RetrievalMetrics.TieAwareNdcgAtK(ranked, relevance, k: 3);

        // "a" is alone at its score; the second "a" must not tie it with "b".
        Assert.True(metric.IsDetermined);
        Assert.Equal(1.0, metric.Expected, precision: 12);
    }

    [Fact]
    public void ArgumentsAreValidated()
    {
        var ranked = Rank([("a", 1.0)]);

        Assert.Throws<ArgumentNullException>(() =>
            RetrievalMetrics.TieAwareNdcgAtK(null!, new Dictionary<string, double>(), 10));
        Assert.Throws<ArgumentNullException>(() =>
            RetrievalMetrics.TieAwareNdcgAtK(ranked, null!, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetrievalMetrics.TieAwareNdcgAtK(ranked, new Dictionary<string, double> { ["a"] = 1 }, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RetrievalMetrics.TieAwareNdcgAtK(ranked, new Dictionary<string, double> { ["a"] = -1 }, 10));
    }

    /// <summary>
    /// A literal transcription of <c>get_ndcg_k</c> from the reference repository, for a score matrix
    /// with a single positive item. Kept close to the original so a divergence is visible by reading.
    /// </summary>
    private static double ReferenceGetNdcgK(double[] scores, int positiveAt, int k)
    {
        double positive = scores[positiveAt];
        int numHigher = scores.Count(s => s > positive);
        int numEqual = scores.Count(s => s == positive);

        int lowest = numHigher + 1;
        int highest = numHigher + numEqual;

        var prefix = new double[k + 1];
        for (int r = 1; r <= k; r++)
            prefix[r] = prefix[r - 1] + 1.0 / Math.Log2(r + 1);

        int start = Math.Max(lowest, 1);
        int end = Math.Min(highest, k);

        return start > end ? 0 : (prefix[end] - prefix[start - 1]) / numEqual;
    }

    private static (double Min, double Max, double Mean) Enumerate(
        (string Id, double Score)[] ranked,
        Dictionary<string, double> relevance,
        int k)
    {
        var ordered = relevance.Values.OrderByDescending(level => level).ToList();
        double idcg = 0;
        int rank = 1;

        foreach (double level in ordered)
        {
            if (rank > k)
                break;

            if (level > 0)
            {
                idcg += (Math.Pow(2, level) - 1) / Math.Log2(rank + 1);
                rank++;
            }
        }

        // Maximal runs of equal score, which is where the order is free.
        var runs = new List<List<int>>();
        for (int i = 0; i < ranked.Length; i++)
        {
            if (i == 0 || ranked[i].Score != ranked[i - 1].Score)
                runs.Add(new List<int>());

            runs[^1].Add(i);
        }

        var values = new List<double>();

        foreach (var ordering in Cartesian(runs.Select(run => Permutations(run)).ToList()))
        {
            double dcg = 0;

            for (int position = 0; position < Math.Min(k, ordering.Count); position++)
            {
                string id = ranked[ordering[position]].Id;
                double level = relevance.GetValueOrDefault(id, 0);
                dcg += (Math.Pow(2, level) - 1) / Math.Log2(position + 2);
            }

            values.Add(idcg == 0 ? 0 : dcg / idcg);
        }

        return (values.Min(), values.Max(), values.Average());
    }

    private static IEnumerable<List<int>> Permutations(List<int> items)
    {
        if (items.Count <= 1)
        {
            yield return new List<int>(items);
            yield break;
        }

        for (int i = 0; i < items.Count; i++)
        {
            var rest = new List<int>(items);
            int head = rest[i];
            rest.RemoveAt(i);

            foreach (var tail in Permutations(rest))
            {
                tail.Insert(0, head);
                yield return tail;
            }
        }
    }

    private static IEnumerable<List<int>> Cartesian(List<IEnumerable<List<int>>> parts)
    {
        var current = new List<int>();

        IEnumerable<List<int>> Walk(int index)
        {
            if (index == parts.Count)
            {
                yield return new List<int>(current);
                yield break;
            }

            foreach (var option in parts[index])
            {
                current.AddRange(option);
                foreach (var result in Walk(index + 1))
                    yield return result;
                current.RemoveRange(current.Count - option.Count, option.Count);
            }
        }

        return Walk(0);
    }

    private static IReadOnlyList<SearchResult> Rank((string Id, double Score)[] results)
    {
        var ranked = new List<SearchResult>(results.Length);
        foreach ((string id, double score) in results)
            ranked.Add(new SearchResult(id, score, new SearchDocument(id, id)));

        return ranked;
    }
}
