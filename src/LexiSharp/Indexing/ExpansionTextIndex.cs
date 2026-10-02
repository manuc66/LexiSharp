using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Expansion;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// An <see cref="InMemoryTextIndex"/> whose documents are additionally indexed under the weak
/// terms returned by an <see cref="ITermExpander"/> — the « semantic lexical index ». The
/// search surface is identical to a plain in-memory index; the difference is purely that the
/// inverted lists carry a few conceptually associated terms per document, injected at synthetic
/// positions after the literal tokens.
/// </summary>
/// <remarks>
/// <para>
/// A query term that never appears in a document's text can now reach it through an expansion
/// entry: term frequency, document frequency, vocabulary (prefix/fuzzy expansion) and the
/// candidate-generation path all see the expanded collection. The original documents
/// (<see cref="Documents"/>, <see cref="TryGetDocument"/>) remain untouched, so highlighting
/// and facets keep working on the source text; <see cref="GetTerms"/> and
/// <see cref="DocumentLength"/> include the injected terms (they are real posting members).
/// </para>
/// <para>
/// Expansion positions start after a gap (<c>doc length + 2</c>), so phrase queries never
/// bridge a literal token and an expansion term; expansion terms themselves simply follow the
/// real token stream for the purposes of length statistics and candidate generation.
/// </para>
/// </remarks>
public sealed class ExpansionTextIndex : ITextIndex, ICandidateIndex, IUnorderedCandidateIndex, IVocabularyIndex, IAccumulatingIndex, IFieldStatisticsIndex
{
    private readonly InMemoryTextIndex _inner;
    private readonly ITermExpander _expander;

    /// <summary>
    /// Wraps the given expander around a fresh <see cref="InMemoryTextIndex"/>. The expansion
    /// is computed on every <see cref="Add"/> / <see cref="Index"/> from the document's own
    /// tokens, and the extra terms are injected into the underlying inverted lists.
    /// </summary>
    /// <param name="termExpander">The source of the additional, weak terms to index.</param>
    /// <param name="tokenizer">Optional tokenizer, matching what scored queries assume. Defaults
    /// to the stock one (shared between the index and <see cref="Tokenizer"/>).</param>
    public ExpansionTextIndex(ITermExpander termExpander, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(termExpander);
        _expander = termExpander;
        _inner = new InMemoryTextIndex(tokenizer);
    }

    /// <summary>The tokenizer shared with <see cref="InMemoryTextIndex"/>.</summary>
    public ITokenizer Tokenizer => _inner.Tokenizer;

    /// <inheritdoc />
    public IReadOnlyCollection<SearchDocument> Documents => _inner.Documents;

    /// <inheritdoc />
    public int Count => _inner.Count;

    /// <inheritdoc />
    public double AverageDocumentLength => _inner.AverageDocumentLength;

    /// <inheritdoc />
    public int VocabularySize => _inner.VocabularySize;

    /// <inheritdoc />
    public long CorpusTokenCount => _inner.CorpusTokenCount;

    /// <inheritdoc />
    public IEnumerable<string> Vocabulary => _inner.Vocabulary;

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

        _inner.Add(document);

