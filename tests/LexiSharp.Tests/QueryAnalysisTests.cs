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
/// The per-query analysis harness. These are the tests that decide whether a sliced result can be
/// believed, and they are here because a report nobody checked counts the wrong thing silently —
/// it produces numbers, they are plausible, and the mistake is only visible in a conclusion drawn
/// three steps later.
/// </summary>
/// <remarks>
/// The behaviours pinned here are the ones whose failure is invisible in the output: a band cut that
/// is not where the data is, an idf that is not the scorer's idf, a "new relevant" that counts
/// unjudged documents, and a report that is short by exactly the queries that failed.
/// </remarks>
public class QueryAnalysisTests
{
    // Named for its type, not for a variable: a field called `Tokenizer` shadows the type inside
    // every method below, which is why the initializer has to spell the type out.
    private static readonly ITokenizer Tokens = Tokenizer.Default;

    [Fact]
    public void ACoverageShareOfZeroIsTheNoneBandNotAnError()
    {
        Assert.Equal(QueryAnalysis.MismatchBand.None, QueryAnalysis.Band(0));
    }

    // The band is compared by name rather than as the enum, because the enum is internal to the
    // harness and a public test method cannot take an internal parameter type. Comparing names also
    // makes a rename of a band a test failure, which is what should happen: the band name is a
    // column heading in the output file.
    [Theory]
    [InlineData(0.0, "None")]
    [InlineData(0.0001, "Low")]
    [InlineData(0.5, "Medium")]
    [InlineData(1.0, "High")]
    public void BandsAreCutWhereTheDefinitionSays(double share, string expected) =>
        Assert.Equal(expected, QueryAnalysis.Band(share).ToString());

    [Fact]
    public void AQueryThatSharesNoIndexedTermWithItsAnswerLandsInTheLowestBand()
    {
        // The index has "bile" and the answer is about bile; the query asks about something else
        // entirely. This is the vocabulary-mismatch case the bands exist to isolate, so the band
        // boundaries are pinned on it directly rather than through a share computed from a real run.
        var index = new InMemoryTextIndex();
        index.Index([
            new SearchDocument("d1", "bile salts digest fat"),
            new SearchDocument("d2", "unrelated document about widgets"),
        ]);

        var corpus = Corpus(
            Document("d1", "", "bile salts digest fat"),
            Document("d2", "", "unrelated document about widgets"));

        var queries = new List<EvaluatedQuery>
        {
            Query("q1", "photosynthesis chlorophyll", ["d1"]),
        };

        var coverage = QueryAnalysis.Describe(queries, index, corpus, Tokens);

        var row = Assert.Single(coverage);
        Assert.Equal(QueryAnalysis.MismatchBand.None, QueryAnalysis.Band(row.JudgedIdfShare));
        Assert.Equal(0, row.JudgedTermsCovered);
    }

    [Fact]
    public void ACoverageShareIsTheIdfWeightedShareAndNotATermCount()
    {
        // The query carries one term its answer has and one it does not, so a term count calls that
        // 50% coverage. This pins that the share is idf-weighted instead: the covered term is the rare
        // one, so the weighting has to lift the number above the count it is correcting.
        var index = new InMemoryTextIndex();

        // "common" is in every document, so its idf is near zero. "bile" is in one of three, and it is
        // the only query term the judged document carries.
        index.Index([
            new SearchDocument("d1", "bile"),
            new SearchDocument("d2", "common"),
            new SearchDocument("d3", "common"),
        ]);

        var corpus = Corpus(Document("d1", "", "bile"), Document("d2", "", "common"), Document("d3", "", "common"));
        var queries = new List<EvaluatedQuery> { Query("q1", "common bile", ["d1"]) };

        var row = Assert.Single(QueryAnalysis.Describe(queries, index, corpus, Tokens));

        Assert.Equal(2, row.QueryTerms);
        Assert.Equal(1, row.JudgedTermsCovered);

        // One of two terms covered, but the covered one is the rare one, so the idf-weighted share
        // is above the raw 0.5. That is the whole reason the column exists.
        Assert.True(row.JudgedIdfShare > 0.5, $"expected the rare term to dominate, got {row.JudgedIdfShare}");
    }

