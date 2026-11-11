using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Eval;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The document diagnosis behind <c>--diagnose-doc</c>. These decide whether a verdict can be
/// believed: a reach order that files a reachable document against the analyzer, an outcome that
/// counts a counterfactual probe as a technique, or a cascade row probed deeper than its pool each
/// produce a confident wrong answer — and a confident wrong answer is the whole product here.
/// </summary>
/// <remarks>
/// The two counterfactual rows (the <c>b=0</c> length probe and the analysis-free control index)
/// are built by the code under test, so the rows these tests supply are stubs wherever a rank has
/// to be forced: what is pinned is the counting, the ordering and the wording, not a score a stub
/// would have to fake. Where a verdict rests on real scoring, the corpus is written small enough
/// that the arithmetic is checkable by hand.
/// </remarks>
public class DocumentDiagnosisTests
{
    [Fact]
    public void AnExcludedDocumentIsReportedAsExcludedRatherThanAsARetrievalFailure()
    {
        // --exclude-query-doc removes the id from every configuration's ranking, so every rank
        // would be null and a table of dashes would read as "no technique could find it". The
        // exclusion is the cause, and no search is even launched for it.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("q1", "", "alpha beta")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "q1"),
        };
        ITextIndex index = Index(corpus);

        DocumentDiagnosisReport report = Diagnose(
            corpus, index, "q1", queryId: "q1", excludeQueryDocument: true);
        QueryDiagnosis query = report.Queries.Single();

