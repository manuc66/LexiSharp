using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// In-memory inverted index: for each term, the documents containing it and the
/// positions within each document. Exposes the corpus statistics required by scorers.
/// </summary>
/// <remarks>
/// Mutations (<c>Add</c>/<c>Remove</c>/<c>Clear</c>/...) are not thread-safe: apply them from a
/// single thread (or synchronize externally). Read-only queries hold no shared mutable state and
/// may run concurrently with one another.
/// </remarks>
public sealed class InMemoryTextIndex : ICandidateIndex, IVocabularyIndex
{
    private readonly ITokenizer _tokenizer;

    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, List<int>>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _corpusFrequencies = new(StringComparer.Ordinal);

    // Per-field statistics, populated from SearchDocument.TextFields. The document's main Text is
    // the field TextFields.Default, and it is tracked here as well as in the flat structures above
    // so a field-aware scorer can read every field through one path.
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, int>>> _fieldFrequencies =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _fieldLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _fieldTotalTokens = new(StringComparer.Ordinal);

    private long _totalTokens;

    public InMemoryTextIndex(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? LexiSharp.Linguistics.Tokenizer.Default;
    }

    /// <summary>The tokenizer used to split documents and queries into terms.</summary>
    public ITokenizer Tokenizer => _tokenizer;

    /// <inheritdoc />
    public IReadOnlyCollection<SearchDocument> Documents => _documents.Values;

    /// <inheritdoc />
    public int Count => _documents.Count;

    /// <inheritdoc />
    public double AverageDocumentLength =>
        Count == 0 ? 0 : (double)_totalTokens / Count;

    /// <inheritdoc />
    public int VocabularySize => _postings.Count;

    /// <inheritdoc />
    public IEnumerable<string> Vocabulary => _postings.Keys;

    /// <inheritdoc />
    public long CorpusTokenCount => _totalTokens;

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

        if (_documents.ContainsKey(document.Id))
            Remove(document.Id);

        _documents[document.Id] = document;

        var mainTerms = _tokenizer.Tokenize(document.Text);

        for (int position = 0; position < mainTerms.Count; position++)
            Post(document.Id, mainTerms[position], position);

        RecordField(TextFields.Default, document.Id, mainTerms);

        // Counted once, here, so the flat corpus total is right on both branches below. Every
        // length-normalized scorer divides by it, so missing it silently rescales the whole
        // ranking.
        _totalTokens += mainTerms.Count;

        if (document.TextFields is not { Count: > 0 })
        {
            _tokens[document.Id] = mainTerms;
            _lengths[document.Id] = mainTerms.Count;
            return;
        }

        // The flat view is the union of every field, so a document that only matches inside a
        // TextField is still reachable by candidate generation and still scores. The list is only
        // allocated when a document actually has text fields.
        var allTerms = new List<string>(mainTerms);
        int nextPosition = mainTerms.Count;

        foreach (var (field, text) in document.TextFields)
        {
            TextFields.Validate(field, nameof(SearchDocument.TextFields));

            var fieldTerms = _tokenizer.Tokenize(text);

            // A gap between the previous run and this one, so a quoted phrase can match inside
            // one field but never bridge two of them.
            nextPosition += FieldPositionGap;

            for (int i = 0; i < fieldTerms.Count; i++)
                Post(document.Id, fieldTerms[i], nextPosition + i);

            nextPosition += fieldTerms.Count;

            RecordField(field, document.Id, fieldTerms);

            allTerms.AddRange(fieldTerms);
            _totalTokens += fieldTerms.Count;
        }

