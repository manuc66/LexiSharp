using System.Diagnostics;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarking;

/// <summary>
/// Runs a retrieval benchmark: builds one shared in-memory index over a corpus, evaluates every
/// requested <see cref="BenchmarkConfig"/> against the same query set, and reports the standard
/// top-<c>k</c> retrieval metrics plus the observed latency. The shared index makes the
/// configurations strictly comparable (same vocabulary, same term frequencies).
/// </summary>
/// <remarks>
/// Queries with an empty relevant set are not averaged (there is nothing to score), mirroring
/// <see cref="Bm25ParameterTuner"/>. The engine is warm when the stopwatch starts: only the
/// search itself is timed.
/// </remarks>
public static class CorpusBenchmark
{
    /// <summary>
    /// Evaluates every configuration against the corpus and the query set.
    /// </summary>
    /// <param name="documents">Corpus documents; indexed once and shared by all configurations.</param>
    /// <param name="queries">Labeled queries. At least one is required (all may have empty relevance).</param>
    /// <param name="configs">Configurations to compare. At least one is required.</param>
    /// <param name="options">Tuning knobs (<see cref="BenchmarkOptions"/> when null).</param>
    /// <param name="cancellationToken">Cancelled between configurations and queries.</param>
    /// <returns>One <see cref="BenchmarkConfigResult"/> per input <paramref name="configs"/>, in order.</returns>
    public static IReadOnlyList<BenchmarkConfigResult> Run(
        IReadOnlyCollection<SearchDocument> documents,
        IReadOnlyCollection<BenchmarkQuery> queries,
        IReadOnlyList<BenchmarkConfig> configs,
        BenchmarkOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(configs);

        if (queries.Count == 0)
            throw new ArgumentException("At least one query is required.", nameof(queries));

        if (configs.Count == 0)
            throw new ArgumentException("At least one configuration is required.", nameof(configs));

        var benchmarkOptions = options ?? new BenchmarkOptions();

        if (benchmarkOptions.TopK <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "TopK must be positive.");

        var queryList = queries.ToList();

        var tokenizer = benchmarkOptions.Tokenizer ?? Tokenizer.Default;
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(documents);

        var results = new List<BenchmarkConfigResult>(configs.Count);

        foreach (var config in configs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(RunConfig(config, index, tokenizer, queryList, benchmarkOptions.TopK, cancellationToken));
        }

        return results;
    }

    private static BenchmarkConfigResult RunConfig(
        BenchmarkConfig config,
        ITextIndex index,
        ITokenizer tokenizer,
        IReadOnlyList<BenchmarkQuery> queries,
        int topK,
        CancellationToken cancellationToken)
    {
        var engine = config.Build(index, tokenizer, queries, topK);
        var searchOptions = new SearchOptions(topK);

        double ndcg = 0, map = 0, mrr = 0, recall = 0, precision = 0, f1 = 0;
        long elapsedTicks = 0;
        int judged = 0;

        var stopwatch = new Stopwatch();

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (query.RelevantDocumentIds.Count == 0)
                continue;

            stopwatch.Restart();
            var retrievedIds = engine
                .Search(query.Text, searchOptions)
                .Select(result => result.DocumentId)
                .ToList();
            stopwatch.Stop();
            elapsedTicks += stopwatch.ElapsedTicks;

            ndcg += RetrievalMetrics.NdcgAtK(retrievedIds, query.RelevantDocumentIds, topK);
            map += RetrievalMetrics.AveragePrecisionAtK(retrievedIds, query.RelevantDocumentIds, topK);
            mrr += RetrievalMetrics.ReciprocalRankAtK(retrievedIds, query.RelevantDocumentIds, topK);
            recall += RetrievalMetrics.RecallAtK(retrievedIds, query.RelevantDocumentIds, topK);
            precision += RetrievalMetrics.PrecisionAtK(retrievedIds, query.RelevantDocumentIds, topK);
            f1 += RetrievalMetrics.F1AtK(retrievedIds, query.RelevantDocumentIds, topK);
            judged++;
        }

        double totalMilliseconds = elapsedTicks / (double)TimeSpan.TicksPerMillisecond;

        return new BenchmarkConfigResult(
            config.Name,
            new BenchmarkMetrics(
                NdcgAtK: judged == 0 ? 0 : ndcg / judged,
                MapAtK: judged == 0 ? 0 : map / judged,
                MrrAtK: judged == 0 ? 0 : mrr / judged,
                RecallAtK: judged == 0 ? 0 : recall / judged,
                PrecisionAtK: judged == 0 ? 0 : precision / judged,
                F1AtK: judged == 0 ? 0 : f1 / judged),
            totalMilliseconds,
            judged == 0 ? 0 : totalMilliseconds / judged,
            judged);
    }
}