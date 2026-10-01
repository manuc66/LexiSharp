using LexiSharp.Core;
using LexiSharp.Expansion;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Pins the capability surface each decorator engine advertises, so a change to that surface is a
/// deliberate decision reviewed in the diff and not an accidental drift.
/// </summary>
/// <remarks>
/// The surfaces are the reviewable half of a silent-trap fix: wrapping an engine hides the wrapped
/// engine's capabilities behind the wrapper's own type, so <c>engine is IFacetedSearchEngine</c>
/// answers about the decorator, not about what it wraps. Each decorator here either forwards a
/// capability (and must be able to prove the forwarding is truthful) or deliberately does not, and
/// the deliberate ones carry the reason in the class's own XML remarks. These tests make the
/// current choice a pinned contract.
/// </remarks>
public class DecoratorCapabilitySurfaceTests
{
    [Fact]
    public void Expanding_ForwardsTheCapabilitiesItCanProveTruthful()
    {
        // Results are the inner engine's own, 1:1, against the *expanded* query — so facets,
        // detail breakdowns and explanations describe exactly the results Search returns.
        // A wrapped engine without the capability throws per call, naming its type.
        var engine = new ExpandingTextSearchEngine(
            new StubEngine(),
            new StubExpander());

        Assert.IsAssignableFrom<IFacetedSearchEngine>(engine);
        Assert.IsAssignableFrom<IDetailedSearchEngine>(engine);
        Assert.IsAssignableFrom<IExplainableSearchEngine>(engine);
        Assert.IsAssignableFrom<IQueryCostProbe>(engine);
    }

    [Fact]
    public void Boosted_DoesNotForwardCapabilitiesItsScoresWouldMisdescribe()
    {
        // A facet page / detail / explanation would describe the pre-boost scores, not the ranking
        // this engine returns — forwarding would be a lie by omission. See the class remarks.
        var engine = new BoostedTextSearchEngine(new StubEngine(), static result => new ScoreBoost(0, 1));

        Assert.IsNotAssignableFrom<IFacetedSearchEngine>(engine);
        Assert.IsNotAssignableFrom<IDetailedSearchEngine>(engine);
        Assert.IsNotAssignableFrom<IExplainableSearchEngine>(engine);
    }

    [Fact]
    public void Reranked_DoesNotForwardCapabilitiesItsScoresWouldMisdescribe()
    {
        // The reranker replaces scores, so any forwarded per-document read-out would describe the
        // pre-rerank ranking. See the class remarks.
        var engine = new RerankedTextSearchEngine(new StubEngine(), new StubReranker());

        Assert.IsNotAssignableFrom<IFacetedSearchEngine>(engine);
        Assert.IsNotAssignableFrom<IDetailedSearchEngine>(engine);
        Assert.IsNotAssignableFrom<IExplainableSearchEngine>(engine);
    }

    [Fact]
    public void Transforming_DoesNotForwardPerDocumentCapabilities()
    {
        // The fusion merges several variants' rankings, so per-document contracts (facets, details,
        // explanations) do not survive it. Deliberate and already stated in the class remarks.
        var engine = new TransformingTextSearchEngine(new StubEngine(), new StubTransformer());

        Assert.IsNotAssignableFrom<IFacetedSearchEngine>(engine);
        Assert.IsNotAssignableFrom<IDetailedSearchEngine>(engine);
        Assert.IsNotAssignableFrom<IExplainableSearchEngine>(engine);
    }

    private sealed class StubEngine : ITextSearchEngine
    {
        public void Index(IEnumerable<SearchDocument> documents) { }

        public void Add(SearchDocument document) { }

        public bool Remove(string documentId) => false;

        public void Clear() { }

        public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null) =>
            Array.Empty<SearchResult>();
    }

    private sealed class StubExpander : ITermExpander
    {
        public IReadOnlyCollection<ExpandedTerm> Expand(IReadOnlyList<string> terms) =>
            Array.Empty<ExpandedTerm>();
    }

    private sealed class StubReranker : IReranker
    {
        public string Name => "stub";

        public IReadOnlyList<SearchResult> Rerank(string query, IReadOnlyList<SearchResult> candidates) =>
            candidates;
    }

    private sealed class StubTransformer : IQueryTransformer
    {
        public string Name => "stub";

        public IReadOnlyList<string> Transform(string query) => [query];
    }
}