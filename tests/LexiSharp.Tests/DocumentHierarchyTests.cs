using System;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class DocumentHierarchyTests
{
    // book-1 → ch-1 → sec-1a → chunk-a1   (and others below)
    private static DocumentHierarchy BookTree() => DocumentHierarchy.FromChildToParent(
        new Dictionary<string, string>
        {
            ["chunk-a1"] = "sec-1a",
            ["chunk-b1"] = "sec-1b",
            ["chunk-c1"] = "sec-2a",
            ["sec-1a"] = "ch-1",
            ["sec-1b"] = "ch-1",
            ["sec-2a"] = "ch-2",
            ["ch-1"] = "book-1",
            ["ch-2"] = "book-1",
        });

    [Fact]
    public void FromChildToParent_WalksAncestorsUpToTheRoot()
    {
        var tree = BookTree();

        Assert.Equal(new[] { "sec-1a", "ch-1", "book-1" }, tree.GetAncestors("chunk-a1"));
        Assert.Equal(new[] { "ch-2", "book-1" }, tree.GetAncestors("sec-2a"));
        Assert.Empty(tree.GetAncestors("book-1"));
        Assert.Empty(tree.GetAncestors("unknown"));

        Assert.Equal(3, tree.Depth("chunk-a1"));
        Assert.Equal(0, tree.Depth("book-1"));
        Assert.Equal(0, tree.Depth("unknown"));

        Assert.True(tree.TryGetParent("chunk-a1", out var parent));
        Assert.Equal("sec-1a", parent);
        Assert.False(tree.TryGetParent("book-1", out _));
    }

    [Fact]
    public void GetDescendants_IsBreadthFirstInDeclaredChildOrder()
    {
        var tree = BookTree();

        Assert.Equal(
            new[] { "ch-1", "ch-2", "sec-1a", "sec-1b", "sec-2a", "chunk-a1", "chunk-b1", "chunk-c1" },
            tree.GetDescendants("book-1"));
        Assert.Equal(new[] { "sec-1a", "sec-1b", "chunk-a1", "chunk-b1" }, tree.GetDescendants("ch-1"));
        Assert.Empty(tree.GetDescendants("chunk-a1"));
        Assert.Empty(tree.GetDescendants("unknown"));
    }

    [Fact]
    public void RootsLeavesAndStructure_AreExact()
    {
        var tree = BookTree();

        Assert.Equal(new[] { "book-1" }, tree.Roots);
        Assert.Equal(9, tree.Count);
        Assert.Equal(9, tree.Nodes.Count);
        Assert.True(tree.IsRoot("book-1"));
        Assert.False(tree.IsRoot("ch-1"));
        Assert.True(tree.IsLeaf("chunk-a1"));
        Assert.True(tree.IsLeaf("chunk-c1"));
        Assert.False(tree.IsLeaf("ch-1"));
        Assert.True(tree.Contains("chunk-a1"));
        Assert.True(tree.Contains("book-1"));
        Assert.False(tree.Contains("unknown"));
    }

    [Fact]
    public void FromParentToChildren_PreservesChildOrder_AndMatchesTheChildToParentView()
    {
        var byChildren = DocumentHierarchy.FromParentToChildren(
            new Dictionary<string, IReadOnlyList<string>>
            {
                ["book-1"] = ["ch-1", "ch-2"],
                ["ch-1"] = ["sec-1a", "sec-1b"],
                ["sec-1a"] = ["chunk-a1"],
            });

        var byParents = DocumentHierarchy.FromChildToParent(
            new Dictionary<string, string>
            {
                ["ch-1"] = "book-1",
                ["ch-2"] = "book-1",
                ["sec-1a"] = "ch-1",
                ["sec-1b"] = "ch-1",
                ["chunk-a1"] = "sec-1a",
            });

        foreach (string root in byChildren.Roots)
            Assert.Equal(byParents.GetDescendants(root), byChildren.GetDescendants(root));
    }

    [Fact]
    public void Construction_RejectsSelfParentingAndBlankIds()
    {
        Assert.Throws<ArgumentException>(
            () => DocumentHierarchy.FromChildToParent(new Dictionary<string, string> { ["a"] = "a" }));

        Assert.Throws<ArgumentException>(
            () => DocumentHierarchy.FromChildToParent(new Dictionary<string, string> { [""] = "b" }));

        Assert.Throws<ArgumentException>(
            () => DocumentHierarchy.FromParentToChildren(
                new Dictionary<string, IReadOnlyList<string>> { ["a"] = [""] }));
    }

    [Fact]
    public void Construction_RejectsANodeWithTwoParents()
    {
        Assert.Throws<ArgumentException>(
            () => DocumentHierarchy.FromParentToChildren(
                new Dictionary<string, IReadOnlyList<string>>
                {
                    ["p1"] = ["child"],
                    ["p2"] = ["child"],
                }));
    }

    [Fact]
    public void Construction_RejectsACycle_AndNamesIt()
    {
        var exception = Assert.Throws<ArgumentException>(
            () => DocumentHierarchy.FromChildToParent(
                new Dictionary<string, string>
                {
                    ["a"] = "b",
                    ["b"] = "c",
                    ["c"] = "a",
                }));

        Assert.Contains("cycle", exception.Message);
    }

    [Fact]
    public void TheEngine_ForwardsSearch_AndResolvesAncestorDocuments()
    {
        var ledger = new Dictionary<string, SearchDocument>(StringComparer.Ordinal)
        {
            ["chunk-a1"] = new("chunk-a1", "the benefit rose twelve percent"),
            ["chunk-b1"] = new("chunk-b1", "the weather in paris"),
            ["sec-1a"] = new("sec-1a", "quarterly results of acme"),
            ["ch-1"] = new("ch-1", "acme annual report"),
            ["book-1"] = new("book-1", "acme corporate documents"),
        };

        var inner = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        inner.Add(ledger["chunk-a1"]);
        inner.Add(ledger["chunk-b1"]);

        var engine = new HierarchicalTextSearchEngine(
            inner,
            BookTree(),
            id => ledger.GetValueOrDefault(id));

        Assert.IsAssignableFrom<IStructureAwareSearchEngine>(engine);

        // Flat retrieval is unchanged: the engine still finds the precise leaf.
        var hits = engine.Search("benefit");
        Assert.Equal("chunk-a1", Assert.Single(hits).DocumentId);

        // The navigation surface walks from the hit's leaf up to the overview.
        Assert.Equal(new[] { "sec-1a", "ch-1", "book-1" }, engine.GetAncestorIds("chunk-a1"));

        var ancestors = engine.GetAncestors("chunk-a1");
        Assert.Equal(new[] { "sec-1a", "ch-1", "book-1" }, ancestors.Select(document => document.Id));
        Assert.Equal("quarterly results of acme", ancestors[0].Text);

        // An ancestor the ledger cannot produce is omitted rather than fabricated.
        var partial = new HierarchicalTextSearchEngine(
            inner,
            BookTree(),
            id => id == "sec-1a" ? ledger["sec-1a"] : null);
        Assert.Equal(new[] { "sec-1a" }, partial.GetAncestors("chunk-a1").Select(document => document.Id));
    }

    [Fact]
    public void WithoutADocumentResolver_GetAncestorsThrows_AndIdsRemainAvailable()
    {
        var engine = new HierarchicalTextSearchEngine(
            new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer()),
            BookTree());

        Assert.Equal(new[] { "sec-1a", "ch-1", "book-1" }, engine.GetAncestorIds("chunk-a1"));
        Assert.Throws<NotSupportedException>(() => engine.GetAncestors("chunk-a1"));
    }

    [Fact]
    public void Writes_ForwardToTheInnerEngine()
    {
        var inner = new RankedTextSearchEngine(new InMemoryTextIndex(), new Bm25Scorer());
        var engine = new HierarchicalTextSearchEngine(inner, BookTree());

        engine.Index([new SearchDocument("chunk-a1", "the benefit rose")]);
        Assert.Single(engine.Search("benefit"));

        engine.Remove("chunk-a1");
        Assert.Empty(engine.Search("benefit"));

        engine.Add(new SearchDocument("chunk-a1", "the benefit rose"));
        Assert.Single(engine.Search("benefit"));

        engine.Clear();
        Assert.Empty(engine.Search("benefit"));
    }
}