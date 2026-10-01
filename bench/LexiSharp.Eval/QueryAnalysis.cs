using System.Globalization;
using System.Text;
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
        InMemoryTextIndex index,
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

    /// <summary>
    /// Writes one row per query per configuration, with the paired deltas against the baseline.
    /// </summary>
    /// <remarks>
    /// Tab-separated, because the ids and query text contain spaces and quotes and a CSV of them
    /// needs an escaping convention that becomes a second thing to get wrong. Header row included:
    /// twenty columns of bare numbers is not readable, and a file nobody reads is not evidence.
    /// </remarks>
    public static void Write(string path, QueryAnalysisReport report, string baseline)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(report);

        var baselineOutcomes = report.OutcomesOf(baseline);

        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        writer.WriteLine(string.Join('\t', new[]
        {
            "query_id", "query_terms", "oov_terms", "idf_mass", "judged_terms_covered", "judged_idf_share",
            "band", "judged_relevant", "config", "relevant_retrieved", "recall", "ndcg",
            "baseline_relevant_retrieved", "delta_recall", "delta_ndcg", "new_relevant", "lost_relevant",
        }));

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

                writer.WriteLine(string.Join('\t', new[]
                {
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
                }));
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
