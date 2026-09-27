using System.Globalization;

namespace LexiSharp.Core;

/// <summary>The pipeline stage a <see cref="TraceStep"/> was recorded at.</summary>
public enum TraceStage
{
    /// <summary>An engine or decorator read the document's score and left it unchanged.</summary>
    Score = 0,

    /// <summary>A result merger fused several engines' rankings for the document.</summary>
    Merge = 1,

    /// <summary>A reranker re-scored the document.</summary>
    Rerank = 2,

    /// <summary>A boost decorator scaled or offset the document's score.</summary>
    Boost = 3,

    /// <summary>A router selected a route for the query (per query, not per document).</summary>
    Route = 4,
}

/// <summary>
/// One recorded step of a document's journey through the search pipeline: the stage it was at,
/// the score going in, the score coming out, and a stage-specific detail string.
/// </summary>
/// <param name="Stage">Where in the pipeline the step was recorded.</param>
/// <param name="DocumentId">Id of the document the step applies to; empty for <see cref="TraceStage.Route"/>.</param>
/// <param name="Before">Score before the stage ran, or the router confidence for <see cref="TraceStage.Route"/>.</param>
/// <param name="After">Score after the stage ran; equal to <paramref name="Before"/> for a pass-through stage.</param>
/// <param name="Detail">Stage-specific breakdown (per-term contributions, source scores, route id, ...). Never parsed by LexiSharp.</param>
public readonly record struct TraceStep(
    TraceStage Stage,
    string DocumentId,
    double Before,
    double After,
    string? Detail);

/// <summary>
/// Collects a <see cref="TraceStep"/> per document per stage, so a ranking can be explained
/// end to end: which engine scored a document, what each merger contributed, what the reranker
/// changed, and what the boost did.
/// </summary>
/// <remarks>
/// <para>
/// <b>Opt-in and null by default.</b> Pass an instance through <see cref="SearchOptions.Trace"/>;
/// when that stays <c>null</c> no engine allocates a step, formats a detail string, or touches a
/// counter. Engines read the trace only on the paths that already have the data in hand, so
/// enabling it does not add work inside the scoring loop.
/// </para>
/// <para>
/// <b>Bounded.</b> A trace stops recording past <see cref="Capacity"/> steps and counts the
/// overflow in <see cref="Dropped"/>; <see cref="IsTruncated"/> then reports that the trace is
/// partial. The bound keeps a trace from growing with the corpus, which matters because a
/// pipeline stage is usually applied to a shortlist rather than to the whole match set.
/// </para>
/// <para>
/// A single instance is reusable across searches — <see cref="Steps"/> keeps accumulating, so a
/// long-lived instance will hit <see cref="Capacity"/> and report truncation. Create one per
/// request to trace one search.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> A trace is a mutable collector: concurrent <see cref="Record"/> calls
/// on one instance can lose or duplicate steps. Give each concurrent search its own trace, the
/// same way each gets its own <see cref="SearchOptions"/>.
/// </para>
/// </remarks>
public sealed class SearchTrace
{
    /// <summary>Default number of steps a trace records before it starts dropping.</summary>
    public const int DefaultCapacity = 256;

    private readonly List<TraceStep> _steps;

    /// <param name="capacity">
    /// Maximum number of steps to record. Must be positive; see <see cref="DefaultCapacity"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public SearchTrace(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "Trace capacity must be positive.");
        }

        Capacity = capacity;
        _steps = new List<TraceStep>(Math.Min(capacity, 64));
    }

    /// <summary>Maximum number of steps this trace records before dropping the rest.</summary>
    public int Capacity { get; }

    /// <summary>The steps recorded so far, in the order the pipeline produced them.</summary>
    public IReadOnlyList<TraceStep> Steps => _steps;

    /// <summary>Number of steps that did not fit within <see cref="Capacity"/>.</summary>
    public int Dropped { get; private set; }

    /// <summary>True when steps were dropped, i.e. <see cref="Steps"/> is not the whole story.</summary>
    public bool IsTruncated => Dropped > 0;

    /// <summary>
    /// How many steps each recorded stage holds, keyed by <see cref="TraceStage"/> and including
    /// only the stages actually recorded.
    /// </summary>
    /// <remarks>
    /// Read-only over <see cref="Steps"/>, so the counts are O(steps) and only reflect the
    /// per-document steps — a stage that ran but matched nothing is absent rather than counted as
    /// zero. Engines that know a stage's wall-clock cost report it through
    /// <see cref="IRetrievalMetrics"/> instead; this method counts steps, it does not time them.
    /// </remarks>
    public IReadOnlyDictionary<TraceStage, int> StageCounts()
    {
        var counts = new Dictionary<TraceStage, int>();

        foreach (TraceStep step in _steps)
        {
            counts.TryGetValue(step.Stage, out int seen);
            counts[step.Stage] = seen + 1;
        }

        return counts;
    }

    /// <summary>Records one step, unless the trace is already at <see cref="Capacity"/>.</summary>
    public void Record(in TraceStep step)
    {
        if (_steps.Count < Capacity)
        {
            _steps.Add(step);
        }
        else
        {
            Dropped++;
        }
    }

    /// <summary>
    /// Records the <see cref="TraceStage.Score"/> step of a page a scoring engine just ranked.
    /// Called with the final page rather than per candidate, so the cost is O(page), not O(corpus).
    /// </summary>
    /// <param name="scorerName">Name of the scorer that produced the scores, used as the step detail.</param>
    /// <param name="page">The ranked page, best first.</param>
    internal void RecordScoreStage(string scorerName, IReadOnlyList<SearchResult> page)
    {
        for (int i = 0; i < page.Count; i++)
        {
            SearchResult result = page[i];
            Record(new TraceStep(TraceStage.Score, result.DocumentId, result.Score, result.Score, scorerName));
        }
    }

    /// <summary>
    /// Renders a <see cref="ScoreBoost"/> as a step detail, invariantly so a trace is comparable
    /// across locales and safe to assert on.
    /// </summary>
    internal static string FormatBoost(ScoreBoost boost) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"x{boost.Multiply:0.####} {boost.Add:+0.####;-0.####;+0}");
}
