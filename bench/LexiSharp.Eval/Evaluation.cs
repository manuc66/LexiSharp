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
    public static (IReadOnlyList<ConfigResult> Results, string TunedDescription) Run(
        BeirCorpus corpus, int topK, int? limit, ITokenizer tokenizer, DenseVectors? dense = null,
        bool tuned = true, IReranker? reranker = null, int rerankCandidates = 100)
    {
        var queries = corpus.Queries
            .Where(query => corpus.TestRelevance.ContainsKey(query.Id))
            .OrderBy(query => query.Id, StringComparer.Ordinal)
            .Select(query => new EvaluatedQuery(query, corpus.TestRelevance[query.Id]))
            .Take(limit ?? int.MaxValue)
            .ToList();

        // Title and text go in as what they are — a named text field and the body — rather than being
        // concatenated. For BM25 this is bit-identical to the flattened form: the flat view is the
        // union of the fields, so the same tokens land at the same frequencies and lengths, and only
        // their order differs, which BM25 does not read. What it buys is a field a field-weighted
        // scorer can actually weigh.
        var documents = corpus.Documents
            .Select(document => new SearchDocument(
                document.Id,
                document.Text,
                TextFields: string.IsNullOrEmpty(document.Title)
                    ? null
                    : new Dictionary<string, string> { ["title"] = document.Title }))
            .ToList();

        // ArguAna carries no title, so a field-weighted configuration there would be the same three
        // rows over again with an inert weight. Say so instead of padding the table.
        bool anyTitle = corpus.Documents.Any(document => !string.IsNullOrEmpty(document.Title));

        var builders = new (string Name, Func<ITextSearchEngine> Factory)[]
        {
            ("BM25 (k1=1.5, b=0.75)", () => Ranked(documents, new Bm25Scorer(1.5, 0.75), tokenizer)),
            ("BM25 (k1=1.2, b=0.75)", () => Ranked(documents, new Bm25Scorer(1.2, 0.75), tokenizer)),
            ("TF-IDF", () => Ranked(documents, new TfIdfScorer(), tokenizer)),
            ("QueryLikelihood (lambda=0.2)", () => Ranked(documents, new QueryLikelihoodScorer(0.2), tokenizer)),
            ("Hybrid BM25+QL weighted", () => Hybrid(documents,
                new WeightedScoreResultMerger(1.0, 1.0), tokenizer)),
            ("Hybrid BM25+QL RRF", () => Hybrid(documents,
                new ReciprocalRankFusionMerger(), tokenizer)),
        };

        if (anyTitle)
        {
            // Three weights rather than one: the question is whether weighting a title helps at all
            // here, and a single point cannot show a direction. BM25F neutral is included because it
            // is not BM25 — on one field it is a different length term, so its own row is the
            // baseline the weighted rows have to beat.
            builders = builders
                .Concat(new (string, Func<ITextSearchEngine>)[]
                {
                    ("BM25F (unweighted)", () => Ranked(documents, TitleWeighted(1.0), tokenizer)),
                    ("BM25F (title 2.0)", () => Ranked(documents, TitleWeighted(2.0), tokenizer)),
                    ("BM25F (title 4.0)", () => Ranked(documents, TitleWeighted(4.0), tokenizer)),
                })
                .ToArray();
        }

        var buildersList = builders.ToList();

        if (dense is not null)
        {
            var denseEngine = new DenseTextSearchEngine(corpus, dense);
            var bm25 = Ranked(documents, new Bm25Scorer(1.5, 0.75), tokenizer);

            buildersList.Add(("Dense multilingual-e5-small", () => denseEngine));
            buildersList.Add(("Hybrid BM25+Dense weighted", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new WeightedScoreResultMerger(1.0, 1.0))));
            buildersList.Add(("Hybrid BM25+Dense RRF", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new ReciprocalRankFusionMerger())));
        }

        if (reranker is not null)
        {
            var bm25 = Ranked(documents, new Bm25Scorer(1.5, 0.75), tokenizer);
            var languageModel = Ranked(documents, new QueryLikelihoodScorer(0.2), tokenizer);

            buildersList.Add(($"BM25 (top{rerankCandidates})+CrossRerank", () =>
                RerankEngines.Reranked(bm25, rerankCandidates, reranker)));
            buildersList.Add(($"QL (top{rerankCandidates})+CrossRerank", () =>
                RerankEngines.Reranked(languageModel, rerankCandidates, reranker)));

            if (dense is not null)
            {
                var rrf = new HybridTextSearchEngine(
                    new ITextSearchEngine[] { bm25, new DenseTextSearchEngine(corpus, dense) },
                    new ReciprocalRankFusionMerger());

                buildersList.Add(($"Hybrid RRF (top{rerankCandidates})+CrossRerank", () =>
                    RerankEngines.Reranked(rrf, rerankCandidates, reranker)));
            }
        }

        var results = new List<ConfigResult>();

        foreach (var (name, factory) in buildersList)
            results.Add(RunConfig(name, factory(), queries, topK));

        string tunedDescription = tuned ? RunTuned(documents, corpus, queries, topK, tokenizer, results) : "skipped (--no-tuned)";

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
        ITokenizer tokenizer,
        List<ConfigResult> results)
    {
        var validation = queries
            .Select(evaluated => new Bm25ValidationQuery(
                evaluated.Query.Text,
                evaluated.Graded.Keys.ToArray()))
            .ToList();

        var index = new InMemoryTextIndex(tokenizer);
        index.Index(documents);

        var tuner = new Bm25ParameterTuner(index, validation, tokenizer);
        var tuned = tuner.Tune(topK: topK, metric: TuningMetric.Ndcg);

        var result = RunConfig(
            $"BM25 tuned (k1={tuned.Parameters.K1:0.##}, b={tuned.Parameters.B:0.##})",
            new RankedTextSearchEngine(index, new Bm25Scorer(tuned.Parameters), tokenizer),
            queries,
            topK);

        results.Add(result);

        return $"k1={tuned.Parameters.K1.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"b={tuned.Parameters.B.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"nDCG@10={result.NdcgAt10.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>BM25F weighting the corpus's <c>title</c> field, everything else neutral.</summary>
    private static Bm25FScorer TitleWeighted(double weight) =>
        new(fieldWeights: new Dictionary<string, double> { ["title"] = weight });

    private static ITextSearchEngine Ranked(IReadOnlyList<SearchDocument> documents, ITextScorer scorer, ITokenizer tokenizer)
    {
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(documents);

        return new RankedTextSearchEngine(index, scorer, tokenizer);
    }

    private static ITextSearchEngine Hybrid(IReadOnlyList<SearchDocument> documents, IResultMerger merger, ITokenizer tokenizer)
    {
        var index = new InMemoryTextIndex(tokenizer);
        index.Index(documents);

        var lexical = new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), tokenizer);
        var languageModel = new RankedTextSearchEngine(index, new QueryLikelihoodScorer(0.2), tokenizer);

        return new HybridTextSearchEngine(new ITextSearchEngine[] { lexical, languageModel }, merger);
    }

    private static string CombineTitleAndText(BeirDocument document) =>
        string.IsNullOrEmpty(document.Title) ? document.Text : document.Title + " " + document.Text;
}