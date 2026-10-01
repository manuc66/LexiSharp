using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class GraphAwareSearchEngineTests
{
    private static readonly SearchDocument[] Corpus =
    [
        new("d1", "acme supplies pumps for the chemical plant"),
        new("d2", "the pumps failed after the v3 upgrade was applied"),
        new("d3", "coffee prices rose this morning"),
    ];

    private static RankedTextSearchEngine PlainEngine()
    {
        var engine = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        engine.Index(Corpus);
        return engine;
    }

    [Fact]
    public void SearchWithGraphFacts_ReturnsTheInnerPage_PlusTheConnectedFacts()
    {
        var bridge = new InMemoryGraphBridge(
        [
            new EntityTriplet("acme", "supplies", "pumps"),
            new EntityTriplet("pumps", "failed_after", "v3"),
            new EntityTriplet("paris", "forecast", "sunny"),
        ],
        ["acme maintenance incidents 2024"]);

        var engine = new GraphAwareSearchEngine(PlainEngine(), bridge);

        var hydrated = engine.SearchWithGraphFacts("which pumps failed after the upgrade");

        // The page is exactly the flat search's page.
        var flat = engine.Search("which pumps failed after the upgrade");
        Assert.Equal(flat.Select(hit => hit.DocumentId), hydrated.Results.Select(hit => hit.DocumentId));
        Assert.Equal(flat.Select(hit => hit.Score), hydrated.Results.Select(hit => hit.Score));

        // The facts are the graph's answer around the query's entities — including the "paris"
        // triplet is excluded because the query never mentions it.
        Assert.Equal(2, hydrated.Facts.Count);
        Assert.Contains(hydrated.Facts, triplet => triplet.Subject == "pumps" && triplet.Predicate == "failed_after");
        Assert.Contains(hydrated.Facts, triplet => triplet.Subject == "acme" && triplet.Predicate == "supplies");
        Assert.Equal(["acme maintenance incidents 2024"], hydrated.CommunitySummaries);
    }

    [Fact]
    public void AQueryThatMinesNoEntities_CarriesAnEmptyContext()
    {
        var bridge = new InMemoryGraphBridge(
        [
            new EntityTriplet("acme", "supplies", "pumps"),
        ],
        ["anything"]);

        var engine = new GraphAwareSearchEngine(PlainEngine(), bridge);

        // A query that matches the corpus but mines no entity carries an empty context while
        // the flat search still answers.
        var hydrated = engine.SearchWithGraphFacts("coffee prices rose this morning");

        Assert.Empty(hydrated.Facts);
        Assert.Empty(hydrated.CommunitySummaries);
        Assert.NotEmpty(hydrated.Results);
        Assert.Equal(0, bridge.LastMaxHops); // the sub-graph was never asked
    }

    [Fact]
    public void MaxHops_IsPassedToTheBridge()
    {
        var bridge = new InMemoryGraphBridge([new EntityTriplet("acme", "supplies", "pumps")], []);

        var engine = new GraphAwareSearchEngine(PlainEngine(), bridge, maxHops: 3);
        engine.SearchWithGraphFacts("acme pumps");

        Assert.Equal(3, bridge.LastMaxHops);
    }

    [Fact]
    public void TheFlatSearch_NeverTouchesTheBridge()
    {
        var broken = new ThrowingGraphBridge();

        var engine = new GraphAwareSearchEngine(PlainEngine(), broken);

        // The flat contract keeps working even though the bridge throws on every call.
        Assert.NotEmpty(engine.Search("pumps"));
        Assert.Throws<InvalidOperationException>(() => engine.SearchWithGraphFacts("pumps"));

        // Writes forward regardless.
        engine.Add(new SearchDocument("d4", "a fourth document about turbines"));
        Assert.Single(engine.Search("turbines"));
        engine.Remove("d4");
        Assert.Empty(engine.Search("turbines"));
    }

    [Fact]
    public void TheCapability_IsDetectedByPatternMatching()
    {
        var engine = new GraphAwareSearchEngine(PlainEngine(), new InMemoryGraphBridge([], []));

        Assert.IsAssignableFrom<IGraphSearchEngine>(engine);
        Assert.True(engine is IGraphSearchEngine);
    }

    /// <summary>A deterministic, in-memory stand-in for a consumer's graph.</summary>
    private sealed class InMemoryGraphBridge : IKnowledgeGraphBridge
    {
        private readonly List<EntityTriplet> _store;
        private readonly IReadOnlyList<string> _summaries;

        public int LastMaxHops { get; private set; }

        public InMemoryGraphBridge(IEnumerable<EntityTriplet> store, IEnumerable<string> summaries)
        {
            _store = store.ToList();
            _summaries = summaries.ToList();
        }

        public string Name => "memory-graph";

        public Task<IReadOnlyList<EntityTriplet>> ExtractRelationsAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EntityTriplet>>(_store
                .Where(triplet => text.IndexOf(triplet.Subject, StringComparison.OrdinalIgnoreCase) >= 0
                               || text.IndexOf(triplet.Object, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList());

        public Task<GraphContext> QuerySubGraphAsync(
            IReadOnlyCollection<string> entities,
            int maxHops = 2,
            CancellationToken cancellationToken = default)
        {
            LastMaxHops = maxHops;

            var matched = _store
                .Where(triplet => entities.Contains(triplet.Subject, StringComparer.Ordinal)
                               || entities.Contains(triplet.Object, StringComparer.Ordinal))
                .ToList();

            return Task.FromResult(new GraphContext(matched, _summaries));
        }
    }

    /// <summary>A bridge that fails on every call — the outage case.</summary>
    private sealed class ThrowingGraphBridge : IKnowledgeGraphBridge
    {
        public string Name => "throwing";

        public Task<IReadOnlyList<EntityTriplet>> ExtractRelationsAsync(
            string text,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("graph is down");

        public Task<GraphContext> QuerySubGraphAsync(
            IReadOnlyCollection<string> entities,
            int maxHops = 2,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("graph is down");
    }
}