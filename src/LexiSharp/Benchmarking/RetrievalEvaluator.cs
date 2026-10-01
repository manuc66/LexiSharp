using System.Diagnostics;
using LexiSharp.Core;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarking;

/// <summary>
/// The outcome of evaluating a live engine against a <see cref="RetrievalEvaluator"/>'s panel:
/// the retrieval metrics averaged over the judged queries, plus the wall-clock time the
/// evaluation took to run them.
/// </summary>
/// <param name="JudgedQueries">
/// Number of panel queries with a non-empty relevant set that were averaged. A query without
/// judgments is loaded but excluded from the averages, the same convention
/// <see cref="CorpusBenchmark"/> uses.
/// </param>
/// <param name="Metrics">Mean metrics over the <paramref name="JudgedQueries"/> queries.</param>
/// <param name="TotalMilliseconds">Wall-clock time spent searching the judged queries.</param>
/// <param name="MillisecondsPerQuery">Mean latency per judged query.</param>
/// <param name="ByQuery">One entry per judged query, in panel order.</param>
/// <param name="IsGraded">
/// True when any judged query carries graded relevance, i.e. the mean nDCG is graded and must
/// not be compared with a binary one. Binary judgments make nDCG convention-independent;
/// graded ones do not.
/// </param>
public sealed record PanelEvaluationResult(
    int JudgedQueries,
    BenchmarkMetrics Metrics,
    double TotalMilliseconds,
    double MillisecondsPerQuery,
    IReadOnlyList<BenchmarkQueryResult> ByQuery,
    bool IsGraded)
{
}

/// <summary>
/// Evaluates a <b>live</b> <see cref="ITextSearchEngine"/> against a fixed panel of labeled
/// queries, on demand — the runtime complement to <see cref="CorpusBenchmark"/>, which builds a
/// fresh index from a document snapshot. Run it periodically against the engine an application
/// actually serves and compare successive results: a panel metric moving is the relevance half
/// of data-drift observability.
/// </summary>
/// <remarks>
/// Every query is searched with the caller's <see cref="SearchOptions"/> except
/// <c>Limit</c>, which is pinned to the metric depth <see cref="TopK"/> — the retrieval depth
/// and the evaluation depth stay one number, as in <see cref="CorpusBenchmark"/>. Each query's
/// metrics come from <see cref="RetrievalMetrics"/>; a query with an empty relevant set is
/// loaded but excluded from the averages; a panel with no judged query reports zeros, not a
/// measurement.
/// <para>
/// The panel is the app's own: relevance comes from the judgments the app pinned. An LLM judge
/// is not shipped — a judge-backed panel is the caller composing <see cref="BenchmarkQuery"/>
/// from <see cref="ICrossEncoderScorer"/> output.
/// </para>
/// </remarks>
public sealed class RetrievalEvaluator
{
    private readonly IReadOnlyList<BenchmarkQuery> _panel;
    private readonly NdcgGain _ndcgGain;

    /// <summary>The evaluation depth: retrieve and measure at this rank.</summary>
    public int TopK { get; }

    /// <summary>One entry per panel query, in panel order.</summary>
    public IReadOnlyList<BenchmarkQuery> Panel => _panel;

    /// <param name="panel">The labeled queries the engine is scored against.</param>
    /// <param name="topK">Evaluation depth, applied to both retrieval and metrics. Must be positive.</param>
    /// <param name="ndcgGain">
    /// nDCG gain convention for graded judgments. Defaults to the exponential convention
    /// (<c>2^rel − 1</c>), the library's own default for graded nDCG. Pass
    /// <see cref="NdcgGain.Linear"/> to match the convention the published baselines use.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="topK"/> is not positive.</exception>
    public RetrievalEvaluator(
        IEnumerable<BenchmarkQuery> panel,
        int topK = 10,
        NdcgGain ndcgGain = NdcgGain.Exponential)
    {
        ArgumentNullException.ThrowIfNull(panel);

        if (topK <= 0)
            throw new ArgumentOutOfRangeException(nameof(topK), topK, "topK must be positive.");

        _panel = panel.ToList();
        _ndcgGain = ndcgGain;
        TopK = topK;
    }

