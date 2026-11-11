using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LexiSharp.Eval;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// The analysis diff behind <c>--analysis-diff</c>. The fingerprint already says two indexes
/// differ; this is what turns that percentage into pairs — judged (query, document) couples whose
/// reach verdict one analysis and not the other would give — so a verdict here has to be the
/// verdict <c>--diagnose-doc</c> would print for the same pair, or the two point the reader at
/// different documents.
/// </summary>
/// <remarks>
/// The comparisons are run through <c>Program.BuildAnalysis</c>, so the analyzers under test are
/// the ones the harness actually ships rather than a copy of their options written out here —
/// a stop list edited in a test would make every verdict below a statement about the test.
/// </remarks>
public class AnalysisDiffTests
{
    [Fact]
    public void AWordTheRunKeepsAndTheOtherAnalysisDropsTurnsReachableIntoAnalysisRemovedOverlap()
    {
        // The pair shares exactly one word and it is a function word: the run's analysis indexes it
        // and reaches the document, the other removes it from both sides and cannot. The sibling
        // documents are there so the verdict cannot be claimed by a condition that would mean
        // something else — d2 gives the query's second term a document frequency under the other
        // analysis (else VocabularyAbsent would win), d3 is a document only stop words can empty.
        BeirCorpus corpus = Corpus(
            documents:
            [
                new BeirDocument("d1", "", "the evidence"),
                new BeirDocument("d2", "", "the hypothesis"),
                new BeirDocument("d3", "", "the"),
            ],
            queries: [new BeirQuery("q1", "the hypothesis")],
            ("q1", "d1"));

        AnalysisDiffReport report = Diff(corpus);

        Assert.Equal(1, report.Verdicts.JudgedPairs);
        Assert.Equal(1, report.Verdicts.ChangedPairs);

        ReachChange change = report.Verdicts.Changes.Single();

        Assert.Equal("q1", change.QueryId);
        Assert.Equal("d1", change.DocumentId);
        Assert.Equal(DocumentReach.Reachable, change.Run);
        Assert.Equal(DocumentReach.AnalysisRemovedOverlap, change.Other);

        // What the verdict rests on: the word is the run's alone, and it is the word d3 is made of.
        Assert.Contains(report.Vocabulary.RunOnly, term => term.Term == "the" && term.DocumentFrequency == 3);
        Assert.Equal(1, report.Vocabulary.EmptiedByOtherCount);
        Assert.Equal("d3", Assert.Single(report.Vocabulary.EmptiedByOther));
        Assert.Equal(0, report.Vocabulary.EmptiedByRunCount);
    }

    [Fact]
    public void StemmingBridgesAVocabularyGapTheRunsAnalysisCannotCross()
    {
        // The beneficial direction, and the one a term count cannot show: "studies" and "study" are
        // two words under this run's analysis and one under Porter's, so a pair no shared term can
        // reach becomes a pair with a shared term. The second document gives "studies" a df here,
        // without which the verdict would be VocabularyAbsent rather than the gap being measured.
        BeirCorpus corpus = Corpus(
            documents:
            [
                new BeirDocument("d1", "", "study results"),
                new BeirDocument("d2", "", "studies in animals"),
            ],
            queries: [new BeirQuery("q1", "studies")],
            ("q1", "d1"));

        AnalysisDiffReport report = Diff(corpus);

        ReachChange change = report.Verdicts.Changes.Single();

        Assert.Equal(DocumentReach.NoVocabularyOverlap, change.Run);
        Assert.Equal(DocumentReach.Reachable, change.Other);
    }

    [Fact]
    public void TheSameAnalysisCannotDisagreeWithItself()
    {
        // The control for every number above: a diff that reports movement between two identical
        // tokenizations is a diff whose counting is wrong, and it would look like a result.
        BeirCorpus corpus = Corpus(
            documents:
            [
                new BeirDocument("d1", "", "the evidence"),
                new BeirDocument("d2", "", "the hypothesis"),
            ],
            queries: [new BeirQuery("q1", "the hypothesis")],
            ("q1", "d1"));

        AnalysisDiffReport report = Diff(corpus, otherAnalyzer: "default");

        Assert.Equal(0, report.Verdicts.ChangedPairs);
        Assert.Equal(1, report.Verdicts.JudgedPairs);
        Assert.DoesNotContain(report.Verdicts.Transitions, transition => transition.Run != transition.Other);
        Assert.Equal(0, report.Vocabulary.RunOnlyCount);
        Assert.Equal(0, report.Vocabulary.OtherOnlyCount);
        Assert.Equal(0, report.Vocabulary.EmptiedByOtherCount);
        Assert.Equal(0, report.Vocabulary.EmptiedByRunCount);
        Assert.Equal(report.RunIndex.TotalTerms, report.OtherIndex.TotalTerms);
    }