    [Fact]
    public void ATermTheIndexHasNeverSeenStillCountsTowardsTheQueryIdfMass()
    {
        // Dropping out-of-vocabulary terms would make a query asking for something the corpus has
        // never contained look like a perfect lexical match — the exact case the hypothesis is about.
        var index = new InMemoryTextIndex();
        index.Index([new SearchDocument("d1", "bile salts")]);

        var corpus = Corpus(Document("d1", "", "bile salts"));
        var queries = new List<EvaluatedQuery> { Query("q1", "bilirubin clearance", ["d1"]) };

        var row = Assert.Single(QueryAnalysis.Describe(queries, index, corpus, Tokens));

        Assert.Equal(2, row.TermsOutOfVocabulary);
        Assert.True(row.IdfMass > 0, "an all-out-of-vocabulary query still carries idf mass");
    }

    [Fact]
    public void AJudgedIdWithNoDocumentBehindItDoesNotThrow()
    {
        // The qrels and the corpus are separate files and disagreeing is a real condition. The
        // alternative is a crash on a run that was otherwise fine, or terms silently dropped, which
        // would report a query as more mismatched than it is.
        var index = new InMemoryTextIndex();
        index.Index([new SearchDocument("d1", "bile salts")]);

        var corpus = Corpus(Document("d1", "", "bile salts"));
        var queries = new List<EvaluatedQuery> { Query("q1", "bile", ["d1", "does-not-exist"]) };

        var row = Assert.Single(QueryAnalysis.Describe(queries, index, corpus, Tokens));

        Assert.Equal(1, row.JudgedTermsCovered);
    }

    [Fact]
    public void TheReportCountsJudgedDocumentsEnteringAndLeavingAndNothingElse()
    {
        // A candidate that swaps unjudged documents for unjudged documents has changed nothing a
        // metric can see. Only judged ids are counted, which is what makes the pair of numbers mean
        // "retrieved something" and "dropped something".
        var judged = new[] { "a", "b", "c" };

        var report = Report(
            Outcome("q1", ["a", "b", "x", "y"], 2, 3),
            Outcome("q1", ["a", "b", "z", "w"], 2, 3));

        var (gained, lost) = Exchange(report, "q1", report.ByConfig["candidate"], report.ByConfig["baseline"]);

        Assert.Equal(0, gained);
        Assert.Equal(0, lost);
    }

    [Fact]
    public void AGainAndALossAreCountedSeparatelySoAPureSwapDoesNotReadAsAGain()
    {
        // The case the two counts exist for. Four documents are judged, a through d: the candidate
        // keeps a and b, drops c and brings in d. One judged document in each direction, so a
        // gain-only reading would call this +1 and be wrong — the ranking lost exactly what it gained
        // and the pair is what says so.
        var report = Report(
            Outcome("q1", ["a", "b", "d"], 3, 4),
            Outcome("q1", ["a", "b", "c"], 3, 4),
            "q1",
            judged: ["a", "b", "c", "d"]);

        var (gained, lost) = Exchange(report, "q1", report.ByConfig["candidate"], report.ByConfig["baseline"]);

        Assert.Equal(1, gained);
        Assert.Equal(1, lost);
    }

    [Fact]
    public void AnUnknownConfigurationNameListsTheOnesTheRunHas()
    {
        // A misspelled name must not read as "no deltas". Every configuration's display name carries
        // its parameters in brackets, so the message prints them in full.
        var report = Report(Outcome("q1", ["a"], 1, 1));

        var error = Assert.Throws<ArgumentException>(() => report.OutcomesOf("BM25 (k1=9, b=9)"));

        // Both names the run does have, in full, so the message is enough to fix the flag without
        // consulting the source.
        Assert.Contains("candidate", error.Message);
        Assert.Contains("baseline", error.Message);
    }

