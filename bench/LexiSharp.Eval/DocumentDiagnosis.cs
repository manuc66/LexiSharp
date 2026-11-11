using System.Globalization;
using System.Text;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>
/// Whether a query and a document share vocabulary the index can score with — the reachability
/// half of a diagnosis, decided before any rank is looked at.
/// </summary>
/// <remarks>
/// The order of the members is the order <see cref="DocumentDiagnosis"/> tests them: conditions
/// that make every rank meaningless (exclusion, an empty query or an empty document) come first,
/// then the two flavors of « no overlap », then the ordinary reachable case.
/// </remarks>
internal enum DocumentReach
{
    /// <summary>The run's convention removes this document from this query's ranking (--exclude-query-doc).</summary>
    Excluded,

    /// <summary>The run's analysis reduces the query to zero terms — there is nothing to match with.</summary>
    QueryHasNoTerms,

    /// <summary>The run's analysis reduces the document to zero terms — there is nothing to match against.</summary>
    DocumentHasNoTerms,

    /// <summary>Every analyzed query term has df 0: the corpus never contains the vocabulary.</summary>
    VocabularyAbsent,

    /// <summary>
    /// No analyzed term in common, but the raw texts do share words and the analysis removes all of
    /// them — the gap is the analyzer, not the corpus.
    /// </summary>
    AnalysisRemovedOverlap,

    /// <summary>No analyzed term in common and no shared surface word — a pure vocabulary gap.</summary>
    NoVocabularyOverlap,

    /// <summary>At least one analyzed query term is present in the document.</summary>
    Reachable,
}

/// <summary>How many of the run's fixed configurations put the document inside the measured page.</summary>
internal enum DiagnosisOutcome
{
    /// <summary>Every probed configuration ranks it within the page — nothing is missing.</summary>
    RetrievedByAll,

    /// <summary>Some do and some do not — the « only a minority surface it » case.</summary>
    RetrievedByMinority,

    /// <summary>No configuration ranks it within the page.</summary>
    NotSurfaced,
}

/// <summary>One analyzed query term and what the document says about it.</summary>
/// <param name="DocumentFrequency">Corpus document frequency, 0 when the index never saw the term.</param>
/// <param name="InDocument">Whether the term is among the document's indexed terms.</param>
internal sealed record QueryTermFact(string Term, int DocumentFrequency, bool InDocument);

/// <summary>One configuration's answer for one query: where it ranks the document, or that it does not.</summary>
/// <param name="Rank">1-based, or <c>null</c> when the document is not among the returned results.</param>
/// <param name="ProbedDepth">How deep this row was asked — its candidate pool when it is a cascade.</param>
/// <param name="Probe">Whether the row is one of the diagnosis's counterfactuals, not a shipped configuration.</param>
internal sealed record ConfigurationRank(string Configuration, int? Rank, double Score, int ProbedDepth, bool Probe);

/// <summary>The lexical facts of one (query, document) pair, computed once and read by every section.</summary>
/// <param name="Facts">Every analyzed query term, in query order.</param>
internal sealed record LexicalReach(
    IReadOnlyList<QueryTermFact> Facts,
    int SurfaceShared,
    IReadOnlyList<string> SurfaceSharedTerms,
    IReadOnlyList<string> SurfaceLostToAnalysis,
    double IdfMass,
    double CoveredIdfMass,
    double CoveredShare,
    QueryAnalysis.MismatchBand Band)
{
    public int OutOfVocabulary => Facts.Count(fact => fact.DocumentFrequency == 0);

    /// <summary>Query terms the corpus has (df > 0) but this document does not say.</summary>
    public IReadOnlyList<QueryTermFact> MissingFromDocument =>
        Facts.Where(fact => !fact.InDocument && fact.DocumentFrequency > 0).ToList();

    public int SharedTerms => Facts.Count(fact => fact.InDocument);
}

/// <summary>What the baseline scorer says about the pair, independent of any ranking.</summary>
internal sealed record ScoreBreakdown(
    double Score,
    int DocumentLength,
    double LengthRatio,
    double LengthNormalization,
    double? PageCutScore,
    int PageResults);

/// <summary>One query's full diagnosis.</summary>
/// <param name="Relevance">The qrel grade, or <c>null</c> when the query does not judge the document.</param>
/// <param name="Missing">
/// The answer to the question the diagnosis exists for: which IR capability is missing here, each
/// entry carrying the measurement that says so. Empty only when nothing is.
/// </param>
internal sealed record QueryDiagnosis(
    string QueryId,
    string QueryText,
    double? Relevance,
    DocumentReach Reach,
    DiagnosisOutcome Outcome,
    LexicalReach Lexical,
    ScoreBreakdown Score,
    IReadOnlyList<ConfigurationRank> Ranks,
    int? BestRank,
    string? BestConfiguration,
    IReadOnlyList<string> Missing);

