using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Compression;

/// <summary>
/// A decorator that adds the <see cref="ICompressingSearchEngine"/> surface to any engine:
/// searching and writing are forwarded unchanged, and <see cref="SearchCompressed"/> returns
/// each hit's text reduced to the query-relevant content by an <see cref="IContextCompressor"/>.
/// </summary>
/// <remarks>
/// <para>
/// The flat <see cref="ITextSearchEngine.Search(string, SearchOptions)"/> contract is
/// untouched — the full text still comes back. <see cref="SearchCompressed"/> runs the same
/// search, tokenizes the query with the engine's own tokenizer, and hands each returned
/// document's text to the compressor; the ranking (ids and scores) is the inner engine's, and
/// each hit's <see cref="CompressedHit.CompressionRatio"/> is measured here, so the ratio has
/// exactly one definition (<c>compressed.Length / source.Length</c>, 0 when the source is
/// empty).
/// </para>
/// <para>
/// This is the last stage before a generation model: the page a RAG application sends as
/// context, reduced to the passages that answer the query. It is deliberately a separate
/// surface rather than a rewrite of the returned <see cref="SearchResult"/>s, because a search
/// result promises the original indexed document.
/// </para>
/// </remarks>
public sealed class CompressingTextSearchEngine : ICompressingSearchEngine
{
    /// <summary>Name this engine reports to <see cref="RetrievalTelemetry"/> and its metrics sinks.</summary>
    public const string EngineName = "CompressingTextSearchEngine";

    private readonly ITextSearchEngine _inner;
    private readonly IContextCompressor _compressor;
    private readonly ITokenizer _tokenizer;

    /// <param name="inner">The engine that actually ranks the documents.</param>
    /// <param name="compressor">The query-aware text compressor.</param>
    /// <param name="tokenizer">
    /// Tokenizer for the query, whose terms are handed to the compressor; should be the one the
    /// compressor expects to see.
    /// </param>
    public CompressingTextSearchEngine(
        ITextSearchEngine inner,
        IContextCompressor compressor,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(compressor);

        _inner = inner;
        _compressor = compressor;
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <summary>The compressor applied to every returned hit.</summary>
    public IContextCompressor Compressor => _compressor;

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _inner.Index(documents);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        _inner.Add(document);
    }

    /// <inheritdoc />
    public void Remove(string documentId) => _inner.Remove(documentId);

    /// <inheritdoc />
    public void Clear() => _inner.Clear();

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _inner.Search(query, options);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null) =>
        _inner.Search(query, options);

    /// <inheritdoc />
    public IReadOnlyList<CompressedHit> SearchCompressed(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var terms = _tokenizer.Tokenize(query);
        var results = _inner.Search(query, options);
        var hits = new CompressedHit[results.Count];

        for (int i = 0; i < results.Count; i++)
        {
            var result = results[i];
            string source = result.Document.Text;
            string compressed = _compressor.Compress(source, terms);
            double ratio = source.Length == 0 ? 0 : compressed.Length / (double)source.Length;

            hits[i] = new CompressedHit(result.DocumentId, result.Score, compressed, ratio);
        }

        return hits;
    }
}