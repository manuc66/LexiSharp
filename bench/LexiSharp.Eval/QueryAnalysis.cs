using System.Globalization;
using System.Text;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>
/// One query's outcome under one configuration: the ranking it produced and the two metrics the
/// table above it reports.
/// </summary>
/// <param name="Retrieved">
/// The document ids in rank order, truncated to the metric depth. Kept rather than the metrics
/// alone because every question this file exists to answer is a question about <em>which</em>
/// documents moved — an aggregate that gained 0.02 nDCG does not say whether the mechanism found
/// documents or reshuffled ones it already had.
/// </param>
internal sealed record QueryOutcome(
    string QueryId,
    string[] Retrieved,
    int RelevantRetrieved,
    int JudgedRelevant,
    double RecallAt10,
    double NdcgAt10);

/// <summary>
/// A query's lexical relationship to the documents judged relevant to it, measured once and
/// independent of any configuration.
/// </summary>
/// <remarks>
/// <para>
/// This is the axis the vocabulary-mismatch hypothesis is about, and it is deliberately not query
/// length. A three-term query can share every term with its answer; an eight-term query can share
/// none. Length is a proxy that survives being wrong, and a proxy is what makes a sliced result
/// unreadable — "expansion helped short queries" is a claim about three tokens, not about mismatch.
/// </para>
/// <para>
/// Coverage is reported as a share of the query's <see cref="IdfMass"/>, not as a count of terms.
/// The count lies: a query term present in every judged document carries no information about which
/// document is the answer, and a term present in one carries a great deal, but both count once. The
/// idf-weighted share is the quantity that says how much of what the query asked for the answer
/// actually says in words.
/// </para>
/// <para>
/// Terms the index has never seen are counted in <see cref="IdfMass"/> at their df=0 idf. Dropping
/// them would make a query that asks for something the corpus has never contained look like a
/// perfect lexical match, which is the exact case the hypothesis is about.
/// </para>
/// </remarks>
internal sealed record QueryCoverage(
    string QueryId,
    int QueryTerms,
    int TermsOutOfVocabulary,
    double IdfMass,
    int JudgedTermsCovered,
    double JudgedIdfShare);

/// <summary>
/// How far apart two configurations' top-10 rankings are for one query — the quantity a reranking
/// or fusion trigger would be built on, measured by <em>ids</em> rather than by score.
/// </summary>
/// <param name="Shared">Ids present in both rankings.</param>
/// <param name="LeftOnly">Ids only the left configuration returned.</param>
/// <param name="RightOnly">Ids only the right configuration returned.</param>
/// <param name="Distance">
/// Jaccard distance over the two id sets: <c>1 - |shared| / |union|</c>. <c>0</c> means both
/// configurations returned the same ids and <c>1</c> means they shared none. Two configurations that
/// both returned nothing are at <c>0</c>: they agree about having found nothing, and the alternative
/// — calling an empty result maximal disagreement — would put the queries no retriever can answer at
/// the top of a table read as "where reranking is needed".
/// </param>
/// <remarks>
/// <para>
/// Rank correlation is deliberately not used. The two rankings are stored truncated to
/// <see cref="MetricDepth.Cutoff"/> and may have different lengths, so a Kendall or Spearman figure
/// over them would be defined by a padding convention nobody reading the column would know. The set
/// distance is defined for every pair of lists, including empty ones.
/// </para>
/// <para>
/// <b>The depth is not a free parameter.</b> The ranking kept per query is cut at
/// <see cref="MetricDepth.Cutoff"/> by <c>Evaluation.Run</c>, so this distance is a property of the
/// top-10 and cannot be asked for at depth 100 without changing what that capture holds — and paying
/// for it in memory, which is the reason it is cut there.
/// </para>
/// <para>
/// <b>This is measurable only after both retrievers have run.</b> It cannot decide whether to pay for
/// a dense encoder; it can decide what to do with the two rankings once they exist — how to weight a
/// fusion, or whether an expensive third stage is worth invoking on this query.
/// </para>
/// </remarks>
internal sealed record TopKDisagreement(
    string QueryId,
    int Shared,
    int LeftOnly,
    int RightOnly,
    double Distance);