    /// <summary>
    /// Runs the panel against <paramref name="engine"/> and reports the metrics at
    /// <see cref="TopK"/>.
    /// </summary>
    /// <param name="engine">The engine to evaluate — the live one, with whatever it currently holds.</param>
    /// <param name="options">
    /// Optional search options inherited by every query (filters, exclusions, ...);
    /// <see cref="SearchOptions.Default"/> when null. <c>Limit</c> is overridden with
    /// <see cref="TopK"/>.
    /// </param>
    /// <param name="cancellationToken">Cancelled between queries.</param>
    /// <exception cref="ArgumentException"><paramref name="engine"/> is null.</exception>
    public PanelEvaluationResult Evaluate(
        ITextSearchEngine engine,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(engine);

        var searchOptions = (options ?? SearchOptions.Default) with { Limit = TopK };

        double ndcg = 0, map = 0, mrr = 0, recall = 0, precision = 0, f1 = 0;
        long elapsedTicks = 0;
        int judged = 0;
        bool graded = false;
        var byQuery = new List<BenchmarkQueryResult>(_panel.Count);
        var stopwatch = new Stopwatch();

        foreach (var query in _panel)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A query with no judgments has nothing to score against; it is loaded but excluded
            // from the averages, the CorpusBenchmark convention.
            if (query.RelevantDocumentIds.Count == 0)
                continue;

            stopwatch.Restart();
            var retrieved = engine.Search(query.Text, searchOptions);
            stopwatch.Stop();
            elapsedTicks += stopwatch.ElapsedTicks;

            var retrievedIds = new List<string>(retrieved.Count);
            var retrievedScores = new List<double>(retrieved.Count);

            foreach (var result in retrieved)
            {
                retrievedIds.Add(result.DocumentId);
                retrievedScores.Add(result.Score);
            }

            double queryNdcg = _ndcgGain == NdcgGain.Linear
                ? RetrievalMetrics.NdcgAtK(retrievedIds, query.GradedRelevance, TopK, NdcgGain.Linear)
                : RetrievalMetrics.NdcgAtK(retrievedIds, query.GradedRelevance, TopK);
            double queryMap = RetrievalMetrics.AveragePrecisionAtK(retrievedIds, query.RelevantDocumentIds, TopK);
            double queryMrr = RetrievalMetrics.ReciprocalRankAtK(retrievedIds, query.RelevantDocumentIds, TopK);
            double queryRecall = RetrievalMetrics.RecallAtK(retrievedIds, query.RelevantDocumentIds, TopK);
            double queryPrecision = RetrievalMetrics.PrecisionAtK(retrievedIds, query.RelevantDocumentIds, TopK);
            double queryF1 = RetrievalMetrics.F1AtK(retrievedIds, query.RelevantDocumentIds, TopK);

            ndcg += queryNdcg;
            map += queryMap;
            mrr += queryMrr;
            recall += queryRecall;
            precision += queryPrecision;
            f1 += queryF1;
            judged++;
            graded |= query.IsGraded;

            byQuery.Add(new BenchmarkQueryResult(
                query.Id,
                query.Text,
                new BenchmarkMetrics(queryNdcg, queryMap, queryMrr, queryRecall, queryPrecision, queryF1),
                retrievedIds,
                FirstRelevantRank(retrievedIds, query.RelevantDocumentIds),
                retrievedScores));
        }

        double totalMilliseconds = elapsedTicks / (double)TimeSpan.TicksPerMillisecond;

        return new PanelEvaluationResult(
            judged,
            new BenchmarkMetrics(
                NdcgAtK: judged == 0 ? 0 : ndcg / judged,
                MapAtK: judged == 0 ? 0 : map / judged,
                MrrAtK: judged == 0 ? 0 : mrr / judged,
                RecallAtK: judged == 0 ? 0 : recall / judged,
                PrecisionAtK: judged == 0 ? 0 : precision / judged,
                F1AtK: judged == 0 ? 0 : f1 / judged),
            totalMilliseconds,
            judged == 0 ? 0 : totalMilliseconds / judged,
            byQuery,
            graded);
    }

    /// <summary>
    /// 1-based position of the best-ranked judged document in <paramref name="retrievedIds"/>, or
    /// <c>null</c> when none of them was retrieved. The same shape
    /// <see cref="CorpusBenchmark"/> reports.
    /// </summary>
    private static int? FirstRelevantRank(
        IReadOnlyList<string> retrievedIds,
        IReadOnlyCollection<string> relevantIds)
    {
        var relevant = new HashSet<string>(relevantIds, StringComparer.Ordinal);

        for (int i = 0; i < retrievedIds.Count; i++)
        {
            if (relevant.Contains(retrievedIds[i]))
                return i + 1;
        }

        return null;
    }
}