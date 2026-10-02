using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

public class ContextEnrichingIndexTests
{
    private static SearchDocument Chunk(
        string id,
        string text,
        params (string Field, string Value)[] fields) =>
        new(id, text, fields.ToDictionary(pair => pair.Field, pair => pair.Value));

    [Fact]
    public void FieldPrefixContextEnricher_PrefixesNamedFieldsInOrder_AndSkipsMissingOnes()
    {
        var enricher = new FieldPrefixContextEnricher(["title", "chapter"]);

        var chunk = Chunk("d1", "the benefit rose twelve percent",
            ("title", "acme annual report"),
            ("chapter", "2024 financial results"),
            ("tags", "unlisted"));

        var enriched = enricher.Enrich(chunk);

        Assert.Equal(
            "acme annual report 2024 financial results the benefit rose twelve percent",
            enriched.Text);
        Assert.Equal("d1", enriched.Id);
        Assert.Equal(chunk.Fields, enriched.Fields);

        // A chunk carrying none of the named fields is returned unchanged.
        var unadorned = Chunk("d2", "plain text", ("kind", "note"));
        Assert.Same(unadorned, enricher.Enrich(unadorned));
    }

    [Fact]
    public void FieldPrefixContextEnricher_RejectsBlankEmptyOrDuplicatedFieldNames()
    {
        Assert.Throws<ArgumentException>(() => new FieldPrefixContextEnricher(["title", " "]));
        Assert.Throws<ArgumentException>(() => new FieldPrefixContextEnricher([]));
        Assert.Throws<ArgumentException>(() => new FieldPrefixContextEnricher(["title", "title"]));
    }

    [Fact]
    public void Indexing_StoresTheEnrichedCorpus_AndSurfacesTheOriginal()
    {
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new FieldPrefixContextEnricher(["title"]));

        var chunk = Chunk("d1", "the benefit rose twelve percent", ("title", "acme annual report"));
        index.Add(chunk);

        // Statistics describe what is scored — the enriched text.
        Assert.Contains("acme", index.GetTerms("d1"));
        Assert.Equal(1, index.DocumentFrequency("acme"));
        Assert.Equal(1, index.TermFrequency("d1", "acme"));
        Assert.Contains("acme", index.Vocabulary);