    [Fact]
    public void TheWrittenFileHasAHeaderAndOneRowPerQuery()
    {
        // Two queries. The outcomes carry one row each, and the coverage list is what the file is
        // walked from, so a configuration that scored both queries produces exactly two data rows.
        var report = Report(Outcome("q1", ["a", "b"], 2, 2));
        var outcomes = report.ByConfig["baseline"];

        var queries = new List<EvaluatedQuery>
        {
            Query("q1", "bile salts", ["a", "b", "c"]),
            Query("q2", "digest bile", ["a", "b", "c"]),
        };

        var index = new InMemoryTextIndex();
        index.Index([new SearchDocument("a", "bile"), new SearchDocument("b", "salts"), new SearchDocument("c", "digest")]);
        var corpus = Corpus(
            Document("a", "", "bile"),
            Document("b", "", "salts"),
            Document("c", "", "digest"));

        var coverage = QueryAnalysis.Describe(queries, index, corpus, Tokens);

        var byConfig = new Dictionary<string, IReadOnlyList<QueryOutcome>>(StringComparer.Ordinal)
        {
            ["baseline"] =
            [
                outcomes[0],
                outcomes[0] with { QueryId = "q2" },
            ],
        };

        var path = Path.Combine(Path.GetTempPath(), $"lexisharp-analysis-{Guid.NewGuid():N}.tsv");

        try
        {
            QueryAnalysis.Write(path, new QueryAnalysisReport(queries, coverage, byConfig), "baseline");

            var lines = File.ReadAllLines(path);

            Assert.Contains("query_id", lines[0]);
            Assert.Contains("judged_idf_share", lines[0]);
            Assert.Contains("new_relevant", lines[0]);
            Assert.Equal(3, lines.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheBandSummaryPrintsAQueryCountBesideEveryMean()
    {
        // A mean over three rows reads exactly like a mean over three hundred. The count is what
        // stops a band of three being read as a trend, which is how a default that changed nothing
        // came to be described as one that changed the ranking.
        var report = Report(Outcome("q1", ["a"], 1, 1), Outcome("q1", ["a"], 1, 1));

        using var writer = new StringWriter();

        QueryAnalysis.WriteBandSummary(writer, report, "candidate", "baseline");

        var text = writer.ToString();

        Assert.Contains("queries", text);
        Assert.Contains("None", text);
        Assert.Contains("High", text);
    }

    private static (int Gained, int Lost) Exchange(
        QueryAnalysisReport report, string queryId,
        IReadOnlyList<QueryOutcome> candidate, IReadOnlyList<QueryOutcome> baseline)
    {
        var candidateOutcome = candidate.Single(outcome => outcome.QueryId == queryId);
        var baselineOutcome = baseline.Single(outcome => outcome.QueryId == queryId);

        var judged = new HashSet<string>(report.Judged(queryId), StringComparer.Ordinal);
        var inCandidate = new HashSet<string>(candidateOutcome.Retrieved, StringComparer.Ordinal);
        var inBaseline = new HashSet<string>(baselineOutcome.Retrieved, StringComparer.Ordinal);

        int gained = inCandidate.Count(id => judged.Contains(id) && !inBaseline.Contains(id));
        int lost = inBaseline.Count(id => judged.Contains(id) && !inCandidate.Contains(id));

        return (gained, lost);
    }

    private static QueryAnalysisReport Report(QueryOutcome candidate, QueryOutcome baseline) =>
        Report(candidate, baseline, candidate.QueryId);

    private static QueryAnalysisReport Report(QueryOutcome outcome) =>
        Report(outcome, outcome, outcome.QueryId);

    private static QueryAnalysisReport Report(
        QueryOutcome candidate, QueryOutcome baseline, string queryId,
        string[]? judged = null)
    {
        var relevant = judged ?? ["a", "b", "c"];

        var queries = new List<EvaluatedQuery> { Query(queryId, "bile salts", relevant) };
        var index = new InMemoryTextIndex();
        index.Index([new SearchDocument("a", "bile"), new SearchDocument("b", "salts"), new SearchDocument("c", "digest")]);
        var corpus = Corpus(
            Document("a", "", "bile"),
            Document("b", "", "salts"),
            Document("c", "", "digest"));

        var coverage = QueryAnalysis.Describe(queries, index, corpus, Tokens);

        return new QueryAnalysisReport(
            queries,
            coverage,
            new Dictionary<string, IReadOnlyList<QueryOutcome>>(StringComparer.Ordinal)
            {
                ["candidate"] = [candidate],
                ["baseline"] = [baseline],
            });
    }

    private static QueryOutcome Outcome(string queryId, string[] retrieved, int relevantRetrieved, int judgedRelevant) =>
        new(queryId, retrieved, relevantRetrieved, judgedRelevant, RecallAt10: 0, NdcgAt10: 0);

    private static EvaluatedQuery Query(string id, string text, string[] relevant) =>
        new(new BeirQuery(id, text), relevant.ToDictionary(r => r, _ => 1.0));

    private static BeirDocument Document(string id, string title, string text) => new(id, title, text);

    private static BeirCorpus Corpus(params BeirDocument[] documents) => new()
    {
        Name = "test",
        Documents = documents,
        Queries = [],
        TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(StringComparer.Ordinal),
    };
}
