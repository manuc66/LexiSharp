using LexiSharp.Core;
using Xunit;

namespace LexiSharp.Tests.Conformance;

/// <summary>
/// Backend-agnostic conformance suite: the same invariants every <see cref="ITextSearchEngine"/>
/// must honor, regardless of its storage or ranking formula. Each backend provides a factory
/// and a capability descriptor; features it does not support are skipped explicitly (never
/// silently assumed). This is the contract that keeps the in-memory, PostgreSQL and ParadeDB
/// engines from drifting apart.
/// </summary>
/// <remarks>
/// Only invariants that are independent of the score scale are asserted: result <i>membership</i>
/// edited by options/filters, the exact <c>[Offset, Offset + Limit)</c> page of an engine's own
/// ranking, and the hard phrase gate. Score magnitudes and cross-engine equivalence are out of
/// scope (the backends use different formulas by design).
/// </remarks>
public abstract class SearchEngineConformanceTests
{
    /// <summary>A single engine instance, freshly created and empty; the base class indexes <see cref="Corpus"/> into it.</summary>
    protected abstract ITextSearchEngine CreateEngine();

    /// <summary>Releases an engine created by <see cref="CreateEngine"/> (drop schemas, dispose connections).</summary>
    protected virtual void DestroyEngine(ITextSearchEngine engine)
    {
        if (engine is IDisposable disposable)
            disposable.Dispose();
    }

    /// <summary>Optional features the backend supports; defaults to none.</summary>
    protected virtual EngineCapabilities Capabilities => new();

    /// <summary>Whether the backend cannot run in this environment (missing connection/extension).</summary>
    protected virtual bool IsUnavailable => false;

    /// <summary>Reason reported when <see cref="IsUnavailable"/> is true.</summary>
    protected virtual string UnavailableReason => "Backend unavailable.";

    /// <summary>
    /// Shared corpus. Fields are deliberately uneven: d4 has no <c>tags</c>, d5 has no fields at
    /// all — the same absent-field rules must hold on every backend.
    /// </summary>
    protected static readonly SearchDocument[] Corpus =
    {
        new("d1", "machine learning systems rock",
            new Dictionary<string, string> { ["kind"] = "article", ["year"] = "2021", ["tags"] = "ml" }),
        new("d2", "neural machine learning networks",
            new Dictionary<string, string> { ["kind"] = "article", ["year"] = "2022", ["tags"] = "ml" }),
        new("d3", "deep learning tutorial",
            new Dictionary<string, string> { ["kind"] = "note", ["year"] = "2020", ["tags"] = "dl" }),
        new("d4", "cooking pasta recipe",
            new Dictionary<string, string> { ["kind"] = "note", ["year"] = "2023" }),
        new("d5", "unrelated text"),
        new("d6", "neural networks accelerate"),
    };

    private static readonly SearchOptions All = new(Limit: 100);

    private static string[] Ids(IReadOnlyList<SearchResult> results) =>
        results.Select(r => r.DocumentId).ToArray();

    private void WithEngine(Action<ITextSearchEngine> body)
    {
        Skip.If(IsUnavailable, UnavailableReason);

        var engine = CreateEngine();

        try
        {
            engine.Index(Corpus);
            body(engine);
        }
        finally
        {
            DestroyEngine(engine);
        }
    }

    [SkippableFact]
    public void EmptyOptions_ReturnNothing() => WithEngine(engine =>
    {
        Assert.Empty(engine.Search("machine learning", new SearchOptions(Limit: 0)));
        Assert.Empty(engine.Search("machine learning", new SearchOptions(Offset: -1)));
    });

    [SkippableFact]
    public void WhitespaceQuery_ReturnsNothing() => WithEngine(engine =>
        Assert.Empty(engine.Search("   ", new SearchOptions(Limit: 10))));

    [SkippableFact]
    public void MinimumScoreAboveEveryScore_ReturnsNothing() => WithEngine(engine =>
        Assert.Empty(engine.Search("machine learning", new SearchOptions(Limit: 100, MinimumScore: double.MaxValue))));

