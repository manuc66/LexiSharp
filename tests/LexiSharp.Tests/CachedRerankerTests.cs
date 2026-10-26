using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

    [Fact]
    public void ARepeatedPairLoadsOnceAndIsCounted()
    {
        // The loader keeps one line per pair and reports the repetition. Refusing a file that repeats a
        // pair would throw away every other pair in it, and silently preferring the second score would
        // hide a file that disagrees with itself.
        string path = TempFile();

        using (var first = new CachedReranker(new FixedReranker(), path))
            first.Rerank("bile salts", Documents());

        string[] lines = File.ReadAllLines(path);
        File.WriteAllText(path, string.Join("\n", lines) + "\n" + lines[0] + "\n");

        using var second = new CachedReranker(new ThrowingReranker(), path);

        Assert.Equal(3, second.Loaded);
        Assert.Equal(1, second.Duplicates);
        Assert.Equal(0, second.Unreadable);
        Assert.Equal(["a", "c", "b"], second.Rerank("bile salts", Documents()).Select(pair => pair.DocumentId));
        Assert.Equal(3, second.Reused);
        Assert.Equal(0, second.Computed);
    }

    [Fact]
    public void ATornTailThatCutTheLastPairResumesToTheUninterruptedRanking()
    {
        // The existing torn-tail test tears a line off a *complete* cache, so nothing is missing and
        // the model never runs. This is the other case: the kill cut the last pair's line, so the
        // resumed run has to score that pair and still rank the query exactly as a run that was
        // never interrupted. The rank-10 boundary is where a difference would show.
        string path = TempFile();
        IReadOnlyList<SearchDocument> candidates = Documents();

        IReadOnlyList<(string DocumentId, float Score)> uninterrupted;
        using (var first = new CachedReranker(new FixedReranker(), path))
            uninterrupted = first.Rerank("bile salts", candidates);

        // Pairs land in candidate order, so the last line is 'c' — the pair the torn write lost.
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(3, lines.Length);
        File.WriteAllText(path, string.Join("\n", lines[..2]) + "\ndeadbeefcafe\tb");

        var inner = new FixedReranker();
        using (var resumed = new CachedReranker(inner, path))
        {
            IReadOnlyList<(string DocumentId, float Score)> results = resumed.Rerank("bile salts", candidates);

            Assert.Equal(uninterrupted, results);
            Assert.Equal(1, resumed.Unreadable);
            Assert.Equal(2, resumed.Loaded);
            Assert.Equal(["c"], Assert.Single(inner.Calls).Ids);
            Assert.Equal(1, resumed.Computed);
            Assert.Equal(2, resumed.Reused);
            Assert.Equal(1, resumed.Appended);
        }

        // The pair the resumed run wrote landed behind the fragment, with no newline between them, so
        // the file's next reader sees one four-field line and keeps the two pairs that did load. A
        // torn tail therefore costs the pair written next as well as the one it cut; it does not cost
        // the file. Stated here because it is the reason a run against a torn cache recomputes one
        // pair per interruption rather than none.
        using var reloaded = new CachedReranker(new ThrowingReranker(), path);
        Assert.Equal(2, reloaded.Loaded);
        Assert.Equal(1, reloaded.Unreadable);
    }

    [Fact]
    public void ParallelWorkersWriteOneWellFormedLinePerPair()
    {
        // The harness reranks from a Parallel.For when --jobs is above one. The log behind this cache
        // is a StreamWriter — one char buffer, not thread-safe — and a pair costs four Writes and a
        // newline; interleaved from several workers, that is how a cache came to hold lines with four
        // fields. Two workers missing the same pair at the same moment is how one came to hold that
        // pair twice. The assertions are the file's invariants rather than the interleaving that broke
        // them, so they hold whatever schedule the pool picks.
        string path = TempFile();

        using (var cache = new CachedReranker(new SlowReranker(TimeSpan.FromMilliseconds(2)), path))
        {
            Parallel.For(0, Queries, new ParallelOptions { MaxDegreeOfParallelism = Workers }, query =>
                cache.Rerank(QueryText(query), Candidates(query)));

            Assert.Equal(Queries * DocumentsPerQuery, cache.Computed);
            Assert.Equal(Queries * DocumentsPerQuery, cache.Appended);
            Assert.Equal(0, cache.Reused);
        }

        string text = File.ReadAllText(path);
        string[] lines = File.ReadAllLines(path);

        // A torn write is a last line with no newline behind it, so the terminator is its own check.
        Assert.EndsWith("\n", text, StringComparison.Ordinal);
        Assert.Equal(Queries * DocumentsPerQuery, lines.Length);

        // (i) and (iii): three fields, none empty, a score that parses — on every line. Four fields is
        // what two workers' writes spliced together produce.
        foreach (string line in lines)
        {
            string[] parts = line.Split('\t');
            Assert.Equal(3, parts.Length);
            Assert.False(string.IsNullOrWhiteSpace(parts[0]), $"no query key in '{line}'");
            Assert.False(string.IsNullOrWhiteSpace(parts[1]), $"no document id in '{line}'");
            Assert.True(
                float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out _),
                $"unparseable score in '{line}'");
        }

        // (ii) through the loader, which is the reader that matters: an inner that throws on a miss
        // means any pair the file lost fails the test with the throw, and the comparison carries the
        // score as well as the order.
        using var reloaded = new CachedReranker(new ThrowingReranker(), path);

        Assert.Equal(Queries * DocumentsPerQuery, reloaded.Loaded);
        Assert.Equal(0, reloaded.Unreadable);

        for (int query = 0; query < Queries; query++)
            Assert.Equal(ExpectedRanking(query), reloaded.Rerank(QueryText(query), Candidates(query)));

        Assert.Equal(Queries * DocumentsPerQuery, reloaded.Reused);
    }

    [Fact]
    public void WorkersThatScoreTheSamePairAppendItOnce()
    {
        // Two workers can miss the same pair at the same moment — the harness shares candidates
        // between the reranked rows of one corpus, and nothing stops two queries from reaching the
        // same document. The decision is that the file carries one line per pair and the loser ranks
        // with the winner's score: the second model pass is wasted, but a duplicate line is not
        // written and the two queries cannot disagree about a score.
        string path = TempFile();
        IReadOnlyList<SearchDocument> candidates = Documents();
        string query = "bile salts";

        var reranker = new RendezvousReranker(Workers);
        var returned = new IReadOnlyList<(string DocumentId, float Score)>[Workers];

        using (var cache = new CachedReranker(reranker, path))
        {
            // Dedicated threads rather than Parallel.For: the rendezvous inside the fake only holds
            // the workers that exist, and with eight of our own there are exactly eight.
            using var start = new ManualResetEventSlim();
            var threads = Enumerable.Range(0, Workers)
                .Select(worker => new Thread(() =>
                {
                    start.Wait();
                    returned[worker] = cache.Rerank(query, candidates);
                }))
                .ToArray();

            foreach (Thread thread in threads)
                thread.Start();

            start.Set();

            foreach (Thread thread in threads)
                Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "a worker did not finish");

            // Every worker missed every pair, so the model scored each pair eight times over. That
            // waste is accepted; what is not accepted is eight lines for three pairs.
            Assert.Equal(Workers * candidates.Count, cache.Computed);
            Assert.Equal(candidates.Count, cache.Appended);
        }

        string[] lines = File.ReadAllLines(path);

        // 8 model passes of 3 pairs, 3 lines: one per pair.
        Assert.Equal(Workers, reranker.Passes);
        Assert.Equal(candidates.Count, lines.Length);
        Assert.All(lines, line => Assert.Equal(3, line.Split('\t').Length));

        Assert.All(returned, result => Assert.Equal(returned[0], result));

        using var reloaded = new CachedReranker(new ThrowingReranker(), path);
        Assert.Equal(3, reloaded.Loaded);
        Assert.Equal(0, reloaded.Unreadable);
        Assert.Equal(3, reloaded.Rerank(query, candidates).Count);
        Assert.Equal(3, reloaded.Reused);
    }

    /// <summary>Queries the parallel test scores, enough of them to keep every worker in the log.</summary>
    private const int Queries = 200;

    private const int DocumentsPerQuery = 10;

    /// <summary>Workers the parallel tests use; above four, so a torn line has somewhere to come from.</summary>
    private const int Workers = 8;

    private static string QueryText(int query) => $"query {query}";

    private static IReadOnlyList<SearchDocument> Candidates(int query) =>
        Enumerable.Range(0, DocumentsPerQuery)
            .Select(document => new SearchDocument(
                $"doc-{query}-{document}",
                $"passage {document} about argument {query}"))
            .ToArray();

    private static IReadOnlyList<(string DocumentId, float Score)> ExpectedRanking(int query) =>
        Candidates(query)
            .Select(candidate => (candidate.Id, Score: SlowReranker.Score(QueryText(query), candidate.Id)))
            .OrderByDescending(pair => pair.Score)
            .ToArray();

    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "lexisharp-rerank-cache-" + Guid.NewGuid().ToString("N") + ".tsv");

    private static IReadOnlyList<SearchDocument> Documents() =>
    [
        new SearchDocument("a", "bile salts emulsify fat"),
        new SearchDocument("b", "salts of bile"),
        new SearchDocument("c", "vesicle fusion"),
    ];

    /// <summary>
    /// A model slow enough that workers reach the log together, and deterministic, so the file can be
    /// checked against what was asked for rather than against itself.
    /// </summary>
    private sealed class SlowReranker(TimeSpan delay) : EvalReranker
    {
        public IReadOnlyList<(string DocumentId, float Score)> Rerank(
            string query, IReadOnlyList<SearchDocument> candidates)
        {
            Thread.Sleep(delay);

            return candidates
                .Select(candidate => (candidate.Id, Score: Score(query, candidate.Id)))
                .OrderByDescending(pair => pair.Score)
                .ToArray();
        }

        public static float Score(string query, string documentId) =>
            1f + ((query.Length * 31) + (documentId.Length * 17)) % 1000 / 1000f;
    }

    /// <summary>
    /// Holds every pass until all of them have started, so the same-pair race is a property of the
    /// test rather than of how the pool happened to schedule eight threads.
    /// </summary>
    private sealed class RendezvousReranker(int workers) : EvalReranker
    {
        private readonly CountdownEvent _arrived = new(workers);
        private int _passes;

        public int Passes => Volatile.Read(ref _passes);

        public IReadOnlyList<(string DocumentId, float Score)> Rerank(
            string query, IReadOnlyList<SearchDocument> candidates)
        {
            Interlocked.Increment(ref _passes);
            _arrived.Signal();

            if (!_arrived.Wait(TimeSpan.FromSeconds(30)))
                throw new TimeoutException("The workers did not all reach the model pass in time.");

            return candidates
                .Select(candidate => (candidate.Id, Score: SlowReranker.Score(query, candidate.Id)))
                .OrderByDescending(pair => pair.Score)
                .ToArray();
        }
    }

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
