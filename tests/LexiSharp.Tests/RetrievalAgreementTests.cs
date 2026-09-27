using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Covers <see cref="RetrievalAgreementAnalyzer"/>. The reason it normalizes per source is the
/// whole point: LexiSharp's own dense lane returns a cosine in [0, 1] while its BM25 lane returns
/// values around 1 to 10, so any comparison of raw contributions across sources is meaningless.
/// </summary>
public class RetrievalAgreementTests
{
    private static DetailedSearchResult Result(string id, double score, params (string Source, double Contribution)[] contributions) =>
        new(id, score, new SearchDocument(id, $"text of {id}"),
            contributions.ToDictionary(pair => pair.Source, pair => pair.Contribution, StringComparer.Ordinal));

    [Fact]
    public void OneSourceMeansSingleSource()
    {
        var page = new[] { Result("d1", 0.5, ("lexical", 0.5)) };

        var report = Assert.Single(RetrievalAgreementAnalyzer.Analyze(page));

        Assert.Equal(RetrievalAgreement.SingleSource, report.Agreement);
        Assert.Equal(["lexical"], report.Sources);
        Assert.Equal(["lexical"], report.StrongSources);
    }

    [Fact]
    public void SeveralSourcesThatAllFindItConvincingIsBroad()
    {
        var page = new[]
        {
            Result("d1", 0.5, ("lexical", 4.8), ("dense", 0.90)),
            Result("d2", 0.4, ("lexical", 4.0), ("dense", 0.80)),
        };

        var reports = RetrievalAgreementAnalyzer.Analyze(page);

        Assert.All(reports, report => Assert.Equal(RetrievalAgreement.Unanimous, report.Agreement));
    }

    [Fact]
    public void SourcesThatDisagreeAreDivergentNotBroad()
    {
        // d1 is the dense lane's favourite and only a middling lexical hit; d2 is the reverse.
        // Both are in the page, but neither is found convincing by everyone.
        var page = new[]
        {
            Result("d1", 0.5, ("lexical", 2.0), ("dense", 0.95)),
            Result("d2", 0.4, ("lexical", 4.8), ("dense", 0.30)),
        };

        var reports = RetrievalAgreementAnalyzer.Analyze(page);
        var byId = reports.ToDictionary(report => report.DocumentId, report => report);

        Assert.Equal(RetrievalAgreement.Disputed, byId["d1"].Agreement);
        Assert.Equal(["dense"], byId["d1"].StrongSources);
        Assert.Equal(["lexical"], byId["d2"].StrongSources);
    }

    [Fact]
    public void StrengthIsRelativeToTheSourcesOwnBestSoScalesDoNotMatter()
    {
        // The same raw score means opposite things to two sources. "bm25" peaked at 5.0, so 0.5 is a
        // tenth of its range; "cosine" peaked at 0.5, so 0.5 is its best possible hit. Reading the
        // raw numbers would call them equally strong, and both readings would be wrong.
        var page = new[]
        {
            Result("d1", 0.5, ("bm25", 0.5), ("cosine", 0.5)),
            Result("d2", 0.4, ("bm25", 5.0), ("cosine", 0.10)),
        };

        var report = RetrievalAgreementAnalyzer
            .Analyze(page)
            .Single(entry => entry.DocumentId == "d1");

        Assert.Equal(0.1, report.Strengths["bm25"], 10);
        Assert.Equal(1.0, report.Strengths["cosine"], 10);

        // One source is enthusiastic, the other is not: that is Divergent, not Broad, and it is the
        // distinction a raw-score comparison would have collapsed.
        Assert.Equal(RetrievalAgreement.Disputed, report.Agreement);
        Assert.Equal(["cosine"], report.StrongSources);
    }

    [Fact]
    public void StrongSourcesAreReportedInAStableOrder()
    {
        // Deterministic output matters: a report that reorders its own labels between runs cannot
        // be diffed, and a golden master over it would be worthless.
        var page = new[] { Result("d1", 1, ("zeta", 1.0), ("alpha", 1.0), ("mu", 1.0)) };

        var report = Assert.Single(RetrievalAgreementAnalyzer.Analyze(page));

        Assert.Equal(["alpha", "mu", "zeta"], report.StrongSources);
    }

    [Fact]
    public void EachSourcesTopDocumentHasFullStrength()
    {
        var page = new[]
        {
            Result("d1", 0.5, ("lexical", 4.8), ("dense", 0.10)),
            Result("d2", 0.4, ("lexical", 2.0), ("dense", 0.90)),
        };

        var byId = RetrievalAgreementAnalyzer
            .Analyze(page)
            .ToDictionary(report => report.DocumentId, report => report);

        Assert.Equal(1.0, byId["d1"].Strengths["lexical"], 10);
        Assert.Equal(1.0, byId["d2"].Strengths["dense"], 10);
    }

