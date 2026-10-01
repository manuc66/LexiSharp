using System.Diagnostics;
using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>
/// The rank the metrics are measured at, independent of how deep a run retrieves.
/// </summary>
/// <remarks>
/// Ten is the depth the published figures are reported at, so it is the depth this harness reports at.
/// A run that retrieves more — a bit-for-bit reproduction run retrieves one extra document per query —
/// is still measured at ten, or it would be reporting a different metric under the same name.
/// </remarks>
internal static class MetricDepth
{
    public const int Cutoff = 10;
}

/// <param name="Outcomes">
/// Per-query detail, or <c>null</c>. Non-null only when the run was asked to analyze: capturing a
/// ranking per query per configuration is real memory — ArguAna at depth 100 over twenty
/// configurations is millions of ids — and a default run that allocated it would pay for an analysis
/// nobody reads.
/// </param>
internal sealed record ConfigResult(
    string Name,
    int Queries,
    double NdcgAt10,
    double MapAt10,
    double MrrAt10,
    double RecallAt10,
    TimeSpan Elapsed,
    IReadOnlyList<QueryOutcome>? Outcomes = null);

/// <param name="Graded">The query's relevance judgments, document id to qrel score.</param>
/// <param name="Excluded">
/// Ids to leave out of this query's ranking, or <c>null</c>. Non-null only under
/// <c>--exclude-query-doc</c>, where the query id is itself a document id — the condition
/// the published ArguAna figure was produced under addresses.
/// </param>
internal sealed record EvaluatedQuery(
    BeirQuery Query,
    IReadOnlyDictionary<string, double> Graded,
    IReadOnlySet<string>? Excluded = null);

internal static class Evaluation
{
    /// <summary>
    /// The corpus as indexable documents. Title and text go in as what they are — a named text
    /// field and the body — rather than being concatenated. For BM25 this is bit-identical to the
    /// flattened form: the flat view is the union of the fields, so the same tokens land at the same
    /// frequencies and lengths, and only their order differs, which BM25 does not read. What it buys
    /// is a field a field-weighted scorer can actually weigh.
    /// </summary>
    /// <remarks>
    /// Shared with the index fingerprint, so the counts a run reports are the counts of the index it
    /// actually scored. Two copies of this would be free to drift, and a fingerprint that no longer
    /// describes the scored index is worse than none: it would keep passing.
    /// </remarks>
    public static IReadOnlyList<SearchDocument> BuildDocuments(BeirCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);

