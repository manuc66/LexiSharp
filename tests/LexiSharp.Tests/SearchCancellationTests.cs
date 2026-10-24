using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// A cancelled token stops a search instead of being accepted and ignored.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ITextSearchEngine.Search"/> takes a <see cref="CancellationToken"/> that, when it was
/// added, no engine consulted: the decorators forwarded it and every leaf dropped it. SonarCloud's
/// S8949 caught the same thing from the other side — ten call sites where a token was in scope and
/// not passed — and the reliability rating on new code fell to C, which failed the quality gate.
/// </para>
/// <para>
/// The fix forwards the token at every one of those sites and checks it in the ranked engine: once on
/// entry, then every 1024 candidates on the two per-document paths and once per entry on the
/// accumulation path. These tests pin the observable half of that — a cancelled token throws — and
/// the forwarding through a decorator, which is what a caller gets from the wrapped engine.
/// </para>
/// <para>
/// What is deliberately not asserted: that a search running on a large corpus stops promptly. That
/// is a property of the checkpoint interval, it is not deterministic to test, and pinning it with a
/// stopwatch would measure the host.
/// </para>
/// </remarks>
public class SearchCancellationTests
{
    private static InMemoryTextIndex Corpus()
    {
        var index = new InMemoryTextIndex();

        for (int i = 0; i < 20; i++)
            index.Add(new SearchDocument($"doc-{i}", "alpha beta gamma delta epsilon"));

        return index;
    }

    [Fact]
    public void ACancelledTokenStopsTheRankedEngine()
    {
        var engine = new RankedTextSearchEngine(Corpus(), new Bm25Scorer());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => engine.Search("alpha", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ACancelledTokenStopsTheSpanOverload()
    {
        var engine = new RankedTextSearchEngine(Corpus(), new Bm25Scorer());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // The span overload is the one the string overload forwards to, so this also pins the hop
        // that used to drop the token.
        Assert.Throws<OperationCanceledException>(() =>
            engine.Search("alpha".AsSpan(), cancellationToken: cancellation.Token));
    }

    [Fact]
    public void AThrownTokenReachesTheCallerThroughADecorator()
    {
        // The decorator forwards the token to the engine it wraps; the cancellation is the inner
        // engine's, and the caller sees it as OperationCanceledException rather than a result.
        var engine = new SynchronizedTextSearchEngine(new RankedTextSearchEngine(Corpus(), new Bm25Scorer()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => engine.Search("alpha", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void AnUncancelledTokenSearchesNormally()
    {
        var engine = new RankedTextSearchEngine(Corpus(), new Bm25Scorer());
        using var cancellation = new CancellationTokenSource();

        var page = engine.Search("alpha", cancellationToken: cancellation.Token);

        Assert.NotEmpty(page);
        Assert.All(page, result => Assert.Equal("doc-", result.DocumentId[..4]));
    }
}