        var terms = _inner.Tokenizer.Tokenize(document.Text);
        Expand(document.Id, terms);
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(documentId);
        return _inner.Remove(documentId);
    }

    /// <inheritdoc />
    public void Clear()
    {
        _inner.Clear();
    }

    /// <inheritdoc />
    public bool Contains(string documentId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(documentId);
        return _inner.Contains(documentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(documentId);
        return _inner.GetTerms(documentId);
    }

    /// <inheritdoc />
    public IReadOnlyList<int> GetTermPositions(string documentId, string term)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(documentId);
        ArgumentNullException.ThrowIfNullOrEmpty(term);
        return _inner.GetTermPositions(documentId, term);
    }

    /// <inheritdoc />
    public int DocumentFrequency(string term)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(term);
        return _inner.DocumentFrequency(term);
    }

    /// <inheritdoc />
    public int CorpusFrequency(string term)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(term);
        return _inner.CorpusFrequency(term);
    }

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(documentId);
        ArgumentNullException.ThrowIfNullOrEmpty(term);
        return _inner.TermFrequency(documentId, term);
    }

    /// <inheritdoc />
    public int DocumentLength(string documentId)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(documentId);
        return _inner.DocumentLength(documentId);
    }

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document)
    {
        ArgumentNullException.ThrowIfNullOrEmpty(documentId);
        return _inner.TryGetDocument(documentId, out document);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> Fields => _inner.Fields;

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped index does not implement <see cref="IFieldStatisticsIndex"/>.</exception>
    public int FieldTermFrequency(string documentId, string field, string term) =>
        FieldStatistics().FieldTermFrequency(documentId, field, term);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped index does not implement <see cref="IFieldStatisticsIndex"/>.</exception>
    public int FieldLength(string documentId, string field) => FieldStatistics().FieldLength(documentId, field);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped index does not implement <see cref="IFieldStatisticsIndex"/>.</exception>
    public double AverageFieldLength(string field) => FieldStatistics().AverageFieldLength(field);

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">The wrapped index does not implement <see cref="IFieldStatisticsIndex"/>.</exception>
    public int FieldDocumentFrequency(string field, string term) =>
        FieldStatistics().FieldDocumentFrequency(field, term);

    /// <summary>
    /// The wrapped index's field statistics, refused by name when it has none — the same per-call
    /// rule the capability-forwarding engines use, because C# has no way to implement an interface
    /// conditionally and a silent zero would make a field-weighted ranking wrong.
    /// </summary>
    private IFieldStatisticsIndex FieldStatistics() =>
        _inner as IFieldStatisticsIndex
        ?? throw new NotSupportedException($"{_inner.GetType().Name} does not implement {nameof(IFieldStatisticsIndex)}.");

    /// <inheritdoc />
    public IEnumerable<SearchDocument> GetCandidateDocuments(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return _inner.GetCandidateDocuments(terms);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwarded rather than reimplemented, so the semantic lexical path gets the same cheap
    /// candidate set the inner index offers. The union is computed in the inner posting lists
    /// either way — that is where expansion terms are posted.
    /// </remarks>
    IEnumerable<SearchDocument> IUnorderedCandidateIndex.GetCandidatesUnordered(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);
        return ((IUnorderedCandidateIndex)_inner).GetCandidatesUnordered(terms);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Forwarded rather than reimplemented, for the same reason as the unordered candidate path:
    /// the inverted lists the accumulation pass walks are the inner index's, and they carry the
    /// injected expansion terms. Not forwarding would leave the semantic lexical path on the
    /// per-document scoring loop while the plain index had moved on — the same silent gap
    /// <c>CandidateEnumerationOrderTests</c> guards for the candidate path.
    /// </remarks>
    int IAccumulatingIndex.OrdinalSpace => ((IAccumulatingIndex)_inner).OrdinalSpace;

    /// <inheritdoc />
    SearchDocument? IAccumulatingIndex.DocumentAt(int ordinal) =>
        ((IAccumulatingIndex)_inner).DocumentAt(ordinal);

    /// <inheritdoc />
    void IAccumulatingIndex.Accumulate<TWeight>(TWeight weight, ScoreAccumulator accumulator) =>
        ((IAccumulatingIndex)_inner).Accumulate(weight, accumulator);

    private void Expand(string documentId, IReadOnlyList<string> terms)
    {
        if (terms.Count == 0)
            return;

        var expansion = _expander.Expand(terms);
        if (expansion.Count == 0)
            return;

        _inner.AddExpansionTerms(documentId, new ExpansionTermEnumerable(expansion));
    }

    private sealed class ExpansionTermEnumerable(IReadOnlyCollection<ExpandedTerm> terms) : IEnumerable<string>
    {
        public IEnumerator<string> GetEnumerator()
        {
            foreach (var term in terms)
                yield return term.Term;
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}