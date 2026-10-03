namespace LexiSharp.Core;

/// <summary>
/// One pipeline stage's share of what a single query cost: what it processed, and how long it took.
/// </summary>
/// <param name="Stage">
/// Stage label, in the same low-cardinality vocabulary <see cref="IRetrievalMetrics.RecordStage"/>
/// takes — the built-in engines use <c>score</c>, <c>retrieve</c>, <c>rerank:&lt;name&gt;</c>,
/// <c>merge</c>, <c>boost</c>. Treat it as opaque.
/// </param>
/// <param name="ItemCount">
/// Items the stage processed: documents scored, candidates handed to a reranker, source rankings
/// merged. For a scoring stage this is every document whose relevance score was <i>computed</i>,
/// whether or not it matched and whether or not it reached the page — the work, not the hits. It
/// is therefore not the figure <see cref="IRetrievalMetrics.RecordSearch"/> reports as
/// <c>candidateCount</c>, which counts the matched set.
/// </param>
/// <param name="Windows">
/// Windows the stage visited, for a stage whose work is expressed in windows; <c>0</c> for a stage
/// with no window notion. See the remarks on <see cref="SearchCosts"/> for why no built-in engine
/// fills this column, and who does.
/// </param>
/// <param name="ElapsedMs">Wall-clock duration of the stage, in milliseconds.</param>
public readonly record struct SearchCostStage(string Stage, long ItemCount, long Windows, double ElapsedMs);

/// <summary>
/// The per-query cost sheet a caller opts into: what the query was made of, and what each pipeline
/// stage it went through processed and cost.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is not.</b> <see cref="SearchTrace"/> explains one query — per document, per
/// stage, why a score is what it is. This counts one query: the input it was given and the work
/// each stage did. And <see cref="IRetrievalMetrics"/> is neither: that contract is aggregate and
/// thread-safe, an engine pushes into it and something downstream decides what to do with the
/// series. A <see cref="SearchCosts"/> instance is the private sheet of one request, handed back
/// to the caller in the <see cref="SearchOptions"/> it passed in — which is what lets a caller
/// hold two configurations side by side on the same queries, per query, instead of reading a mean
/// over runs.
/// </para>
/// <para>
/// <b>Windows are the column no built-in engine fills, and that is a contract rather than an
/// omission.</b> A scorer is a pure function of (document, query, index):
/// <see cref="ITextScorer.Score"/> is handed no request, so it cannot reach this sheet, and
/// neither can an <see cref="IReranker"/> from <see cref="IReranker.Rerank"/>. The stages that
/// know how many windows they visited are engines, decorators and caller-side stages, which do
/// receive the options and therefore can record it. A stage with no window notion records
/// <c>0</c>. The consequence is worth stating plainly: <em>no library code reports windows</em>,
/// and a report that shows a non-zero <see cref="SearchCostStage.Windows"/> is reporting work an
/// application measured and handed in.
/// </para>
/// <para>
/// <b>Opt-in and null by default.</b> An engine reads <see cref="SearchOptions.Costs"/> and does
/// nothing when it is <c>null</c>: no timestamp is taken, no row is built, nothing is allocated,
/// which is the same bargain <see cref="SearchTrace"/> makes. Attaching a sheet therefore does not
/// change what a search returns — it only observes it.
/// </para>
/// <para>
/// <b>Bounded.</b> The stage rows stop at <see cref="Capacity"/> and the overflow is counted in
/// <see cref="Dropped"/>, which <see cref="IsTruncated"/> then reports. A stage row is
/// request-sized, not corpus-sized, so the bound is a guard against a pipeline that invents
/// stages rather than a memory ceiling.
/// </para>
/// <para>
/// <b>Not thread-safe.</b> A sheet is a mutable collector: concurrent searches writing to one
/// instance can lose or duplicate rows, and the token counter can be lost. Give each concurrent
/// search its own, the same way each gets its own <see cref="SearchOptions"/>.
/// </para>
/// <para>
/// <b>Accumulates.</b> A single instance keeps adding: reuse across searches fills it to
/// <see cref="Capacity"/> and it then reports truncation, which is how <see cref="SearchTrace"/>
/// behaves. Create one per request to cost one request.
/// </para>
/// </remarks>
public sealed class SearchCosts
{
    /// <summary>Default number of stage rows a sheet records before it starts dropping them.</summary>
    public const int DefaultCapacity = 64;

    private readonly List<SearchCostStage> _stages;

    /// <summary>Creates an empty sheet that records at most <paramref name="capacity"/> stages.</summary>
    /// <param name="capacity">Maximum number of stage rows to keep.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public SearchCosts(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be positive.");

        Capacity = capacity;
        _stages = new List<SearchCostStage>(Math.Min(capacity, 8));
    }

    /// <summary>Maximum number of stage rows this sheet records before it starts dropping them.</summary>
    public int Capacity { get; }

    /// <summary>The rows recorded so far, in the order the pipeline produced them.</summary>
    public IReadOnlyList<SearchCostStage> Stages => _stages;

    /// <summary>Number of rows that did not fit within <see cref="Capacity"/>.</summary>
    public int Dropped { get; private set; }

    /// <summary>True when rows were dropped, i.e. <see cref="Stages"/> is not the whole story.</summary>
    public bool IsTruncated => Dropped > 0;

    /// <summary>
    /// The tokens the request's query was parsed into, summed by <see cref="AddTokens"/>.
    /// </summary>
    /// <remarks>
    /// The count the engine records is the parser's output, before the query terms are
    /// deduplicated: it is the length of the question as written, which is the figure a cost
    /// report wants. The deduplicated list is what the scorer sees, and how many distinct terms
    /// that was is a property of the query rather than of the cost.
    /// </remarks>
    public long Tokens { get; private set; }

    /// <summary>Adds to the request's token count.</summary>
    /// <param name="count">Tokens to add; must not be negative.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is negative.</exception>
    public void AddTokens(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        Tokens += count;
    }

    /// <summary>Records one stage, or counts it as dropped once <see cref="Capacity"/> is reached.</summary>
    /// <param name="stage">The stage's row.</param>
    public void Record(in SearchCostStage stage)
    {
        if (_stages.Count < Capacity)
            _stages.Add(stage);
        else
            Dropped++;
    }
}