        _tokens[document.Id] = allTerms;
        _lengths[document.Id] = allTerms.Count;
    }

    /// <summary>
    /// Number of unused positions inserted between two token runs (main text to first field, field
    /// to field), so positions belonging to different fields are never adjacent and a phrase
    /// query cannot span a field boundary. Same idea, and same value, as
    /// <see cref="ExpansionPositionOffset"/>.
    /// </summary>
    private const int FieldPositionGap = 2;

    /// <summary>
    /// Adds one occurrence of <paramref name="term"/> to the flat inverted lists at
    /// <paramref name="position"/>.
    /// </summary>
    private void Post(string documentId, string term, int position)
    {
        if (!_postings.TryGetValue(term, out var postings))
        {
            postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            _postings[term] = postings;
        }

        if (!postings.TryGetValue(documentId, out var positions))
        {
            positions = new List<int>(2);
            postings[documentId] = positions;
        }

        positions.Add(position);

        _corpusFrequencies.TryGetValue(term, out int corpusCount);
        _corpusFrequencies[term] = corpusCount + 1;
    }

    /// <summary>
    /// Records one field's tokens in the per-field statistics, replacing whatever the document
    /// had for that field. The document is expected to have been removed already.
    /// </summary>
    private void RecordField(string field, string documentId, IReadOnlyList<string> terms)
    {
        if (!_fieldFrequencies.TryGetValue(field, out var byTerm))
        {
            byTerm = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            _fieldFrequencies[field] = byTerm;
        }

        foreach (string term in terms)
        {
            if (!byTerm.TryGetValue(term, out var byDocument))
            {
                byDocument = new Dictionary<string, int>(StringComparer.Ordinal);
                byTerm[term] = byDocument;
            }

            byDocument.TryGetValue(documentId, out int count);
            byDocument[documentId] = count + 1;
        }

        if (!_fieldLengths.TryGetValue(field, out var lengths))
        {
            lengths = new Dictionary<string, int>(StringComparer.Ordinal);
            _fieldLengths[field] = lengths;
        }

        lengths[documentId] = terms.Count;
        _fieldTotalTokens[field] = (_fieldTotalTokens.TryGetValue(field, out long total) ? total : 0)
            + terms.Count;
    }

    /// <summary>
    /// Number of synthetic positions inserted between a document's literal tokens and its
    /// expansion terms, so a phrase query can never bridge across the boundary.
    /// </summary>
    private const int ExpansionPositionOffset = 2;

    /// <summary>
    /// Indexes additional terms for an already-indexed document (the « semantic lexical »
    /// expansion). Each term counts once, at a synthetic position strictly after the
    /// document's literal tokens, and participates in the same statistics (document length,
    /// corpus frequencies) as any regular token. Terms the document already contains are
    /// skipped. The <see cref="SearchDocument"/> itself is left untouched.
    /// </summary>
    /// <param name="documentId">An id currently present in the index.</param>
    /// <param name="additionalTerms">The distinct terms to add.</param>
    public void AddExpansionTerms(string documentId, IEnumerable<string> additionalTerms)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        ArgumentNullException.ThrowIfNull(additionalTerms);

        if (!_documents.TryGetValue(documentId, out _))
            throw new KeyNotFoundException($"Unknown document id: '{documentId}'.");

        var existing = new HashSet<string>(_tokens[documentId], StringComparer.Ordinal);
        int nextPosition = _lengths[documentId] + ExpansionPositionOffset;

        var added = new List<string>();
        int addedCount = 0;

        foreach (var term in additionalTerms)
        {
            if (term.Length == 0 || !existing.Add(term))
                continue;

            if (!_postings.TryGetValue(term, out var postings))
            {
                postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                _postings[term] = postings;
            }

            if (!postings.TryGetValue(documentId, out var positions))
            {
                positions = new List<int>(1);
                postings[documentId] = positions;
            }

            positions.Add(nextPosition++);
            added.Add(term);
            addedCount++;

            _corpusFrequencies.TryGetValue(term, out int corpusCount);
            _corpusFrequencies[term] = corpusCount + 1;
        }

        if (addedCount == 0)
            return;

        var allTokens = new List<string>(_tokens[documentId]);
        allTokens.AddRange(added);
        _tokens[documentId] = allTokens;

        _lengths[documentId] += addedCount;
        _totalTokens += addedCount;
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        if (!_documents.Remove(documentId))
            return false;

        if (_tokens.TryGetValue(documentId, out var terms))
        {
            foreach (var term in terms)
            {
                if (_postings.TryGetValue(term, out var postings) &&
                    postings.Remove(documentId) && postings.Count == 0)
                {
                    _postings.Remove(term);
                }

                _corpusFrequencies.TryGetValue(term, out int corpusCount);
                if (corpusCount <= 1)
                    _corpusFrequencies.Remove(term);
                else
                    _corpusFrequencies[term] = corpusCount - 1;
            }
        }

        _tokens.Remove(documentId);

        if (_lengths.Remove(documentId, out int length))
            _totalTokens -= length;

        RemoveFromFieldStatistics(documentId);

        return true;
    }

    /// <summary>
    /// Drops one document from every per-field structure, and forgets a field once no document
    /// carries it any more, so <see cref="Fields"/> never reports a stale name.
    /// </summary>
    private void RemoveFromFieldStatistics(string documentId)
    {
        foreach (string field in _fieldFrequencies.Keys.ToList())
        {
            if (!_fieldFrequencies.TryGetValue(field, out var byTerm))
                continue;

            foreach (var entry in byTerm)
            {
                if (entry.Value.Remove(documentId) && entry.Value.Count == 0)
                    byTerm.Remove(entry.Key);
            }

            if (byTerm.Count == 0)
                _fieldFrequencies.Remove(field);
        }

        foreach (string field in _fieldLengths.Keys.ToList())
        {
            if (!_fieldLengths.TryGetValue(field, out var lengths))
                continue;

            if (!lengths.Remove(documentId, out int removedLength))
                continue;

            if (_fieldTotalTokens.TryGetValue(field, out long total) &&
                total - removedLength <= 0)
            {
                _fieldTotalTokens.Remove(field);
            }
            else
            {
                _fieldTotalTokens[field] = total - removedLength;
            }

            if (lengths.Count == 0)
            {
                _fieldLengths.Remove(field);
                _fieldTotalTokens.Remove(field);
            }
        }
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _postings.Clear();
        _tokens.Clear();
        _lengths.Clear();
        _corpusFrequencies.Clear();
        _fieldFrequencies.Clear();
        _fieldLengths.Clear();
        _fieldTotalTokens.Clear();
        _totalTokens = 0;
    }

    /// <inheritdoc />
    public bool Contains(string documentId) => _documents.ContainsKey(documentId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId) =>
        _tokens.TryGetValue(documentId, out var terms)
            ? terms
            : Array.Empty<string>();

    /// <inheritdoc />
    public IReadOnlyList<int> GetTermPositions(string documentId, string term)
    {
        if (_postings.TryGetValue(term, out var postings) &&
            postings.TryGetValue(documentId, out var positions))
        {
            return positions;
        }

        return Array.Empty<int>();
    }

    /// <inheritdoc />
    public int DocumentFrequency(string term) =>
        _postings.TryGetValue(term, out var postings)
            ? postings.Count
            : 0;

    /// <inheritdoc />
    public int CorpusFrequency(string term) =>
        _corpusFrequencies.TryGetValue(term, out int count)
            ? count
            : 0;

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term) =>
        _postings.TryGetValue(term, out var postings) &&
        postings.TryGetValue(documentId, out var positions)
            ? positions.Count
            : 0;

    /// <inheritdoc />
    public int DocumentLength(string documentId) =>
        _lengths.TryGetValue(documentId, out int length)
            ? length
            : 0;

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [NotNullWhen(true)] out SearchDocument? document) =>
        _documents.TryGetValue(documentId, out document);

    /// <inheritdoc />
    public IEnumerable<SearchDocument> GetCandidateDocuments(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0 || _postings.Count == 0)
            return Array.Empty<SearchDocument>();

        return terms.Count == 1
            ? EnumerateSingleTerm(terms[0])
            : EnumerateMultiTerm(terms);
    }

    // A single term enumerates its posting list in document-insertion order, which is exactly
    // the corpus order — no candidate set needed, indistinguishable from a full scan.
    private IEnumerable<SearchDocument> EnumerateSingleTerm(string term)
    {
        if (!_postings.TryGetValue(term, out var postings) || postings.Count == 0)
            yield break;

        foreach (var documentId in postings.Keys)
        {
            if (_documents.TryGetValue(documentId, out var document))
                yield return document;
        }
    }

    private IEnumerable<SearchDocument> EnumerateMultiTerm(IReadOnlyList<string> terms)
    {
        // Mark the candidates in a set local to this call. This used to be a shared,
        // epoch-stamped dictionary reused across queries, but that is a read-vs-read race:
        // two concurrent searches mutate the same map, so one can overwrite the other's
        // marks and drop (or leak) candidates. The marking state must never outlive the
        // call, which also keeps it correct across interleaved enumerations.
        var candidates = new HashSet<SearchDocument>(ReferenceEqualityComparer.Instance);

        // Per-query hot path: LINQ Where on these loops would allocate per candidate. // NOSONAR:S3267
        foreach (var term in terms)
        {
            if (_postings.TryGetValue(term, out var postings))
            {
                foreach (var documentId in postings.Keys)
                    candidates.Add(_documents[documentId]);
            }
        }

        if (candidates.Count == 0)
            yield break;

        // Re-enumerate the corpus so ties keep the corpus order, performing plain identity
        // lookups (no string hashing) against the local candidate set.
        foreach (var document in _documents.Values)
        {
            if (candidates.Contains(document))
                yield return document;
        }
    }

    /// <inheritdoc />
    public TextIndexStatistics GetStatistics() => TextIndexStatistics.From(this);

    /// <inheritdoc />
    /// <remarks>
    /// The default field always comes first, the named ones after it in ordinal order, so the
    /// sequence is stable across runs and safe to assert on.
    /// </remarks>
    public IReadOnlyCollection<string> Fields
    {
        get
        {
            // The default field lives in the same map as the named ones, so it has to be filtered
            // out here rather than prepended blindly — it is not in `named`.
            if (_fieldLengths.Count == 0)
                return [TextFields.Default];

            var named = _fieldLengths.Keys
                .Where(name => name != TextFields.Default)
                .Order(StringComparer.Ordinal)
                .ToArray();

            var fields = new string[named.Length + 1];
            fields[0] = TextFields.Default;
            named.CopyTo(fields, 1);
            return fields;
        }
    }

    /// <inheritdoc />
    public bool HasFieldStatistics => true;

    /// <inheritdoc />
    public int FieldTermFrequency(string documentId, string field, string term)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        TextFields.Validate(field, nameof(field));
        ArgumentNullException.ThrowIfNull(term);

        return _fieldFrequencies.TryGetValue(field, out var byTerm) &&
               byTerm.TryGetValue(term, out var byDocument) &&
               byDocument.TryGetValue(documentId, out int count)
            ? count
            : 0;
    }

    /// <inheritdoc />
    public int FieldLength(string documentId, string field)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentId);
        TextFields.Validate(field, nameof(field));

        return _fieldLengths.TryGetValue(field, out var lengths) &&
               lengths.TryGetValue(documentId, out int length)
            ? length
            : 0;
    }

    /// <inheritdoc />
    public double AverageFieldLength(string field)
    {
        TextFields.Validate(field, nameof(field));

        if (!_fieldLengths.TryGetValue(field, out var lengths) || lengths.Count == 0)
            return 0;

        return (double)(_fieldTotalTokens.TryGetValue(field, out long total) ? total : 0)
            / lengths.Count;
    }

    /// <inheritdoc />
    public int FieldDocumentFrequency(string field, string term)
    {
        TextFields.Validate(field, nameof(field));
        ArgumentNullException.ThrowIfNull(term);

        return _fieldFrequencies.TryGetValue(field, out var byTerm) &&
               byTerm.TryGetValue(term, out var byDocument)
            ? byDocument.Count
            : 0;
    }
}