/// <summary>
/// A rank correlation, or the reason one was not computed. A <c>null</c> correlation is printed as
/// "-" with its reason, never as a zero: a zero is a measurement, and "not enough rows" is not one.
/// </summary>
internal readonly record struct RankCorrelation(double? Rho, int Pairs, string? Unavailable)
{
    /// <summary>No correlation exists for this input, and <paramref name="why"/> says why.</summary>
    public static RankCorrelation NotComputed(int pairs, string why) => new(null, pairs, why);
}

/// <summary>
/// Everything the per-query report needs, gathered once so the two writers take one argument and
/// cannot be handed a coverage list from one run and outcomes from another.
/// </summary>
internal sealed record QueryAnalysisReport(
    IReadOnlyList<EvaluatedQuery> Queries,
    IReadOnlyList<QueryCoverage> Coverage,
    IReadOnlyDictionary<string, IReadOnlyList<QueryOutcome>> ByConfig)
{
    /// <summary>
    /// Pairs a named candidate against a named baseline, or throws naming both what was asked for
    /// and what is available.
    /// </summary>
    /// <remarks>
    /// A misspelled configuration name must not read as "no deltas". Every configuration in a run
    /// has a bracketed parameter list in its display name, so the available names are printed in
    /// full rather than matched by prefix.
    /// </remarks>
    public (string Candidate, string Baseline, IReadOnlyList<QueryOutcome> CandidateOutcomes,
        IReadOnlyList<QueryOutcome> BaselineOutcomes) Pair(string candidate, string baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseline);

        if (!ByConfig.TryGetValue(candidate, out var candidateOutcomes))
            throw new ArgumentException(
                $"No configuration named '{candidate}'. This run has: {string.Join(", ", ByConfig.Keys)}.",
                nameof(candidate));

        if (!ByConfig.TryGetValue(baseline, out var baselineOutcomes))
            throw new ArgumentException(
                $"No configuration named '{baseline}'. This run has: {string.Join(", ", ByConfig.Keys)}.",
                nameof(baseline));

        return (candidate, baseline, candidateOutcomes, baselineOutcomes);
    }

    /// <summary>
    /// The outcomes of one named configuration, or throws naming what the run does have.
    /// </summary>
    public IReadOnlyList<QueryOutcome> OutcomesOf(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return ByConfig.TryGetValue(name, out var outcomes)
            ? outcomes
            : throw new ArgumentException(
                $"No configuration named '{name}'. This run has: {string.Join(", ", ByConfig.Keys)}.", nameof(name));
    }

    /// <summary>The judged document ids for a query, or an empty set if the run did not judge it.</summary>
    public IReadOnlyCollection<string> Judged(string queryId)
    {
        for (int i = 0; i < Queries.Count; i++)
        {
            if (string.Equals(Queries[i].Query.Id, queryId, StringComparison.Ordinal))
                return Queries[i].Graded.Keys as IReadOnlyCollection<string>
                    ?? Queries[i].Graded.Keys.ToArray();
        }

        return [];
    }
}

/// <summary>
/// The per-query record a metrics table cannot express: which queries a configuration rescued, which
/// it hurt, and whether the queries it rescued are the ones a stated mechanism predicts.
/// </summary>
/// <remarks>
/// Built on the same principle as the reference corpus's <c>diff</c> and for the same reason. A mean
/// decomposes into two different stories — a mechanism that found new documents, and a mechanism
/// that reordered ones already retrieved — and the two call for opposite follow-ups, so the mean is
/// not a summary of the run but a loss of it.
/// </remarks>
internal static class QueryAnalysis
{
    /// <summary>Coverage bands, by the share of a query's idf mass its judged documents carry.</summary>
    public enum MismatchBand
    {
        /// <summary>No judged document shares any indexed term with the query.</summary>
        None,

        /// <summary>Some, but under a third, of the query's idf mass.</summary>
        Low,

        /// <summary>A third to two thirds.</summary>
        Medium,

        /// <summary>Over two thirds — the query is lexically close to its own answer.</summary>
        High,
    }

