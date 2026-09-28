using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Guards the invariant that lets the engine skip candidate ordering: the page it returns does
/// not depend on the order candidates were produced in.
/// </summary>
/// <remarks>
/// <see cref="IUnorderedCandidateIndex"/> exists because
/// <see cref="ICandidateIndex.GetCandidateDocuments"/> documents that its result is in corpus
/// order, and honouring that costs a walk of the whole corpus however few documents matched
/// (0.242 ms against 0.001 ms for the union alone, on a 10,000-document index and a 35-document
/// candidate set). The engine therefore asks for the unordered set instead — which is only
/// correct while the ranking order is total, and these tests are what make that total order a
/// checked property rather than a comment.
/// </remarks>
public class CandidateEnumerationOrderTests
{
    /// <summary>
    /// A corpus whose candidate set is one large block of *exact* score ties, reached through the
    /// candidate path rather than a full scan.
    /// </summary>
    /// <remarks>
    /// Two shapes matter and they pull against each other. The tied documents must score
    /// identically, which needs identical term frequencies and identical lengths; and the
    /// candidate path must be taken, which needs the query terms to be rare enough that
    /// <c>sum(df) / Count &lt; 0.5</c>. Every tied document carries the same two rare terms
    /// once each and the same filler, so both hold at once.
    /// </remarks>
    private const int TiedDocuments = 100;
    private const int FillerDocuments = 900;

    private static InMemoryTextIndex CreateTiedCorpus()
    {
        var documents = new List<SearchDocument>();

        for (int i = 0; i < TiedDocuments; i++)
            documents.Add(new SearchDocument($"tied-{i:D4}", "alphaone alphabeta filler filler filler"));

        for (int i = 0; i < FillerDocuments; i++)
            documents.Add(new SearchDocument($"filler-{i:D4}", "unrelatedtoken onlyhere padding padding"));

        var index = new InMemoryTextIndex();
        index.Index(documents);
        return index;
    }

    [Fact]
    public void GetCandidatesUnordered_ReturnsTheSameSetAsTheOrderedPath()
    {
        var index = CreateTiedCorpus();
        string[] terms = ["alphaone", "alphabeta"];

        var ordered = index.GetCandidateDocuments(terms)
            .Select(d => d.Id)
            .ToList();

        var unordered = ((IUnorderedCandidateIndex)index)
            .GetCandidatesUnordered(terms)
            .Select(d => d.Id)
            .ToList();

        Assert.Equal(TiedDocuments, ordered.Count);

        // SetEquals alone would not notice a union that emitted a document twice, so the count
        // is asserted as well as the membership.
        Assert.Equal(ordered.Count, unordered.Count);
        Assert.Equal(
            ordered.ToHashSet(StringComparer.Ordinal),
            unordered.ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Search_ThroughTheCandidatePath_BreaksExactTiesByOrdinalId()
    {
        var index = CreateTiedCorpus();
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        // Two terms, rare enough that the engine enumerates candidates instead of scanning: the
        // 100 tied documents plus a handful of unrelated ones, well under half the corpus.
        var results = engine.Search("alphaone alphabeta", new SearchOptions(Limit: 10));

        Assert.Equal(10, results.Count);

        // Every tied document scores the same, so the page is decided entirely by the tie-break.
        // If candidate order could reach the result, these ids would be whatever the posting
        // lists happened to produce.
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"tied-{i:D4}").ToArray(),
            results.Select(r => r.Document.Id).ToArray());

        Assert.Single(results.Select(r => r.Score).Distinct());
    }

