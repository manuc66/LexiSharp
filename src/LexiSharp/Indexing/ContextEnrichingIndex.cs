using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;

namespace LexiSharp.Indexing;

/// <summary>
/// An <see cref="ITextIndex"/> whose documents first pass through an
/// <see cref="IChunkContextEnricher"/>: the <b>enriched</b> text is what gets tokenized and
/// scored, while the <b>original</b> document the caller supplied stays the one
/// <see cref="Documents"/>, <see cref="TryGetDocument"/> and every search result display.
/// The search surface is identical to a plain index; the difference is purely that the corpus
/// the scorer sees is the enriched one.
/// </summary>
/// <remarks>
/// <para>
/// What each side sees, spelled out:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>Enriched</b> — the inner index: its postings, statistics
/// (<see cref="DocumentFrequency"/>, <see cref="DocumentLength"/>, <see cref="GetTerms"/>,
/// <see cref="Vocabulary"/>, <see cref="Core.TextIndexStatistics"/>, ...) and its candidate
/// enumeration all describe what is scored.
/// </description></item>
/// <item><description>
/// <b>Original</b> — <see cref="Documents"/>, <see cref="TryGetDocument"/>, the candidate
/// documents handed to the engine, and therefore <c>SearchResult.Document</c>: results,
/// highlighting and facets keep working on the caller's source text, exactly as
/// <see cref="ExpansionTextIndex"/> keeps its documents untouched.
/// </description></item>
/// </list>
/// <para>
/// The enricher runs once per <see cref="Add"/>/<see cref="Index"/> from the caller's
/// document, synchronously; a model-backed implementation blocks on its async work (see
/// <see cref="IChunkContextEnricher"/>). A failing enricher propagates: indexing the original
/// when enrichment was asked for would change what is scored silently, so there is no
/// fallback. An enricher that changes the document id is rejected at <see cref="Add"/>, since
/// the raw store and the inner index are both keyed by the id the caller supplied.
/// </para>
/// <para>
/// The inner index must implement the capability interfaces the engine's fast paths rely on
/// (<see cref="ICandidateIndex"/>, <see cref="IVocabularyIndex"/>, and the internal
/// accumulating/unordered-candidate capabilities); the constructor validates this rather than
/// letting a search discover the gap mid-flight. Per-field statistics stay optional, exactly
/// as on a plain index.
/// </para>
/// </remarks>
public sealed class ContextEnrichingIndex : ITextIndex, ICandidateIndex, IUnorderedCandidateIndex, IVocabularyIndex, IAccumulatingIndex
{
    private readonly ITextIndex _inner;
    private readonly IChunkContextEnricher _enricher;

    /// <summary>Document id → the caller's original document, the display side.</summary>
    private readonly Dictionary<string, SearchDocument> _originals = new(StringComparer.Ordinal);

    /// <summary>
    /// Wraps the given enricher around <paramref name="index"/>. The inner index must implement
    /// <see cref="ICandidateIndex"/> and <see cref="IVocabularyIndex"/> (and the internal
    /// capability interfaces), or the constructor throws.
    /// </summary>
    /// <param name="index">The index to enrich documents before adding to.</param>
    /// <param name="enricher">The transformation applied to each document at index time.</param>
    /// <exception cref="ArgumentException"><paramref name="index"/> lacks a capability the engine's fast paths need.</exception>
    public ContextEnrichingIndex(ITextIndex index, IChunkContextEnricher enricher)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(enricher);

        if (index is not ICandidateIndex)
            throw new ArgumentException(
                $"{index.GetType().Name} does not provide candidate enumeration " +
                $"({nameof(ICandidateIndex)}), which the search engine's scoring paths rely on.",
                nameof(index));

        if (index is not IVocabularyIndex)
            throw new ArgumentException(
                $"{index.GetType().Name} does not expose its vocabulary " +
                $"({nameof(IVocabularyIndex)}), which prefix/fuzzy query expansion reads.",
                nameof(index));

        if (index is not IUnorderedCandidateIndex)
            throw new ArgumentException(
                $"{index.GetType().Name} does not provide the unordered candidate path, " +
                $"which the engine prefers when it does not need corpus order.",
                nameof(index));

        if (index is not IAccumulatingIndex)
            throw new ArgumentException(
                $"{index.GetType().Name} does not provide the term-at-a-time accumulation path, " +
                $"which the engine uses to score head queries.",
                nameof(index));

