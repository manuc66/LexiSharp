using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
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
    /// BM25 over an index enriched with corpus-learned « semantic lexical » expansion
    /// (<see cref="PmiTermExpander"/> learned from the benchmark corpus itself): documents are
    /// additionally filed under weakly associated terms, so queries match conceptually related
    /// documents they never mention literally. Builds its own expanded index out of the shared
    /// corpus.
    /// </summary>
    public static BenchmarkConfig Bm25Semantic(PmiTermExpanderOptions? options = null) =>
        new(name: "BM25 + semantic", build: (index, tokenizer, _, _) =>
        {
            var expander = PmiTermExpander.LearnFrom(index.Documents, tokenizer, options);
            var expanded = new ExpansionTextIndex(expander, tokenizer);
            expanded.Index(index.Documents);
            return new RankedTextSearchEngine(expanded, new Bm25Scorer(), tokenizer);
        });

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

    /// <summary>
    /// <see cref="Bm25FScorer"/> over a field weighting, which is the configuration this scorer
    /// exists for. <paramref name="fieldWeights"/> names the fields to weight, e.g.
    /// <c>["title"] = 2.0</c>.
    /// </summary>
    /// <remarks>
    /// Only meaningful on a corpus whose documents declare those <paramref name="fieldWeights"/>
    /// keys as <see cref="SearchDocument.TextFields"/>; a weight on a field the corpus does not
    /// have is inert, and every unmentioned field keeps a neutral weight. On a single-field corpus
    /// this is a length-normalized BM25 variant, not BM25 — measure before drawing a conclusion.
    /// </remarks>
    public static BenchmarkConfig Bm25F(IReadOnlyDictionary<string, double>? fieldWeights = null) =>
        new(name: fieldWeights is null or { Count: 0 } ? "BM25F" : "BM25F (weighted)", build: (index, tokenizer, _, _) =>
            new RankedTextSearchEngine(index, new Bm25FScorer(fieldWeights: fieldWeights), tokenizer));

    /// <summary>
    /// BM25 followed by <see cref="ProximityReranker"/>: the same first stage, with candidates
    /// re-ordered by how tightly the query terms cluster in each document.
    /// </summary>
    /// <remarks>
    /// A first-stage/second-stage pair, so the difference from the stock BM25 configuration is
    /// attributable to proximity alone. <paramref name="strength"/> is <c>0</c> for a no-op and
    /// <c>1</c> for the full <c>1 - n/W</c> decay; the effect is corpus-dependent by nature, since
    /// documents that repeat their terms far apart are exactly the ones this demotes.
    /// </remarks>
    public static BenchmarkConfig Bm25Proximity(
        double strength = 1.0,
        ProximityMode mode = ProximityMode.Damp,
        int maxCandidates = 100) =>
        new(name: $"BM25 + proximity ({Describe(mode)}, s={strength})", build: (index, tokenizer, _, _) =>
            new RerankedTextSearchEngine(
                new RankedTextSearchEngine(index, new Bm25Scorer(), tokenizer),
                new ProximityReranker(index, tokenizer, strength, mode),
                maxCandidates));

    private static string Describe(ProximityMode mode) =>
        mode == ProximityMode.Boost ? "boost" : "damp";

    /// <summary>
    /// <see cref="Bm25FScorer"/> with <c>(k1, b)</c> and the weights of <paramref name="weightedFields"/>
    /// fitted on the labeled queries by <see cref="Bm25FParameterTuner"/>.
    /// </summary>
    /// <remarks>
    /// Measures the headroom tuning buys on the validation set itself, so it is an optimistic upper
    /// bound rather than a fair baseline — the same caveat that applies to
    /// <see cref="Bm25Tuned"/>, and for the same reason: the best of N configurations is scored on
    /// the N queries that chose it. Compare it against <see cref="Bm25Tuned"/>, never against the
    /// published numbers.
    /// </remarks>
    public static BenchmarkConfig Bm25FTuned(
        IReadOnlyCollection<string>? weightedFields = null,
        int topK = 10,
        TuningMetric metric = TuningMetric.F1) =>
        new(name: weightedFields is { Count: > 0 } ? "BM25F (tuned)" : "BM25F (tuned, no weights)", build: (index, tokenizer, queries, k) =>
        {
            var tuningQueries = queries
                .Select(query => new Bm25ValidationQuery(query.Text, query.RelevantDocumentIds))
                .ToList();

            var tuner = new Bm25FParameterTuner(index, tuningQueries, tokenizer);

            // The reference corpus has no text fields, so asking the tuner to weight a field it
            // does not have would throw. Search whatever the index actually declares, and say so in
            // the config name when there is nothing to weight.
            var fields = weightedFields is { Count: > 0 }
                ? weightedFields
                : index.Fields.Where(field => field != TextFields.Default).ToArray();

            var tuned = fields.Count == 0
                ? tuner.Tune(topK: k, metric: metric)
                : tuner.Tune(weightedFields: fields, topK: k, metric: metric);

            return new RankedTextSearchEngine(index, new Bm25FScorer(tuned.Parameters), tokenizer);
        });

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