        // The display side is the caller's original.
        Assert.True(index.TryGetDocument("d1", out var stored));
        Assert.Equal("the benefit rose twelve percent", stored!.Text);
        var doc = Assert.Single(index.Documents);
        Assert.Equal("the benefit rose twelve percent", doc.Text);
        Assert.Equal(1, index.Count);
    }

    [Fact]
    public void Engine_ScoresTheEnrichedCorpus_AndReturnsTheRawDocument()
    {
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new FieldPrefixContextEnricher(["title"]));
        index.Index(
        [
            Chunk("d1", "the benefit rose twelve percent", ("title", "acme annual report")),
            Chunk("d2", "the weather in paris is sunny", ("title", "regional forecasts")),
        ]);

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var hits = engine.Search("acme");

        var hit = Assert.Single(hits);
        Assert.Equal("d1", hit.DocumentId);
        // A term the raw text never spells out scores, and the display text is the raw one.
        Assert.Equal("the benefit rose twelve percent", hit.Document.Text);
    }

    [Fact]
    public void Enricher_ThatChangesTheId_IsRejected()
    {
        var index = new ContextEnrichingIndex(new InMemoryTextIndex(), new IdSwappingEnricher());

        var exception = Assert.Throws<ArgumentException>(
            () => index.Add(new SearchDocument("d1", "the benefit rose")));

        Assert.Contains("preserve", exception.Message);
    }

    [Fact]
    public void Remove_Replace_AndClear_KeepTheOriginalStoreInStep()
    {
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new FieldPrefixContextEnricher(["title"]));

        index.Add(Chunk("d1", "first chunk", ("title", "acme")));
        index.Add(Chunk("d2", "second chunk", ("title", "globex")));
        Assert.Equal(2, index.Count);

        // Replacing an id drops the previous entry: one document, the new text.
        index.Add(Chunk("d1", "rewritten chunk", ("title", "initech")));
        Assert.Equal(2, index.Count);
        Assert.True(index.TryGetDocument("d1", out var replaced));
        Assert.Equal("rewritten chunk", replaced!.Text);
        Assert.Equal(1, index.DocumentFrequency("initech"));
        Assert.Equal(0, index.DocumentFrequency("acme"));

        Assert.True(index.Remove("d1"));
        Assert.False(index.Contains("d1"));
        Assert.False(index.TryGetDocument("d1", out _));
        Assert.Equal(1, index.Count);

        index.Clear();
        Assert.Equal(0, index.Count);
        Assert.Empty(index.Documents);
        Assert.Equal(0, index.DocumentFrequency("globex"));
    }

    [Fact]
    public void SearchResults_OnTheAccumulationPath_DisplayTheRawText()
    {
        // df("alpha") + df("beta") + df("gamma") = 9 ≥ 8, no filters and no phrases: the
        // term-at-a-time accumulation path runs, and the documents it ranks must come out of
        // the raw store just like the fallback's.
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new FieldPrefixContextEnricher(["title"]));
        index.Index(
        [
            Chunk("d1", "alpha beta gamma report one", ("title", "acme")),
            Chunk("d2", "alpha beta gamma report two", ("title", "globex")),
            Chunk("d3", "alpha beta gamma report three", ("title", "initech")),
        ]);

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var hits = engine.Search("alpha beta gamma");

        Assert.Equal(3, hits.Count);
        Assert.All(hits, hit =>
        {
            // The accumulation path ranks the enriched corpus but returns the originals.
            Assert.StartsWith("alpha beta gamma", hit.Document.Text);
            Assert.DoesNotContain("acme", hit.Document.Text);
            Assert.DoesNotContain("globex", hit.Document.Text);
            Assert.DoesNotContain("initech", hit.Document.Text);
        });
    }

    [Fact]
    public void SearchResults_OnTheFallbackPaths_DisplayTheRawText()
    {
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new FieldPrefixContextEnricher(["title"]));
        index.Index(
        [
            Chunk("d1", "alpha beta gamma report one", ("title", "acme")),
            Chunk("d2", "alpha beta gamma report two", ("title", "globex")),
            Chunk("d3", "alpha beta gamma report three", ("title", "initech")),
        ]);

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        // A metadata filter pushes the search to the per-document loop.
        var filtered = engine.Search(
            "alpha",
            new SearchOptions(Filters: [new MetadataFilter("title", MetadataFilterOperator.NotEqual, "nope")]));
        Assert.Equal(3, filtered.Count);
        Assert.All(filtered, hit => Assert.StartsWith("alpha beta gamma", hit.Document.Text));

        // A phrase query pushes it to the per-document loop as well.
        var phrased = engine.Search("\"alpha beta\"");
        Assert.Equal(3, phrased.Count);
        Assert.All(phrased, hit => Assert.StartsWith("alpha beta gamma", hit.Document.Text));
    }

    [Fact]
    public void FacetsAndFilters_SeeTheOriginalFields()
    {
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new FieldPrefixContextEnricher(["title"]));
        index.Add(Chunk("d1", "the benefit rose twelve percent", ("title", "acme annual report")));

        var engine = new RankedTextSearchEngine(index, new Bm25Scorer());

        var faceted = engine.SearchWithFacets("acme", facetFields: ["title"]);
        var bucket = Assert.Single(faceted.Buckets);
        Assert.Equal("acme annual report", Assert.Single(bucket.Values).Value);

        var filtered = engine.Search(
            "acme",
            new SearchOptions(Filters: [new MetadataFilter("title", MetadataFilterOperator.Equal, "acme annual report")]));
        Assert.Single(filtered);
    }

    [Fact]
    public void Statistics_DescribeTheEnrichedCorpus()
    {
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new FieldPrefixContextEnricher(["title"]));
        index.Add(Chunk("d1", "the benefit rose", ("title", "acme annual report")));

        // The token count counts what is scored: the prefix plus the original text.
        var statistics = index.GetStatistics();
        Assert.Equal(6, statistics.TokenCount);
        Assert.Equal(1, statistics.DocumentCount);
        Assert.Equal(6, index.DocumentLength("d1"));
    }

    [Fact]
    public void Facade_ContextEnricher_ReachesDocumentsBeyondTheirLiteralText()
    {
        var index = new LexiSharpIndex<SearchDocument>(options =>
        {
            options.ContextEnricher = new FieldPrefixContextEnricher(["title"]);
            options.UseBm25();
        });

        index.AddRange(
        [
            Chunk("d1", "the benefit rose twelve percent", ("title", "acme annual report")),
            Chunk("d2", "the weather in paris is sunny", ("title", "regional forecasts")),
        ]);

        var hits = index.Search("acme");

        var hit = Assert.Single(hits);
        Assert.Equal("d1", hit.DocumentId);
        // The typed facade returns the caller's own document, untouched by enrichment.
        Assert.Equal("the benefit rose twelve percent", hit.Document.Text);
    }

    [Fact]
    public void NoOpEnricher_IsInterchangeableWithThePlainIndex()
    {
        // An enricher that changes nothing turns the facade into a pure forwarder, and that is
        // the contract to pin: every member of every interface needs to agree with the plain
        // index on the same documents, because enrichment must change what is *matched*, not
        // how the index behaves. The empty document also covers the empty-fields guard.
        var documents = new[]
        {
            new SearchDocument("empty", "", Fields: null),
            new SearchDocument("a", "alpha beta gamma", Fields: new Dictionary<string, string> { ["title"] = "x" }),
            new SearchDocument("b", "alpha gamma gamma delta"),
        };

        var plain = new InMemoryTextIndex();
        plain.Index(documents);
        var enriched = new ContextEnrichingIndex(new InMemoryTextIndex(), new NoOpEnricher());
        enriched.Index(documents);

        Assert.Equal(plain.Count, enriched.Count);
        Assert.Equal(plain.Documents.Select(d => d.Id), enriched.Documents.Select(d => d.Id));
        Assert.Equal(plain.AverageDocumentLength, enriched.AverageDocumentLength);
        Assert.Equal(plain.VocabularySize, enriched.VocabularySize);
        Assert.Equal(plain.CorpusTokenCount, enriched.CorpusTokenCount);
        Assert.Equal(plain.Vocabulary.OrderBy(term => term), enriched.Vocabulary.OrderBy(term => term));
        Assert.Equal(plain.Fields, enriched.Fields);
        Assert.Equal(plain is IFieldStatisticsIndex, enriched is IFieldStatisticsIndex);

        foreach (string id in new[] { "empty", "a", "b" })
        {
            Assert.Equal(plain.Contains(id), enriched.Contains(id));
            Assert.Equal(plain.DocumentLength(id), enriched.DocumentLength(id));
            Assert.Equal(plain.GetTerms(id), enriched.GetTerms(id));
            Assert.Equal(
                plain.TryGetDocument(id, out var plainDoc),
                enriched.TryGetDocument(id, out var enrichedDoc));
            Assert.Equal(plainDoc?.Text, enrichedDoc?.Text);
        }

        foreach (string term in new[] { "alpha", "gamma", "delta", "absent" })
        {
            Assert.Equal(plain.DocumentFrequency(term), enriched.DocumentFrequency(term));
            Assert.Equal(plain.CorpusFrequency(term), enriched.CorpusFrequency(term));
            Assert.Equal(
                plain.FieldDocumentFrequency(TextFields.Default, term),
                enriched.FieldDocumentFrequency(TextFields.Default, term));

            foreach (string id in new[] { "a", "b" })
            {
                Assert.Equal(plain.TermFrequency(id, term), enriched.TermFrequency(id, term));
                Assert.Equal(plain.GetTermPositions(id, term), enriched.GetTermPositions(id, term));
                Assert.Equal(
                    plain.FieldTermFrequency(id, TextFields.Default, term),
                    enriched.FieldTermFrequency(id, TextFields.Default, term));
                Assert.Equal(
                    plain.FieldLength(id, TextFields.Default),
                    enriched.FieldLength(id, TextFields.Default));
            }
        }

        Assert.Equal(
            plain.AverageFieldLength(TextFields.Default),
            enriched.AverageFieldLength(TextFields.Default));

        // Mutation forwards too: removing a document on either side leaves the other in the
        // same state, because the two indexes were built from the same corpus.
        Assert.True(enriched.Remove("b"));
        Assert.False(enriched.Contains("b"));
        Assert.True(plain.Remove("b"));
        Assert.False(plain.Contains("b"));
        Assert.Equal(plain.Count, enriched.Count);
    }

    [Fact]
    public void Constructor_RequiresAnEnumerationCapableIndex()
    {
        Assert.Throws<ArgumentException>(
            () => new ContextEnrichingIndex(new BareIndex(), new NoOpEnricher()));
    }

    [Fact]
    public void CandidateEnumeration_RejectsNullTermsAtTheCall_NotAtTheFirstMoveNext()
    {
        var index = new ContextEnrichingIndex(
            new InMemoryTextIndex(),
            new NoOpEnricher());

        index.Add(new SearchDocument("a", "alpha beta"));

        // Both are iterators, and a yield method's body does not run until MoveNext. Asserting only
        // that the sequence throws would pass against a lazy check that had done nothing wrong on
        // this line, so the sequence is deliberately not enumerated: the exception has to be
        // observable from the call alone.
        Assert.Throws<ArgumentNullException>(() => index.GetCandidateDocuments(null!));
        Assert.Throws<ArgumentNullException>(
            () => ((IUnorderedCandidateIndex)index).GetCandidatesUnordered(null!));
    }

    /// <summary>An enricher that re-ids the document — which the index must reject.</summary>
    private sealed class IdSwappingEnricher : IChunkContextEnricher
    {
        public string Name => "id-swapper";

        public SearchDocument Enrich(SearchDocument document) =>
            document with { Id = "changed" };
    }

    /// <summary>An enricher that changes nothing, so the facade has nothing to add.</summary>
    private sealed class NoOpEnricher : IChunkContextEnricher
    {
        public string Name => "no-op";

        public SearchDocument Enrich(SearchDocument document) => document;
    }

    /// <summary>The bare ITextIndex a caller could hand over, lacking every fast-path capability.</summary>
    private sealed class BareIndex : ITextIndex
    {
        private readonly Dictionary<string, SearchDocument> _held = new(StringComparer.Ordinal);

        public IReadOnlyCollection<SearchDocument> Documents => _held.Values.ToList();

        public int Count => _held.Count;

        public double AverageDocumentLength => 0;

        public int VocabularySize => 0;

        public long CorpusTokenCount => 0;

        public void Index(IEnumerable<SearchDocument> documents)
        {
            Clear();
            foreach (var document in documents)
                Add(document);
        }

        public void Add(SearchDocument document) => _held[document.Id] = document;

        public bool Remove(string documentId) => _held.Remove(documentId);

        public void Clear() => _held.Clear();

        public bool Contains(string documentId) => _held.ContainsKey(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => Array.Empty<string>();

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) => Array.Empty<int>();

        public int DocumentFrequency(string term) => 0;

        public int CorpusFrequency(string term) => 0;

        public int TermFrequency(string documentId, string term) => 0;

        public int DocumentLength(string documentId) => 0;

        public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document) =>
            _held.TryGetValue(documentId, out document);
    }
}