    /// <summary>
    /// Describes every query's lexical relationship to its judged documents.
    /// </summary>
    /// <param name="index">
    /// Read for document frequency only, so the idf weighting is the same weighting the BM25 rows
    /// score with. An idf taken from a different index would put the band cut points somewhere other
    /// than where the corpus puts them.
    /// </param>
    /// <param name="corpus">
    /// Read for the document text behind each judged id, which is the side of the comparison that
    /// needs tokenizing.
    /// </param>
    /// <remarks>
    /// Every judged document is tokenized once, not once per query that judges it. NFCorpus has 3 633
    /// documents against 323 queries and ArguAna 8 674 against 1 406, so a per-query tokenization
    /// would be a quadratic cost paid to recompute the same strings.
    /// </remarks>
    public static IReadOnlyList<QueryCoverage> Describe(
        IReadOnlyList<EvaluatedQuery> queries,
        ITextIndex index,
        BeirCorpus corpus,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(tokenizer);

        var textById = corpus.Documents.ToDictionary(document => document.Id, document => document.Text);
        var tokensById = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        HashSet<string> TokensOf(string documentId)
        {
            if (tokensById.TryGetValue(documentId, out var cached))
                return cached;

            var tokens = new HashSet<string>(StringComparer.Ordinal);

            if (textById.TryGetValue(documentId, out string? text))
            {
                var tokenized = tokenizer.Tokenize(text);

                for (int i = 0; i < tokenized.Count; i++)
                    tokens.Add(tokenized[i]);
            }

            // A judged id with no document behind it still lands in the cache, as an empty set. The
            // qrels and the corpus are separate files and disagreeing is a real condition, not a
            // malformed one; silently dropping those terms would report a query as more mismatched
            // than it is.
            tokensById[documentId] = tokens;
            return tokens;
        }

        var described = new List<QueryCoverage>(queries.Count);

        foreach (var evaluated in queries)
        {
            var queryTerms = new HashSet<string>(tokenizer.Tokenize(evaluated.Query.Text), StringComparer.Ordinal);

            double mass = 0;
            int outOfVocabulary = 0;

            foreach (string term in queryTerms)
            {
                int df = index.DocumentFrequency(term);

                if (df == 0)
                    outOfVocabulary++;

                mass += Idf(index.Count, df);
            }

            // The union, not the mean over judged documents: a query whose twenty judged documents
            // share a term collectively is lexically closer to its answer set than one where the same
            // term appears in a single document, and averaging would score those two alike.
            var judgedTerms = new HashSet<string>(StringComparer.Ordinal);

            foreach (string documentId in evaluated.Graded.Keys)
                judgedTerms.UnionWith(TokensOf(documentId));

            int covered = 0;
            double coveredMass = 0;

            foreach (string term in queryTerms)
            {
                if (!judgedTerms.Contains(term))
                    continue;

                covered++;
                coveredMass += Idf(index.Count, index.DocumentFrequency(term));
            }

            described.Add(new QueryCoverage(
                evaluated.Query.Id,
                queryTerms.Count,
                outOfVocabulary,
                mass,
                covered,
                // An empty query idf mass would divide by zero. It cannot arise from a query that
                // tokenized to nothing and was scored, but a division that silently yields NaN and
                // then lands in a TSV is worse than a stated zero.
                mass <= 0 ? 0 : coveredMass / mass));
        }

        return described;
    }

    /// <summary>Buckets a coverage share. The cut points are a reading, not a result.</summary>
    public static MismatchBand Band(double judgedIdfShare) => judgedIdfShare switch
    {
        <= 0 => MismatchBand.None,
        < 1.0 / 3.0 => MismatchBand.Low,
        < 2.0 / 3.0 => MismatchBand.Medium,
        _ => MismatchBand.High,
    };

    /// <summary>Bands of top-10 disagreement, from agreement to no shared id at all.</summary>
    public enum DisagreementBand
    {
        /// <summary>Under a fifth of the union differs — the two rankings are nearly the same list.</summary>
        Agree,

        /// <summary>A fifth to a half differs.</summary>
        Mixed,

        /// <summary>A half to four fifths differ — the rankings overlap but lead to different places.</summary>
        MostlyDisagree,

        /// <summary>Over four fifths differ, up to and including no shared id.</summary>
        Disagree,
    }

    /// <summary>Buckets a Jaccard distance. The cut points are a reading, not a result.</summary>
    public static DisagreementBand DisagreementBandOf(double distance) => distance switch
    {
        < 0.2 => DisagreementBand.Agree,
        < 0.5 => DisagreementBand.Mixed,
        < 0.8 => DisagreementBand.MostlyDisagree,
        _ => DisagreementBand.Disagree,
    };

