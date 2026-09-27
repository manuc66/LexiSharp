using LexiSharp.Core;
using Microsoft.Extensions.Logging;

namespace LexiSharp.AspNetCore;

/// <summary>
/// Bridges a <see cref="RetrievalTelemetry"/> onto <see cref="ILogger"/>, so an application that
/// already has a logging pipeline gets retrieval events in it without the LexiSharp core taking a
/// dependency on <c>Microsoft.Extensions.Logging</c>.
/// </summary>
/// <remarks>
/// Events keep their own <see cref="RetrievalLogEvent.Message"/> as a self-contained human-readable
/// sentence, and the structured fields (<c>Engine</c>, <c>Event</c>, <c>ElapsedMs</c>, <c>Count</c>)
/// travel alongside it, so a log aggregator can build dashboards without parsing the sentence.
/// </remarks>
/// <example>
/// <code>
/// var telemetry = RetrievalLogger.Telemetry(
///     loggerFactory.CreateLogger("search"), RetrievalLogLevel.Debug);
///
/// var index = new LexiSharpIndex&lt;MyDocument&gt;(o =&gt;
/// {
///     o.Id = d =&gt; d.Id;
///     o.Text = d =&gt; d.Body;
///     o.Telemetry = telemetry;
/// });
/// </code>
/// </example>
public static class RetrievalLogger
{
    /// <summary>
    /// Default level below which events are dropped. Stage events are emitted at
    /// <see cref="RetrievalLogLevel.Debug"/>, so the default keeps per-stage detail out of the log
    /// while one line per completed search still lands.
    /// </summary>
    public const RetrievalLogLevel DefaultMinimumLevel = RetrievalLogLevel.Information;

    /// <summary>
    /// Wraps an <see cref="ILogger"/> as a <see cref="RetrievalLog"/> sink.
    /// </summary>
    /// <param name="logger">Destination for the events.</param>
    /// <param name="minimumLevel">
    /// Events below this level are dropped before anything is written. Defaults to
    /// <see cref="DefaultMinimumLevel"/>.
    /// </param>
    public static RetrievalLog For(ILogger logger, RetrievalLogLevel minimumLevel = DefaultMinimumLevel)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return entry => Write(logger, minimumLevel, entry);
    }

    /// <summary>
    /// Wraps an <see cref="ILoggerFactory"/> as a <see cref="RetrievalLog"/> sink.
    /// </summary>
    /// <param name="loggerFactory">Factory creating the category logger.</param>
    /// <param name="categoryName">Logger category; defaults to <c>LexiSharp</c>.</param>
    /// <param name="minimumLevel">Events below this level are dropped.</param>
    public static RetrievalLog For(
        ILoggerFactory loggerFactory,
        string categoryName = "LexiSharp",
        RetrievalLogLevel minimumLevel = DefaultMinimumLevel)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        return For(loggerFactory.CreateLogger(categoryName), minimumLevel);
    }

    /// <summary>
    /// A ready-made <see cref="RetrievalTelemetry"/> logging to <paramref name="logger"/>, for the
    /// common case where logs are wanted without metrics.
    /// </summary>
    /// <param name="logger">Destination for the events.</param>
    /// <param name="minimumLevel">Events below this level are dropped.</param>
    public static RetrievalTelemetry Telemetry(
        ILogger logger,
        RetrievalLogLevel minimumLevel = DefaultMinimumLevel) =>
        RetrievalTelemetry.Logging(For(logger, minimumLevel));

    /// <summary>
    /// A ready-made <see cref="RetrievalTelemetry"/> logging through <paramref name="loggerFactory"/>.
    /// </summary>
    /// <param name="loggerFactory">Factory creating the category logger.</param>
    /// <param name="categoryName">Logger category; defaults to <c>LexiSharp</c>.</param>
    /// <param name="minimumLevel">Events below this level are dropped.</param>
    public static RetrievalTelemetry Telemetry(
        ILoggerFactory loggerFactory,
        string categoryName = "LexiSharp",
        RetrievalLogLevel minimumLevel = DefaultMinimumLevel) =>
        RetrievalTelemetry.Logging(For(loggerFactory, categoryName, minimumLevel));

    private static void Write(ILogger logger, RetrievalLogLevel minimumLevel, RetrievalLogEvent entry)
    {
        if (entry.Level < minimumLevel)
            return;

        // Belt and braces: the sink's own threshold is separate from the host's configuration, and
        // a disabled logger should cost nothing beyond this check.
        LogLevel level = Map(entry.Level);
        if (!logger.IsEnabled(level))
            return;

        logger.Log(
            level,
            "[{Engine}] {Event}: {Message} elapsedMs={ElapsedMs:0.###} count={Count}",
            entry.Engine,
            entry.Event,
            entry.Message,
            entry.ElapsedMs,
            entry.Count);
    }

    private static LogLevel Map(RetrievalLogLevel level) => level switch
    {
        RetrievalLogLevel.Debug => LogLevel.Debug,
        RetrievalLogLevel.Warning => LogLevel.Warning,
        _ => LogLevel.Information,
    };
}
