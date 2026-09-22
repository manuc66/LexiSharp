using System.Diagnostics;
using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

internal sealed record ConfigResult(
    string Name,
    int Queries,
    double NdcgAt10,
    double MapAt10,
    double MrrAt10,
    double RecallAt10,
    TimeSpan Elapsed);

internal sealed record EvaluatedQuery(BeirQuery Query, IReadOnlyDictionary<string, double> Graded);

internal static class Evaluation
{
    private static readonly Tokenizer Tokenizer = Tokenizer.Default;

    public static (IReadOnlyList<ConfigResult> Results, string TunedDescription) Run(
        BeirCorpus corpus, int topK, int? limit, DenseVectors? dense = null, bool tuned = true)
    {
        var queries = corpus.Queries
            .Where(query => corpus.TestRelevance.ContainsKey(query.Id))
            .OrderBy(query => query.Id, StringComparer.Ordinal)
            .Select(query => new EvaluatedQuery(query, corpus.TestRelevance[query.Id]))
            .Take(limit ?? int.MaxValue)
            .ToList();

        var documents = corpus.Documents
            .Select(document => new SearchDocument(document.Id, CombineTitleAndText(document)))
            .ToList();

        var builders = new (string Name, Func<ITextSearchEngine> Factory)[]
        {
            ("BM25 (k1=1.5, b=0.75)", () => Ranked(documents, new Bm25Scorer(1.5, 0.75))),
            ("BM25 (k1=1.2, b=0.75)", () => Ranked(documents, new Bm25Scorer(1.2, 0.75))),
            ("TF-IDF", () => Ranked(documents, new TfIdfScorer())),
            ("QueryLikelihood (lambda=0.2)", () => Ranked(documents, new QueryLikelihoodScorer(0.2))),
            ("Hybrid BM25+QL weighted", () => Hybrid(documents,
                new WeightedScoreResultMerger(1.0, 1.0))),
            ("Hybrid BM25+QL RRF", () => Hybrid(documents,
                new ReciprocalRankFusionMerger())),
        };

        var buildersList = builders.ToList();

        if (dense is not null)
        {
            var denseEngine = new DenseTextSearchEngine(corpus, dense);
            var bm25 = Ranked(documents, new Bm25Scorer(1.5, 0.75));

            buildersList.Add(("Dense multilingual-e5-small", () => denseEngine));
            buildersList.Add(("Hybrid BM25+Dense weighted", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new WeightedScoreResultMerger(1.0, 1.0))));
            buildersList.Add(("Hybrid BM25+Dense RRF", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new ReciprocalRankFusionMerger())));
        }

        var results = new List<ConfigResult>();

        foreach (var (name, factory) in buildersList)
            results.Add(RunConfig(name, factory(), queries, topK));

        string tunedDescription = tuned ? RunTuned(documents, corpus, queries, topK, results) : "skipped (--no-tuned)";

        return (results, tunedDescription);
    }

    private static ConfigResult RunConfig(string name, ITextSearchEngine engine, IReadOnlyList<EvaluatedQuery> queries, int topK)
    {
        double ndcg = 0, map = 0, mrr = 0, recall = 0;
        var sw = Stopwatch.StartNew();

        foreach (var evaluated in queries)
        {
            string[] retrieved = engine.Search(evaluated.Query.Text, new SearchOptions(topK))
                .Select(result => result.DocumentId)
                .ToArray();

            var relevant = evaluated.Graded.Keys.ToArray();

            ndcg += RetrievalMetrics.NdcgAtK(retrieved, evaluated.Graded, topK);
            map += RetrievalMetrics.AveragePrecisionAtK(retrieved, relevant, topK);
            mrr += RetrievalMetrics.ReciprocalRankAtK(retrieved, relevant, topK);
            recall += RetrievalMetrics.RecallAtK(retrieved, relevant, topK);
        }

        sw.Stop();

        return new ConfigResult(
            name,
            queries.Count,
            ndcg / queries.Count,
            map / queries.Count,
            mrr / queries.Count,
            recall / queries.Count,
            sw.Elapsed);
    }

    private static string RunTuned(
        IReadOnlyList<SearchDocument> documents,
        BeirCorpus corpus,
        IReadOnlyList<EvaluatedQuery> queries,
        int topK,
        List<ConfigResult> results)
    {
        var validation = queries
            .Select(evaluated => new Bm25ValidationQuery(
                evaluated.Query.Text,
                evaluated.Graded.Keys.ToArray()))
            .ToList();

        var index = new InMemoryTextIndex(Tokenizer);
        index.Index(documents);

        var tuner = new Bm25ParameterTuner(index, validation, Tokenizer);
        var tuned = tuner.Tune(topK: topK, metric: TuningMetric.Ndcg);

        var result = RunConfig(
            $"BM25 tuned (k1={tuned.Parameters.K1:0.##}, b={tuned.Parameters.B:0.##})",
            new RankedTextSearchEngine(index, new Bm25Scorer(tuned.Parameters), Tokenizer),
            queries,
            topK);

        results.Add(result);

        return $"k1={tuned.Parameters.K1.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"b={tuned.Parameters.B.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"nDCG@10={result.NdcgAt10.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    private static ITextSearchEngine Ranked(IReadOnlyList<SearchDocument> documents, ITextScorer scorer)
    {
        var index = new InMemoryTextIndex(Tokenizer);
        index.Index(documents);

        return new RankedTextSearchEngine(index, scorer, Tokenizer);
    }

    private static ITextSearchEngine Hybrid(IReadOnlyList<SearchDocument> documents, IResultMerger merger)
    {
        var index = new InMemoryTextIndex(Tokenizer);
        index.Index(documents);

        var lexical = new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), Tokenizer);
        var languageModel = new RankedTextSearchEngine(index, new QueryLikelihoodScorer(0.2), Tokenizer);

        return new HybridTextSearchEngine(new ITextSearchEngine[] { lexical, languageModel }, merger);
    }

    private static string CombineTitleAndText(BeirDocument document) =>
        string.IsNullOrEmpty(document.Title) ? document.Text : document.Title + " " + document.Text;
}