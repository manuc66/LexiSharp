using System;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Linguistics;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class TransformingTextSearchEngineTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "token refresh keeps the session alive"),
        new("d2", "the oauth access token expires"),
        new("d3", "coffee prices rose this morning"),
    ];

    private static RankedTextSearchEngine PlainEngine() =>
        new(new InMemoryTextIndex(), new Bm25Scorer());

    private static RankedTextSearchEngine IndexedPlain()
    {
        var engine = PlainEngine();
        engine.Index(Corpus);
        return engine;
    }

    [Fact]
    public void ExpansionQueryTransformer_ReturnsTheQueryPlusAWidenedVariant()
    {
        var transformer = new ExpansionQueryTransformer(new FixedTermExpander("token", "refresh"));

        var variants = transformer.Transform("session alive");

        Assert.Equal(2, variants.Count);
        Assert.Equal("session alive", variants[0]);
        Assert.Equal("session alive token refresh", variants[1]);
    }

    [Fact]
    public void ExpansionQueryTransformer_PassesExplicitSyntaxAndEmptyQueriesThrough()
    {
        var transformer = new ExpansionQueryTransformer(new FixedTermExpander("token", "refresh"));

        Assert.Equal(["\"session alive\""], transformer.Transform("\"session alive\""));
        Assert.Equal(["term*"], transformer.Transform("term*"));
        Assert.Equal([""], transformer.Transform(""));
    }

    [Fact]
    public void IdentityTransformer_IsInterchangeableWithTheInnerEngine()
    {
        var plain = IndexedPlain();
        var transformed = new TransformingTextSearchEngine(PlainEngine(), new IdentityTransformer());
        foreach (var document in Corpus)
            transformed.Add(document);

        var plainHits = plain.Search("session token");
        var transformedHits = transformed.Search("session token");

        Assert.Equal(plainHits.Select(hit => hit.DocumentId), transformedHits.Select(hit => hit.DocumentId));
        Assert.Equal(plainHits.Select(hit => hit.Score), transformedHits.Select(hit => hit.Score));

        // Writes forward: removal and clear land on the inner engine.
        transformed.Remove("d3");
        Assert.DoesNotContain(transformed.Search("token"), hit => hit.DocumentId == "d3");
        transformed.Clear();
        Assert.Empty(transformed.Search("token"));
    }

    [Fact]
    public void ASecondVariant_ReachesDocumentsTheQueryAloneMisses()
    {
        var engine = new TransformingTextSearchEngine(PlainEngine(), new AppendTermsTransformer("token"));
        engine.Index(Corpus);

        // "session" alone matches d1 only; the widened variant reaches d2 through its tokens.
        var hits = engine.Search("session");

        Assert.Contains(hits, hit => hit.DocumentId == "d1");
        Assert.Contains(hits, hit => hit.DocumentId == "d2");
        Assert.DoesNotContain(hits, hit => hit.DocumentId == "d3");
    }

    [Fact]
    public void Fusion_KeepsEachDocumentsBestScoreAcrossVariants()
    {
        var inner = IndexedPlain();
        var fused = new TransformingTextSearchEngine(PlainEngine(), new FixedVariantsTransformer("session", "token"));
        fused.Index(Corpus);

        var fusedHits = fused.Search("going nowhere", new SearchOptions(Limit: 10));

        // Two documents are reachable across the variants, each once, at the max of its
        // per-variant scores.
        Assert.Equal(2, fusedHits.Count);
        Assert.Equal(fusedHits.Count, fusedHits.Select(hit => hit.DocumentId).Distinct().Count());

        var byVariant1 = inner.Search("session", new SearchOptions(Limit: 10))
            .ToDictionary(hit => hit.DocumentId, hit => hit.Score);
        var byVariant2 = inner.Search("token", new SearchOptions(Limit: 10))
            .ToDictionary(hit => hit.DocumentId, hit => hit.Score);

        foreach (var hit in fusedHits)
        {
            byVariant1.TryGetValue(hit.DocumentId, out double fromFirst);
            byVariant2.TryGetValue(hit.DocumentId, out double fromSecond);
            Assert.Equal(Math.Max(fromFirst, fromSecond), hit.Score);
        }
    }

    [Fact]
    public void AReturnedEmptyList_FallsBackToTheUntransformedQuery()
    {
        var plain = IndexedPlain();
        var engine = new TransformingTextSearchEngine(PlainEngine(), new EmptyTransformer());
        engine.Index(Corpus);

        var hits = engine.Search("session token");

        Assert.Equal(plain.Search("session token").Select(hit => hit.Score), hits.Select(hit => hit.Score));
    }

    [Fact]
    public void AThrowingTransformer_FallsBackToTheUntransformedQuery()
    {
        var plain = IndexedPlain();
        var engine = new TransformingTextSearchEngine(PlainEngine(), new ThrowingTransformer());
        engine.Index(Corpus);

        Assert.Equal(
            plain.Search("session").Select(hit => hit.DocumentId),
            engine.Search("session").Select(hit => hit.DocumentId));
    }

    [Fact]
    public void BlankAndDuplicateVariants_AreIgnoredAndSearchedOnce()
    {
        var engine = new TransformingTextSearchEngine(
            PlainEngine(),
            new FixedVariantsTransformer("session", " ", "session"));
        engine.Index(Corpus);

        // Only "session" survives the filtering, and it is searched once.
        var hits = engine.Search("going nowhere");

        Assert.Single(hits);
        Assert.Equal("d1", hits[0].DocumentId);
    }

    [Fact]
    public void PagingAndFilters_ApplyToTheFusedPage()
    {
        var engine = new TransformingTextSearchEngine(PlainEngine(), new AppendTermsTransformer("token"));
        engine.Index(Corpus);

        // Two documents match the fused query; the page cuts from the fused ranking.
        Assert.Equal(2, engine.Search("session", new SearchOptions(Limit: 10)).Count);
        Assert.Single(engine.Search("session", new SearchOptions(Limit: 1)));
        Assert.Single(engine.Search("session", new SearchOptions(Limit: 1, Offset: 1)));

        var filtered = engine.Search(
            "session",
            new SearchOptions(Limit: 10, Filters: [new MetadataFilter("kind", MetadataFilterOperator.Equal, "x")]));
        Assert.Empty(filtered);
    }

    [Fact]
    public void EstimateCandidateCount_SumsThePerVariantEstimates()
    {
        var inner = IndexedPlain();
        var engine = new TransformingTextSearchEngine(inner, new FixedVariantsTransformer("session", "token"));
        var probe = (IQueryCostProbe)inner;
        var options = SearchOptions.Default;

        // df("session") = 1, df("token") = 2: the wrapper runs both variants, so its estimate
        // is the sum of theirs.
        Assert.Equal(3, ((IQueryCostProbe)engine).EstimateCandidateCount("ignored", options));

        // The "no opinion" case runs the untransformed query, so its estimate is the probe's own.
        Assert.Equal(
            probe.EstimateCandidateCount("session", options),
            ((IQueryCostProbe)new TransformingTextSearchEngine(inner, new EmptyTransformer()))
                .EstimateCandidateCount("session", options));
    }

    [Fact]
    public void Trace_RecordsOneMergeStepPerPageDocument()
    {
        var engine = new TransformingTextSearchEngine(PlainEngine(), new AppendTermsTransformer("token"));
        engine.Index(Corpus);

        var trace = new SearchTrace();
        var hits = engine.Search("session", new SearchOptions(Limit: 10, Trace: trace));

        var mergeSteps = trace.Steps.Where(step => step.Stage == TraceStage.Merge).ToList();

        Assert.Equal(hits.Count, mergeSteps.Count);
        Assert.All(mergeSteps, step => Assert.Equal("append-token", step.Detail));
        // Each variant's search also recorded its own score stage before the fusion.
        Assert.True(trace.Steps.Count(step => step.Stage == TraceStage.Score) >= mergeSteps.Count);
    }

    [Fact]
    public void Facade_QueryTransformer_RunsTheFullPipelinePerVariant()
    {
        var index = new LexiSharpIndex<SearchDocument>(options =>
        {
            options.QueryTransformer = new AppendTermsTransformer("token");
            options.UseBm25();
        });

        index.AddRange(Corpus);

        var hits = index.Search("session");

        // d2 is reached through the widened variant, and the hit still carries the caller's
        // own document.
        var hit = Assert.Single(hits, h => h.DocumentId == "d2");
        Assert.Equal("the oauth access token expires", hit.Document.Text);
    }

    /// <summary>An expander that always emits the same fixed terms.</summary>
    private sealed class FixedTermExpander(params string[] fixedTerms) : ITermExpander
    {
        public IReadOnlyCollection<ExpandedTerm> Expand(IReadOnlyList<string> terms) =>
            fixedTerms.Select(term => new ExpandedTerm(term, 1.0)).ToList();
    }

    /// <summary>A transformer that changes nothing: [query] only.</summary>
    private sealed class IdentityTransformer : IQueryTransformer
    {
        public string Name => "identity";

        public IReadOnlyList<string> Transform(string query) => [query];
    }

    /// <summary>A transformer that appends fixed terms: [query, query + extra].</summary>
    private sealed class AppendTermsTransformer(string extra) : IQueryTransformer
    {
        public string Name => "append-token";

        public IReadOnlyList<string> Transform(string query) => [query, $"{query} {extra}"];
    }

    /// <summary>A transformer with a fixed variant list, independent of the query.</summary>
    private sealed class FixedVariantsTransformer(params string[] variants) : IQueryTransformer
    {
        public string Name => "fixed-variants";

        public IReadOnlyList<string> Transform(string query) => variants;
    }

    /// <summary>A transformer that returns nothing — the "no opinion" case.</summary>
    private sealed class EmptyTransformer : IQueryTransformer
    {
        public string Name => "empty";

        public IReadOnlyList<string> Transform(string query) => Array.Empty<string>();
    }

    /// <summary>A transformer that always throws — the "broken seam" case.</summary>
    private sealed class ThrowingTransformer : IQueryTransformer
    {
        public string Name => "throwing";

        public IReadOnlyList<string> Transform(string query) => throw new InvalidOperationException("boom");
    }
}