    [Fact]
    public void TheChangeListIsCappedAndTheCountIsNot()
    {
        // A corpus-wide diff moves thousands of pairs on a real corpus; the printed ids are a
        // sample and the number above them is the set. A cap that silently became the total would
        // read as "22 pairs changed" on a corpus where 1 667 did.
        const int judged = 22;

        var queries = Enumerable.Range(0, judged)
            .Select(index => new BeirQuery($"q{index:D2}", "the hypothesis"))
            .ToArray();
        BeirCorpus corpus = Corpus(
            documents:
            [
                new BeirDocument("d1", "", "the evidence"),
                new BeirDocument("d2", "", "the hypothesis"),
            ],
            queries: queries,
            queries.Select(query => (query.Id, "d1")).ToArray());

        AnalysisDiffReport report = Diff(corpus);

        Assert.Equal(judged, report.Verdicts.ChangedPairs);
        Assert.Equal(AnalysisDiff.MaxChanges, report.Verdicts.Changes.Count);

        string output = Write(report);

        Assert.Contains("(+2 more — the count above is the whole set)", output);
        Assert.Contains($"{judged} of {judged} pairs", output);
    }

    [Fact]
    public void AComparisonWithNoJudgedPairSaysSoRatherThanPrintingAnEmptyTable()
    {
        // The qrels and the corpus are separate files, and a judged id with no document behind it
        // has nothing to be reachable against. The per-query analysis counts such a query's
        // judgments; here the honest answer is that there was nothing to compare.
        BeirCorpus corpus = Corpus(
            documents: [new BeirDocument("d1", "", "the evidence")],
            queries: [new BeirQuery("q1", "the hypothesis")],
            ("q1", "d2"));

        AnalysisDiffReport report = Diff(corpus);

        Assert.Equal(0, report.Verdicts.JudgedPairs);

        Assert.Contains("no judged (query, document) pair to compare", Write(report));
    }

    [Fact]
    public void AIndexThatCannotEnumerateItsVocabularyIsReportedAsNotMeasured()
    {
        // Built rather than measured: every index this harness ships implements IVocabularyIndex,
        // so the branch exists for a store that does not, and it must not print as a diff with
        // nothing in it — the verdict comparison needs neither list and still ran.
        var report = new AnalysisDiffReport(
            "left analysis",
            "right analysis",
            new IndexFingerprint(2, 2, 10),
            new IndexFingerprint(2, 2, 8),
            new VocabularyDiff(false, 0, 0, [], [], [], 0, [], 0),
            new VerdictDiff(0, 0, [], []));

        string output = Write(report);

        Assert.Contains("vocabulary      : not measured", output);
        Assert.Contains("reach verdicts   : no judged", output);
    }

    private static AnalysisDiffReport Diff(BeirCorpus corpus, string runAnalyzer = "default", string otherAnalyzer = "english")
    {
        // Fully qualified: samples/LexiSharp.Demo's top-level statements put a `Program` in the
        // global namespace, which wins over this file's using-directive.
        (ITokenizer runTokenizer, string runDescription, TokenizerOptions runOptions) =
            LexiSharp.Eval.Program.BuildAnalysis(runAnalyzer, stem: false);
        (ITokenizer otherTokenizer, string otherDescription, TokenizerOptions otherOptions) =
            LexiSharp.Eval.Program.BuildAnalysis(otherAnalyzer, stem: false);

        var runIndex = new InMemoryTextIndex(runTokenizer);
        runIndex.Index(Evaluation.BuildDocuments(corpus));
        var otherIndex = new InMemoryTextIndex(otherTokenizer);
        otherIndex.Index(Evaluation.BuildDocuments(corpus));

        return AnalysisDiff.Compare(
            corpus,
            Evaluation.BuildQueries(corpus, limit: null, excludeQueryDocument: false),
            runIndex, runTokenizer, runOptions, runDescription,
            otherIndex, otherTokenizer, otherOptions, otherDescription);
    }

    private static BeirCorpus Corpus(
        BeirDocument[] documents,
        BeirQuery[] queries,
        params (string QueryId, string DocumentId)[] judgments)
    {
        var relevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal);

        foreach ((string queryId, string documentId) in judgments)
        {
            relevance[queryId] = new Dictionary<string, double>(StringComparer.Ordinal)
            {
                [documentId] = 1.0,
            };
        }

        return new BeirCorpus
        {
            Name = "test",
            Documents = documents,
            Queries = queries,
            TestRelevance = relevance,
        };
    }

    private static string Write(AnalysisDiffReport report)
    {
        using var writer = new StringWriter();
        AnalysisDiff.Write(writer, report);
        return writer.ToString();
    }
}
