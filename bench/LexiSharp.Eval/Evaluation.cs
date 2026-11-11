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

/// <summary>
/// One row of the metrics table: a name a reader compares across runs, and the engine that
/// produces it.
/// </summary>
/// <param name="Name">The display name — the column a diff or a pin is keyed on.</param>
/// <param name="Factory">Builds the engine. Invoked once per scored set, not once per query.</param>
/// <param name="CandidatePool">
/// How many candidates a cascade row (proximity, cross-encoder) reorders at its shipped operating
/// point, or <c>null</c> for a row whose candidate set is the whole result set. The document
/// diagnosis probes a cascade row at this depth rather than at its own probe depth: a rerank row
/// asked for a thousand candidates is a different, far more expensive row than the one the table
/// measures, and past its pool the row cannot surface a document the base engine never returned.
/// </param>
/// <param name="IsProbe">
/// <c>true</c> for the rows a diagnosis adds beyond the table (length-normalization probes, the
/// unanalyzed-overlap control). A probe measures a hypothesis, not a shipped configuration, so it
/// is labelled in the report and never counted as « a technique that retrieved it ».
/// </param>
internal sealed record ConfigurationRow(
    string Name,
    Func<ITextSearchEngine> Factory,
    int? CandidatePool = null,
    bool IsProbe = false);

/// <summary>
/// What one run can probe: its fixed rows, the notes explaining absent ones, and the two
/// capabilities the row list is gated on.
/// </summary>
/// <param name="Notes">
/// Why a row a reader might expect is not here — the same notes the table prints, so a diagnosis
/// and a table state the same absences.
/// </param>
internal sealed record ConfigurationSet(
    IReadOnlyList<ConfigurationRow> Configurations,
    IReadOnlyList<string> Notes,
    bool AnyTitle,
    bool FieldStatistics);

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

/// <summary>
/// A tuned configuration measured on both sides of the held-out split: the fit set it was chosen on
/// (<paramref name="Fit"/>, the in-sample figure) and the score set withheld from fitting
/// (<paramref name="Score"/>).
/// </summary>
internal sealed record HeldOutComparison(string Name, ConfigResult Fit, ConfigResult Score);