    /// <summary>
    /// Measures how far apart two named configurations' rankings are, query by query.
    /// </summary>
    /// <param name="report">The run, whose per-configuration rankings are already in memory.</param>
    /// <param name="left">One configuration's display name, e.g. <c>"BM25 (k1=1.5, b=0.75)"</c>.</param>
    /// <param name="right">The other's, e.g. <c>"Dense multilingual-e5-small"</c>.</param>
    /// <remarks>
    /// <para>
    /// The point of the pair is that it crosses representation, not parameters: BM25 against a dense
    /// retriever is two different answers to the same question, whereas two BM25 parameterisations
    /// disagree mostly about ties. Nothing here enforces that — it is a choice made on the command
    /// line, and the summary names both configurations so the choice is readable in the output.
    /// </para>
    /// <para>
    /// Pairing a configuration with itself is refused rather than measured: the distance is zero for
    /// every query, which would print as a column of zeros and read as a finding about agreement.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<TopKDisagreement> DescribeDisagreement(
        QueryAnalysisReport report, string left, string right)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentException.ThrowIfNullOrWhiteSpace(left);
        ArgumentException.ThrowIfNullOrWhiteSpace(right);

        if (string.Equals(left, right, StringComparison.Ordinal))
            throw new ArgumentException(
                $"'{left}' is named on both sides of the disagreement pair. A configuration agrees with "
                + "itself on every query, and a column of zeros reads as a measurement.",
                nameof(right));

        var leftOutcomes = report.OutcomesOf(left);
        var rightOutcomes = report.OutcomesOf(right);

        var described = new List<TopKDisagreement>(report.Coverage.Count);

        foreach (var coverage in report.Coverage)
        {
            // Missing rather than short: every configuration in a run scores every query, and a
            // report that silently skipped the ones it could not pair would slice a table over a
            // subset nobody named.
            var leftOutcome = Find(leftOutcomes, coverage.QueryId)
                ?? throw new InvalidOperationException(
                    $"Configuration '{left}' has no outcome for query '{coverage.QueryId}', so the "
                    + "disagreement column would be short by exactly the queries that failed.");
            var rightOutcome = Find(rightOutcomes, coverage.QueryId)
                ?? throw new InvalidOperationException(
                    $"Configuration '{right}' has no outcome for query '{coverage.QueryId}', so the "
                    + "disagreement column would be short by exactly the queries that failed.");

            var leftTop = new HashSet<string>(leftOutcome.Retrieved, StringComparer.Ordinal);
            var rightTop = new HashSet<string>(rightOutcome.Retrieved, StringComparer.Ordinal);

            // The smaller set is walked: the count is symmetric and the loop is not.
            var smaller = leftTop.Count <= rightTop.Count ? leftTop : rightTop;
            var larger = ReferenceEquals(smaller, leftTop) ? rightTop : leftTop;

            int shared = 0;

            foreach (string id in smaller)
            {
                if (larger.Contains(id))
                    shared++;
            }

            int union = leftTop.Count + rightTop.Count - shared;

            described.Add(new TopKDisagreement(
                coverage.QueryId,
                shared,
                leftTop.Count - shared,
                rightTop.Count - shared,
                // Both empty is agreement about nothing, not maximal disagreement; the doc comment on
                // the record says why that convention is the readable one.
                union == 0 ? 0 : 1.0 - (double)shared / union));
        }

        return described;
    }

    /// <summary>
    /// Writes one row per query per configuration, with the paired deltas against the baseline.
    /// </summary>
    /// <remarks>
    /// Tab-separated, because the ids and query text contain spaces and quotes and a CSV of them
    /// needs an escaping convention that becomes a second thing to get wrong. Header row included:
    /// a row of twenty-odd bare numbers is not readable, and a file nobody reads is not evidence.
    /// </remarks>
    public static void Write(
        string path,
        QueryAnalysisReport report,
        string baseline,
        IReadOnlyList<TopKDisagreement>? disagreement = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        var baselineOutcomes = report.OutcomesOf(baseline);

        // Keyed rather than indexed by position: the write walks the coverage list and the distance
        // list is built from it, but a pairing that depends on two orders agreeing is a bug that
        // appears only after one of them is changed.
        var disagreementByQuery = disagreement?.ToDictionary(row => row.QueryId, StringComparer.Ordinal);

        // The depth is read from the constant rather than written into a literal, so a column headed
        // "at_10" cannot be produced by a run that captured a different depth.
        string depth = $"at_{MetricDepth.Cutoff}";

        List<string> header =
        [
            "query_id", "query_terms", "oov_terms", "idf_mass", "judged_terms_covered", "judged_idf_share",
            "band", "judged_relevant", "config", "relevant_retrieved", "recall", "ndcg",
            "baseline_relevant_retrieved", "delta_recall", "delta_ndcg", "new_relevant", "lost_relevant",
        ];

        if (disagreementByQuery is not null)
            header.AddRange([$"disagreement_{depth}", $"shared_{depth}", "left_only", "right_only"]);

        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine(string.Join('\t', header));

        // Configuration is the inner loop so a query's rows are adjacent, and the outer loop walks the
        // queries in the order the run scored them, so two runs of the same corpus line up.
        foreach (var coverage in report.Coverage)
        {
            QueryOutcome? base_ = Find(baselineOutcomes, coverage.QueryId);
            var judged = report.Judged(coverage.QueryId);

            foreach (var (name, outcomes) in report.ByConfig)
            {
                QueryOutcome? outcome = Find(outcomes, coverage.QueryId);

                if (outcome is null)
                    continue;

                (int gained, int lost) = base_ is null
                    ? (0, 0)
                    : Exchange(outcome.Retrieved, base_.Retrieved, judged);

                List<string> cells =
                [
                    coverage.QueryId,
                    I(coverage.QueryTerms),
                    I(coverage.TermsOutOfVocabulary),
                    F(coverage.IdfMass),
                    I(coverage.JudgedTermsCovered),
                    F(coverage.JudgedIdfShare),
                    Band(coverage.JudgedIdfShare).ToString(),
                    I(outcome.JudgedRelevant),
                    name,
                    I(outcome.RelevantRetrieved),
                    F(outcome.RecallAt10),
                    F(outcome.NdcgAt10),
                    base_ is null ? string.Empty : I(base_.RelevantRetrieved),
                    base_ is null ? string.Empty : F(outcome.RecallAt10 - base_.RecallAt10),
                    base_ is null ? string.Empty : F(outcome.NdcgAt10 - base_.NdcgAt10),
                    base_ is null ? string.Empty : I(gained),
                    base_ is null ? string.Empty : I(lost),
                ];

                if (disagreementByQuery is not null)
                {
                    if (!disagreementByQuery.TryGetValue(coverage.QueryId, out var row))
                        throw new InvalidOperationException(
                            $"The disagreement list has no row for query '{coverage.QueryId}', so the file "
                            + "would be short by exactly the queries that failed.");

                    cells.Add(F(row.Distance));
                    cells.Add(I(row.Shared));
                    cells.Add(I(row.LeftOnly));
                    cells.Add(I(row.RightOnly));
                }

                writer.WriteLine(string.Join('\t', cells));
            }
        }
    }

    /// <summary>
    /// Prints the mean deltas per mismatch band, which is the question the whole file was built for:
    /// does the candidate help where the theory says it should, and only there.
    /// </summary>
    /// <remarks>
    /// A band's query count is printed next to its means, and no verdict is attached to any of them.
    /// The count is what stops a band of three being read as a trend, and a table that cannot say how
    /// few rows it has is how a default that changed nothing came to be described as one that
    /// changed the ranking.
    /// </remarks>
    public static void WriteBandSummary(
        TextWriter writer, QueryAnalysisReport report, string candidate, string baseline)
    {
        ArgumentNullException.ThrowIfNull(writer);

        var (_, _, candidateOutcomes, baselineOutcomes) = report.Pair(candidate, baseline);

        writer.WriteLine();
        writer.WriteLine($"Per-mismatch-band means, '{candidate}' against '{baseline}'.");
        writer.WriteLine(
            "Bands cut on the share of a query's idf mass that its judged documents carry — not on query length.");
        writer.WriteLine(
            "new/lost are judged documents entering and leaving the top " + MetricDepth.Cutoff
            + ", per query, averaged over the band. No band here is a verdict; read the count first.");

        var bands = new[] { MismatchBand.None, MismatchBand.Low, MismatchBand.Medium, MismatchBand.High };

        string[] header = ["band", "queries", "d_recall", "d_ndcg", "new_rel", "lost_rel"];
        int[] widths = [8, 9, 11, 11, 10, 10];

        writer.WriteLine(string.Join(" ", header.Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))));
        writer.WriteLine(new string('-', widths.Sum() + (widths.Length - 1)));

        foreach (var band in bands)
        {
            int queries = 0;
            double deltaRecall = 0, deltaNdcg = 0;
            int gained = 0, lost = 0;

            foreach (var coverage in report.Coverage.Where(row => Band(row.JudgedIdfShare) == band))
            {
                QueryOutcome? a = Find(candidateOutcomes, coverage.QueryId);
                QueryOutcome? b = Find(baselineOutcomes, coverage.QueryId);

                if (a is null || b is null)
                    continue;

                (int g, int l) = Exchange(a.Retrieved, b.Retrieved, report.Judged(coverage.QueryId));

                queries++;
                deltaRecall += a.RecallAt10 - b.RecallAt10;
                deltaNdcg += a.NdcgAt10 - b.NdcgAt10;
                gained += g;
                lost += l;
            }

            string[] cells = queries == 0
                ? [band.ToString(), "0", "-", "-", "-", "-"]
                :
                [
                    band.ToString(),
                    I(queries),
                    F(deltaRecall / queries),
                    F(deltaNdcg / queries),
                    F((double)gained / queries),
                    F((double)lost / queries),
                ];

            writer.WriteLine(string.Join(" ", cells.Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))));
        }
    }

    /// <summary>
    /// Slices a candidate's per-query delta by how far apart the two named retrievers' rankings are,
    /// which is the question the disagreement columns exist for: does the signal separate the queries
    /// a second stage helps from the ones it does not.
    /// </summary>
    /// <param name="writer">Where the table goes.</param>
    /// <param name="report">The run.</param>
    /// <param name="disagreement">The distances, from <see cref="DescribeDisagreement"/>.</param>
    /// <param name="candidate">The configuration whose delta is being explained.</param>
    /// <param name="baseline">What the delta is measured against.</param>
    /// <remarks>
    /// The table asks a question about a third configuration pair than the one the distance is
    /// measured between, and that is the point: the distance is a property of two retrievers, while
    /// the delta is the effect of adding a stage on top of them. A table where the two pairs coincide
    /// (BM25 against dense, explained by BM25 against dense) would be measuring the delta against
    /// itself.
    /// </remarks>
    public static void WriteDisagreementSummary(
        TextWriter writer,
        QueryAnalysisReport report,
        IReadOnlyList<TopKDisagreement> disagreement,
        string candidate,
        string baseline)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(disagreement);

        var (_, _, candidateOutcomes, baselineOutcomes) = report.Pair(candidate, baseline);

        int identical = 0;
        int disjoint = 0;

        foreach (var row in disagreement)
        {
            if (row.Distance <= 0)
                identical++;
            else if (row.Distance >= 1)
                disjoint++;
        }

        string depth = $"top-{MetricDepth.Cutoff}";

        writer.WriteLine();
        writer.WriteLine($"Per-{depth}-disagreement-band means, '{candidate}' against '{baseline}'.");
        writer.WriteLine(
            $"Disagreement is 1 - |shared| / |union| over the ids each configuration returned at depth {MetricDepth.Cutoff}; "
            + "0 means the same ids, 1 means none in common. Two configurations that both returned nothing count as agreeing.");
        writer.WriteLine(
            $"Identical top-{MetricDepth.Cutoff} sets: {I(identical)} of {I(disagreement.Count)} queries. "
            + $"No id in common: {I(disjoint)}.");
        writer.WriteLine(
            "The second number is the one to read before the table: a distance that is 1 for most queries "
            + "bands the corpus into one row and says nothing about separation.");
        writer.WriteLine(
            "Bands are cut at 0.2 / 0.5 / 0.8 of that distance. The cut points are a reading, not a result, "
            + "and the count column is what stops a band of three being read as a trend.");

        var bands = new[]
        {
            DisagreementBand.Agree,
            DisagreementBand.Mixed,
            DisagreementBand.MostlyDisagree,
            DisagreementBand.Disagree,
        };

        string[] header = ["band", "queries", "d_recall", "d_ndcg", "new_rel", "lost_rel", "mean_dist"];
        int[] widths = [16, 9, 11, 11, 10, 10, 11];

        writer.WriteLine(string.Join(" ", header.Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))));
        writer.WriteLine(new string('-', widths.Sum() + (widths.Length - 1)));

        foreach (var band in bands)
        {
            int queries = 0;
            double deltaRecall = 0, deltaNdcg = 0, distance = 0;
            int gained = 0, lost = 0;

            foreach (var row in disagreement)
            {
                if (DisagreementBandOf(row.Distance) != band)
                    continue;

                QueryOutcome? a = Find(candidateOutcomes, row.QueryId);
                QueryOutcome? b = Find(baselineOutcomes, row.QueryId);

                if (a is null || b is null)
                    continue;

                (int g, int l) = Exchange(a.Retrieved, b.Retrieved, report.Judged(row.QueryId));

                queries++;
                distance += row.Distance;
                deltaRecall += a.RecallAt10 - b.RecallAt10;
                deltaNdcg += a.NdcgAt10 - b.NdcgAt10;
                gained += g;
                lost += l;
            }

            string[] cells = queries == 0
                ? [band.ToString(), "0", "-", "-", "-", "-", "-"]
                :
                [
                    band.ToString(),
                    I(queries),
                    F(deltaRecall / queries),
                    F(deltaNdcg / queries),
                    F((double)gained / queries),
                    F((double)lost / queries),
                    F(distance / queries),
                ];

            writer.WriteLine(string.Join(" ", cells.Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))));
        }

        // The bands are a reading; the rank correlation is the same relation without cut points. Both
        // are printed because they disagree in a readable way — a monotone trend that no band shows is
        // real, and a band table with no correlation is a shape without a direction.
        var pairs = new List<(double X, double Y)>(disagreement.Count);

        foreach (var row in disagreement)
        {
            QueryOutcome? a = Find(candidateOutcomes, row.QueryId);
            QueryOutcome? b = Find(baselineOutcomes, row.QueryId);

            if (a is not null && b is not null)
                pairs.Add((row.Distance, a.NdcgAt10 - b.NdcgAt10));
        }

        RankCorrelation correlation = Spearman(pairs);

        writer.WriteLine();

        if (correlation.Rho is { } rho)
        {
            writer.WriteLine(
                $"Spearman rank correlation between disagreement and delta_ndcg: {F(rho)} over "
                + $"{I(correlation.Pairs)} queries, average ranks for ties. Exploratory: one correlation, one corpus, "
                + "and a distance that is only measurable once both retrievers have already run.");
        }
        else
        {
            writer.WriteLine(
                $"Spearman rank correlation not computed: {correlation.Unavailable} ({I(correlation.Pairs)} "
                + "comparable queries).");
        }
    }

    /// <summary>
    /// Spearman's rank correlation, computed as Pearson's correlation of the average ranks.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The <c>1 - 6·Σd² / (n(n²-1))</c> shortcut is not used: it is exact only when neither column
    /// repeats a value, and both of these columns repeat heavily — a distance is <c>0</c> for every
    /// query where the two retrievers returned the same ids, and a delta is <c>0</c> whenever a
    /// configuration changed nothing. Average ranks for ties, then Pearson on the ranks, is the
    /// definition that survives them.
    /// </para>
    /// <para>
    /// No p-value is produced, and that is a decision rather than an omission. The queries are not a
    /// sample from a population this table describes; they are the whole test set of one corpus, and a
    /// significance test would dress a descriptive statistic as an inference.
    /// </para>
    /// </remarks>
    public static RankCorrelation Spearman(IReadOnlyList<(double X, double Y)> pairs)
    {
        ArgumentNullException.ThrowIfNull(pairs);

        // Non-finite rows are dropped rather than counted: pairs are built from metrics that are
        // always finite, so a NaN here means a caller passed something the report cannot contain, and
        // silently folding it into a rank would move every row below it.
        var xs = new List<double>(pairs.Count);
        var ys = new List<double>(pairs.Count);

        foreach (var (x, y) in pairs)
        {
            if (double.IsFinite(x) && double.IsFinite(y))
            {
                xs.Add(x);
                ys.Add(y);
            }
        }

        if (xs.Count < 3)
            return RankCorrelation.NotComputed(xs.Count, "fewer than three comparable queries");

        var xRanks = AverageRanks(xs);
        var yRanks = AverageRanks(ys);

        double meanX = 0, meanY = 0;

        for (int i = 0; i < xRanks.Length; i++)
        {
            meanX += xRanks[i];
            meanY += yRanks[i];
        }

        meanX /= xRanks.Length;
        meanY /= xRanks.Length;

        double covariance = 0, varianceX = 0, varianceY = 0;

        for (int i = 0; i < xRanks.Length; i++)
        {
            double dx = xRanks[i] - meanX;
            double dy = yRanks[i] - meanY;

            covariance += dx * dy;
            varianceX += dx * dx;
            varianceY += dy * dy;
        }

        // A constant column has no rank order to correlate with. Dividing here would produce a NaN
        // that reads as a correlation of "not a number" in a table, which is worse than saying so.
        if (varianceX <= 0 || varianceY <= 0)
            return RankCorrelation.NotComputed(xs.Count, "one of the two columns is constant");

        double rho = covariance / Math.Sqrt(varianceX * varianceY);

        return new RankCorrelation(Math.Clamp(rho, -1.0, 1.0), xs.Count, null);
    }

    /// <summary>Ranks values ascending, giving every run of equal values its mean rank.</summary>
    private static double[] AverageRanks(IReadOnlyList<double> values)
    {
        int[] order = Enumerable.Range(0, values.Count).OrderBy(index => values[index]).ToArray();
        var ranks = new double[values.Count];

        int start = 0;

        while (start < order.Length)
        {
            int end = start;

            // Compared against the run's first value, so a chain of three equal values is one run
            // rather than two overlapping pairs.
            while (end + 1 < order.Length
                && values[order[end + 1]].Equals(values[order[start]]))
            {
                end++;
            }

            double rank = (start + end) / 2.0 + 1.0;

            for (int i = start; i <= end; i++)
                ranks[order[i]] = rank;

            start = end + 1;
        }

        return ranks;
    }

    /// <summary>
    /// BM25's own idf, so the weighting matches the scorer whose rows are being sliced.
    /// </summary>
    /// <remarks>
    /// Not a plain log(N/df): the smoothed form is the one <c>Bm25Scorer</c> uses, and a different
    /// weighting would put the band cut points somewhere other than where the corpus puts them.
    /// </remarks>
    private static double Idf(int documentCount, int documentFrequency) =>
        Math.Log(1.0 + ((documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));

    /// <summary>
    /// The judged documents that entered and left the retrieved set, in either direction.
    /// </summary>
    /// <remarks>
    /// Both directions, and only judged documents. A configuration that swaps three unjudged
    /// documents for three others has changed nothing a metric can see, and a configuration that
    /// gains two judged documents and loses two has gained nothing either — the pair of counts is the
    /// only reading in which a positive <c>delta_ndcg</c> means what it appears to mean.
    /// </remarks>
    private static (int Gained, int Lost) Exchange(
        string[] candidate, string[] baseline, IReadOnlyCollection<string> judged)
    {
        var judgedSet = new HashSet<string>(judged, StringComparer.Ordinal);

        var inCandidate = new HashSet<string>(candidate, StringComparer.Ordinal);
        var inBaseline = new HashSet<string>(baseline, StringComparer.Ordinal);

        int gained = 0, lost = 0;

        foreach (string id in inCandidate)
        {
            if (judgedSet.Contains(id) && !inBaseline.Contains(id))
                gained++;
        }

        foreach (string id in inBaseline)
        {
            if (judgedSet.Contains(id) && !inCandidate.Contains(id))
                lost++;
        }

        return (gained, lost);
    }

    private static QueryOutcome? Find(IReadOnlyList<QueryOutcome> outcomes, string queryId)
    {
        for (int i = 0; i < outcomes.Count; i++)
        {
            if (string.Equals(outcomes[i].QueryId, queryId, StringComparison.Ordinal))
                return outcomes[i];
        }

        return null;
    }

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
