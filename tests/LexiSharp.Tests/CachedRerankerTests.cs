using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Eval;
using Xunit;

// The library has a public IReranker too; this file is about the harness's internal one.
using EvalReranker = LexiSharp.Eval.IReranker;

namespace LexiSharp.Tests;

/// <summary>
/// The rerank cache: what makes a ten-hour run resumable. The tests are about the two ways that can
/// fail silently — a resumed run that scores differently from an uninterrupted one, and a cache that
/// believes a half-written line.
/// </summary>
public class CachedRerankerTests
{
    [Fact]
    public void ASecordRunIsServedWithoutCallingTheModelAgain()
    {
        string path = TempFile();
        IReadOnlyList<SearchDocument> candidates = Documents();

        using (var first = new CachedReranker(new FixedReranker(), path))
        {
            first.Rerank("bile salts", candidates);
            Assert.Equal(3, first.Computed);
            Assert.Equal(0, first.Reused);
        }

        // A throwing inner is the assertion: if the cache missed, the test fails with the throw
        // rather than with a score comparison that could pass for the wrong reason.
        using var second = new CachedReranker(new ThrowingReranker(), path);

        IReadOnlyList<(string DocumentId, float Score)> results = second.Rerank("bile salts", candidates);

        Assert.Equal(3, second.Reused);
        Assert.Equal(0, second.Computed);
        Assert.Equal(["a", "c", "b"], results.Select(pair => pair.DocumentId));
    }

    [Fact]
    public void OnlyThePairsTheCacheHasNotSeenReachTheModel()
    {
        string path = TempFile();

        using (var first = new CachedReranker(new FixedReranker(), path))
            first.Rerank("bile salts", Documents().Take(2).ToArray());

        var inner = new FixedReranker();
        using var second = new CachedReranker(inner, path);

        second.Rerank("bile salts", Documents());

        // Two of three are cached, so the second run answers one candidate, not three. This is what
        // makes an interrupted run resume at the pair rather than at the query.
        string[] scored = Assert.Single(inner.Calls).Ids;
        Assert.Equal(["c"], scored);
        Assert.Equal(1, second.Computed);
        Assert.Equal(2, second.Reused);
    }

    [Fact]
    public void ATieKeepsTheCandidateOrderItHadBeforeCaching()
    {
        // The uncached reranker orders by score descending and, being a stable sort, keeps the
        // candidate order on a tie. A resumed run has to reproduce that: the rank-10 boundary is
        // exactly where a tie decides a query's nDCG.
        string path = TempFile();

        using (var first = new CachedReranker(new ConstantReranker(0.5f), path))
            first.Rerank("bile salts", Documents());

        using var second = new CachedReranker(new ConstantReranker(0.5f), path);
        IReadOnlyList<(string DocumentId, float Score)> results = second.Rerank("bile salts", Documents());

        Assert.Equal(["a", "b", "c"], results.Select(pair => pair.DocumentId));
    }

    [Fact]
    public void ATruncatedLastLineIsSkippedAndTheRestIsKept()
    {
        // What a kill during a write leaves behind. Refusing the file would throw away every hour
        // already in it.
        string path = TempFile();

        using (var first = new CachedReranker(new FixedReranker(), path))
            first.Rerank("bile salts", Documents());

        File.AppendAllText(path, "deadbeefcafe\tb");

        using var second = new CachedReranker(new ThrowingReranker(), path);
        IReadOnlyList<(string DocumentId, float Score)> results = second.Rerank("bile salts", Documents());

        Assert.Equal(1, second.Unreadable);
        Assert.Equal(3, second.Loaded);
        Assert.Equal(["a", "c", "b"], results.Select(pair => pair.DocumentId));
    }

    [Fact]
    public void ADifferentQueryIsANotACacheHit()
    {
        // Keying by document id alone would serve one query's scores to another's candidates — a
        // wrong answer that looks like a fast run.
        string path = TempFile();

        using (var first = new CachedReranker(new FixedReranker(), path))
            first.Rerank("bile salts", Documents());

        var inner = new FixedReranker();
        using var second = new CachedReranker(inner, path);

        second.Rerank("vesicle fusion", Documents());

        Assert.Equal(3, inner.Calls.Single().Ids.Length);
    }

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "lexisharp-rerank-cache-" + Guid.NewGuid().ToString("N") + ".tsv");

    private static IReadOnlyList<SearchDocument> Documents() =>
    [
        new SearchDocument("a", "bile salts emulsify fat"),
        new SearchDocument("b", "salts of bile"),
        new SearchDocument("c", "vesicle fusion"),
    ];

    /// <summary>A deterministic score, so the order is the query's and not the clock's.</summary>
    private class FixedReranker : EvalReranker
    {
        public List<(string Query, string[] Ids)> Calls { get; } = [];

        public virtual IReadOnlyList<(string DocumentId, float Score)> Rerank(
            string query, IReadOnlyList<SearchDocument> candidates)
        {
            Calls.Add((query, candidates.Select(candidate => candidate.Id).ToArray()));

            return candidates
                .Select(candidate => (candidate.Id, Score: (query.Length * 10f) + candidate.Text.Length))
                .OrderByDescending(pair => pair.Score)
                .ToArray();
        }
    }

    private sealed class ConstantReranker(float score) : EvalReranker
    {
        public IReadOnlyList<(string DocumentId, float Score)> Rerank(
            string query, IReadOnlyList<SearchDocument> candidates) =>
            candidates.Select(candidate => (candidate.Id, score)).OrderByDescending(pair => pair.Item2).ToArray();
    }

    private sealed class ThrowingReranker : EvalReranker
    {
        public IReadOnlyList<(string DocumentId, float Score)> Rerank(
            string query, IReadOnlyList<SearchDocument> candidates) =>
            throw new InvalidOperationException(
                "The cache missed, so the model would have run: the point of the test is that it does not.");
    }
}