/// <summary>Everything one <c>--diagnose-doc</c> invocation produced for one dataset.</summary>
/// <param name="Rejection">
/// Why there is no per-query section, or <c>null</c>. A rejection is reported rather than thrown
/// so that <c>--dataset all</c> still diagnoses the corpora that do hold the document.
/// </param>
internal sealed record DocumentDiagnosisReport(
    string DocumentId,
    string? Title,
    int? IndexedTerms,
    double AverageDocumentLength,
    int ConfigurationCount,
    int ProbeDepth,
    int PageDepth,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> NotMeasured,
    string? Rejection,
    IReadOnlyList<QueryDiagnosis> Queries);

/// <summary>
/// Why one document is or is not surfaced — for one corpus, one run configuration, one query at a
/// time.
/// </summary>
/// <remarks>
/// <para>
/// The dual of the per-query analysis: that file asks « which queries does this configuration
/// miss », this asks « why does no technique — or only a minority — surface this document ». It
/// probes the exact rows <see cref="Evaluation.BuildConfigurations"/> gives the metrics table, so a
/// diagnosis and a table can never disagree about what was probed, and it adds two labelled
/// counterfactual probes (a length-free scorer, an index built without the run's analysis) because
/// « too long » and « the analyzer ate the match » are hypotheses a rank alone cannot decide.
/// </para>
/// <para>
/// Queries are read as plain analyzed terms — the same convention as the per-query analysis file —
/// so <c>x:y</c> and <c>*</c> are text here, not operators, whatever <c>--query-syntax</c> says about
/// the searches themselves. Searches do pass the run's convention through, including
/// <c>--exclude-query-doc</c>: an excluded document is reported as excluded rather than as a
/// retrieval failure, because no technique is allowed to return it.
/// </para>
/// <para>
/// Tuned rows are not probed (they are fitted on the query set — see
/// <see cref="Evaluation.BuildConfigurations"/>), and lanes the run never enabled are reported as
/// not measured rather than counted as failures: a diagnosis that called a lane it never ran
/// "missing" would be the loudest wrong answer in the file.
/// </para>
/// </remarks>
internal static class DocumentDiagnosis
{
    /// <summary>The two counterfactual rows' names, matched exactly rather than by substring.</summary>
    internal const string LengthProbeName = "BM25 (k1=1.5, b=0) [length probe]";

    internal const string AnalysisProbeName = "BM25 (no analysis) [analysis probe]";

    /// <summary>
    /// Diagnoses one document against every test query that judges it, or against the one query
    /// <paramref name="queryId"/> names.
    /// </summary>
    /// <param name="probeDepth">
    /// How deep each configuration is searched. Raised to the page depth when smaller, and capped at
    /// the corpus: a depth past the last document finds nothing extra, and a depth under the page
    /// would report a page cut the probe never reached.
    /// </param>
    public static DocumentDiagnosisReport Diagnose(
        string documentId,
        string? queryId,
        BeirCorpus corpus,
        ITextIndex index,
        ITokenizer tokenizer,
        TokenizerOptions tokenizerOptions,
        ConfigurationSet configurations,
        QueryTermWeighting queryTerms,
        int probeDepth,
        int pageDepth,
        bool parseQuerySyntax,
        bool excludeQueryDocument)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(tokenizerOptions);
        ArgumentNullException.ThrowIfNull(configurations);

        int depth = Math.Min(Math.Max(probeDepth, pageDepth), Math.Max(index.Count, 1));

        // A rejection stops before any index work: on --dataset all the corpora that do not hold
        // this document must cost nothing beyond the lookup.
        BeirDocument? document = null;

        foreach (BeirDocument candidate in corpus.Documents)
        {
            if (string.Equals(candidate.Id, documentId, StringComparison.Ordinal))
            {
                document = candidate;
                break;
            }
        }

        string? rejection = document is null
            ? $"no document with id '{documentId}' in {corpus.Name} ({corpus.Documents.Count:N0} documents)"
            : !index.Contains(documentId)
                ? $"the corpus holds '{documentId}' but the index does not — nothing to diagnose against it"
                : null;

        // The queries: the ones that judge the document, or the one the caller named. Built through
        // the run's own query list so the exclusion convention cannot diverge from the table's.
        var queries = new List<EvaluatedQuery>();

        if (rejection is null && queryId is not null)
        {
            BeirQuery? named = null;

            foreach (BeirQuery candidate in corpus.Queries)
            {
                if (string.Equals(candidate.Id, queryId, StringComparison.Ordinal))
                {
                    named = candidate;
                    break;
                }
            }

            if (named is null)
            {
                rejection = $"no query with id '{queryId}' in {corpus.Name} ({corpus.Queries.Count:N0} queries)";
            }
            else
            {
                double? relevance = null;

                if (corpus.TestRelevance.TryGetValue(named.Id, out var graded)
                    && graded.TryGetValue(documentId, out double grade))
                {
                    relevance = grade;
                }

                queries.Add(new EvaluatedQuery(named, relevance is null
                    ? new Dictionary<string, double>(StringComparer.Ordinal)
                    : new Dictionary<string, double>(StringComparer.Ordinal) { [documentId] = relevance.Value },
                    ExclusionsFor(corpus, named.Id, excludeQueryDocument)));
            }
        }
        else if (rejection is null)
        {
            foreach (EvaluatedQuery evaluated in Evaluation.BuildQueries(corpus, limit: null, excludeQueryDocument))
            {
                if (evaluated.Graded.TryGetValue(documentId, out double grade) && grade > 0)
                    queries.Add(evaluated);
            }

            if (queries.Count == 0)
            {
                rejection =
                    $"no test query judges '{documentId}' as relevant — pass --diagnose-query <id> to diagnose it "
                    + "against a specific query anyway";
            }
        }