    [Fact]
    public void Search_PrefersTheUnorderedCandidatePathWhenTheIndexOffersIt()
    {
        var index = new CountingUnorderedIndex(CreateTiedCorpus());
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var results = engine.Search("alphaone alphabeta", new SearchOptions(Limit: 10));

        // The engine took the cheap path, not the ordered one.
        Assert.Equal(1, index.UnorderedCalls);
        Assert.Equal(0, index.OrderedCalls);

        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"tied-{i:D4}").ToArray(),
            results.Select(r => r.Document.Id).ToArray());
    }

    /// <summary>
    /// The property that actually licenses skipping the ordering pass: the page is a function of
    /// the scores, not of the order the candidates arrived in.
    /// </summary>
    /// <remarks>
    /// Asserting the expected ids against a hostile order is not enough on its own. A window that
    /// is order-dependent can still happen to return the right ids for one particular order —
    /// a "keep the last N offered" window returns the ten smallest ids when they are offered
    /// descending and the ten largest when they are offered ascending, so a single-order
    /// assertion passes on half of the broken implementations. Running the same query with the
    /// candidates offered in both directions and comparing the two results has no such gap.
    /// </remarks>
    [Fact]
    public void Search_ThroughTheUnorderedPath_IgnoresTheOrderCandidatesArriveIn()
    {
        var index = new CountingUnorderedIndex(CreateTiedCorpus());
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());
        var options = new SearchOptions(Limit: 10);

        index.CandidateOrder = OrderBy.AscendingId;
        var ascending = engine.Search("alphaone alphabeta", options);

        index.CandidateOrder = OrderBy.DescendingId;
        var descending = engine.Search("alphaone alphabeta", options);

        // Every tied document scores identically, so the two runs have the same scores by
        // construction and the ids are the only thing left that could differ.
        Assert.Equal(ascending.Select(r => r.Score).ToArray(), descending.Select(r => r.Score).ToArray());
        Assert.Equal(
            ascending.Select(r => r.Document.Id).ToArray(),
            descending.Select(r => r.Document.Id).ToArray());

        // And that shared answer is the one the total order dictates: the ten smallest ids
        // among the tied block.
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"tied-{i:D4}").ToArray(),
            ascending.Select(r => r.Document.Id).ToArray());
    }

    [Fact]
    public void Search_FallsBackToTheOrderedPathWhenTheIndexDoesNotOfferTheUnorderedOne()
    {
        var index = new OrderedOnlyIndex(CreateTiedCorpus());
        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var results = engine.Search("alphaone alphabeta", new SearchOptions(Limit: 10));

        Assert.Equal(1, index.OrderedCalls);
        Assert.Equal(
            Enumerable.Range(0, 10).Select(i => $"tied-{i:D4}").ToArray(),
            results.Select(r => r.Document.Id).ToArray());
    }

    [Fact]
    public void ExpansionIndex_ForwardsTheUnorderedCapabilityToItsInnerIndex()
    {
        // ExpansionTextIndex wraps an InMemoryTextIndex. If it did not forward, the semantic
        // lexical path would silently keep paying for the ordering walk while the plain one
        // stopped — the kind of gap no ranking test would report.
        var expander = new FixedExpander();
        var index = new ExpansionTextIndex(expander);

        index.Index(Enumerable.Range(0, TiedDocuments)
            .Select(i => new SearchDocument($"tied-{i:D4}", "alphaone alphabeta filler filler filler")));

        var unordered = ((IUnorderedCandidateIndex)index)
            .GetCandidatesUnordered(["alphaone", "alphabeta"])
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);

        var ordered = index.GetCandidateDocuments(["alphaone", "alphabeta"])
            .Select(d => d.Id)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(TiedDocuments, unordered.Count);
        Assert.Equal(ordered, unordered);
    }

    /// <summary>Maps every document to one extra term, so the test does not depend on PMI statistics.</summary>
    private sealed class FixedExpander : ITermExpander
    {
        public IReadOnlyCollection<ExpandedTerm> Expand(IReadOnlyList<string> terms) =>
            [new ExpandedTerm("expansion", 0.5)];
    }

    /// <summary>Which way the stub hands its candidates back, to vary the order on purpose.</summary>
    public enum OrderBy
    {
        AscendingId,
        DescendingId,
    }

    /// <summary>
    /// Wraps a real index, counts which candidate path the engine chose, and hands back the
    /// unordered candidates in whichever direction <see cref="CandidateOrder"/> asks for.
    /// </summary>
    private sealed class CountingUnorderedIndex(InMemoryTextIndex inner) : IUnorderedCandidateIndex
    {
        public int UnorderedCalls { get; private set; }

        public int OrderedCalls { get; private set; }

        public OrderBy CandidateOrder { get; set; } = OrderBy.AscendingId;

        public IEnumerable<SearchDocument> GetCandidatesUnordered(IReadOnlyList<string> terms)
        {
            UnorderedCalls++;
            IComparer<string> comparer = CandidateOrder == OrderBy.AscendingId
                ? StringComparer.Ordinal
                : Comparer<string>.Create((a, b) => StringComparer.Ordinal.Compare(b, a));

            return ((IUnorderedCandidateIndex)inner)
                .GetCandidatesUnordered(terms)
                .OrderBy(d => d.Id, comparer);
        }

        public IEnumerable<SearchDocument> GetCandidateDocuments(IReadOnlyList<string> terms)
        {
            OrderedCalls++;
            return inner.GetCandidateDocuments(terms);
        }

        public IReadOnlyCollection<SearchDocument> Documents => inner.Documents;

        public int Count => inner.Count;

        public double AverageDocumentLength => inner.AverageDocumentLength;

        public int VocabularySize => inner.VocabularySize;

        public long CorpusTokenCount => inner.CorpusTokenCount;

        public void Index(IEnumerable<SearchDocument> documents) => inner.Index(documents);

        public void Add(SearchDocument document) => inner.Add(document);

        public bool Remove(string documentId) => inner.Remove(documentId);

        public void Clear() => inner.Clear();

        public bool Contains(string documentId) => inner.Contains(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => inner.GetTerms(documentId);

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
            inner.GetTermPositions(documentId, term);

        public int DocumentFrequency(string term) => inner.DocumentFrequency(term);

        public int CorpusFrequency(string term) => inner.CorpusFrequency(term);

        public int TermFrequency(string documentId, string term) => inner.TermFrequency(documentId, term);

        public int DocumentLength(string documentId) => inner.DocumentLength(documentId);

        public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document) =>
            inner.TryGetDocument(documentId, out document);
    }

    /// <summary>The same index without the capability, i.e. what a third-party index looks like.</summary>
    private sealed class OrderedOnlyIndex(InMemoryTextIndex inner) : ICandidateIndex
    {
        public int OrderedCalls { get; private set; }

        public IEnumerable<SearchDocument> GetCandidateDocuments(IReadOnlyList<string> terms)
        {
            OrderedCalls++;
            return inner.GetCandidateDocuments(terms);
        }

        public IReadOnlyCollection<SearchDocument> Documents => inner.Documents;

        public int Count => inner.Count;

        public double AverageDocumentLength => inner.AverageDocumentLength;

        public int VocabularySize => inner.VocabularySize;

        public long CorpusTokenCount => inner.CorpusTokenCount;

        public void Index(IEnumerable<SearchDocument> documents) => inner.Index(documents);

        public void Add(SearchDocument document) => inner.Add(document);

        public bool Remove(string documentId) => inner.Remove(documentId);

        public void Clear() => inner.Clear();

        public bool Contains(string documentId) => inner.Contains(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => inner.GetTerms(documentId);

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
            inner.GetTermPositions(documentId, term);

        public int DocumentFrequency(string term) => inner.DocumentFrequency(term);

        public int CorpusFrequency(string term) => inner.CorpusFrequency(term);

        public int TermFrequency(string documentId, string term) => inner.TermFrequency(documentId, term);

        public int DocumentLength(string documentId) => inner.DocumentLength(documentId);

        public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document) =>
            inner.TryGetDocument(documentId, out document);
    }
}