/// <summary>
/// What a <c>--heldout</c> run measured, or why it could not measure it.
/// </summary>
/// <param name="UntunedBaseline">
/// The fixed BM25 row (<c>k1=1.5, b=0.75</c>) scored on the score set, or <c>null</c>. It is not a
/// fitted row and has no fit-set figure; it is the reference a held-out tuned number has to beat for
/// tuning to have bought anything on the queries that did not choose the parameters.
/// </param>
/// <param name="NotRunReason">
/// Non-null when the split was asked for but no tuned row could be measured on it — a degenerate
/// partition, or a run that fits nothing. The reason is printed rather than the table being omitted,
/// so the absence has a stated cause.
/// </param>
internal sealed record HeldOutReport(
    double Fraction,
    int TotalQueries,
    int FitQueries,
    int ScoreQueries,
    IReadOnlyList<HeldOutComparison> Rows,
    ConfigResult? UntunedBaseline = null,
    string? NotRunReason = null);

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
    /// <param name="tuningSearchOptions">
    /// The <see cref="SearchOptions"/> the tuned rows' candidate searches start from, or <c>null</c>
    /// for the tuners' defaults. Pass the same options this run reports with when the two conventions
    /// differ — <see cref="SearchOptions.ParseQuerySyntax"/> is the one that changes the winner on a
    /// corpus whose queries carry syntax. The harness's <c>--tune-search-options</c> is what passes it.
    /// </param>
    /// <param name="heldOutFraction">
    /// The share of the validation queries withheld from fitting and used to score the tuned rows, or
    /// <c>null</c> for the default in-sample table. Set, every tuner fits on the remaining queries; the
    /// tuned rows in <c>Results</c> are fitted and scored on that fit set, and the returned
    /// <see cref="HeldOutReport"/> carries each one's score-set figures. The untuned rows are unaffected
    /// and still score every query.
    /// </param>
    /// <returns>
    /// The per-configuration results, a description of how the k1/b tuning went, the configurations
    /// that were skipped because this index cannot serve them — a capability the corpus alone does not
    /// decide, so it is reported rather than left as a gap in the table — and the held-out report, or
    /// <c>null</c> when no split was asked for.
    /// </returns>
    public static (IReadOnlyList<ConfigResult> Results, string TunedDescription, IReadOnlyList<string> Notes, HeldOutReport? HeldOut) Run(
        BeirCorpus corpus, ITextIndex index, int topK, int? limit, ITokenizer tokenizer,
        bool excludeQueryDocument = false,
        NdcgGain ndcgGain = NdcgGain.Exponential,
        Bm25Parameters? referenceBm25 = null,
        bool queryFrequency = false,
        bool scoreRounding = false,
        bool saturationConstant = true,
        bool singlePrecision = false,
        DenseVectors? dense = null,
        bool tuned = true, IReranker? reranker = null, int rerankCandidates = 100, int jobs = 1,
        bool parseQuerySyntax = false, bool captureOutcomes = false, bool tuneBm25F = false,
        IReadOnlyList<(string Name, ITermExpander Expander)>? queryExpansions = null,
        SearchOptions? tuningSearchOptions = null,
        double? heldOutFraction = null,
        bool bm25Va = false,
        bool tuneBm25Va = false,
        IReadOnlyList<(string Name, IQueryTermWeighter Weighter)>? queryReweights = null,
        IReadOnlyList<double>? spreadAlphas = null,
        int spreadWindow = 8)
    {
        // Excluding a query's own document id is a no-op unless the corpus numbers its queries as
        // documents, so the check needs no corpus-specific configuration. One membership test per
        // query, not one scan of the corpus per query.
        List<EvaluatedQuery> queries = BuildQueries(corpus, limit, excludeQueryDocument);

        // The split is over the queries this run scores, after --limit. A held-out report is returned
        // even when the partition is degenerate, so the flag never fails silently: the reason is in
        // NotRunReason and the caller prints it.
        HeldOutReport? heldOut = null;
        List<EvaluatedQuery> fitQueries = queries;
        List<EvaluatedQuery>? scoreQueries = null;

        if (heldOutFraction is { } fraction)
        {
            (List<EvaluatedQuery> fit, List<EvaluatedQuery> score) =
                HeldOutSplit.Partition(queries, evaluated => evaluated.Query.Id, fraction);

            string? degenerate = fit.Count == 0
                ? "the score set took every query, so there was no fit set"
                : score.Count == 0
                    ? "the fit set took every query, so there was no score set"
                    : null;

            if (degenerate is null)
            {
                fitQueries = fit;
                scoreQueries = score;
            }

            heldOut = new HeldOutReport(
                fraction, queries.Count, fit.Count, score.Count, [], NotRunReason: degenerate);
        }

        // Null means the library default (distinct), so the shared value below is what every
        // scorer in this run was actually built with.
        var queryTerms = QueryTermsFor(queryFrequency);

        ConfigurationSet configurations = BuildConfigurations(
            corpus, index, tokenizer, queryTerms, referenceBm25, saturationConstant, singlePrecision,
            bm25Va, queryExpansions, queryReweights, spreadAlphas, spreadWindow, dense, reranker,
            rerankCandidates);

        bool anyTitle = configurations.AnyTitle;
        bool fieldStatistics = configurations.FieldStatistics;
        var notes = new List<string>(configurations.Notes);

        var results = new List<ConfigResult>();

        foreach (ConfigurationRow row in configurations.Configurations)
            results.Add(RunConfig(row.Name, row.Factory(), queries, topK, ndcgGain, jobs, parseQuerySyntax, scoreRounding, captureOutcomes));

        // Each fitted configuration is scored on the fit set here, and on the withheld score set when
        // one exists. Without --heldout the fit set is the whole query list, so these are the rows and
        // numbers the table has always carried.
        var heldOutRows = new List<HeldOutComparison>();

        // BM25F's own tuner, opt-in. It costs 25 (k1, b) evaluations plus one per field weight where
        // the other tuned rows cost 25 or 125, so a default run and the pinned replay do not pay for
        // it — and the row exists to answer one question the fixed weights cannot: whether a weight
        // beats leaving the fields neutral when both are fitted.
        if (tuneBm25F && anyTitle && fieldStatistics)
        {
            Bm25FTuningResult fitted = new Bm25FParameterTuner(
                    index, Validation(fitQueries), tokenizer, tuningSearchOptions)
                .Tune(weightedFields: ["title"], topK: topK, metric: TuningMetric.Ndcg);

            double titleWeight = fitted.Parameters.FieldWeights is { } weights
                && weights.TryGetValue("title", out double weight)
                    ? weight
                    : 1.0;

            string name =
                $"BM25F tuned (k1={fitted.Parameters.K1.ToString("0.##", CultureInfo.InvariantCulture)}, "
                + $"b={fitted.Parameters.B.ToString("0.##", CultureInfo.InvariantCulture)}, "
                + $"title={titleWeight.ToString("0.##", CultureInfo.InvariantCulture)})";

            ITextSearchEngine Engine() => Ranked(index, new Bm25FScorer(fitted.Parameters), tokenizer);

            ConfigResult fitResult = RunConfig(
                name, Engine(), fitQueries, topK, ndcgGain, jobs, parseQuerySyntax, scoreRounding, captureOutcomes);
            results.Add(fitResult);

            if (scoreQueries is not null)
            {
                heldOutRows.Add(new HeldOutComparison(
                    name,
                    fitResult,
                    RunConfig(name, Engine(), scoreQueries, topK, ndcgGain, jobs, parseQuerySyntax, scoreRounding, captureOutcomes)));
            }

            notes.Add(
                $"BM25F tuned in-sample{(scoreQueries is null ? string.Empty : " on the fit set")}: k1={fitted.Parameters.K1.ToString("0.##", CultureInfo.InvariantCulture)}, "
                + $"b={fitted.Parameters.B.ToString("0.##", CultureInfo.InvariantCulture)}, "
                + $"title={titleWeight.ToString("0.##", CultureInfo.InvariantCulture)}, "
                + $"{fitted.EvaluatedConfigurations} configurations, "
                + $"WeightingHelped={fitted.WeightingHelped} "
                + $"(fitted {fitted.MetricScore.ToString("0.###", CultureInfo.InvariantCulture)} against "
                + $"{fitted.UnweightedMetricScore.ToString("0.###", CultureInfo.InvariantCulture)} unweighted). "
                + "In-sample and an oracle, like every other tuned row.");
        }

        string tunedDescription = tuned
            ? RunTuned(
                index, fitQueries, scoreQueries, topK, tokenizer, ndcgGain, results, heldOutRows, jobs,
                parseQuerySyntax, tuningSearchOptions, captureOutcomes)
            : "skipped (--no-tuned)";

        if (tuned)
        {
            // The delta axis is a free parameter of both variants, so "tuned BM25" next to "BM25+ at
            // delta=1.0" is tuned-against-unfitted. These two rows close that, which is the only way
            // the variant rows in docs/ranking.md mean what a reader takes them to mean.
            RunTunedVariants(
                index, fitQueries, scoreQueries, topK, tokenizer, ndcgGain, results, heldOutRows, out string plusDelta,
                out string lDelta, jobs, parseQuerySyntax, tuningSearchOptions, captureOutcomes);

            tunedDescription += $"; {plusDelta}, {lDelta}";
        }

        if (tuned && tuneBm25Va)
        {
            // The three fixed points say what verboseness does; this row says what a corpus-chosen
            // b does, which is the configuration the normalization literature actually fits. A
            // winner at b = 1 is the null result and is printed as one.
            tunedDescription += $"; {RunTunedVa(index, fitQueries, scoreQueries, topK, tokenizer, ndcgGain, results, heldOutRows, jobs, parseQuerySyntax, tuningSearchOptions, captureOutcomes)}";
        }

        if (heldOut is not null)
        {
            string? reason = heldOut.NotRunReason;
            ConfigResult? untunedBaseline = null;

            if (reason is null && heldOutRows.Count == 0)
            {
                reason = "no tuned row was fitted, so there is nothing to score on the score set "
                       + "(pass --no-tuned only with --tune-bm25f for a BM25F row).";
            }

            // The score set needs a fixed reference: a held-out tuned figure below the fit-set figure
            // is a gap, but only a comparison against the same queries says whether tuning bought
            // anything there at all. One extra pass over the score set, only when a split ran.
            if (reason is null && scoreQueries is not null)
            {
                untunedBaseline = RunConfig(
                    "BM25 (k1=1.5, b=0.75)",
                    Ranked(index, new Bm25Scorer(1.5, 0.75, queryTerms), tokenizer),
                    scoreQueries, topK, ndcgGain, jobs, parseQuerySyntax, scoreRounding);
            }

            heldOut = heldOut with
            {
                Rows = heldOutRows,
                UntunedBaseline = untunedBaseline,
                NotRunReason = reason,
            };
        }

        return (results, tunedDescription, notes, heldOut);
    }

    /// <summary>
    /// The name of the fixed BM25 row every other row and every default baseline is read against —
    /// the literal is shared so the table, the analysis defaults and the document diagnosis's
    /// lookup of this row cannot drift apart into three strings that read the same and differ.
    /// </summary>
    internal const string BaselineConfigurationName = "BM25 (k1=1.5, b=0.75)";

    /// <summary>
    /// The candidate pool the proximity rows reorder at their shipped operating point — the same
    /// <c>maxCandidates</c> <see cref="Proximity"/> passes, named once so the row metadata and the
    /// engine it describes cannot disagree.
    /// </summary>
    internal const int ProximityCandidates = 100;

    /// <summary>
    /// The query-term weighting every scorer in a run is built with: <c>null</c>-means-default is
    /// resolved once here rather than at each construction site, so a run cannot end up with rows
    /// scored under two different weightings because one site read the flag differently.
    /// </summary>
    internal static QueryTermWeighting QueryTermsFor(bool queryFrequency) =>
        queryFrequency ? QueryTermWeighting.QueryFrequency : QueryTermWeighting.Distinct;

    /// <summary>
    /// The run's fixed configurations — every row of the metrics table that needs no fitting — as
    /// named engine factories, plus the notes explaining rows that are absent.
    /// </summary>
    /// <remarks>
    /// Built once and shared by <see cref="Run"/> and the document diagnosis (<c>--diagnose-doc</c>):
    /// a diagnosis that probed a different list would answer a question this harness does not
    /// measure, and a list written out twice is a list that drifts — the failure mode the per-query
    /// analysis already documents for query lists. Tuned rows are absent by construction: they are
    /// fitted against the query set inside <see cref="Run"/>, so a diagnosis states that it probes
    /// the fixed rows only rather than paying a grid search to include them.
    /// </remarks>
    internal static ConfigurationSet BuildConfigurations(
        BeirCorpus corpus,
        ITextIndex index,
        ITokenizer tokenizer,
        QueryTermWeighting queryTerms,
        Bm25Parameters? referenceBm25,
        bool saturationConstant,
        bool singlePrecision,
        bool bm25Va,
        IReadOnlyList<(string Name, ITermExpander Expander)>? queryExpansions,
        IReadOnlyList<(string Name, IQueryTermWeighter Weighter)>? queryReweights,
        IReadOnlyList<double>? spreadAlphas,
        int spreadWindow,
        DenseVectors? dense,
        IReranker? reranker,
        int rerankCandidates)
    {
        // ArguAna carries no title, so a field-weighted configuration there would be the same three
        // rows over again with an inert weight. Say so instead of padding the table.
        bool anyTitle = corpus.Documents.Any(document => !string.IsNullOrEmpty(document.Title));

        // Titles are a property of the corpus; per-field statistics are a property of the index. A
        // field-weighted ranking needs both, and a store that keeps no field statistics refuses it by
        // name rather than reading a silent zero — so the rows are gated on the capability the scorer
        // actually reads, and their absence is reported rather than left as a gap in the table.
        bool fieldStatistics = index is IFieldStatisticsIndex;
        var notes = new List<string>();

        var builders = new ConfigurationRow[]
        {
            new(BaselineConfigurationName, () => Ranked(index, new Bm25Scorer(1.5, 0.75, queryTerms), tokenizer)),
            new("BM25 (k1=1.2, b=0.75)", () => Ranked(index, new Bm25Scorer(1.2, 0.75, queryTerms), tokenizer)),
        };

        // The published BM25 baselines were produced at the retrieval stack's own defaults,
        // k1=0.9 and b=0.4, which is not one of the two rows above. A run has to be able to measure
        // that operating point, or a comparison against a published number is a comparison of two
        // different configurations. Opt-in, so the default table is unchanged.
        if (referenceBm25 is { } reference)
        {
            builders = builders
                .Concat(new ConfigurationRow[]
                {
                    // Invariant formatting: a comma here would read as two parameters on a
                    // machine whose culture uses it, in a table whose whole job is naming them.
                    new($"BM25 (k1={reference.K1.ToString("0.##", CultureInfo.InvariantCulture)}, "
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

        // BM25VA is a direction, not one point: b = 1 is the degeneration control (the same scorer
        // as BM25, which is what the identity test pins), b = 0 removes length from the
        // normalization entirely, and b = 0.5 sits between them. Opt-in, so the default table keeps
        // the rows and the run time it had.
        if (bm25Va)
        {
            builders = builders
                .Concat(new ConfigurationRow[]
                {
                    // The control the identity test pins: BM25VA at b = 1 *is* BM25 at b = 1, so these
                    // two rows must print the same numbers. The default BM25 row is at b = 0.75 and is
                    // deliberately a different operating point, not the control.
                    new("BM25 (k1=1.5, b=1) [control]", () => Ranked(index, new Bm25Scorer(1.5, 1.0, queryTerms), tokenizer)),
                    new("BM25VA (k1=1.5, b=1)", () => Ranked(index, new Bm25VaScorer(1.5, 1.0, queryTerms), tokenizer)),
                    new("BM25VA (k1=1.5, b=0.5)", () => Ranked(index, new Bm25VaScorer(1.5, 0.5, queryTerms), tokenizer)),
                    new("BM25VA (k1=1.5, b=0)", () => Ranked(index, new Bm25VaScorer(1.5, 0.0, queryTerms), tokenizer)),
                })
                .ToArray();
        }

        builders = builders
            .Concat(new ConfigurationRow[]
            {
                new("TF-IDF", () => Ranked(index, new TfIdfScorer(), tokenizer)),
                new("QueryLikelihood (lambda=0.2)", () => Ranked(index, new QueryLikelihoodScorer(0.2), tokenizer)),
                new("Hybrid BM25+QL weighted", () => Hybrid(index,
                    new WeightedScoreResultMerger(1.0, 1.0), tokenizer)),
                new("Hybrid BM25+QL RRF", () => Hybrid(index,
                    new ReciprocalRankFusionMerger(), tokenizer)),
            })
            .ToArray();

        if (anyTitle && fieldStatistics)
        {
            // Three weights rather than one: the question is whether weighting a title helps at all
            // here, and a single point cannot show a direction. BM25F neutral is included because it
            // is not BM25 — on one field it is a different length term, so its own row is the
            // baseline the weighted rows have to beat.
            builders = builders
                .Concat(new ConfigurationRow[]
                {
                    new("BM25F (unweighted)", () => Ranked(index, TitleWeighted(1.0), tokenizer)),
                    new("BM25F (title 2.0)", () => Ranked(index, TitleWeighted(2.0), tokenizer)),
                    new("BM25F (title 4.0)", () => Ranked(index, TitleWeighted(4.0), tokenizer)),
                })
                .ToArray();
        }
        else if (anyTitle)
        {
            notes.Add(
                "BM25F (unweighted / title 2.0 / title 4.0) skipped: the corpus carries titles, but "
                + $"{index.GetType().Name} keeps no per-field statistics (IFieldStatisticsIndex), and a "
                + "field-weighted ranking over absent statistics would be wrong with no error to show for it.");
        }

        // Single-field BM25 variants, measured against the tuned BM25 row rather than the default
        // one: a default-versus-default comparison mostly measures which default fits the corpus.
        // These two rows fix delta at the papers' starting value, which is why RunTunedVariants adds
        // a tuned row for each of them — otherwise the table compares a fitted BM25 against two
        // unfitted variants and calls it a verdict on the formula.
        builders = builders
            .Concat(new ConfigurationRow[]
            {
                new("BM25+ (delta=1.0)", () => Ranked(index, new Bm25PlusScorer(1.5, 0.75, 1.0, queryTerms), tokenizer)),
                new("BM25L (delta=0.5)", () => Ranked(index, new Bm25LScorer(1.5, 0.75, 0.5, queryTerms), tokenizer)),
            })
            .ToArray();

        // Proximity is corpus-dependent by nature, and the two shapes are not interchangeable, so
        // both are measured. The first stage is identical to the BM25 row, so the difference is
        // proximity alone and nothing else.
        builders = builders
            .Concat(new ConfigurationRow[]
            {
                new("BM25 + proximity (damp, s=0.25)", () => Proximity(index, 0.25, ProximityMode.Damp, tokenizer), CandidatePool: ProximityCandidates),
                new("BM25 + proximity (damp, s=1)", () => Proximity(index, 1.0, ProximityMode.Damp, tokenizer), CandidatePool: ProximityCandidates),
                new("BM25 + proximity (boost, s=1)", () => Proximity(index, 1.0, ProximityMode.Boost, tokenizer), CandidatePool: ProximityCandidates),
            })
            .ToArray();

        var buildersList = builders.ToList();

        // Query-side expansion, one row per learned expander. The index is untouched and the corpus
        // statistics BM25 reads are therefore the ones the lexical rows above read, which is the
        // whole point: ExpansionTextIndex is the document-side variant and changes avgdl, so a
        // comparison between it and these rows would be a comparison of two indexes.
        foreach (var (name, expander) in queryExpansions ?? [])
        {
            buildersList.Add(new(name, () => new ExpandingTextSearchEngine(
                Ranked(index, new Bm25Scorer(1.5, 0.75, queryTerms), tokenizer),
                expander,
                tokenizer)));
        }

        // Query-term reweighting, one row per strength. The graph is learned once by the caller and
        // shared; each row rebuilds the scorer per search because a weight is a property of the
        // query, not of the engine. The BM25 operating point is the run's reference pair when one
        // was asked for, so the reweighting can be measured on top of the tuned corpus point rather
        // than only on the library default.
        if (queryReweights is { Count: > 0 })
        {
            Bm25Parameters reweightParameters = referenceBm25 ?? new Bm25Parameters(1.5, 0.75);

            foreach (var (name, weighter) in queryReweights)
            {
                buildersList.Add(new(name, () => new ReweightingTextSearchEngine(
                    index,
                    new Bm25Scorer(reweightParameters.K1, reweightParameters.B, queryTerms),
                    weighter,
                    tokenizer)));
            }
        }

        // Spread is a direction, and the direction is the interaction with b: the same statistic at
        // b = 0, 0.5 and 1 shows whether the spatial count makes length normalization redundant.
        // alpha = 0 is the raw-count control and must reproduce BM25 at the same b.
        if (spreadAlphas is { Count: > 0 })
        {
            foreach (double alpha in spreadAlphas)
            {
                foreach (double b in new[] { 0.0, 0.5, 1.0 })
                {
                    string name =
                        $"BM25 spread (alpha={alpha.ToString("0.##", CultureInfo.InvariantCulture)}, "
                        + $"b={b.ToString("0.##", CultureInfo.InvariantCulture)}, w={spreadWindow})";

                    buildersList.Add(new(name, () => new RankedTextSearchEngine(
                        index,
                        new SpreadBm25Scorer(1.5, b, spreadWindow, alpha, queryTerms),
                        tokenizer)));
                }
            }
        }

        if (dense is not null)
        {
            var denseEngine = new DenseTextSearchEngine(corpus, dense);
            var bm25 = Ranked(index, new Bm25Scorer(1.5, 0.75), tokenizer);

            buildersList.Add(new("Dense multilingual-e5-small", () => denseEngine));
            buildersList.Add(new("Hybrid BM25+Dense weighted", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new WeightedScoreResultMerger(1.0, 1.0))));
            buildersList.Add(new("Hybrid BM25+Dense RRF", () => new HybridTextSearchEngine(
                new ITextSearchEngine[] { bm25, denseEngine }, new ReciprocalRankFusionMerger())));
        }

        if (reranker is not null)
        {
            var bm25 = Ranked(index, new Bm25Scorer(1.5, 0.75), tokenizer);
            var languageModel = Ranked(index, new QueryLikelihoodScorer(0.2), tokenizer);

            buildersList.Add(new($"BM25 (top{rerankCandidates})+CrossRerank", () =>
                RerankEngines.Reranked(bm25, rerankCandidates, reranker), CandidatePool: rerankCandidates));
            buildersList.Add(new($"QL (top{rerankCandidates})+CrossRerank", () =>
                RerankEngines.Reranked(languageModel, rerankCandidates, reranker), CandidatePool: rerankCandidates));

            if (dense is not null)
            {
                var rrf = new HybridTextSearchEngine(
                    new ITextSearchEngine[] { bm25, new DenseTextSearchEngine(corpus, dense) },
                    new ReciprocalRankFusionMerger());

                buildersList.Add(new($"Hybrid RRF (top{rerankCandidates})+CrossRerank", () =>
                    RerankEngines.Reranked(rrf, rerankCandidates, reranker), CandidatePool: rerankCandidates));
            }
        }

        return new ConfigurationSet(buildersList, notes, anyTitle, fieldStatistics);
    }

    /// <summary>The tuners' validation rows, one per query the fit set holds.</summary>
    private static List<Bm25ValidationQuery> Validation(IReadOnlyList<EvaluatedQuery> queries) =>
        queries
            .Select(evaluated => new Bm25ValidationQuery(
                evaluated.Query.Text,
                evaluated.Graded.Keys.ToArray(),
                evaluated.Excluded))
            .ToList();

    /// <summary>
    /// Scores an engine that owns its own index, on the same queries and with the same metrics as
    /// <see cref="Run"/>. A PostgreSQL backend is the case this exists for: nothing in it is an
    /// <c>ITextIndex</c>, so it cannot be a row of that table, but the rankings it returns are
    /// measurable by the same code and comparable as metrics.
    /// </summary>
    /// <param name="engine">Already connected; this method indexes nothing.</param>
    /// <remarks>
    /// Query syntax is passed as off. A backend that implements <c>IQuerySyntaxSupport</c> decides
    /// that itself — the PostgreSQL engines always hand the text to <c>websearch_to_tsquery</c> — and
    /// saying "off" here is the accurate statement that this harness asked for nothing.
    /// </remarks>
    public static ConfigResult RunEngine(
        BeirCorpus corpus,
        ITextSearchEngine engine,
        string name,
        int topK,
        int? limit,
        NdcgGain ndcgGain,
        bool excludeQueryDocument = false,
        int jobs = 1,
        bool captureOutcomes = false)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(engine);

        List<EvaluatedQuery> queries = BuildQueries(corpus, limit, excludeQueryDocument);

        return RunConfig(
            name, engine, queries, topK, ndcgGain, jobs,
            parseQuerySyntax: false, captureOutcomes: captureOutcomes);
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
        ITextIndex index,
        IReadOnlyList<EvaluatedQuery> fitQueries,
        IReadOnlyList<EvaluatedQuery>? scoreQueries,
        int topK,
        ITokenizer tokenizer,
        NdcgGain ndcgGain,
        List<ConfigResult> results,
        List<HeldOutComparison> heldOutRows,
        int jobs,
        bool parseQuerySyntax,
        SearchOptions? tuningSearchOptions,
        bool captureOutcomes = false)
    {
        var tuner = new Bm25ParameterTuner(index, Validation(fitQueries), tokenizer, tuningSearchOptions);
        var tuned = tuner.Tune(topK: topK, metric: TuningMetric.Ndcg);

        string name = $"BM25 tuned (k1={tuned.Parameters.K1:0.##}, b={tuned.Parameters.B:0.##})";

        ITextSearchEngine Engine() =>
            new RankedTextSearchEngine(index, new Bm25Scorer(tuned.Parameters), tokenizer);

        ConfigResult result = RunConfig(
            name, Engine(), fitQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes);

        results.Add(result);

        if (scoreQueries is not null)
        {
            heldOutRows.Add(new HeldOutComparison(
                name,
                result,
                RunConfig(name, Engine(), scoreQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes)));
        }

        return $"k1={tuned.Parameters.K1.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"b={tuned.Parameters.B.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"nDCG@10={result.NdcgAt10.ToString("0.###", CultureInfo.InvariantCulture)}";
    }

    /// <summary>
    /// Fits <c>(k1, b)</c> for BM25VA on the same queries as the tuned BM25 row, and states the one
    /// reading that matters: a winner at <c>b = 1</c> is BM25, so verboseness bought nothing.
    /// </summary>
    private static string RunTunedVa(
        ITextIndex index,
        IReadOnlyList<EvaluatedQuery> fitQueries,
        IReadOnlyList<EvaluatedQuery>? scoreQueries,
        int topK,
        ITokenizer tokenizer,
        NdcgGain ndcgGain,
        List<ConfigResult> results,
        List<HeldOutComparison> heldOutRows,
        int jobs,
        bool parseQuerySyntax,
        SearchOptions? tuningSearchOptions,
        bool captureOutcomes = false)
    {
        var tuner = new Bm25VaParameterTuner(index, Validation(fitQueries), tokenizer, tuningSearchOptions);
        Bm25VaTuningResult tuned = tuner.Tune(topK: topK, metric: TuningMetric.Ndcg);

        string name = $"BM25VA tuned (k1={tuned.K1:0.##}, b={tuned.B:0.##})";

        ITextSearchEngine Engine() =>
            new RankedTextSearchEngine(index, new Bm25VaScorer(tuned.K1, tuned.B), tokenizer);

        ConfigResult result = RunConfig(
            name, Engine(), fitQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes);

        results.Add(result);

        if (scoreQueries is not null)
        {
            heldOutRows.Add(new HeldOutComparison(
                name,
                result,
                RunConfig(name, Engine(), scoreQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes)));
        }

        string reading = tuned.B >= 1.0
            ? "b=1 is BM25, so verboseness bought nothing"
            : $"b={tuned.B.ToString("0.##", CultureInfo.InvariantCulture)} keeps "
              + $"{((1.0 - tuned.B) * 100).ToString("0.#", CultureInfo.InvariantCulture)} % verboseness";

        return $"BM25VA tuned: k1={tuned.K1.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"b={tuned.B.ToString("0.####", CultureInfo.InvariantCulture)}, "
             + $"nDCG@10={result.NdcgAt10.ToString("0.###", CultureInfo.InvariantCulture)} ({reading})";
    }

    /// <summary>
    /// Fits <c>(k1, b, delta)</c> for both variants on the same queries as the tuned BM25 row, and
    /// reports each winner's delta plus whether the bound beat no bound.
    /// </summary>
    /// <remarks>
    /// Fitted in-sample on all three rows, so the comparison is fair between them and none of them
    /// is a held-out number unless <paramref name="scoreQueries"/> is non-null, in which case the
    /// fitted winners are scored on the withheld set as well. The variants search 125 configurations
    /// to BM25's 25, so their margins carry more fitted noise — the reason the winner's own nDCG is
    /// printed next to the parameters rather than the delta alone.
    /// </remarks>
    private static void RunTunedVariants(
        ITextIndex index,
        IReadOnlyList<EvaluatedQuery> fitQueries,
        IReadOnlyList<EvaluatedQuery>? scoreQueries,
        int topK,
        ITokenizer tokenizer,
        NdcgGain ndcgGain,
        List<ConfigResult> results,
        List<HeldOutComparison> heldOutRows,
        out string plusDelta,
        out string lDelta,
        int jobs,
        bool parseQuerySyntax,
        SearchOptions? tuningSearchOptions,
        bool captureOutcomes = false)
    {
        var validation = Validation(fitQueries);

        var plus = new Bm25PlusParameterTuner(index, validation, tokenizer, tuningSearchOptions)
            .Tune(topK: topK, metric: TuningMetric.Ndcg);

        string plusName = $"BM25+ tuned (k1={plus.K1:0.##}, b={plus.B:0.##}, delta={plus.Delta:0.##})";

        ITextSearchEngine Plus() =>
            new RankedTextSearchEngine(index, new Bm25PlusScorer(plus.K1, plus.B, plus.Delta), tokenizer);

        ConfigResult plusFit = RunConfig(
            plusName, Plus(), fitQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes);

        results.Add(plusFit);

        if (scoreQueries is not null)
        {
            heldOutRows.Add(new HeldOutComparison(
                plusName,
                plusFit,
                RunConfig(plusName, Plus(), scoreQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes)));
        }

        // BM25+ at delta = 0 IS Bm25Scorer, so the tuner's own unfloored score is BM25's on the same
        // grid. Worth stating in the output: it means this row and the bm25-tuned row are fitted by
        // the same search over the same 25 (k1, b) pairs, and the ONLY thing the variant search adds
        // is the delta axis. So the gap between this row and the unfloored score is what delta bought.
        plusDelta = Describe("BM25+", plus.Delta, plus.UnflooredMetricScore, plus.MetricScore, plus.DeltaHelped);

        var l = new Bm25LParameterTuner(index, validation, tokenizer, tuningSearchOptions)
            .Tune(topK: topK, metric: TuningMetric.Ndcg);

        string lName = $"BM25L tuned (k1={l.K1:0.##}, b={l.B:0.##}, delta={l.Delta:0.##})";

        ITextSearchEngine L() =>
            new RankedTextSearchEngine(index, new Bm25LScorer(l.K1, l.B, l.Delta), tokenizer);

        ConfigResult lFit = RunConfig(
            lName, L(), fitQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes);

        results.Add(lFit);

        if (scoreQueries is not null)
        {
            heldOutRows.Add(new HeldOutComparison(
                lName,
                lFit,
                RunConfig(lName, L(), scoreQueries, topK, ndcgGain, jobs, parseQuerySyntax, captureOutcomes: captureOutcomes)));
        }

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
        ITextIndex index,
        double strength,
        ProximityMode mode,
        ITokenizer tokenizer)
    {
        return new RerankedTextSearchEngine(
            new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), tokenizer),
            new ProximityReranker(index, tokenizer, strength, mode),
            maxCandidates: ProximityCandidates);
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
    private static ITextSearchEngine Ranked(ITextIndex index, ITextScorer scorer, ITokenizer tokenizer) =>
        new RankedTextSearchEngine(index, scorer, tokenizer);

    /// <summary>Two lexical engines over one shared index, merged.</summary>
    private static ITextSearchEngine Hybrid(
        ITextIndex index, IResultMerger merger, ITokenizer tokenizer)
    {
        var lexical = new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.75), tokenizer);
        var languageModel = new RankedTextSearchEngine(index, new QueryLikelihoodScorer(0.2), tokenizer);

        return new HybridTextSearchEngine(new ITextSearchEngine[] { lexical, languageModel }, merger);
    }

    private static string CombineTitleAndText(BeirDocument document) =>
        string.IsNullOrEmpty(document.Title) ? document.Text : document.Title + " " + document.Text;
}