        // The two counterfactual probes. The analysis-free index is built once per invocation and
        // only when the document and query set are real: a rejected diagnosis costs no reindexing.
        var surfaceTokenizer = SurfaceTokenizer(tokenizerOptions);

        var rows = new List<ConfigurationRow>(configurations.Configurations);

        if (rejection is null)
        {
            // The control index is the second, analysis-free reading of the same corpus: what the
            // two texts say without the run's analyzer in the way. Built per invocation rather than
            // cached, because its shape depends on the analyzer in force — a cached one would
            // outlive the flag that justified it.
            var surfaceIndex = new InMemoryTextIndex(surfaceTokenizer);
            surfaceIndex.Index(Evaluation.BuildDocuments(corpus));

            rows.Add(new ConfigurationRow(
                LengthProbeName,
                () => new RankedTextSearchEngine(index, new Bm25Scorer(1.5, 0.0, queryTerms), tokenizer),
                IsProbe: true));
            rows.Add(new ConfigurationRow(
                AnalysisProbeName,
                () => new RankedTextSearchEngine(surfaceIndex, new Bm25Scorer(1.5, 0.75, queryTerms), surfaceTokenizer),
                IsProbe: true));
        }

        // Engines are built once per row and reused across queries: a factory wraps the shared index,
        // and building one per query would rebuild the hybrid lanes' inner engines for nothing.
        var engines = new List<(ConfigurationRow Row, ITextSearchEngine Engine)>(rows.Count);

        foreach (ConfigurationRow row in rows)
            engines.Add((row, row.Factory()));

        var diagnoses = new List<QueryDiagnosis>(queries.Count);

        foreach (EvaluatedQuery evaluated in queries)
        {
            diagnoses.Add(DiagnoseQuery(
                evaluated, document!, engines, index, tokenizer, surfaceTokenizer, queryTerms,
                depth, pageDepth, parseQuerySyntax));
        }