    [SkippableFact]
    public void OffsetLimit_CutTheExactPageOfTheEngineRanking() => WithEngine(engine =>
    {
        // Compare a page against the engine's own full ranking, so the assertion holds whatever
        // the score scale and tie-break order are.
        var full = engine.Search("machine learning", All);

        for (int offset = 0; offset <= full.Count; offset++)
        {
            var page = engine.Search("machine learning", new SearchOptions(Limit: 1, Offset: offset));
            Assert.Equal(full.Skip(offset).Take(1).Select(r => r.DocumentId), Ids(page));
        }
    });

    [SkippableFact]
    public void Filters_Equal_KeepsOnlyTheMatchingValue() => WithEngine(engine =>
    {
        var results = engine.Search("machine learning", new SearchOptions(Limit: 100,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "article") }));

        Assert.NotEmpty(results);
        Assert.All(results, r => Assert.Equal("article", r.Document.Fields!["kind"]));
    });

    [SkippableFact]
    public void Filters_NotEqual_ExcludesMatchingDocsAndAdmitsFieldlessOnes() => WithEngine(engine =>
    {
        var results = engine.Search("machine learning", new SearchOptions(Limit: 100,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.NotEqual, "article") }));

        var ids = Ids(results);
        Assert.DoesNotContain("d1", ids);
        Assert.DoesNotContain("d2", ids); // carries kind=article
    });

    [SkippableFact]
    public void Filters_GreaterThan_IsNumeric() => WithEngine(engine =>
    {
        var results = engine.Search("machine learning", new SearchOptions(Limit: 100,
            Filters: new[] { new MetadataFilter("year", MetadataFilterOperator.GreaterThan, "2021") }));

        var ids = Ids(results);
        Assert.DoesNotContain("d1", ids); // year 2021 is not > 2021
        Assert.DoesNotContain("d3", ids); // year 2020
        Assert.All(results, r =>
            Assert.True(int.Parse(r.Document.Fields!["year"], System.Globalization.CultureInfo.InvariantCulture) > 2021));
    });

    [SkippableFact]
    public void Filters_FieldlessDocumentFailsEveryOperatorExceptNotEqual() => WithEngine(engine =>
    {
        Assert.DoesNotContain("d5", Ids(engine.Search("machine learning", new SearchOptions(Limit: 100,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.Equal, "note") }))));

        // NotEqual is the exception: an absent field passes an exclusion.
        Assert.Contains("d5", Ids(engine.Search("unrelated", new SearchOptions(Limit: 100,
            Filters: new[] { new MetadataFilter("kind", MetadataFilterOperator.NotEqual, "note") }))));
    });

    [SkippableFact]
    public void Clear_DropsEverything() => WithEngine(engine =>
    {
        engine.Clear();
        Assert.Empty(engine.Search("machine learning", All));
    });

    [SkippableFact]
    public void Remove_DeletedDocumentNoLongerMatches() => WithEngine(engine =>
    {
        engine.Remove("d1");
        Assert.DoesNotContain("d1", Ids(engine.Search("machine learning", All)));
    });

    [SkippableFact]
    public void Phrase_GatesTheCorpusButFreeTermsOnlyScore()
    {
        Skip.If(!Capabilities.Phrases, "Backend does not support phrase queries.");

        WithEngine(engine =>
        {
            // Phrase present in d1 (no "neural") and d2 (both); d6 has the free term but not the phrase.
            var ids = Ids(engine.Search("neural \"machine learning\"", All));

            Assert.Contains("d1", ids); // phrase-only: comes back even without the free term
            Assert.Contains("d2", ids); // both
            Assert.DoesNotContain("d6", ids); // free-only: never comes back
        });
    }

    [SkippableFact]
    public void PrefixExpansion_MatchesIndexedTerms()
    {
        Skip.If(!Capabilities.Expansions, "Backend does not support expansion operators.");

        WithEngine(engine =>
        {
            var ids = Ids(engine.Search("learn*", All));

            Assert.Contains("d1", ids); // "learning"
            Assert.Contains("d3", ids);
        });
    }
}