    [Fact]
    public void ASourceThatReturnedNothingIsAbsentRatherThanWeak()
    {
        // Being outside a source's depth is a different fact from being ranked low by it, and the
        // difference is the whole question in the "did the semantic lane even see this?" debate.
        var page = new[] { Result("d1", 0.5, ("lexical", 4.8)) };

        var report = Assert.Single(RetrievalAgreementAnalyzer.Analyze(page, sourceNames: ["lexical", "dense"]));

        Assert.Contains("dense", report.AbsentSources);
        Assert.DoesNotContain("dense", report.Strengths.Keys);
    }

    [Fact]
    public void TheStrongThresholdIsHonoured()
    {
        var page = new[]
        {
            Result("d1", 0.5, ("lexical", 4.8), ("dense", 0.10)),
            Result("d2", 0.4, ("lexical", 4.0), ("dense", 0.50)),
        };

        var strict = RetrievalAgreementAnalyzer
            .Analyze(page)
            .ToDictionary(report => report.DocumentId, report => report);

        var lenient = RetrievalAgreementAnalyzer
            .Analyze(page, strongThreshold: 0.1)
            .ToDictionary(report => report.DocumentId, report => report);

        // d1's dense strength is 0.10/0.50 = 0.2: above a 0.1 threshold, below the 0.5 default.
        Assert.Equal(RetrievalAgreement.Disputed, strict["d1"].Agreement);
        Assert.Equal(RetrievalAgreement.Unanimous, lenient["d1"].Agreement);
        Assert.Equal(["dense", "lexical"], lenient["d1"].StrongSources);
    }

    [Fact]
    public void ASingleDocumentPageIsStillClassified()
    {
        var report = Assert.Single(RetrievalAgreementAnalyzer.Analyze([Result("d1", 0.5, ("lexical", 1.0))]));

        // One source present, so SingleSource - not Broad, even though its only source is strong.
        Assert.Equal(RetrievalAgreement.SingleSource, report.Agreement);
    }

    [Fact]
    public void ANonPositiveBestScoreDoesNotBecomeAFalseOne()
    {
        // A source that only ever returns zero or negative scores cannot be normalized; treating its
        // documents as strong would be worse than treating them as weak.
        var page = new[]
        {
            Result("d1", 0.1, ("weird", 0.0)),
            Result("d2", 0.2, ("weird", -1.0)),
        };

        var reports = RetrievalAgreementAnalyzer.Analyze(page);

        Assert.All(reports, report =>
        {
            Assert.Equal(0.0, report.Strengths["weird"], 10);
            Assert.Empty(report.StrongSources);
        });
    }

    [Fact]
    public void AnEmptyPageProducesNothing()
        => Assert.Empty(RetrievalAgreementAnalyzer.Analyze([]));

    [Theory]
    [InlineData(0.0)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void AThresholdOutsideTheUnitRangeIsRejected(double threshold)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RetrievalAgreementAnalyzer.Analyze([Result("d1", 1, ("s", 1))], threshold));
    }

    [Fact]
    public void SummarizeCountsEachCategory()
    {
        // One of each: unanimous, single source, disputed, and the lukewarm case where several
        // sources returned the document and none of them is enthusiastic.
        var page = new[]
        {
            Result("d1", 0.5, ("lexical", 4.8), ("dense", 4.5)),
            Result("d2", 0.4, ("lexical", 4.0)),
            Result("d3", 0.3, ("lexical", 4.0), ("dense", 0.5)),
            Result("d4", 0.2, ("lexical", 0.1), ("dense", 0.1)),
        };

        var reports = RetrievalAgreementAnalyzer.Analyze(page);
        var byId = reports.ToDictionary(report => report.DocumentId, report => report);

        Assert.Equal(RetrievalAgreement.Unanimous, byId["d1"].Agreement);
        Assert.Equal(RetrievalAgreement.SingleSource, byId["d2"].Agreement);
        Assert.Equal(RetrievalAgreement.Disputed, byId["d3"].Agreement);
        Assert.Equal(RetrievalAgreement.Lukewarm, byId["d4"].Agreement);

        var summary = RetrievalAgreementAnalyzer.Summarize(reports);

        Assert.Equal(1, summary[RetrievalAgreement.Unanimous]);
        Assert.Equal(1, summary[RetrievalAgreement.SingleSource]);
        Assert.Equal(1, summary[RetrievalAgreement.Disputed]);
        Assert.Equal(1, summary[RetrievalAgreement.Lukewarm]);
    }
}