        return new DocumentDiagnosisReport(
            documentId,
            document?.Title,
            index.Contains(documentId) ? index.DocumentLength(documentId) : null,
            index.AverageDocumentLength,
            configurations.Configurations.Count,
            depth,
            pageDepth,
            configurations.Notes,
            NotMeasured(configurations),
            rejection,
            diagnoses);
    }

    /// <summary>Prints the report: document facts first, then one block per query.</summary>
    public static void Write(TextWriter writer, DocumentDiagnosisReport report)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        if (report.Rejection is not null)
        {
            writer.WriteLine($"Diagnosis of document '{report.DocumentId}': {report.Rejection}.");
            return;
        }

        writer.WriteLine(
            $"Diagnosis of document '{report.DocumentId}'"
            + (string.IsNullOrEmpty(report.Title) ? " (no title)" : $": \"{report.Title}\""));
        writer.WriteLine(
            $"  indexed terms: {I(report.IndexedTerms!.Value)} (avgdl {F(report.AverageDocumentLength)}, "
            + $"{F(report.IndexedTerms.Value / Math.Max(report.AverageDocumentLength, 1e-9))}x); "
            + $"probed {I(report.ConfigurationCount)} configurations + 2 probes at depth {I(report.ProbeDepth)}, "
            + $"page top-{I(report.PageDepth)}; tuned rows not probed.");

        // The notes come before the NotMeasured line that points at them: a pointer that reads
        // backwards is a puzzle, not a pointer.
        foreach (string note in report.Notes)
            writer.WriteLine($"  note: {note}");

        if (report.NotMeasured.Count > 0)
            writer.WriteLine($"  not measured in this run: {string.Join("; ", report.NotMeasured)}.");

        foreach (QueryDiagnosis query in report.Queries)
            WriteQuery(writer, query, report);

        int surfaced = report.Queries.Count(query => query.Outcome != DiagnosisOutcome.NotSurfaced);

        writer.WriteLine(
            $"  summary: surfaced inside top-{I(report.PageDepth)} by at least one configuration on "
            + $"{I(surfaced)} of {I(report.Queries.Count)} query/queries.");
    }

    private static void WriteQuery(TextWriter writer, QueryDiagnosis query, DocumentDiagnosisReport report)
    {
        LexicalReach lexical = query.Lexical;
        ScoreBreakdown score = query.Score;

        writer.WriteLine();
        writer.WriteLine(
            $"-- query {query.QueryId}: \"{query.QueryText}\" "
            + (query.Relevance is { } relevance ? $"(relevance {F(relevance)})" : "(does not judge this document)")
            + " --");
        writer.WriteLine(
            $"  reach    : {query.Reach} — {I(lexical.SharedTerms)} of {I(lexical.Facts.Count)} analyzed query terms "
            + $"in the document, {I(lexical.OutOfVocabulary)} out of vocabulary");
        writer.WriteLine(
            $"  surface  : {I(lexical.SurfaceShared)} shared before analysis"
            + ListOf(lexical.SurfaceSharedTerms)
            + (lexical.SurfaceLostToAnalysis.Count > 0
                ? $"; analysis removes {I(lexical.SurfaceLostToAnalysis.Count)}{ListOf(lexical.SurfaceLostToAnalysis)}"
                : string.Empty));
        writer.WriteLine(
            $"  idf      : mass {F(lexical.IdfMass)}, this document carries {F(lexical.CoveredIdfMass)} "
            + $"(band {lexical.Band})");
        writer.WriteLine(
            $"  score    : BM25 (k1=1.5, b=0.75) = {F(score.Score)}"
            + (score.Score > 0 ? " across the shared terms" : " — no term contributes")
            + $"; length {I(score.DocumentLength)} ({F(score.LengthRatio)}x avgdl, normalization {F(score.LengthNormalization)})");

        // An excluded document was never searched for — a table of dashes would claim the opposite,
        // that every configuration was asked and none answered. Say what happened instead.
        if (query.Reach == DocumentReach.Excluded)
        {
            writer.WriteLine(
                "  ranks    : not probed — --exclude-query-doc withholds this document from every "
                + "configuration for this query.");
        }
        else
        {
            writer.WriteLine(
                $"  page     : baseline "
                + (RankOf(query, Evaluation.BaselineConfigurationName) is { } baselineRank
                    ? $"rank {I(baselineRank)}"
                    : "absent")
                + $", cut {F(score.PageCutScore ?? 0)}"
                + (score.PageResults < report.PageDepth ? $" ({I(score.PageResults)} results — page not full)" : string.Empty));

            writer.WriteLine($"  ranks    : {I(query.Ranks.Count)} rows, depth {I(report.ProbeDepth)}, page top-{I(report.PageDepth)}");

            var header = new[] { "configuration", "rank", "score" };
            int[] widths = [46, 8, 12];

            writer.WriteLine(
                "    " + string.Join(" ", header.Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))));

            foreach (ConfigurationRank rank in query.Ranks)
            {
                string[] cells =
                [
                    rank.Configuration + CappedDepth(rank, report.ProbeDepth),
                    rank.Rank is { } value ? I(value) : "–",
                    rank.Rank is null ? string.Empty : F(rank.Score),
                ];

                writer.WriteLine(
                    "    " + string.Join(" ", cells.Select((cell, i) => i == 0 ? cell.PadRight(widths[i]) : cell.PadLeft(widths[i]))));
            }

            int shipped = query.Ranks.Count(rank => !rank.Probe);
            int inPage = query.Ranks.Count(rank => !rank.Probe && rank.Rank is int value && value <= report.PageDepth);

            if (query.Ranks.Any(rank => rank.ProbedDepth < report.ProbeDepth))
                writer.WriteLine(
                    $"    † probed at its candidate pool rather than depth {I(report.ProbeDepth)} (cascade rows cannot "
                    + "reorder what their base engine never returned).");

            writer.WriteLine(
                $"  outcome  : {query.Outcome} — "
                + (query.Outcome == DiagnosisOutcome.RetrievedByAll
                    ? $"all {I(shipped)} configurations rank it inside the page"
                    : query.BestRank is { } best
                        ? $"{I(inPage)} of {I(shipped)} inside the page; best {query.BestConfiguration} at rank {I(best)}"
                        : $"no configuration returns it within depth {I(report.ProbeDepth)}"));
        }

        if (query.Missing.Count > 0)
        {
            writer.WriteLine("  missing  :");

            foreach (string entry in query.Missing)
                writer.WriteLine($"    • {entry}");
        }
    }

    /// <summary>Lanes this run never enabled — reported as not measured, never as a failure.</summary>
    private static IReadOnlyList<string> NotMeasured(ConfigurationSet configurations)
    {
        var names = configurations.Configurations.Select(row => row.Name).ToList();
        var missing = new List<string>();

        if (!names.Any(name => name.Contains("Dense", StringComparison.Ordinal)))
            missing.Add("dense semantics (pass --dense)");

        if (!names.Any(name => name.Contains("expansion", StringComparison.Ordinal)))
            missing.Add("corpus-derived query expansion (pass --expand-density)");

        if (!names.Any(name => name.Contains("CrossRerank", StringComparison.Ordinal)))
            missing.Add("cross-encoder reranking (pass --rerank)");

        if (!names.Any(name => name.StartsWith("BM25F", StringComparison.Ordinal)) && configurations.AnyTitle)
        {
            // Self-contained rather than a pointer: the reader of a diagnosis may not have the
            // table's note in front of them, and the cause is already known here.
            missing.Add(configurations.FieldStatistics
                ? "field weighting (no BM25F rows in this configuration set)"
                : "field weighting (pass an index that keeps per-field statistics — the corpus has titles)");
        }

        return missing;
    }

    /// <summary>
    /// The analysis-free reading of a pair's texts: the same options with the two rules that can
    /// remove a word undone — no stop list, no stemmer, single characters kept — which is what a
    /// reader sees when they look at the query and the document.
    /// </summary>
    /// <remarks>
    /// Shared with the analysis diff, which derives one per side: « the texts do share this word »
    /// has to mean the same thing there as it does here, or a verdict that blames the analyzer is
    /// comparing two definitions of the surface.
    /// </remarks>
    internal static Tokenizer SurfaceTokenizer(TokenizerOptions options) =>
        new(options with
        {
            RemoveStopWords = false,
            StopWords = null,
            Stemmer = null,
            KeepSingleCharTerms = true,
        });

    /// <summary>
    /// The lexical relationship of one (query, document) pair to one index: the analyzed terms with
    /// their document frequency and presence, what the two texts share before analysis, and what
    /// analysis removed from that overlap — plus the idf mass and its band.
    /// </summary>
    /// <remarks>
    /// Named once here because the document diagnosis and the analysis diff must read the same
    /// facts: a second analyzer measured any other way would be a second definition of « shared »,
    /// and the two verdicts would disagree about words rather than about analyzers.
    /// </remarks>
    internal static LexicalReach Measure(
        IReadOnlyTextIndex index,
        ITokenizer tokenizer,
        ITokenizer surfaceTokenizer,
        string queryText,
        BeirDocument document)
    {
        IReadOnlyList<string> queryTokens = tokenizer.Tokenize(queryText);
        var analyzedShared = new HashSet<string>(queryTokens, StringComparer.Ordinal);
        var documentTerms = new HashSet<string>(index.GetTerms(document.Id), StringComparer.Ordinal);
        analyzedShared.IntersectWith(documentTerms);

        var facts = new List<QueryTermFact>(queryTokens.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (string term in queryTokens)
        {
            if (!seen.Add(term))
                continue;

            facts.Add(new QueryTermFact(term, index.DocumentFrequency(term), documentTerms.Contains(term)));
        }

        // The surface probe: the same texts under a tokenizer with no stop list, no stemmer and
        // single characters kept — what a reader sees as shared words. A token that is shared here
        // but has no analyzed form in the shared set is a match the analysis discarded.
        var surfaceQuery = new HashSet<string>(surfaceTokenizer.Tokenize(queryText), StringComparer.Ordinal);
        var surfaceDocument = new HashSet<string>(surfaceTokenizer.Tokenize(document.Text), StringComparer.Ordinal);

        if (!string.IsNullOrEmpty(document.Title))
            surfaceDocument.UnionWith(surfaceTokenizer.Tokenize(document.Title));

        var surfaceShared = surfaceQuery.Intersect(surfaceDocument, StringComparer.Ordinal).ToList();
        surfaceShared.Sort(StringComparer.Ordinal);

        var surfaceLost = new List<string>();

        foreach (string token in surfaceShared)
        {
            var forms = tokenizer.Tokenize(token);

            if (!forms.Any(form => analyzedShared.Contains(form)))
                surfaceLost.Add(token);
        }

        double idfMass = 0;
        double coveredMass = 0;

        foreach (QueryTermFact fact in facts)
        {
            double idf = QueryAnalysis.Idf(index.Count, fact.DocumentFrequency);
            idfMass += idf;

            if (fact.InDocument)
                coveredMass += idf;
        }

        double coveredShare = idfMass > 0 ? coveredMass / idfMass : 0;

        return new LexicalReach(
            facts,
            surfaceShared.Count,
            surfaceShared,
            surfaceLost,
            idfMass,
            coveredMass,
            coveredShare,
            QueryAnalysis.Band(coveredShare));
    }

    /// <summary>
    /// Which condition of <see cref="DocumentReach"/> holds for the pair, in the order the enum
    /// documents: conditions that make every rank meaningless first, then the two no-overlap
    /// flavors, then the ordinary reachable case.
    /// </summary>
    /// <param name="excluded">
    /// Whether the run's convention withholds this document from this query's ranking — checked
    /// first, because no technique is allowed to return it and every rank would read as a failure.
    /// </param>
    /// <param name="documentLength">
    /// How many terms the document contributes to the index, so that an indexed document the
    /// analysis emptied is distinguishable from one the corpus never held words for.
    /// </param>
    internal static DocumentReach Reach(bool excluded, LexicalReach lexical, int documentLength)
    {
        if (excluded)
            return DocumentReach.Excluded;

        if (lexical.Facts.Count == 0)
            return DocumentReach.QueryHasNoTerms;

        if (documentLength == 0)
            return DocumentReach.DocumentHasNoTerms;

        if (lexical.Facts.All(fact => fact.DocumentFrequency == 0))
            return DocumentReach.VocabularyAbsent;

        if (lexical.SharedTerms == 0 && lexical.SurfaceShared > 0)
            return DocumentReach.AnalysisRemovedOverlap;

        if (lexical.SharedTerms == 0)
            return DocumentReach.NoVocabularyOverlap;

        return DocumentReach.Reachable;
    }

    private static QueryDiagnosis DiagnoseQuery(
        EvaluatedQuery evaluated,
        BeirDocument document,
        IReadOnlyList<(ConfigurationRow Row, ITextSearchEngine Engine)> engines,
        ITextIndex index,
        ITokenizer tokenizer,
        ITokenizer surfaceTokenizer,
        QueryTermWeighting queryTerms,
        int depth,
        int pageDepth,
        bool parseQuerySyntax)
    {
        string documentId = document.Id;
        string queryText = evaluated.Query.Text;

        LexicalReach lexical = Measure(index, tokenizer, surfaceTokenizer, queryText, document);
        IReadOnlyList<string> queryTokens = tokenizer.Tokenize(queryText);

        // The baseline score, from the scorer itself rather than from a search: the document may be
        // absent from every result set and still have arithmetic worth reporting.
        var baselineScorer = new Bm25Scorer(1.5, 0.75, queryTerms);
        ScoreExplanation explanation = baselineScorer.Explain(documentId, queryTokens, index);

        // Ranks, one search per row. A cascade row is capped at its candidate pool: asking a rerank
        // row for more candidates than it ships with measures a row no table reports.
        var ranks = new List<ConfigurationRank>(engines.Count);
        IReadOnlyList<SearchResult>? baselineResults = null;
        bool excluded = evaluated.Excluded?.Contains(documentId) ?? false;
        int? lengthProbeRank = null;
        int? analysisProbeRank = null;

        foreach ((ConfigurationRow row, ITextSearchEngine engine) in engines)
        {
            int limit = Math.Min(depth, row.CandidatePool ?? depth);
            int? rank = null;
            double score = 0;

            if (!excluded)
            {
                IReadOnlyList<SearchResult> results = engine.Search(
                    queryText,
                    new SearchOptions(limit, ExcludedDocumentIds: evaluated.Excluded, ParseQuerySyntax: parseQuerySyntax));

                if (string.Equals(row.Name, Evaluation.BaselineConfigurationName, StringComparison.Ordinal))
                    baselineResults = results;

                for (int i = 0; i < results.Count; i++)
                {
                    if (string.Equals(results[i].DocumentId, documentId, StringComparison.Ordinal))
                    {
                        rank = i + 1;
                        score = results[i].Score;
                        break;
                    }
                }
            }

            ranks.Add(new ConfigurationRank(row.Name, rank, score, limit, row.IsProbe));

            if (row.IsProbe && string.Equals(row.Name, LengthProbeName, StringComparison.Ordinal))
                lengthProbeRank = rank;
            else if (row.IsProbe && string.Equals(row.Name, AnalysisProbeName, StringComparison.Ordinal))
                analysisProbeRank = rank;
        }

        // The page cut is the baseline's score at the last page slot: the number a buried document
        // would have had to reach. Null when the baseline returned less than a page — then the
        // document's absence says something stronger than any gap could.
        double? pageCut = null;
        int pageResults = baselineResults?.Count ?? 0;

        if (baselineResults is { Count: > 0 })
            pageCut = baselineResults[Math.Min(pageDepth, baselineResults.Count) - 1].Score;

        var scoreBreakdown = new ScoreBreakdown(
            explanation.TotalScore,
            explanation.DocumentLength,
            explanation.LengthRatio,
            explanation.LengthNormalization,
            pageCut,
            pageResults);

        // The reach verdict, from the shared definition: conditions that make every rank
        // meaningless come first, then the two no-overlap flavors, then the reachable case.
        DocumentReach reach = Reach(excluded, lexical, explanation.DocumentLength);

        // The outcome counts the run's own rows only: a probe measures a hypothesis, and counting it
        // as « a technique that retrieved it » would claim a shipped configuration that does not exist.
        var probed = ranks.Where(rank => !rank.Probe).ToList();
        var surfaced = probed.Where(rank => rank.Rank is int value && value <= pageDepth).ToList();

        DiagnosisOutcome outcome =
            probed.Count > 0 && surfaced.Count == probed.Count ? DiagnosisOutcome.RetrievedByAll
            : surfaced.Count > 0 ? DiagnosisOutcome.RetrievedByMinority
            : DiagnosisOutcome.NotSurfaced;

        ConfigurationRank? best = probed
            .Where(rank => rank.Rank is not null)
            .OrderBy(rank => rank.Rank)
            .ThenBy(rank => rank.Configuration, StringComparer.Ordinal)
            .FirstOrDefault();

        var missing = BuildMissing(
            reach,
            outcome,
            lexical,
            scoreBreakdown,
            probed,
            surfaced,
            best,
            lengthProbeRank,
            analysisProbeRank,
            pageDepth,
            depth,
            baselineRank: RankOf(ranks, Evaluation.BaselineConfigurationName));

        return new QueryDiagnosis(
            evaluated.Query.Id,
            queryText,
            evaluated.Graded.TryGetValue(documentId, out double grade) ? grade : null,
            reach,
            outcome,
            lexical,
            scoreBreakdown,
            ranks,
            best?.Rank,
            best?.Configuration,
            missing);
    }

    /// <summary>
    /// The capability statements, each one carrying the measurement that asserts it. Ordered from
    /// the cause that makes every rank meaningless down to the fine-grained ranking evidence.
    /// </summary>
    // The rules are deliberately evidence-first: an entry that names a capability without naming a
    // row, a rank or an idf number would be advice, and advice is what a reader cannot check.
    private static IReadOnlyList<string> BuildMissing(
        DocumentReach reach,
        DiagnosisOutcome outcome,
        LexicalReach lexical,
        ScoreBreakdown score,
        IReadOnlyList<ConfigurationRank> probed,
        IReadOnlyList<ConfigurationRank> surfaced,
        ConfigurationRank? best,
        int? lengthProbeRank,
        int? analysisProbeRank,
        int pageDepth,
        int depth,
        int? baselineRank)
    {
        if (outcome == DiagnosisOutcome.RetrievedByAll)
            return [];

        var missing = new List<string>();
        bool page = baselineRank is null || baselineRank > pageDepth;

        switch (reach)
        {
            case DocumentReach.Excluded:
                missing.Add(
                    "nothing to recover within retrieval: --exclude-query-doc removes this document from this "
                    + "query's ranking by construction (the query id is the document id); drop the flag to let any "
                    + "technique return it.");
                break;

            case DocumentReach.QueryHasNoTerms:
                missing.Add(
                    "an analysis that keeps the query's words: the run's analysis leaves the query with no terms at "
                    + "all, so every technique receives an empty query. Change the analyzer (--analyzer) or the query.");
                break;

            case DocumentReach.DocumentHasNoTerms:
                missing.Add(
                    $"an analysis that keeps the document's words: it contributes no indexed terms, so no technique "
                    + "can match against it. Reindex with a laxer analyzer, or store the text where it survives.");
                break;

            case DocumentReach.VocabularyAbsent:
                missing.Add(
                    "corpus content: the vocabulary the query asks with is absent from the corpus ("
                    + string.Join(", ", lexical.Facts.Select(fact => $"{fact.Term} (df 0)"))
                    + "). No technique over this corpus — lexical, corpus-trained or fused — can match words it "
                    + "never held; what recovers it is outside retrieval: corpus growth, a synonym/translation "
                    + "resource, or a query in the corpus's own words.");
                break;

            case DocumentReach.AnalysisRemovedOverlap:
                missing.Add(
                    $"a laxer analysis: the texts share {lexical.SurfaceShared} surface word(s)"
                    + ListOf(lexical.SurfaceLostToAnalysis)
                    + " and the run's analysis removes them all"
                    + (analysisProbeRank is { } controlRank && controlRank <= pageDepth
                        ? $" — the {AnalysisProbeName} ranks it {controlRank} inside the page, so keeping those words "
                          + "would surface it (analyzer rules, not ranking, are what is missing here)."
                        : analysisProbeRank is { } deepControlRank
                            ? $" — and even the analysis-free control only reaches rank {deepControlRank} at depth "
                              + $"{depth}, so those words alone do not carry it either: the gap stays vocabulary "
                              + "(expansion, synonyms, paraphrase) even before ranking."
                            : " — and the analysis-free control does not return it within the probe depth either."));
                break;

            case DocumentReach.NoVocabularyOverlap:
                missing.Add(
                    "a vocabulary bridge: no analyzed term is in common, so every term-overlap scorer scores 0 and "
                    + "never enumerates this document. What closes a gap like this is association or semantics — "
                    + "corpus-derived expansion, synonyms, paraphrase, or a dense encoder.");
                break;
        }

        if (reach != DocumentReach.Reachable)
            return missing;

        // Length: the b=0 probe is the controlled experiment — same scorer, same index, only the
        // normalization removed, so a rank it reaches and the baseline does not is the
        // normalization's doing and nothing else's.
        if (page && lengthProbeRank is { } probeRank && probeRank <= pageDepth)
        {
            missing.Add(
                $"length normalization: {LengthProbeName} ranks it {probeRank} within the page where "
                + $"'{Evaluation.BaselineConfigurationName}' does not — the document is {F(score.LengthRatio)}x "
                + $"the average length, and normalization divides every term frequency by "
                + $"{F(score.LengthNormalization)}. A lower b, a length-free scorer (BM25L, BM25VA, spread), "
                + "passage splitting, or field weighting would surface it.");
        }

        if (outcome == DiagnosisOutcome.RetrievedByMinority)
        {
            // The minority question, answered from both sides: which capabilities recover it, and
            // which ones the rows that miss it lack.
            if (page)
            {
                var recovering = surfaced
                    .Select(rank => (Capability: CapabilityOf(rank.Configuration), rank))
                    .GroupBy(pair => pair.Capability, StringComparer.Ordinal)
                    .Select(group => group.First())
                    .ToList();

                if (recovering.Count > 0)
                {
                    missing.Add(
                        "recovers it: "
                        + string.Join(", ", recovering.Select(pair =>
                            $"{pair.Capability} ('{pair.rank.Configuration}' at rank {pair.rank.Rank})"))
                        + $" — '{Evaluation.BaselineConfigurationName}' (rank {baselineRank?.ToString(CultureInfo.InvariantCulture) ?? "absent"}) does not.");
                }
            }

            var failing = probed
                .Where(rank => rank.Rank is null || rank.Rank > pageDepth)
                .ToList();

            if (failing.Count > 0)
            {
                missing.Add(
                    "missed by "
                    + string.Join(", ", failing
                        .Select(rank => CapabilityOf(rank.Configuration))
                        .Distinct(StringComparer.Ordinal))
                    + $" ({failing.Count} of {probed.Count} rows leave it outside the top-{pageDepth}: "
                    + string.Join(", ", failing
                        .Select(rank => rank.Rank is { } value
                            ? $"{rank.Configuration} stops at {value}"
                            : $"{rank.Configuration} never returns it")
                        .Take(4))
                    + (failing.Count > 4 ? ", …" : string.Empty)
                    + ").");
            }
        }

        if (outcome == DiagnosisOutcome.NotSurfaced)
        {
            if (best is not null)
            {
                missing.Add(
                    $"depth versus score: '{best.Configuration}' is the closest at rank {best.Rank} of {depth} with "
                    + $"score {F(best.Score)}"
                    + (score.PageCutScore is { } cut ? $", against a top-{pageDepth} cut of {F(cut)}" : string.Empty)
                    + " — reachable and scored, so what is missing is a stronger signal for this query "
                    + "(reranking, fusion over deeper candidates, better-weighted fields), not vocabulary.");
            }
            else
            {
                missing.Add(
                    $"score: no shipped configuration returns it within depth {depth} although "
                    + $"{lexical.SharedTerms} analyzed term(s) are in common — the shared terms carry too little "
                    + "weight to enter the result set.");
            }

            if (lexical.Band != QueryAnalysis.MismatchBand.High && lexical.MissingFromDocument.Count > 0)
            {
                missing.Add(
                    $"discriminating terms: the document carries {F(lexical.CoveredShare)} of the query's idf mass "
                    + $"(band {lexical.Band}) and does not say "
                    + string.Join(", ", lexical.MissingFromDocument.Select(fact => $"{fact.Term} (df {fact.DocumentFrequency})"))
                    + " — expansion, field weighting if the title says them, or paraphrase-aware matching would have "
                    + "to supply them.");
            }
        }

        return missing;
    }

    private static string CapabilityOf(string configuration)
    {
        // Order matters: a cascade row is named for its base engine ("Hybrid RRF (top100)+CrossRerank")
        // and would otherwise be filed under fusion.
        if (configuration.Contains("CrossRerank", StringComparison.Ordinal))
            return "cross-encoder reranking";

        if (configuration.Contains("BM25F", StringComparison.Ordinal))
            return "field weighting (title)";

        if (configuration.Contains("expansion", StringComparison.Ordinal))
            return "corpus-derived query expansion";

        if (configuration.Contains("reweight", StringComparison.Ordinal))
            return "query-term reweighting";

        if (configuration.Contains("spread", StringComparison.Ordinal))
            return "spread term frequency";

        if (configuration.Contains("proximity", StringComparison.Ordinal))
            return "proximity";

        if (configuration.Contains("BM25VA", StringComparison.Ordinal))
            return "verboseness normalization";

        if (configuration.Contains("Dense", StringComparison.Ordinal))
            return configuration.StartsWith("Hybrid", StringComparison.Ordinal)
                ? "fusion with a dense lane"
                : "dense semantics";

        if (configuration.StartsWith("Hybrid", StringComparison.Ordinal))
            return "score/rank fusion";

        if (configuration.Contains("BM25+", StringComparison.Ordinal) || configuration.Contains("BM25L", StringComparison.Ordinal))
            return "lower-bounded term frequency (BM25+/BM25L)";

        if (configuration.Contains("b=0)", StringComparison.Ordinal))
            return "length-free scoring";

        return "lexical scoring (BM25 family)";
    }

    private static int? RankOf(QueryDiagnosis query, string configuration)
    {
        foreach (ConfigurationRank rank in query.Ranks)
        {
            if (string.Equals(rank.Configuration, configuration, StringComparison.Ordinal))
                return rank.Rank;
        }

        return null;
    }

    private static int? RankOf(IReadOnlyList<ConfigurationRank> ranks, string configuration)
    {
        foreach (ConfigurationRank rank in ranks)
        {
            if (string.Equals(rank.Configuration, configuration, StringComparison.Ordinal))
                return rank.Rank;
        }

        return null;
    }

    /// <summary>The exclusions this query runs under, mirroring <see cref="Evaluation.BuildQueries"/>.</summary>
    private static IReadOnlySet<string>? ExclusionsFor(BeirCorpus corpus, string queryId, bool excludeQueryDocument)
    {
        if (!excludeQueryDocument)
            return null;

        foreach (BeirDocument document in corpus.Documents)
        {
            if (string.Equals(document.Id, queryId, StringComparison.Ordinal))
                return new HashSet<string>(StringComparer.Ordinal) { queryId };
        }

        return null;
    }

    /// <summary>A capped inline list: queries are short, but a runaway tokenization must not print a wall.</summary>
    private static string ListOf(IReadOnlyList<string> items)
    {
        const int cap = 12;

        if (items.Count == 0)
            return " []";

        return " ["
            + string.Join(", ", items.Take(cap))
            + (items.Count > cap ? $", … (+{items.Count - cap})" : string.Empty)
            + "]";
    }

    /// <summary>Marks a row that probed at its candidate pool rather than at the run's depth.</summary>
    private static string CappedDepth(ConfigurationRank rank, int depth) =>
        rank.ProbedDepth < depth ? "†" : string.Empty;

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string I(int value) => value.ToString(CultureInfo.InvariantCulture);
}