        return corpus.Documents
            .Select(document => new SearchDocument(
                document.Id,
                document.Text,
                TextFields: string.IsNullOrEmpty(document.Title)
                    ? null
                    : new Dictionary<string, string> { ["title"] = document.Title }))
            .ToList();
    }

    /// <param name="index">
    /// The index every lexical config scores against. Built once by the caller and shared: the
    /// configs differ in their scorer, not in what they read, so an index per config was the same
    /// corpus tokenized once per row of the table. <c>Search</c> only reads it, which is also what
    /// lets the query loop below run concurrently over the one copy.
    /// </param>
    /// <param name="jobs">
    /// Upper bound on queries in flight. One leaves the loop serial; the default leaves a core free
    /// rather than taking every core, because a sweep that saturates the machine is a sweep nobody
    /// can run anything else on.
    /// </param>
    public static (IReadOnlyList<ConfigResult> Results, string TunedDescription) Run(
        BeirCorpus corpus, InMemoryTextIndex index, int topK, int? limit, ITokenizer tokenizer,
        bool excludeQueryDocument = false,
        NdcgGain ndcgGain = NdcgGain.Exponential,
        Bm25Parameters? referenceBm25 = null,
        bool queryFrequency = false,
        bool scoreRounding = false,
        bool saturationConstant = true,
        bool singlePrecision = false,
        DenseVectors? dense = null,
        bool tuned = true, IReranker? reranker = null, int rerankCandidates = 100, int jobs = 1,
        bool parseQuerySyntax = false, bool captureOutcomes = false,
        IReadOnlyList<(string Name, ITermExpander Expander)>? queryExpansions = null)
    {
        // Excluding a query's own document id is a no-op unless the corpus numbers its queries as
        // documents, so the check needs no corpus-specific configuration. One membership test per
        // query, not one scan of the corpus per query.
        List<EvaluatedQuery> queries = BuildQueries(corpus, limit, excludeQueryDocument);

        // Documents are still built here: the field-bearing view is what anyTitle and the
        // field-weighted configs need, and it is the same set the caller's index was built from.
        // Building it costs nothing against the corpus; tokenizing it again per config did.
        var documents = BuildDocuments(corpus);

        // Null means the library default (distinct), so the shared value below is what every
        // scorer in this run was actually built with.
        var queryTerms = queryFrequency ? QueryTermWeighting.QueryFrequency : QueryTermWeighting.Distinct;

        // ArguAna carries no title, so a field-weighted configuration there would be the same three
        // rows over again with an inert weight. Say so instead of padding the table.
        bool anyTitle = corpus.Documents.Any(document => !string.IsNullOrEmpty(document.Title));

        var builders = new (string Name, Func<ITextSearchEngine> Factory)[]
        {
            ("BM25 (k1=1.5, b=0.75)", () => Ranked(index, new Bm25Scorer(1.5, 0.75, queryTerms), tokenizer)),
            ("BM25 (k1=1.2, b=0.75)", () => Ranked(index, new Bm25Scorer(1.2, 0.75, queryTerms), tokenizer)),
        };

        // The published BM25 baselines were produced at the retrieval stack's own defaults,
        // k1=0.9 and b=0.4, which is not one of the two rows above. A run has to be able to measure
        // that operating point, or a comparison against a published number is a comparison of two
        // different configurations. Opt-in, so the default table is unchanged.
        if (referenceBm25 is { } reference)
        {
            builders = builders
                .Concat(new (string, Func<ITextSearchEngine>)[]
                {
                    // Invariant formatting: a comma here would read as two parameters on a
                    // machine whose culture uses it, in a table whose whole job is naming them.
                    ($"BM25 (k1={reference.K1.ToString("0.##", CultureInfo.InvariantCulture)}, "
                      + $"b={reference.B.ToString("0.##", CultureInfo.InvariantCulture)})",
                        () => Ranked(
                            index,
                            new Bm25Scorer(
                                reference.K1, reference.B, queryTerms, saturationConstant,
                                singlePrecision ? Bm25Arithmetic.SinglePrecision : Bm25Arithmetic.Double),
                            tokenizer)),
                })
                .ToArray();
        }

        builders = builders
            .Concat(new (string, Func<ITextSearchEngine>)[]
            {
                ("TF-IDF", () => Ranked(index, new TfIdfScorer(), tokenizer)),
                ("QueryLikelihood (lambda=0.2)", () => Ranked(index, new QueryLikelihoodScorer(0.2), tokenizer)),
                ("Hybrid BM25+QL weighted", () => Hybrid(index,
                    new WeightedScoreResultMerger(1.0, 1.0), tokenizer)),
                ("Hybrid BM25+QL RRF", () => Hybrid(index,
                    new ReciprocalRankFusionMerger(), tokenizer)),
            })
            .ToArray();

        if (anyTitle)
        {
            // Three weights rather than one: the question is whether weighting a title helps at all
            // here, and a single point cannot show a direction. BM25F neutral is included because it
            // is not BM25 — on one field it is a different length term, so its own row is the
            // baseline the weighted rows have to beat.
            builders = builders
                .Concat(new (string, Func<ITextSearchEngine>)[]
                {
                    ("BM25F (unweighted)", () => Ranked(index, TitleWeighted(1.0), tokenizer)),
                    ("BM25F (title 2.0)", () => Ranked(index, TitleWeighted(2.0), tokenizer)),
                    ("BM25F (title 4.0)", () => Ranked(index, TitleWeighted(4.0), tokenizer)),
                })
                .ToArray();
        }

        // Single-field BM25 variants, measured against the tuned BM25 row rather than the default
        // one: a default-versus-default comparison mostly measures which default fits the corpus.
        // These two rows fix delta at the papers' starting value, which is why RunTunedVariants adds
        // a tuned row for each of them — otherwise the table compares a fitted BM25 against two
        // unfitted variants and calls it a verdict on the formula.
        builders = builders
            .Concat(new (string, Func<ITextSearchEngine>)[]
            {
                ("BM25+ (delta=1.0)", () => Ranked(index, new Bm25PlusScorer(1.5, 0.75, 1.0, queryTerms), tokenizer)),
                ("BM25L (delta=0.5)", () => Ranked(index, new Bm25LScorer(1.5, 0.75, 0.5, queryTerms), tokenizer)),
            })
            .ToArray();

        // Proximity is corpus-dependent by nature, and the two shapes are not interchangeable, so
        // both are measured. The first stage is identical to the BM25 row, so the difference is
        // proximity alone and nothing else.
        builders = builders
            .Concat(new (string, Func<ITextSearchEngine>)[]
            {
                ("BM25 + proximity (damp, s=0.25)", () => Proximity(index, 0.25, ProximityMode.Damp, tokenizer)),
                ("BM25 + proximity (damp, s=1)", () => Proximity(index, 1.0, ProximityMode.Damp, tokenizer)),
                ("BM25 + proximity (boost, s=1)", () => Proximity(index, 1.0, ProximityMode.Boost, tokenizer)),
            })
            .ToArray();

        var buildersList = builders.ToList();

        // Query-side expansion, one row per learned expander. The index is untouched and the corpus
        // statistics BM25 reads are therefore the ones the lexical rows above read, which is the
        // whole point: ExpansionTextIndex is the document-side variant and changes avgdl, so a
        // comparison between it and these rows would be a comparison of two indexes.
        foreach (var (name, expander) in queryExpansions ?? [])
        {
            buildersList.Add((name, () => new ExpandingTextSearchEngine(
                Ranked(index, new Bm25Scorer(1.5, 0.75, queryTerms), tokenizer),
                expander,
                tokenizer)));
        }

        if (dense is not null)
        {
            var denseEngine = new DenseTextSearchEngine(corpus, dense);
            var bm25 = Ranked(index, new Bm25Scorer(1.5, 0.75), tokenizer);

            buildersList.Add(("Dense multilingual-e5-small", () => denseEngine));
            buildersList.Add(("Hybrid BM25+Dense weighted", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new WeightedScoreResultMerger(1.0, 1.0))));
            buildersList.Add(("Hybrid BM25+Dense RRF", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new ReciprocalRankFusionMerger())));
        }

        if (reranker is not null)
        {
            var bm25 = Ranked(index, new Bm25Scorer(1.5, 0.75), tokenizer);
            var languageModel = Ranked(index, new QueryLikelihoodScorer(0.2), tokenizer);

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
            results.Add(RunConfig(name, factory(), queries, topK, ndcgGain, jobs, parseQuerySyntax, scoreRounding, captureOutcomes));

        string tunedDescription = tuned
            ? RunTuned(index, corpus, queries, topK, tokenizer, ndcgGain, results, jobs, parseQuerySyntax, captureOutcomes)
            : "skipped (--no-tuned)";

        if (tuned)
        {
            // The delta axis is a free parameter of both variants, so "tuned BM25" next to "BM25+ at
            // delta=1.0" is tuned-against-unfitted. These two rows close that, which is the only way
            // the variant rows in docs/ranking.md mean what a reader takes them to mean.
            RunTunedVariants(index, queries, topK, tokenizer, ndcgGain, results, out string plusDelta, out string lDelta, jobs, parseQuerySyntax, captureOutcomes);

            tunedDescription += $"; {plusDelta}, {lDelta}";
        }

        return (results, tunedDescription);
    }

    /// <summary>
    /// The queries a run scores, in the order it scores them: every query with a test judgement,
    /// ordered by id so a run is reproducible from the corpus alone, cut to <paramref name="limit"/>.
    /// </summary>
    /// <remarks>
    /// Shared with the run-file export. A run file and a metrics table computed from two different
    /// query lists would look comparable and not be, and that is not a difference anyone reading the
    /// output would catch.
    /// </remarks>
    internal static List<EvaluatedQuery> BuildQueries(BeirCorpus corpus, int? limit, bool excludeQueryDocument)
    {
        var documentIds = excludeQueryDocument
            ? corpus.Documents.Select(document => document.Id).ToHashSet(StringComparer.Ordinal)
            : null;

        return corpus.Queries
            .Where(query => corpus.TestRelevance.ContainsKey(query.Id))
            .OrderBy(query => query.Id, StringComparer.Ordinal)
            .Select(query => new EvaluatedQuery(
                query,
                corpus.TestRelevance[query.Id],
                documentIds is not null && documentIds.Contains(query.Id)
                    ? new HashSet<string>(StringComparer.Ordinal) { query.Id }
                    : null))
            .Take(limit ?? int.MaxValue)
            .ToList();
    }

    private static ConfigResult RunConfig(
        string name, ITextSearchEngine engine, IReadOnlyList<EvaluatedQuery> queries, int topK,
        NdcgGain ndcgGain, int jobs, bool parseQuerySyntax, bool scoreRounding = false,
        bool captureOutcomes = false)
    {
        // One slot per query, summed in query order at the end. Folding each query's contribution
        // into one shared double as it finishes would make the totals depend on which worker got
        // there first, so the printed figures would move with the thread count; per-query slots
        // summed by index add in the same order a serial loop would, and the figures come out
        // identical to three decimals and to the bit. What changes is the elapsed column, which is
        // wall-clock time and should.
        //
        // Running the queries concurrently is only sound because Search reads the shared index and
        // writes nothing, so it is checked rather than assumed: a parallel sweep is compared against
        // a serial one on the same corpus in the determinism tests, scores and rankings both.
        int count = queries.Count;
        var ndcg = new double[count];
        var map = new double[count];
        var mrr = new double[count];
        var recall = new double[count];

        // One slot per query, written by index, for the same reason the metric arrays above are: the
        // analysis file has to come out identical from a parallel run and a serial one, or a diff
        // against a recorded baseline would be reporting the thread count.
        var outcomes = captureOutcomes ? new QueryOutcome?[count] : null;

        var sw = Stopwatch.StartNew();

        void Score(int i)
        {
            EvaluatedQuery evaluated = queries[i];

            string[] retrieved = engine.Search(
                    evaluated.Query.Text,
                    // The rounding is a reproduction of what the reference writes down, not a scoring
                    // choice, so it is off unless the run asks for it: on it, the metrics measure a
                    // different ranking function.
                    new SearchOptions(
                        topK,
                        ExcludedDocumentIds: evaluated.Excluded,
                        ParseQuerySyntax: parseQuerySyntax)
                    .WithScoreRounding(scoreRounding
                        ? ScoreRounding.FourDecimals
                        : ScoreRounding.None))
                .Select(result => result.DocumentId)
                .ToArray();

            string[] relevant = evaluated.Graded.Keys.ToArray();

            // Measured at MetricDepth.Cutoff, not at topK. The two are different things: topK is how deep the
            // run retrieves, and a reproduction run retrieves deeper than it measures so that the extra
            // documents can be compared score for score. The metric is a property of the evaluation
            // protocol, not of how much was retrieved — computing it at the retrieval depth would report
            // nDCG@11 under a heading that says nDCG@10, which is a number that cannot be compared with
            // anything published.
            ndcg[i] = RetrievalMetrics.NdcgAtK(retrieved, evaluated.Graded, MetricDepth.Cutoff, ndcgGain);
            map[i] = RetrievalMetrics.AveragePrecisionAtK(retrieved, relevant, MetricDepth.Cutoff);
            mrr[i] = RetrievalMetrics.ReciprocalRankAtK(retrieved, relevant, MetricDepth.Cutoff);
            recall[i] = RetrievalMetrics.RecallAtK(retrieved, relevant, MetricDepth.Cutoff);

            if (outcomes is not null)
            {
                // Truncated to the metric depth, so the analysis file's "new_relevant" and
                // "lost_relevant" columns count documents that were actually scored. A reranking
                // configuration retrieves far deeper than it is measured at, and a document that
                // entered at rank 40 changed no metric.
                int depth = Math.Min(retrieved.Length, MetricDepth.Cutoff);

                // Counted here rather than recovered from recall: recall is a ratio, so the count is
                // only recoverable by multiplying back and rounding, and a query whose recall rounds
                // to the wrong integer would put a wrong "relevant_retrieved" in the analysis file
                // beside the correct recall it came from.
                var judged = new HashSet<string>(relevant, StringComparer.Ordinal);
                int relevantRetrieved = 0;

                for (int r = 0; r < depth; r++)
                {
                    if (judged.Contains(retrieved[r]))
                        relevantRetrieved++;
                }

                outcomes[i] = new QueryOutcome(
                    evaluated.Query.Id,
                    retrieved[..depth],
                    relevantRetrieved,
                    relevant.Length,
                    recall[i],
                    ndcg[i]);
            }
        }

        if (jobs > 1 && count > 1)
            Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = jobs }, Score);
        else
            for (int i = 0; i < count; i++)
                Score(i);

        sw.Stop();

        return new ConfigResult(
            name,
            count,
            ndcg.Sum() / count,
            map.Sum() / count,
            mrr.Sum() / count,
            recall.Sum() / count,
            sw.Elapsed,
            // The slots are dense: RunConfig scores every query in the list it was handed, so a null
            // here would mean the loop did not run, and dropping those silently would produce an
            // analysis file that is short by exactly the queries that failed.
            outcomes?.Select(outcome => outcome ?? throw new InvalidOperationException(
                $"Configuration '{name}' left a query unscored, so its per-query analysis is incomplete."))
                .ToList());
    }

    private static string RunTuned(
        InMemoryTextIndex index,
        BeirCorpus corpus,
        IReadOnlyList<EvaluatedQuery> queries,
        int topK,
        ITokenizer tokenizer,
        NdcgGain ndcgGain,
        List<ConfigResult> results,
        int jobs,
        bool parseQuerySyntax,
        bool captureOutcomes = false)
    {
        var validation = queries
            .Select(evaluated => new Bm25ValidationQuery(
                evaluated.Query.Text,
                evaluated.Graded.Keys.ToArray(),
                evaluated.Excluded))
            .ToList();

        var tuner = new Bm25ParameterTuner(index, validation, tokenizer);
        var tuned = tuner.Tune(topK: topK, metric: TuningMetric.Ndcg);

        var result = RunConfig(
            $"BM25 tuned (k1={tuned.Parameters.K1:0.##}, b={tuned.Parameters.B:0.##})",
            new RankedTextSearchEngine(index, new Bm25Scorer(tuned.Parameters), tokenizer),
            queries,
            topK,
            ndcgGain,
            jobs,
            parseQuerySyntax,
            captureOutcomes: captureOutcomes);

        results.Add(result);

        return $"k1={tuned.Parameters.K1.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"b={tuned.Parameters.B.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"nDCG@10={result.NdcgAt10.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Fits <c>(k1, b, delta)</c> for both variants on the same queries as the tuned BM25 row, and
    /// reports each winner's delta plus whether the bound beat no bound.
    /// </summary>
    /// <remarks>
    /// Fitted in-sample on all three rows, so the comparison is fair between them and none of them
    /// is a held-out number. The variants search 125 configurations to BM25's 25, so their margins
    /// carry more fitted noise — the reason the winner's own nDCG is printed next to the parameters
    /// rather than the delta alone.
    /// </remarks>
    private static void RunTunedVariants(
        InMemoryTextIndex index,
        IReadOnlyList<EvaluatedQuery> queries,
        int topK,
        ITokenizer tokenizer,
        NdcgGain ndcgGain,
        List<ConfigResult> results,
        out string plusDelta,
        out string lDelta,
        int jobs,
        bool parseQuerySyntax,
        bool captureOutcomes = false)
    {
        var validation = queries
            .Select(evaluated => new Bm25ValidationQuery(
                evaluated.Query.Text,
                evaluated.Graded.Keys.ToArray(),
                evaluated.Excluded))
            .ToList();

        var plus = new Bm25PlusParameterTuner(index, validation, tokenizer)
            .Tune(topK: topK, metric: TuningMetric.Ndcg);

        results.Add(RunConfig(
            $"BM25+ tuned (k1={plus.K1:0.##}, b={plus.B:0.##}, delta={plus.Delta:0.##})",
            new RankedTextSearchEngine(index, new Bm25PlusScorer(plus.K1, plus.B, plus.Delta), tokenizer),
            queries,
            topK,
            ndcgGain,
            jobs,
            parseQuerySyntax,
            captureOutcomes: captureOutcomes));

        // BM25+ at delta = 0 IS Bm25Scorer, so the tuner's own unfloored score is BM25's on the same
        // grid. Worth stating in the output: it means this row and the bm25-tuned row are fitted by
        // the same search over the same 25 (k1, b) pairs, and the ONLY thing the variant search adds
        // is the delta axis. So the gap between this row and the unfloored score is what delta bought.
        plusDelta = Describe("BM25+", plus.Delta, plus.UnflooredMetricScore, plus.MetricScore, plus.DeltaHelped);

        var l = new Bm25LParameterTuner(index, validation, tokenizer)
            .Tune(topK: topK, metric: TuningMetric.Ndcg);

        results.Add(RunConfig(
            $"BM25L tuned (k1={l.K1:0.##}, b={l.B:0.##}, delta={l.Delta:0.##})",
            new RankedTextSearchEngine(index, new Bm25LScorer(l.K1, l.B, l.Delta), tokenizer),
            queries,
            topK,
            ndcgGain,
            jobs,
            parseQuerySyntax,
            captureOutcomes: captureOutcomes));

        // Same reading applies: BM25L at delta = 0 is BM25 too, the compression cancelling.
        lDelta = Describe("BM25L", l.Delta, l.UnflooredMetricScore, l.MetricScore, l.DeltaHelped);
    }

    /// <summary>
    /// The tuner's own verdict on its added axis, with both scores so the claim is checkable rather
    /// than asserted: <c>unfloored</c> is the best any (k1, b) reached with no lower bound, which
    /// for both variants is plain BM25 over the same grid.
    /// </summary>
    private static string Describe(
        string name, double delta, double unfloored, double scored, bool helped)
    {
        string reading = double.IsNaN(unfloored)
            ? "no delta=0 in the grid, so nothing was compared"
            : $"{(helped ? "beat" : "did not beat")} the unfloored {unfloored.ToString("0.###", CultureInfo.InvariantCulture)}";

        return $"{name} delta={delta.ToString("0.####", CultureInfo.InvariantCulture)}"
             + $" ({reading}, scored {scored.ToString("0.###", CultureInfo.InvariantCulture)})";
    }

    /// <summary>
    /// BM25 over the shared index, re-ranked by proximity. The first stage matches the BM25 row.
    /// </summary>
    private static ITextSearchEngine Proximity(
        InMemoryTextIndex index,
        double strength,
        ProximityMode mode,
        ITokenizer tokenizer)
    {
        return new RerankedTextSearchEngine(
            new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), tokenizer),
            new ProximityReranker(index, tokenizer, strength, mode),
            maxCandidates: 100);
    }

    /// <summary>BM25F weighting the corpus's <c>title</c> field, everything else neutral.</summary>
    private static Bm25FScorer TitleWeighted(double weight) =>
        new(fieldWeights: new Dictionary<string, double> { ["title"] = weight });

    /// <summary>
    /// A lexical engine over the shared index. The scorer is what differs between configs, so the
    /// index is not rebuilt: it holds the same documents, the same terms and the same statistics for
    /// every row of the table, and tokenizing the corpus again per row was the single largest cost
    /// in a run before the index was shared.
    /// </summary>
    private static ITextSearchEngine Ranked(InMemoryTextIndex index, ITextScorer scorer, ITokenizer tokenizer) =>
        new RankedTextSearchEngine(index, scorer, tokenizer);

    /// <summary>Two lexical engines over one shared index, merged.</summary>
    private static ITextSearchEngine Hybrid(
        InMemoryTextIndex index, IResultMerger merger, ITokenizer tokenizer)
    {
        var lexical = new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), tokenizer);
        var languageModel = new RankedTextSearchEngine(index, new QueryLikelihoodScorer(0.2), tokenizer);

        return new HybridTextSearchEngine(new ITextSearchEngine[] { lexical, languageModel }, merger);
    }

    private static string CombineTitleAndText(BeirDocument document) =>
        string.IsNullOrEmpty(document.Title) ? document.Text : document.Title + " " + document.Text;
}