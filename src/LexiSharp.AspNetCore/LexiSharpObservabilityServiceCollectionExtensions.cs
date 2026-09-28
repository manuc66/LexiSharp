using LexiSharp.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

// CA1859 ("use a concrete type instead of the interface") is suppressed on the lines
// below. The interface is the published return type: a List<T> or an array in its place
// would hand callers a mutable collection through a contract that says they cannot have
// one, and what it saves is a single interface dispatch per call, which no measurement in
// docs/benchmarks.md attributes time to.

namespace LexiSharp.AspNetCore;

/// <summary>
/// One-call registration of the LexiSharp observability pieces on an
/// <see cref="IServiceCollection"/>.
/// </summary>
public static class LexiSharpObservabilityServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="InMemoryRetrievalMetrics"/> as the singleton
    /// <see cref="IRetrievalMetrics"/>, for reading retrieval counters in-process.
    /// </summary>
    /// <remarks>
    /// The collector itself is exposed as <see cref="InMemoryRetrievalMetrics"/> so a diagnostics
    /// endpoint can call <see cref="InMemoryRetrievalMetrics.Snapshot"/> and get every counter, not
    /// just the interface. Registering it again replaces the previous instance, so a caller can
    /// substitute their own <see cref="IRetrievalMetrics"/> without a duplicate registration.
    /// </remarks>
    public static IServiceCollection AddLexiSharpRetrievalMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<InMemoryRetrievalMetrics>();
        services.TryAddSingleton<IRetrievalMetrics>(
            static provider => provider.GetRequiredService<InMemoryRetrievalMetrics>());

        return services;
    }

    /// <summary>
    /// Registers the index health check, so <c>/health</c> reports whether search is actually able
    /// to answer.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="index">
    /// The index to watch, resolved once at registration. Prefer the
    /// <see cref="AddLexiSharpSearchHealthCheck(Microsoft.Extensions.DependencyInjection.IServiceCollection, Func{IServiceProvider, ITextIndex}, Action{LexiSharpIndexHealthCheckOptions})"/>
    /// overload when the index is itself built by a factory in the container.
    /// </param>
    /// <param name="configure">Optional tuning of the check.</param>
    public static IServiceCollection AddLexiSharpSearchHealthCheck(
        this IServiceCollection services,
        ITextIndex index,
        Action<LexiSharpIndexHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(index);

        return services.AddLexiSharpSearchHealthCheck(_ => index, configure);
    }

    /// <summary>
    /// Registers the index health check against an index resolved from the container at call time.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="indexFactory">
    /// Resolves the index to watch, invoked once per health check so a container-scoped index is
    /// not captured at startup.
    /// </param>
    /// <param name="configure">Optional tuning of the check.</param>
    public static IServiceCollection AddLexiSharpSearchHealthCheck(
        this IServiceCollection services,
        Func<IServiceProvider, ITextIndex> indexFactory,
        Action<LexiSharpIndexHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(indexFactory);

        var options = new LexiSharpIndexHealthCheckOptions();
        configure?.Invoke(options);

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            options.Name,
            sp => CreateCheck(sp, indexFactory, options),
            failureStatus: null,
            tags: options.Tags));

        return services;
    }

    private static IHealthCheck CreateCheck( // NOSONAR:CA1859
        IServiceProvider provider,
        Func<IServiceProvider, ITextIndex> indexFactory,
        LexiSharpIndexHealthCheckOptions options)
    {
        ITextIndex index = indexFactory(provider)
            ?? throw new InvalidOperationException(
                "The LexiSharp health check resolved a null index. The indexFactory must return a live index.");

        return new LexiSharpIndexHealthCheck(
            index,
            options.MinimumDocumentCount,
            options.Probe,
            options.LastIndexedAt,
            options.MaxAge);
    }
}

/// <summary>Tuning for the registration helpers on <see cref="LexiSharpObservabilityServiceCollectionExtensions"/>.</summary>
public sealed class LexiSharpIndexHealthCheckOptions
{
    /// <summary>Name the check is registered under. Default: <c>lexisharp</c>.</summary>
    public string Name { get; set; } = "lexisharp";

    /// <summary>Tags applied to the registration, so <c>/health</c> can be filtered. Default: <c>["ready"]</c>.</summary>
    public IReadOnlyCollection<string> Tags { get; set; } = new[] { "ready" };

    /// <summary>Document count below which the index is unhealthy. Default: <c>1</c>.</summary>
    public int MinimumDocumentCount { get; set; } = 1;

    /// <summary>Optional asynchronous check of the backing store. A faulted probe is unhealthy.</summary>
    public Func<CancellationToken, Task>? Probe { get; set; }

    /// <summary>Optional accessor for when the index was last populated; use with <see cref="MaxAge"/>.</summary>
    public Func<DateTimeOffset?>? LastIndexedAt { get; set; }

    /// <summary>Optional freshness budget; a staler index is degraded rather than unhealthy.</summary>
    public TimeSpan? MaxAge { get; set; }
}
