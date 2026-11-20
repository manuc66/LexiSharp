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
/// <para>
/// nDCG uses <see cref="BenchmarkQuery.GradedRelevance"/>, with exponential gains; a binary query
/// is one whose gains are all <c>1</c>, which degenerates to the binary formula. The other five
/// metrics use the derived <see cref="BenchmarkQuery.RelevantDocumentIds"/>, so a graded run and a
/// binary run of the same corpus stay comparable on recall, MAP, MRR, precision and F1 — while
/// nDCG values from the two kinds of qrels are not comparable to each other.
/// </para>
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

        // One result past the page, so a tie that reaches the cut is detected rather than hidden.
        // The reported page stays at topK: metrics, the golden master and callers all see the same
        // result list as before.
        var searchOptions = new SearchOptions(topK + 1);

        double ndcg = 0, map = 0, mrr = 0, recall = 0, precision = 0, f1 = 0;
        long elapsedTicks = 0;
        int judged = 0;
        var perQuery = new List<BenchmarkQueryResult>();

        var stopwatch = new Stopwatch();

        foreach (var query in queries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (query.RelevantDocumentIds.Count == 0)
                continue;

            stopwatch.Restart();
            var retrieved = engine.Search(query.Text, searchOptions, cancellationToken).ToList();
            stopwatch.Stop();
            elapsedTicks += stopwatch.ElapsedTicks;

            // The page is what the metrics and the golden master see; `retrieved` may hold one extra
            // result, which is what makes the cut's tie structure observable.
            IReadOnlyList<SearchResult> page = retrieved.Count > topK ? retrieved.Take(topK).ToList() : retrieved;

            var retrievedIds = new List<string>(page.Count);
            var retrievedScores = new List<double>(page.Count);

            foreach (var result in page)
            {
                retrievedIds.Add(result.DocumentId);
                retrievedScores.Add(result.Score);
            }

            double queryNdcg = RetrievalMetrics.NdcgAtK(retrievedIds, query.GradedRelevance, topK);
            double queryMap = RetrievalMetrics.AveragePrecisionAtK(retrievedIds, query.RelevantDocumentIds, topK);
            double queryMrr = RetrievalMetrics.ReciprocalRankAtK(retrievedIds, query.RelevantDocumentIds, topK);
            double queryRecall = RetrievalMetrics.RecallAtK(retrievedIds, query.RelevantDocumentIds, topK);
            double queryPrecision = RetrievalMetrics.PrecisionAtK(retrievedIds, query.RelevantDocumentIds, topK);
            double queryF1 = RetrievalMetrics.F1AtK(retrievedIds, query.RelevantDocumentIds, topK);

            ndcg += queryNdcg;
            map += queryMap;
            mrr += queryMrr;
            recall += queryRecall;
            precision += queryPrecision;
            f1 += queryF1;
            judged++;

            var discrimination = QueryDiscrimination.Measure(retrieved, topK, complete: retrieved.Count <= topK);

            // A class that reaches past the cut is incomplete here, so the expectation cannot be
            // computed exactly; null says "not computable", which the report keeps distinct from zero.
            TieAwareMetric? tieAware = discrimination.CutStraddled
                ? null
                : RetrievalMetrics.TieAwareNdcgAtK(retrieved, query.GradedRelevance, topK);

            perQuery.Add(new BenchmarkQueryResult(
                query.Id,
                query.Text,
                new BenchmarkMetrics(queryNdcg, queryMap, queryMrr, queryRecall, queryPrecision, queryF1),
                retrievedIds,
                FirstRelevantRank(retrievedIds, query.RelevantDocumentIds),
                retrievedScores)
            {
                Discrimination = discrimination,
                TieAwareNdcg = tieAware,
            });
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
            judged)
        { PerQuery = perQuery, TieAware = TieAwareSummary.Of(perQuery) };
    }

    /// <summary>
    /// 1-based position of the best-ranked judged document in <paramref name="retrievedIds"/>, or
    /// <c>null</c> when none of them was retrieved. The judgments are graded, so "best ranked"
    /// means the one that appears first, not the one with the highest gain.
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