        _inner = index;
        _enricher = enricher;
    }

    /// <summary>The enricher applied to every document at index time.</summary>
    public IChunkContextEnricher Enricher => _enricher;

    /// <inheritdoc />
    /// <remarks>
    /// The caller's original documents, in the inner index's order — the display side of the
    /// dual-text split. The count is the indexed one either way.
    /// </remarks>
    public IReadOnlyCollection<SearchDocument> Documents => new OriginalDocumentView(this);

    /// <inheritdoc />
    public int Count => _inner.Count;

    /// <inheritdoc />
    public double AverageDocumentLength => _inner.AverageDocumentLength;

    /// <inheritdoc />
    public int StatisticDocumentCount => _inner.StatisticDocumentCount;

    /// <inheritdoc />
    public int VocabularySize => _inner.VocabularySize;

    /// <inheritdoc />
    public long CorpusTokenCount => _inner.CorpusTokenCount;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        Clear();

        foreach (var document in documents)
            Add(document);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var enriched = _enricher.Enrich(document);

        if (!string.Equals(enriched.Id, document.Id, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The enricher {_enricher.Name} changed the document id from '{document.Id}' to " +
                $"'{enriched.Id}'. The id the caller supplied keys the corpus; enrichment must preserve it.",
                nameof(document));
        }

        // Replacing an existing id, as the inner index does: drop the previous entry so the
        // raw store and the postings stay in step.
        if (_inner.Contains(document.Id))
        {
            _inner.Remove(document.Id);
            _originals.Remove(document.Id);
        }

        _inner.Add(enriched);
        _originals[document.Id] = document;
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        bool removed = _inner.Remove(documentId);

        if (removed)
            _originals.Remove(documentId);

        return removed;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _inner.Clear();
        _originals.Clear();
    }

    /// <inheritdoc />
    public bool Contains(string documentId) => _inner.Contains(documentId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId) => _inner.GetTerms(documentId);

    /// <inheritdoc />
    public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
        _inner.GetTermPositions(documentId, term);

    /// <inheritdoc />
    public int DocumentFrequency(string term) => _inner.DocumentFrequency(term);

    /// <inheritdoc />
    public int CorpusFrequency(string term) => _inner.CorpusFrequency(term);

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term) =>
        _inner.TermFrequency(documentId, term);

    /// <inheritdoc />
    public int DocumentLength(string documentId) => _inner.DocumentLength(documentId);

    /// <inheritdoc />
    /// <remarks>Returns the caller's original document, not the enriched one stored for scoring.</remarks>
    public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document)
    {
        if (_originals.TryGetValue(documentId, out var original))
        {
            document = original;
            return true;
        }

        document = null;
        return false;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Fields => _inner.Fields;

    /// <inheritdoc />
    public bool HasFieldStatistics => _inner.HasFieldStatistics;

    /// <inheritdoc />
    public int FieldTermFrequency(string documentId, string field, string term) =>
        _inner.FieldTermFrequency(documentId, field, term);

    /// <inheritdoc />
    public int FieldLength(string documentId, string field) => _inner.FieldLength(documentId, field);

    /// <inheritdoc />
    public double AverageFieldLength(string field) => _inner.AverageFieldLength(field);

    /// <inheritdoc />
    public int FieldDocumentFrequency(string field, string term) =>
        _inner.FieldDocumentFrequency(field, term);

    /// <inheritdoc />
    public TextIndexStatistics GetStatistics() => TextIndexStatistics.From(this);

    /// <inheritdoc />
    public IEnumerable<string> Vocabulary => ((IVocabularyIndex)_inner).Vocabulary;

    /// <inheritdoc />
    /// <remarks>
    /// The candidate documents are the inner ones — the enriched corpus is what scoring sees —
    /// resolved to the caller's originals, in the inner index's order, so the relative order
    /// contract of <see cref="ICandidateIndex"/> is preserved; the engine's ranking never
    /// depends on it (its window breaks ties on document id).
    /// </remarks>
    public IEnumerable<SearchDocument> GetCandidateDocuments(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        foreach (var document in ((ICandidateIndex)_inner).GetCandidateDocuments(terms))
            yield return Resolve(document);
    }

    /// <inheritdoc />
    IEnumerable<SearchDocument> IUnorderedCandidateIndex.GetCandidatesUnordered(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        foreach (var document in ((IUnorderedCandidateIndex)_inner).GetCandidatesUnordered(terms))
            yield return Resolve(document);
    }

    /// <inheritdoc />
    int IAccumulatingIndex.OrdinalSpace => ((IAccumulatingIndex)_inner).OrdinalSpace;

    /// <inheritdoc />
    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal)
    {
        var document = ((IAccumulatingIndex)_inner).DocumentAt(ordinal);
        return document is null ? null : Resolve(document);
    }

    /// <inheritdoc />
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator) =>
        ((IAccumulatingIndex)_inner).Accumulate(weight, accumulator);

    /// <summary>
    /// The caller's original for an inner (enriched) document. The fallback — the document
    /// itself — is unreachable in normal operation: <see cref="Add"/> and <see cref="Remove"/>
    /// keep the two stores in step, so every document the inner holds has an original.
    /// </summary>
    private SearchDocument Resolve(SearchDocument document) =>
        _originals.TryGetValue(document.Id, out var original) ? original : document;

    /// <summary>
    /// A live view over the inner index's order, resolving each document to its original —
    /// allocation-free to enumerate, like <c>InMemoryTextIndex.DocumentView</c>.
    /// </summary>
    private sealed class OriginalDocumentView(ContextEnrichingIndex owner) : IReadOnlyCollection<SearchDocument>
    {
        public int Count => owner._inner.Count;

        public IEnumerator<SearchDocument> GetEnumerator()
        {
            foreach (var document in owner._inner.Documents)
                yield return owner.Resolve(document);
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}