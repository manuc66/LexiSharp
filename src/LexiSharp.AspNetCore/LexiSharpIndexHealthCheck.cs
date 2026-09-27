using LexiSharp.Core;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace LexiSharp.AspNetCore;

/// <summary>
/// Reports whether a search index is ready to serve traffic: non-empty, and optionally connected
/// to its backing store and not too stale.
/// </summary>
/// <remarks>
/// <para>
/// Register it alongside the rest of your health checks and map
/// <c>app.MapHealthChecks("/health")</c>:
///
/// <code>
/// services.AddHealthChecks()
///         .AddCheck&lt;LexiSharpIndexHealthCheck&gt;("lexisharp");
/// </code>
///
/// A non-empty index is <see cref="HealthCheckResult.Healthy"/>; an empty one is
/// <see cref="HealthCheckResult.Unhealthy"/>, because an index that holds nothing answers every
/// query with zero results, which is a silent failure rather than an obvious one.
/// </para>
/// <para>
/// The optional <c>probe</c> is how a database-backed engine gets verified: pass a delegate that
/// round-trips a cheap query (<c>SELECT 1</c>), and its failure is reported as unhealthy. The
/// default is "no backing store to check", which is the right assumption for the in-memory engine.
/// </para>
/// </remarks>
public sealed class LexiSharpIndexHealthCheck : IHealthCheck
{
    private readonly ITextIndex _index;
    private readonly Func<CancellationToken, Task>? _probe;
    private readonly int _minimumDocumentCount;
    private readonly Func<DateTimeOffset?>? _lastIndexedAt;
    private readonly TimeSpan? _maxAge;

    /// <param name="index">The index to verify.</param>
    /// <param name="minimumDocumentCount">
    /// Document count below which the index is reported unhealthy. Default: <c>1</c>.
    /// </param>
    /// <param name="probe">
    /// Optional asynchronous check of the backing store (database connectivity, ...). A faulted or
    /// cancelled probe is reported unhealthy.
    /// </param>
    /// <param name="lastIndexedAt">
    /// Optional accessor for when the index was last populated, used only together with
    /// <paramref name="maxAge"/>. Must be provided alongside it.
    /// </param>
    /// <param name="maxAge">
    /// Optional freshness budget. When the index was last populated more than this ago, the check
    /// is degraded. Requires <paramref name="lastIndexedAt"/>.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="index"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="maxAge"/> is given without <paramref name="lastIndexedAt"/>, or the other way round.
    /// </exception>
    public LexiSharpIndexHealthCheck(
        ITextIndex index,
        int minimumDocumentCount = 1,
        Func<CancellationToken, Task>? probe = null,
        Func<DateTimeOffset?>? lastIndexedAt = null,
        TimeSpan? maxAge = null)
    {
        ArgumentNullException.ThrowIfNull(index);

        if (maxAge.HasValue != (lastIndexedAt is not null))
        {
            throw new ArgumentException(
                "lastIndexedAt and maxAge must be supplied together: an age budget is meaningless without knowing the age.");
        }

        _index = index;
        _minimumDocumentCount = minimumDocumentCount;
        _probe = probe;
        _lastIndexedAt = lastIndexedAt;
        _maxAge = maxAge;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var data = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["documents"] = _index.Count,
            ["vocabulary"] = _index.VocabularySize,
        };

        if (_index.Count < _minimumDocumentCount)
        {
            data["expectedDocuments"] = _minimumDocumentCount;

            return HealthCheckResult.Unhealthy(
                $"The search index holds {_index.Count} document(s), below the expected {_minimumDocumentCount}.",
                data: data);
        }

        if (_maxAge.HasValue && _lastIndexedAt is not null)
        {
            DateTimeOffset? lastIndexed = _lastIndexedAt();

            if (lastIndexed is null)
            {
                return HealthCheckResult.Degraded(
                    "The index has never been populated since startup.",
                    data: data);
            }

            TimeSpan age = DateTimeOffset.UtcNow - lastIndexed.Value;
            data["indexAge"] = age;

            if (age > _maxAge.Value)
            {
                return HealthCheckResult.Degraded(
                    $"The index was last populated {age:g} ago, beyond the {_maxAge.Value:g} budget.",
                    data: data);
            }
        }

        if (_probe is not null)
        {
            try
            {
                await _probe(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The caller is shutting the probe down, not the store: surface it as such rather
                // than as a connectivity failure.
                return HealthCheckResult.Degraded("The backing-store probe was cancelled.");
            }
            catch (Exception ex)
            {
                return HealthCheckResult.Unhealthy(
                    "The backing store is not reachable.", ex, data);
            }
        }

        return HealthCheckResult.Healthy(
            $"The search index holds {_index.Count} document(s) and {_index.VocabularySize} term(s).",
            data);
    }
}