        // The document is reachable (it says "alpha", the query asks for it), so this only passes
        // if the exclusion is tested before every other condition.
        Assert.Equal(DocumentReach.Excluded, query.Reach);
        Assert.Equal(DiagnosisOutcome.NotSurfaced, query.Outcome);
        Assert.All(query.Ranks, rank => Assert.True(rank.Rank is null));
        Assert.Single(query.Missing);
        Assert.Contains("--exclude-query-doc", query.Missing[0]);
        Assert.Contains("not probed — --exclude-query-doc", Write(report));
    }

    [Fact]
    public void AQueryTheAnalysisReducesToNothingIsFiledAsAnEmptyQueryNotAsAVocabularyGap()
    {
        // "x" is a single character and the analyzer drops it, so the query reaches every engine
        // as an empty string. Filing that as a vocabulary gap would blame the corpus for a query
        // that never asked anything.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha beta")],
            Queries = [new BeirQuery("q1", "x")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        QueryDiagnosis query = Diagnose(corpus, index, "d1", queryId: "q1").Queries.Single();

        Assert.Equal(DocumentReach.QueryHasNoTerms, query.Reach);
        Assert.Single(query.Missing);
        Assert.Contains("the run's analysis leaves the query with no terms", query.Missing[0]);
    }

    [Fact]
    public void ADocumentTheAnalysisReducesToNothingCannotBeMatchedAgainst()
    {
        // d1 carries one word and the analyzer drops it, so the index holds a document with no
        // terms — reachable from the corpus, absent from the postings. The sibling document is
        // there so the query's term has df > 0: without it, VocabularyAbsent would be the
        // competing verdict and the order of the two would go unpinned.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "x"), new BeirDocument("d2", "", "alpha")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        // The premise the verdict reads: the document is in the index and contributes no terms.
        Assert.True(index.Contains("d1"));
        Assert.Equal(0, index.DocumentLength("d1"));

        QueryDiagnosis query = Diagnose(corpus, index, "d1", queryId: "q1").Queries.Single();

        Assert.Equal(DocumentReach.DocumentHasNoTerms, query.Reach);
        Assert.Single(query.Missing);
        Assert.Contains("it contributes no indexed terms", query.Missing[0]);
    }

    [Fact]
    public void AVocabularyTheCorpusNeverHeldIsFiledAsCorpusContentNotAsAScoringMiss()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "zulu"), new BeirDocument("d2", "", "delta")],
            Queries = [new BeirQuery("q1", "alphax")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        QueryDiagnosis query = Diagnose(corpus, index, "d1", queryId: "q1").Queries.Single();

        Assert.Equal(DocumentReach.VocabularyAbsent, query.Reach);
        Assert.Equal(1, query.Lexical.OutOfVocabulary);
        Assert.Single(query.Missing);
        Assert.Contains("corpus content", query.Missing[0]);
        Assert.Contains("alphax (df 0)", query.Missing[0]);
    }

    [Fact]
    public void ASurfaceWordTheAnalysisAteIsFiledAgainstTheAnalyzerNotTheCorpus()
    {
        // The two texts share the word "a", and the analyzer drops every single character, so the
        // analyzed intersection is empty while a reader looking at the texts sees a word in
        // common. That difference is what this verdict exists to name — and the control index is
        // what says whether keeping the word would have been enough.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "zulu a"), new BeirDocument("d2", "", "alpha")],
            Queries = [new BeirQuery("q1", "alpha a")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        QueryDiagnosis query = Diagnose(corpus, index, "d1", queryId: "q1").Queries.Single();

        Assert.Equal(DocumentReach.AnalysisRemovedOverlap, query.Reach);
        Assert.Equal(1, query.Lexical.SurfaceShared);
        Assert.Single(query.Lexical.SurfaceLostToAnalysis);
        Assert.Equal("a", query.Lexical.SurfaceLostToAnalysis[0]);
        Assert.Equal(0, query.Lexical.SharedTerms);
        Assert.Single(query.Missing);
        Assert.Contains("a laxer analysis", query.Missing[0]);
        Assert.Contains("the BM25 (no analysis) [analysis probe]", query.Missing[0]);
    }

    [Fact]
    public void NoSharedWordAtAllIsFiledAsAVocabularyGap()
    {
        // The sibling document gives the query's term df > 0, so VocabularyAbsent cannot claim it;
        // nothing else is left except the pure gap.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "zulu"), new BeirDocument("d2", "", "alpha beta")],
            Queries = [new BeirQuery("q1", "beta")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        QueryDiagnosis query = Diagnose(corpus, index, "d1", queryId: "q1").Queries.Single();

        Assert.Equal(DocumentReach.NoVocabularyOverlap, query.Reach);
        Assert.Equal(0, query.Lexical.SurfaceShared);
        Assert.Single(query.Missing);
        Assert.Contains("vocabulary bridge", query.Missing[0]);
    }

    [Fact]
    public void ASharedAnalyzedTermMakesTheDocumentReachable()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha zulu"), new BeirDocument("d2", "", "beta")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        QueryDiagnosis query = Diagnose(corpus, index, "d1", queryId: "q1").Queries.Single();

        Assert.Equal(DocumentReach.Reachable, query.Reach);
        Assert.Equal(1, query.Lexical.SharedTerms);
        Assert.Equal(1.0, query.Lexical.CoveredShare, 10);
        Assert.Equal(QueryAnalysis.MismatchBand.High, query.Lexical.Band);
    }

    [Fact]
    public void CounterfactualProbesAreNeverCountedAsTechniquesThatRetrievedTheDocument()
    {
        // Four rows are probed, two of them shipped and two of them the diagnosis's own
        // counterfactuals — and the control index does find the document inside the page. The
        // outcome must still be read over the two shipped rows alone: a probe is a hypothesis, and
        // "2 of 4 retrieved it" would claim two configurations this harness has never run.
        (BeirCorpus corpus, ITextIndex index) = RankingFixture(
            ("d1", "", "alpha"),
            ("d2", "", "alpha alpha beta gamma delta"));
        var stubs = new ConfigurationSet(
            [
                new ConfigurationRow(Evaluation.BaselineConfigurationName, () => new StubEngine(("d2", 5.0))),
                new ConfigurationRow("BM25+ (delta=1.0)", () => new StubEngine(("d1", 9.0), ("d2", 4.0))),
            ],
            Notes: [], AnyTitle: false, FieldStatistics: false);

        DocumentDiagnosisReport report = Diagnose(
            corpus, index, "d1", queryId: "q1", pageDepth: 1, configurations: stubs);
        QueryDiagnosis query = report.Queries.Single();

        Assert.Equal(4, query.Ranks.Count);
        Assert.Equal(2, query.Ranks.Count(rank => rank.Probe));
        Assert.Equal(DiagnosisOutcome.RetrievedByMinority, query.Outcome);
        Assert.Equal(1, query.BestRank);
        Assert.Equal("BM25+ (delta=1.0)", query.BestConfiguration);

        // The control index put it inside the top-1 — the fact the count must not absorb.
        Assert.Contains(query.Ranks, rank => rank.Probe && rank.Rank is int value && value <= 1);
        Assert.Contains(query.Missing, entry => entry.Contains("1 of 2 rows leave it outside the top-1"));

        // The length probe misses it here (without normalization the two-alphabet document's term
        // frequency wins), so no length verdict is claimed on a rank that does not support one.
        Assert.DoesNotContain(query.Missing, entry => entry.StartsWith("length normalization"));
    }

    [Fact]
    public void TheBaselineRowIsNeverFiledAsLengthFree()
    {
        // The regression the "b=0)" match exists for: the shipped baseline is b=0.75, and a
        // substring test on "b=0" would file it as a length-free scorer in the very line that is
        // supposed to say which capabilities missed the document.
        (BeirCorpus corpus, ITextIndex index) = RankingFixture(
            ("d1", "", "alpha"),
            ("d2", "", "alpha alpha beta gamma delta"));
        var stubs = new ConfigurationSet(
            [
                new ConfigurationRow(Evaluation.BaselineConfigurationName, () => new StubEngine(("d2", 5.0))),
                new ConfigurationRow("BM25+ (delta=1.0)", () => new StubEngine(("d1", 9.0), ("d2", 4.0))),
            ],
            Notes: [], AnyTitle: false, FieldStatistics: false);

        QueryDiagnosis query = Diagnose(
            corpus, index, "d1", queryId: "q1", pageDepth: 1, configurations: stubs).Queries.Single();

        string missedBy = query.Missing.Single(entry => entry.StartsWith("missed by", StringComparison.Ordinal));

        Assert.Contains("lexical scoring (BM25 family)", missedBy);
        Assert.DoesNotContain("length-free", missedBy);
        Assert.Contains("BM25 (k1=1.5, b=0.75) never returns it", missedBy);

        string recovers = query.Missing.Single(entry => entry.StartsWith("recovers it", StringComparison.Ordinal));

        Assert.Contains("lower-bounded term frequency (BM25+/BM25L)", recovers);
        Assert.Contains("'BM25 (k1=1.5, b=0.75)' (rank absent) does not", recovers);
    }

    [Fact]
    public void WhenEveryShippedConfigurationSurfacesItNothingIsReportedAsMissing()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha"), new BeirDocument("d2", "", "beta")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);
        var configurations = new ConfigurationSet(
            [
                new ConfigurationRow(Evaluation.BaselineConfigurationName, () => new StubEngine(("d1", 2.0), ("d2", 1.0))),
                new ConfigurationRow("TF-IDF", () => new StubEngine(("d1", 2.0), ("d2", 1.0))),
            ],
            Notes: [], AnyTitle: false, FieldStatistics: false);

        QueryDiagnosis query = Diagnose(
            corpus, index, "d1", queryId: "q1", configurations: configurations).Queries.Single();

        // The probes are in the row list whatever they found; the verdict is empty because every
        // shipped row ranks it inside the page, and it stays empty.
        Assert.Equal(4, query.Ranks.Count);
        Assert.Equal(DiagnosisOutcome.RetrievedByAll, query.Outcome);
        Assert.Empty(query.Missing);
    }

    [Fact]
    public void ACascadeRowIsProbedAtItsCandidatePoolAndTheDepthIsCappedAtTheCorpus()
    {
        // Asking a rerank row for a thousand candidates measures a row no table reports, and a
        // depth past the last document finds nothing extra. Both bounds are what the † in the
        // output promises the reader.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents =
            [
                new BeirDocument("d1", "", "alpha"),
                new BeirDocument("d2", "", "beta"),
                new BeirDocument("d3", "", "gamma"),
            ],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);
        var configurations = new ConfigurationSet(
            [
                new ConfigurationRow("pooled row", () => new StubEngine(("d1", 3.0)), CandidatePool: 1),
                new ConfigurationRow("over-pooled row", () => new StubEngine(("d1", 3.0)), CandidatePool: 1000),
                new ConfigurationRow("unpooled row", () => new StubEngine(("d1", 3.0))),
            ],
            Notes: [], AnyTitle: false, FieldStatistics: false);

        DocumentDiagnosisReport report = Diagnose(
            corpus, index, "d1", queryId: "q1", probeDepth: 1000, pageDepth: 1, configurations: configurations);
        QueryDiagnosis query = report.Queries.Single();

        Assert.Equal(3, report.ProbeDepth);
        Assert.Equal(1, query.Ranks.Single(rank => rank.Configuration == "pooled row").ProbedDepth);
        Assert.Equal(3, query.Ranks.Single(rank => rank.Configuration == "over-pooled row").ProbedDepth);
        Assert.Equal(3, query.Ranks.Single(rank => rank.Configuration == "unpooled row").ProbedDepth);
        Assert.Contains("† probed at its candidate pool", Write(report));
    }

    [Fact]
    public void AnUnknownDocumentIsRejectedInsteadOfReportedWithAnEmptyDiagnosis()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        DocumentDiagnosisReport report = Diagnose(corpus, index, "nope");

        Assert.NotNull(report.Rejection);
        Assert.Empty(report.Queries);
        Assert.Contains("Diagnosis of document 'nope': no document with id 'nope'", Write(report));
    }

    [Fact]
    public void ADocumentTheCorpusHoldsButTheIndexDoesNotSaysSoRatherThanProbingNothing()
    {
        // The two files can disagree, and on --dataset all a corpus without the document must cost
        // nothing beyond the lookup: an empty report would read as "diagnosed, nothing found".
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha"), new BeirDocument("d2", "", "beta")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);
        index.Remove("d1");

        DocumentDiagnosisReport report = Diagnose(corpus, index, "d1", queryId: "q1");

        Assert.NotNull(report.Rejection);
        Assert.Empty(report.Queries);
        Assert.Contains("the corpus holds 'd1' but the index does not", Write(report));
    }

    [Fact]
    public void ADocumentNoQueryJudgesIsRejectedWithAPointerToTheQueryFlag()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha"), new BeirDocument("d2", "", "alpha beta")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double>(StringComparer.Ordinal) { ["d2"] = 1.0 },
            },
        };
        ITextIndex index = Index(corpus);

        DocumentDiagnosisReport report = Diagnose(corpus, index, "d1");

        Assert.NotNull(report.Rejection);
        Assert.Empty(report.Queries);
        Assert.Contains("no test query judges 'd1' as relevant", Write(report));
        Assert.Contains("--diagnose-query", Write(report));
    }

    [Fact]
    public void AnUnknownQueryIdIsRejectedByName()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        DocumentDiagnosisReport report = Diagnose(corpus, index, "d1", queryId: "qX");

        Assert.NotNull(report.Rejection);
        Assert.Empty(report.Queries);
        Assert.Contains("no query with id 'qX'", Write(report));
    }

    [Fact]
    public void AQueryThatDoesNotJudgeTheDocumentIsDiagnosedAndSaysSo()
    {
        // "Why does MY query not return it" is the ordinary way to ask, and a query that passes no
        // qrel for this document must not read as a judged miss.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "", "alpha"), new BeirDocument("d2", "", "alpha beta")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double>(StringComparer.Ordinal) { ["d2"] = 1.0 },
            },
        };
        ITextIndex index = Index(corpus);

        DocumentDiagnosisReport report = Diagnose(corpus, index, "d1", queryId: "q1");
        QueryDiagnosis query = report.Queries.Single();

        Assert.Null(query.Relevance);
        Assert.Contains("(does not judge this document)", Write(report));
    }

    [Fact]
    public void LanesTheRunNeverEnabledAreReportedAsNotMeasuredRatherThanAsFailures()
    {
        // A diagnosis that called a lane it never ran "missing" would be the loudest wrong answer
        // in the file: the reader would go looking for a defect in a configuration that was never
        // asked for.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "Alpha title", "alpha")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);
        var configurations = new ConfigurationSet(
            [
                new ConfigurationRow(Evaluation.BaselineConfigurationName, () => new StubEngine(("d1", 1.0))),
                new ConfigurationRow("TF-IDF", () => new StubEngine(("d1", 1.0))),
            ],
            Notes: [], AnyTitle: true, FieldStatistics: false);

        DocumentDiagnosisReport report = Diagnose(
            corpus, index, "d1", queryId: "q1", configurations: configurations);

        Assert.Equal(4, report.NotMeasured.Count);
        Assert.Contains(report.NotMeasured, entry => entry.StartsWith("dense semantics", StringComparison.Ordinal));
        Assert.Contains(report.NotMeasured, entry => entry.StartsWith("corpus-derived query expansion", StringComparison.Ordinal));
        Assert.Contains(report.NotMeasured, entry => entry.StartsWith("cross-encoder reranking", StringComparison.Ordinal));
        Assert.Contains(report.NotMeasured, entry => entry.StartsWith("field weighting (pass an index", StringComparison.Ordinal));
        Assert.Contains("not measured in this run:", Write(report));
    }

    [Fact]
    public void AnEnabledLaneDisappearsFromNotMeasured()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "Alpha title", "alpha")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);
        var configurations = new ConfigurationSet(
            [
                new ConfigurationRow("Dense multilingual-e5-small", () => new StubEngine(("d1", 1.0))),
                new ConfigurationRow("BM25 + query expansion (density<=0.05, pmi)", () => new StubEngine(("d1", 1.0))),
                new ConfigurationRow("BM25 (top100)+CrossRerank", () => new StubEngine(("d1", 1.0))),
                new ConfigurationRow("BM25F (title 2.0)", () => new StubEngine(("d1", 1.0))),
            ],
            Notes: [], AnyTitle: true, FieldStatistics: true);

        DocumentDiagnosisReport report = Diagnose(
            corpus, index, "d1", queryId: "q1", configurations: configurations);

        Assert.Empty(report.NotMeasured);
    }

    [Fact]
    public void TheDiagnosisProbesExactlyTheRowsTheMetricsTableRuns()
    {
        // Written out twice, the row list drifts: a diagnosis probing a different set would answer
        // a question this harness does not measure, and nothing in either output would say so.
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents =
            [
                new BeirDocument("d1", "Alpha title", "alpha beta"),
                new BeirDocument("d2", "", "beta gamma"),
            ],
            Queries = [new BeirQuery("q1", "alpha"), new BeirQuery("q2", "beta")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal)
            {
                ["q1"] = new Dictionary<string, double>(StringComparer.Ordinal) { ["d1"] = 1.0 },
                ["q2"] = new Dictionary<string, double>(StringComparer.Ordinal) { ["d2"] = 1.0 },
            },
        };
        ITextIndex index = Index(corpus);
        var tokenizer = new Tokenizer(new TokenizerOptions());

        var (results, _, notes, _) = Evaluation.Run(
            corpus, index, topK: 10, limit: null, tokenizer: tokenizer, tuned: false);
        ConfigurationSet configurations = Evaluation.BuildConfigurations(
            corpus, index, tokenizer, Evaluation.QueryTermsFor(queryFrequency: false),
            referenceBm25: null, saturationConstant: true, singlePrecision: false, bm25Va: false,
            queryExpansions: null, queryReweights: null, spreadAlphas: null, spreadWindow: 8,
            dense: null, reranker: null, rerankCandidates: 100);

        Assert.Equal(results.Select(result => result.Name), configurations.Configurations.Select(row => row.Name));
        Assert.Equal(notes, configurations.Notes);
        Assert.Contains(configurations.Configurations, row => row.Name == Evaluation.BaselineConfigurationName);
        Assert.DoesNotContain(configurations.Configurations, row => row.IsProbe);
    }

    [Fact]
    public void TheReportStatesWhatItProbedAndSummarisesTheQueries()
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument("d1", "An alpha title", "alpha beta")],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", "d1"),
        };
        ITextIndex index = Index(corpus);

        string output = Write(Diagnose(corpus, index, "d1", queryId: "q1"));

        Assert.Contains("Diagnosis of document 'd1': \"An alpha title\"", output);
        Assert.Contains("indexed terms:", output);
        Assert.Contains("tuned rows not probed", output);
        Assert.Contains("-- query q1:", output);
        Assert.Contains("reach    :", output);
        Assert.Contains("score    : BM25 (k1=1.5, b=0.75)", output);
        Assert.Contains("summary: surfaced inside top-", output);
    }

    /// <summary>
    /// A two-document corpus whose target, <c>d1</c>, shares one analyzed term with the query —
    /// the reach every one of the ordering tests needs before it can pin what comes second.
    /// </summary>
    private static (BeirCorpus Corpus, ITextIndex Index) RankingFixture(
        (string Id, string Title, string Text) first,
        (string Id, string Title, string Text) second)
    {
        var corpus = new BeirCorpus
        {
            Name = "test",
            Documents = [new BeirDocument(first.Id, first.Title, first.Text), new BeirDocument(second.Id, second.Title, second.Text)],
            Queries = [new BeirQuery("q1", "alpha")],
            TestRelevance = Qrels("q1", first.Id),
        };

        return (corpus, Index(corpus));
    }

    private static Dictionary<string, IReadOnlyDictionary<string, double>> Qrels(string queryId, params string[] documentIds) =>
        new(StringComparer.Ordinal)
        {
            [queryId] = documentIds.ToDictionary(id => id, _ => 1.0, StringComparer.Ordinal),
        };

    private static ITextIndex Index(BeirCorpus corpus, TokenizerOptions? options = null)
    {
        var index = new InMemoryTextIndex(new Tokenizer(options ?? new TokenizerOptions()));
        index.Index(Evaluation.BuildDocuments(corpus));
        return index;
    }

    private static DocumentDiagnosisReport Diagnose(
        BeirCorpus corpus,
        ITextIndex index,
        string documentId,
        string? queryId = null,
        int probeDepth = 1000,
        int pageDepth = 10,
        ConfigurationSet? configurations = null,
        bool excludeQueryDocument = false,
        TokenizerOptions? options = null)
    {
        options ??= new TokenizerOptions();

        // Built from the same options as the index above, so the reach a test reads is the reach
        // the corpus was tokenized with rather than a second analyzer's.
        return DocumentDiagnosis.Diagnose(
            documentId,
            queryId,
            corpus,
            index,
            new Tokenizer(options),
            options,
            configurations ?? Configurations(corpus, index, options),
            Evaluation.QueryTermsFor(queryFrequency: false),
            probeDepth,
            pageDepth,
            parseQuerySyntax: false,
            excludeQueryDocument);
    }

    private static ConfigurationSet Configurations(BeirCorpus corpus, ITextIndex index, TokenizerOptions options) =>
        Evaluation.BuildConfigurations(
            corpus, index, new Tokenizer(options), Evaluation.QueryTermsFor(queryFrequency: false),
            referenceBm25: null, saturationConstant: true, singlePrecision: false, bm25Va: false,
            queryExpansions: null, queryReweights: null, spreadAlphas: null, spreadWindow: 8,
            dense: null, reranker: null, rerankCandidates: 100);

    private static string Write(DocumentDiagnosisReport report)
    {
        using var writer = new StringWriter();
        DocumentDiagnosis.Write(writer, report);
        return writer.ToString();
    }

    /// <summary>A row that returns a fixed page, so a rank in a test is a decision rather than arithmetic.</summary>
    private sealed class StubEngine : ITextSearchEngine
    {
        private readonly (string Id, double Score)[] _results;

        public StubEngine(params (string Id, double Score)[] results) => _results = results;

        public void Index(IEnumerable<SearchDocument> documents) => throw new NotSupportedException();

        public void Add(SearchDocument document) => throw new NotSupportedException();

        public bool Remove(string documentId) => throw new NotSupportedException();

        public void Clear() => throw new NotSupportedException();

        public IReadOnlyList<SearchResult> Search(
            string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
        {
            int limit = options?.Limit ?? int.MaxValue;
            var page = new List<SearchResult>(Math.Min(limit, _results.Length));

            foreach ((string id, double score) in _results)
            {
                if (page.Count >= limit)
                    break;

                page.Add(new SearchResult(id, score, new SearchDocument(id, string.Empty)));
            }

            return page;
        }
    }
}
