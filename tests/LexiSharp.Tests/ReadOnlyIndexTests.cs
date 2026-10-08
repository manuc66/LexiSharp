using System;
using System.Collections.Generic;
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Ranking;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// An engine searches an index it cannot write to, and says so when a write is asked of it.
/// </summary>
/// <remarks>
/// This is what a corpus read from a segment — or from anywhere else immutable — needs: the read
/// surface, no write capability, and a refusal that names the reason rather than a silent no-op.
/// </remarks>
public class ReadOnlyIndexTests
{
    [Fact]
    public void AnEngineOverAReadOnlyIndexSearchesIt()
    {
        var writable = new InMemoryTextIndex();
        writable.Index(new[]
        {
            new SearchDocument("1", "the search engine ranks documents with bm25"),
            new SearchDocument("2", "bm25 is a ranking function for search"),
            new SearchDocument("3", "unrelated text about cooking"),
        });

        var readOnly = new ReadOnlyIndex(writable);
        var engineOverWritable = new RankedTextSearchEngine(writable, new Bm25Scorer());
        var engineOverReadOnly = new RankedTextSearchEngine(readOnly, new Bm25Scorer());

        var expected = engineOverWritable.Search("search ranking");
        var actual = engineOverReadOnly.Search("search ranking");

        Assert.Equal(expected.Count, actual.Count);

        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i].DocumentId, actual[i].DocumentId);
            Assert.Equal(expected[i].Score, actual[i].Score);
        }

        Assert.Equal(writable.Count, readOnly.Count);
        Assert.Equal(writable.DocumentFrequency("bm25"), readOnly.DocumentFrequency("bm25"));
        Assert.Equal(writable.DocumentLength("2"), readOnly.DocumentLength("2"));
    }

    [Fact]
    public void TheMutationsRefuseAReadOnlyIndex()
    {
        var writable = new InMemoryTextIndex();
        writable.Add(new SearchDocument("1", "one document"));
        var engine = new RankedTextSearchEngine(new ReadOnlyIndex(writable), new Bm25Scorer());

        // Each refusal names the type, so a caller who did not expect it can see what it holds.
        Assert.Contains("read-only", Assert.Throws<NotSupportedException>(() => engine.Add(new SearchDocument("2", "two"))).Message);
        Assert.Contains("read-only", Assert.Throws<NotSupportedException>(() => engine.Remove("1")).Message);
        Assert.Contains("read-only", Assert.Throws<NotSupportedException>(() => engine.Clear()).Message);
        Assert.Contains("read-only", Assert.Throws<NotSupportedException>(() => engine.Index(new[] { new SearchDocument("3", "three") })).Message);

        // And nothing was written through the refusal.
        Assert.Equal(1, writable.Count);
    }

    /// <summary>The read surface, delegated; no write capability at all.</summary>
    private sealed class ReadOnlyIndex(IReadOnlyTextIndex inner) : IReadOnlyTextIndex
    {
        public IReadOnlyCollection<SearchDocument> Documents => inner.Documents;

        public int Count => inner.Count;

        public double AverageDocumentLength => inner.AverageDocumentLength;

        public int VocabularySize => inner.VocabularySize;

        public long CorpusTokenCount => inner.CorpusTokenCount;

        public bool Contains(string documentId) => inner.Contains(documentId);

        public IReadOnlyList<string> GetTerms(string documentId) => inner.GetTerms(documentId);

        public IReadOnlyList<int> GetTermPositions(string documentId, string term) => inner.GetTermPositions(documentId, term);

        public int DocumentFrequency(string term) => inner.DocumentFrequency(term);

        public int CorpusFrequency(string term) => inner.CorpusFrequency(term);

        public int TermFrequency(string documentId, string term) => inner.TermFrequency(documentId, term);

        public int DocumentLength(string documentId) => inner.DocumentLength(documentId);

        public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document) =>
            inner.TryGetDocument(documentId, out document);
    }
}
