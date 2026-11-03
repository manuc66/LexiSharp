using System;
using System.Collections.Generic;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The document-at-a-time traversal's block-bound pruning: what it skips, and what it must not change.
/// </summary>
/// <remarks>
/// The bound is the sum of each holding term's window maximum — the pair every segment block header
/// already carries — so the traversal can prove a document cannot reach the page without summing its
/// terms. These tests drive the traversal directly, with a sink that counts what it was offered, so
/// "it skipped" is an assertion rather than an inference from a faster run.
/// </remarks>
public class DocumentAtATimePruningTests
{
    /// <summary>A weight that is monotone in the frequency and ignores the length, so a bound is readable.</summary>
    private readonly struct FrequencyWeight : IWeightFunction
    {
        public double Weight(int termFrequency, int documentLength) => termFrequency;
    }

    /// <summary>Counts the contributions it is asked to evaluate, so "no bound was computed" is an assertion.</summary>
    private struct CountingWeight : IWeightFunction
    {
        public static int Calls;

        public double Weight(int termFrequency, int documentLength)
        {
            Calls++;
            return termFrequency;
        }
    }

    [Fact]
    public void ItSkipsADocumentWhoseBoundCannotReachThePage()
    {
        // Two terms. Document 0 carries both (score 2), document 200 carries one (score 1), and a
        // page of one is full after the first.
        PostingArrays[] postings =
        [
            Plate([0, 200], [1, 1], maxFrequencies: [1, 1], minLengths: [1, 1]),
            Plate([0], [1], maxFrequencies: [1], minLengths: [1]),
        ];

        var exhaustive = new CountingSink(limit: 1);
        DocumentAtATime.Score([new FrequencyWeight(), new FrequencyWeight()], postings, Lengths(256), exhaustive);

        var pruned = new CountingSink(limit: 1) { Pruning = true };
        DocumentAtATime.Score([new FrequencyWeight(), new FrequencyWeight()], postings, Lengths(256), pruned);

        Assert.Equal(2, exhaustive.Offered.Count);
        Assert.Single(pruned.Offered);

        // The document that survived is the one the exhaustive pass kept, at the same score.
        Assert.Equal(exhaustive.Offered[0], pruned.Offered[0]);
        Assert.Equal(2, pruned.Offered[0].Score);
    }

    /// <summary>
    /// A tie is never pruned: a document scoring exactly the page's worst can still be ordered ahead of
    /// it by its identifier, so the comparison is strictly below.
    /// </summary>
    [Fact]
    public void ItDoesNotPruneADocumentThatTiesThePage()
    {
        PostingArrays[] postings =
        [
            Plate([0, 200], [1, 1], maxFrequencies: [1, 1], minLengths: [1, 1]),
        ];

        var exhaustive = new CountingSink(limit: 1);
        DocumentAtATime.Score([new FrequencyWeight()], postings, Lengths(256), exhaustive);

        var pruned = new CountingSink(limit: 1) { Pruning = true };
        DocumentAtATime.Score([new FrequencyWeight()], postings, Lengths(256), pruned);

        Assert.Equal(2, exhaustive.Offered.Count);
        Assert.Equal(2, pruned.Offered.Count);
        Assert.Equal(exhaustive.Offered, pruned.Offered);
    }

    /// <summary>
    /// Without window bounds the traversal must not prune at all: a missing bound read as zero would
    /// skip documents that belong in the page.
    /// </summary>
    [Fact]
    public void APlateWithoutBoundsIsNeverPruned()
    {
        PostingArrays[] postings =
        [
            new PostingArrays([0, 200], [1, 1]),
            new PostingArrays([0], [1]),
        ];

        var exhaustive = new CountingSink(limit: 1);
        DocumentAtATime.Score([new FrequencyWeight(), new FrequencyWeight()], postings, Lengths(256), exhaustive);

        var pruned = new CountingSink(limit: 1) { Pruning = true };
        DocumentAtATime.Score([new FrequencyWeight(), new FrequencyWeight()], postings, Lengths(256), pruned);

        Assert.Equal(exhaustive.Offered, pruned.Offered);
    }

    /// <summary>
    /// A page that is not full cannot be beaten, so the traversal must not compute a bound for it: a
    /// bound per term per candidate that is thrown away is what made the pruned tail query slower than
    /// the unpruned one on a 100,000-document corpus.
    /// </summary>
    [Fact]
    public void ItComputesNoBoundWhileThePageIsNotFull()
    {
        PostingArrays[] postings =
        [
            Plate([0, 200], [1, 1], maxFrequencies: [1, 1], minLengths: [1, 1]),
        ];

        // A page of ten over two candidates: the threshold never exists, so nothing may be bounded.
        CountingWeight.Calls = 0;

        var plain = new CountingSink(limit: 10);
        DocumentAtATime.Score([new CountingWeight()], postings, Lengths(256), plain);

        int unpruned = CountingWeight.Calls;

        CountingWeight.Calls = 0;

        var pruning = new CountingSink(limit: 10) { Pruning = true };
        DocumentAtATime.Score([new CountingWeight()], postings, Lengths(256), pruning);

        Assert.Equal(unpruned, CountingWeight.Calls);
        Assert.Equal(plain.Offered, pruning.Offered);
    }

    private static PostingArrays Plate(int[] ordinals, int[] frequencies, int[]? maxFrequencies, int[]? minLengths) =>
        new(ordinals, frequencies, maxFrequencies, minLengths);

    private static int[] Lengths(int count)
    {
        var lengths = new int[count];
        Array.Fill(lengths, 1);

        return lengths;
    }

    /// <summary>
    /// A page of one, counting what it is offered. Its threshold is the worst kept score, which for a
    /// limit of one is the only score retained.
    /// </summary>
    private sealed class CountingSink(int limit) : IDocumentScoreSink
    {
        private readonly List<double> _kept = [];

        public List<(int Ordinal, double Score)> Offered { get; } = [];

        public bool Pruning { get; init; }

        public void Score(int ordinal, double score)
        {
            Offered.Add((ordinal, score));

            if (_kept.Count < limit)
            {
                _kept.Add(score);
                return;
            }

            _kept.Sort();
            _kept[0] = Math.Max(_kept[0], score);
        }

        public bool HasThreshold => _kept.Count >= limit;

        public bool CanPrune(double upperBound) =>
            Pruning && _kept.Count >= limit && upperBound < _kept.Min();
    }
}
