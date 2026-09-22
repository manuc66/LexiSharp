using System.Diagnostics.CodeAnalysis;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Indexing;

/// <summary>
/// In-memory inverted index: for each term, the documents containing it and the
/// positions within each document. Exposes the corpus statistics required by scorers.
/// </summary>
/// <remarks>
/// Not thread-safe; mutate it from a single thread (or synchronize externally).
/// </remarks>
public sealed class InMemoryTextIndex : ICandidateIndex, IVocabularyIndex
{
    private readonly ITokenizer _tokenizer;

    private readonly Dictionary<string, SearchDocument> _documents = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, List<int>>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<string>> _tokens = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _lengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _corpusFrequencies = new(StringComparer.Ordinal);

    private readonly Dictionary<SearchDocument, long> _candidateMarks =
        new(ReferenceEqualityComparer.Instance);
    private long _candidateEpoch;

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

        var terms = _tokenizer.Tokenize(document.Text);
        _tokens[document.Id] = terms;
        _lengths[document.Id] = terms.Count;
        _totalTokens += terms.Count;

        for (int position = 0; position < terms.Count; position++)
        {
            string term = terms[position];

            if (!_postings.TryGetValue(term, out var postings))
            {
                postings = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                _postings[term] = postings;
            }

            if (!postings.TryGetValue(document.Id, out var positions))
            {
                positions = new List<int>(2);
                postings[document.Id] = positions;
            }

            positions.Add(position);

            _corpusFrequencies.TryGetValue(term, out int corpusCount);
            _corpusFrequencies[term] = corpusCount + 1;
        }
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

        return true;
    }

    /// <inheritdoc />
    public void Clear()
    {
        _documents.Clear();
        _postings.Clear();
        _tokens.Clear();
        _lengths.Clear();
        _corpusFrequencies.Clear();
        _candidateMarks.Clear();
        _candidateEpoch = 0;
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
        long epoch = MarkCandidates(terms);

        foreach (var document in _documents.Values)
        {
            if (_candidateMarks.TryGetValue(document, out long marked) && marked == epoch)
                yield return document;
        }
    }

    private long MarkCandidates(IReadOnlyList<string> terms)
    {
        // The marking dictionary is reused across queries and keyed by reference identity, so the
        // re-enumeration over the corpus performs plain identity lookups (no string hashing) while
        // the tie-break order stays the corpus order. Entries are never removed; a size cap
        // protects long-lived churning indexes.
        if (_candidateMarks.Count > _documents.Count + 1024)
            _candidateMarks.Clear();

        long epoch = ++_candidateEpoch;

        // Per-query hot path: LINQ Where on these loops would allocate per candidate. // NOSONAR:S3267
        foreach (var term in terms)
        {
            if (_postings.TryGetValue(term, out var postings))
            {
                foreach (var documentId in postings.Keys)
                    _candidateMarks[_documents[documentId]] = epoch;
            }
        }

        return epoch;
    }

    /// <inheritdoc />
    public TextIndexStatistics GetStatistics() => TextIndexStatistics.From(this);
}