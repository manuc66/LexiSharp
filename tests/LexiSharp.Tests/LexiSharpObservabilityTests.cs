using System.Collections.Concurrent;
using LexiSharp;
using LexiSharp.AspNetCore;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers the ASP.NET Core observability pieces: the <see cref="RetrievalLogger"/> adapter and
/// <see cref="LexiSharpIndexHealthCheck"/>, neither of which needs a host to be exercised.
/// </summary>
public class LexiSharpObservabilityTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "oauth access token renewal"),
        new("d2", "vector search retrieval pipeline"),
    ];

    private sealed class RecordingLogger : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Entries.Enqueue((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public void LoggerAdapterWritesOneLinePerSearch()
    {
        var logger = new RecordingLogger();
        var index = new InMemoryTextIndex();
        var engine = new RankedTextSearchEngine(
            index, new Bm25Scorer(), telemetry: RetrievalLogger.Telemetry(logger));
        engine.Index(Corpus);

        engine.Search("token");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains(RankedTextSearchEngine.EngineName, entry.Message, StringComparison.Ordinal);
        Assert.Contains(RetrievalLogEvents.Search, entry.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoggerAdapterMapsWarningLevel()
    {
        var logger = new RecordingLogger();
        var engine = new RankedTextSearchEngine(
            new InMemoryTextIndex(),
            new Bm25Scorer(),
            telemetry: RetrievalLogger.Telemetry(logger));

        // Empty index: the engine degrades but answers, which is a warning, not an error.
        engine.Search("token");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
    }

    [Fact]
    public void LoggerAdapterDropsEventsBelowItsOwnThreshold()
    {
        var logger = new RecordingLogger();
        RetrievalLog sink = RetrievalLogger.For(logger, RetrievalLogLevel.Warning);

        sink(new RetrievalLogEvent(RetrievalLogLevel.Debug, "e", "stage", "m", 1, 1));
        sink(new RetrievalLogEvent(RetrievalLogLevel.Information, "e", "search", "m", 1, 1));

        Assert.Empty(logger.Entries);

        sink(new RetrievalLogEvent(RetrievalLogLevel.Warning, "e", "search", "m", 1, 1));

        Assert.Single(logger.Entries);
    }

    [Fact]
    public void LoggerAdapterAcceptsALoggerFactory()
    {
        RetrievalLog sink = RetrievalLogger.For(NullLoggerFactory.Instance);

        // NullLogger discards everything, so no event is observable afterwards and the property
        // under test is that adapting a factory is accepted and its sink is safe to call. Asserted,
        // because a bare sink(...) call is a test that passes even with an empty adapter.
        Assert.Null(Record.Exception(() =>
            sink(new RetrievalLogEvent(RetrievalLogLevel.Information, "e", "search", "m", 1, 1))));
    }

    [Fact]
    public void LoggerAdapterRejectsNullLogger()
    {
        Assert.Throws<ArgumentNullException>(() => RetrievalLogger.For((ILogger)null!));
        Assert.Throws<ArgumentNullException>(() => RetrievalLogger.For((ILoggerFactory)null!));
    }

    [Fact]
    public void HealthyWhenTheIndexHoldsDocuments()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus);

        var result = Check(new LexiSharpIndexHealthCheck(index));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(Corpus.Length, result.Data["documents"]);
    }

    [Fact]
    public void EmptyIndexIsUnhealthy()
    {
        // An index holding nothing answers every query with zero results: a silent failure, so it
        // must not read as healthy.
        var result = Check(new LexiSharpIndexHealthCheck(new InMemoryTextIndex()));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public void MinimumDocumentCountIsHonoured()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus);

        var result = Check(new LexiSharpIndexHealthCheck(index, minimumDocumentCount: 10));

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Equal(10, result.Data["expectedDocuments"]);
    }

    [Fact]
    public async Task HealthyWhenTheProbeSucceeds()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus);

        var check = new LexiSharpIndexHealthCheck(index, probe: _ => Task.CompletedTask);

        var result = await CheckAsync(check);

        Assert.Equal(HealthStatus.Healthy, result.Status);
    }

    [Fact]
    public async Task UnreachableBackingStoreIsUnhealthy()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus);

        var check = new LexiSharpIndexHealthCheck(
            index, probe: _ => throw new InvalidOperationException("connection refused"));

        var result = await CheckAsync(check);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.IsType<InvalidOperationException>(result.Exception);
    }

    [Fact]
    public async Task StaleIndexIsDegradedNotUnhealthy()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus);

        var check = new LexiSharpIndexHealthCheck(
            index,
            lastIndexedAt: () => DateTimeOffset.UtcNow.AddHours(-2),
            maxAge: TimeSpan.FromMinutes(30));

        var result = await CheckAsync(check);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task NeverPopulatedIndexIsDegraded()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus);

        var check = new LexiSharpIndexHealthCheck(
            index, lastIndexedAt: () => null, maxAge: TimeSpan.FromMinutes(30));

        var result = await CheckAsync(check);

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public void AgeBudgetWithoutAccessorIsRejected()
    {
        var index = new InMemoryTextIndex();

        // A budget with no way to read the age would silently never fire.
        Assert.Throws<ArgumentException>(
            () => new LexiSharpIndexHealthCheck(index, maxAge: TimeSpan.FromMinutes(1)));

        Assert.Throws<ArgumentException>(
            () => new LexiSharpIndexHealthCheck(index, lastIndexedAt: () => DateTimeOffset.UtcNow));
    }

    [Fact]
    public void NullIndexIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => new LexiSharpIndexHealthCheck(null!));
    }

    [Fact]
    public void RegistrationAddsTheCheckToTheContainer()
    {
        var index = new InMemoryTextIndex();
        index.Index(Corpus);

        var services = new ServiceCollection();
        services.AddLexiSharpRetrievalMetrics();
        services.AddLexiSharpSearchHealthCheck(_ => index);

        using var provider = services.BuildServiceProvider();

        var metrics = provider.GetRequiredService<InMemoryRetrievalMetrics>();
        var registrations = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<HealthCheckServiceOptions>>();

        Assert.NotNull(metrics);
        Assert.Contains(registrations.Value.Registrations, r => r.Name == "lexisharp");
        Assert.Contains(registrations.Value.Registrations, r => r.Name == "lexisharp" && r.Tags.Contains("ready"));
    }

    [Fact]
    public void MetricsRegistrationExposesBothTheConcreteAndTheInterface()
    {
        var services = new ServiceCollection();
        services.AddLexiSharpRetrievalMetrics();

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<InMemoryRetrievalMetrics>(),
            provider.GetRequiredService<IRetrievalMetrics>());
    }

    [Fact]
    public void TelemetryFlowsFromTheIndexOptionsToTheEngine()
    {
        var metrics = new InMemoryRetrievalMetrics();

        var index = new LexiSharpIndex<SearchDocument>(o => o.Telemetry = new RetrievalTelemetry(metrics: metrics));
        index.AddRange(Corpus);
        index.Search("token");

        // The facade must forward telemetry to the engine it builds, otherwise configuring it on
        // the options object would look supported while recording nothing.
        Assert.Equal(1, metrics.SearchCount);
    }

    // A real registration rather than Registration = default, which is a null in a non-nullable
    // field (CS8625) and hands a check a context that cannot occur under a real host. Wrapping the
    // check under test is the coherent choice: nothing here reads Registration, but a context that
    // describes the check being run is the one a host would have passed.
    private static HealthCheckResult Check(IHealthCheck check) =>
        CheckAsync(check).GetAwaiter().GetResult();

    private static Task<HealthCheckResult> CheckAsync(IHealthCheck check) =>
        check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(
                "lexisharp-test",
                check,
                failureStatus: null,
                tags: null,
                timeout: null),
        });
}
