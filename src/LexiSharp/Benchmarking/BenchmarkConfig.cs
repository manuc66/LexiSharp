using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Benchmarking;

/// <summary>
/// A named recipe for building the search engine a benchmark run evaluates. The build closure
/// receives the shared index and the query set so a configuration can adapt to them (for
/// example a tuner that fits its parameters on the labeled queries).
/// </summary>
/// <remarks>
/// The static factories cover the stock comparisons of a retrieval benchmark; arbitrary
/// engines — including reranked pipelines over the same index — can be wrapped through the
/// public constructor.
/// </remarks>
public sealed record BenchmarkConfig
{
    private readonly Func<ITextIndex, ITokenizer, IReadOnlyList<BenchmarkQuery>, int, ITextSearchEngine> _build;

    /// <summary>Display name used in tables and JSON reports.</summary>
    public string Name { get; }

    /// <param name="name">Display name; must not be blank.</param>
    /// <param name="build">
    /// Builds the engine to evaluate. Arguments: the shared index, the shared tokenizer, the
    /// query set (useful for tuning), and the retrieval depth <c>topK</c> (useful to size an
    /// RRF candidate pool).
    /// </param>
    public BenchmarkConfig(string name, Func<ITextIndex, ITokenizer, IReadOnlyList<BenchmarkQuery>, int, ITextSearchEngine> build)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(build);

        Name = name;
        _build = build;
    }

    internal ITextSearchEngine Build(ITextIndex index, ITokenizer tokenizer, IReadOnlyList<BenchmarkQuery> queries, int topK)
        => _build(index, tokenizer, queries, topK);

    /// <summary>Stock <see cref="Bm25Scorer"/> ranking.</summary>
    public static BenchmarkConfig Bm25(double k1 = 1.5, double b = 0.75) =>
        new(name: "BM25", build: (index, tokenizer, _, _) =>
            new RankedTextSearchEngine(index, new Bm25Scorer(k1, b), tokenizer));

    /// <summary>
    /// BM25 with <c>(k1, b)</c> fitted on the labeled queries through
    /// <see cref="Bm25ParameterTuner"/>. Measures the headroom parameter tuning buys on the
    /// validation set itself (an optimistic upper bound compared to a held-out test set).
    /// </summary>
    public static BenchmarkConfig Bm25Tuned(int topK = 10, TuningMetric metric = TuningMetric.F1) =>
        new(name: "BM25 (tuned)", build: (index, tokenizer, queries, k) =>
        {
            var tuningQueries = queries
                .Select(query => new Bm25ValidationQuery(query.Text, query.RelevantDocumentIds))
                .ToList();
            var tuned = new Bm25ParameterTuner(index, tuningQueries, tokenizer)
                .Tune(topK: k, metric: metric);
            return new RankedTextSearchEngine(index, new Bm25Scorer(tuned.Parameters), tokenizer);
        });

    /// <summary>Stock <see cref="TfIdfScorer"/> ranking.</summary>
    public static BenchmarkConfig TfIdf() =>
        new(name: "TF-IDF", build: (index, tokenizer, _, _) =>
            new RankedTextSearchEngine(index, new TfIdfScorer(), tokenizer));

    /// <summary>Stock <see cref="QueryLikelihoodScorer"/> ranking.</summary>
    public static BenchmarkConfig QueryLikelihood(double lambda = 0.2) =>
        new(name: "QueryLikelihood", build: (index, tokenizer, _, _) =>
            new RankedTextSearchEngine(index, new QueryLikelihoodScorer(lambda), tokenizer));

    /// <summary>
    /// Reciprocal Rank Fusion over one <see cref="RankedTextSearchEngine"/> per scorer, using
    /// the in-memory index shared by every configuration of the run.
    /// </summary>
    public static BenchmarkConfig HybridRrf(params ITextScorer[] scorers)
    {
        ArgumentNullException.ThrowIfNull(scorers);

        if (scorers.Length == 0)
            throw new ArgumentException("At least one scorer is required.", nameof(scorers));

        string name = "RRF: " + string.Join(" + ", scorers.Select(scorer => scorer.Name));

        return new BenchmarkConfig(name, (index, tokenizer, _, k) =>
        {
            var engines = scorers
                .Select(scorer => (ITextSearchEngine)new RankedTextSearchEngine(index, scorer, tokenizer))
                .ToList();
            return new HybridTextSearchEngine(
                engines,
                new ReciprocalRankFusionMerger(),
                minCandidatesPerEngine: Math.Max(50, k),
                sourceNames: scorers.Select(scorer => scorer.Name).ToList());
        });
    }
}