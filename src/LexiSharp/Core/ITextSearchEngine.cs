namespace LexiSharp.Core;

/// <summary>
/// The public search surface of the library: index documents and rank them against a query.
/// </summary>
/// <remarks>
/// Thread-safety: concurrent <see cref="Search(string, SearchOptions)"/> calls are supported.
/// <see cref="Index"/>,
/// <see cref="Add"/>, <see cref="Remove"/> and <see cref="Clear"/> must not run concurrently with
/// each other or with a <see cref="Search(string, SearchOptions)"/>. Engines do not synchronize writes internally;
/// synchronize externally (e.g. train off-lock on a snapshot, then atomically swap the engine
/// instance) when writes and reads can overlap.
/// </remarks>
public interface ITextSearchEngine
{
    /// <summary>Indexes a batch of documents, replacing any previously indexed content.</summary>
    void Index(IEnumerable<SearchDocument> documents);

    /// <summary>Adds a single document to the engine.</summary>
    void Add(SearchDocument document);

    /// <summary>Removes the document with the given id, if present.</summary>
    void Remove(string documentId);

    /// <summary>Drops every document from the engine.</summary>
    void Clear();

    /// <summary>
    /// Searches the index for the query and returns the top-ranked documents.
    /// </summary>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional search options (<see cref="SearchOptions.Default"/> when null).</param>
    IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null);

    /// <summary>
    /// Searches the index for the query and returns the top-ranked documents, without
    /// materializing the query as a string.
    /// </summary>
    /// <remarks>
    /// The default implementation copies the span into a string and forwards to
    /// <see cref="Search(string, SearchOptions?)"/>; engines that can tokenize a span directly
    /// should override it. A <c>null</c> literal still binds to the <see cref="string"/>
    /// overload, so the span path never needs a null check.
    /// </remarks>
    /// <param name="query">Raw query text; it is tokenized internally.</param>
    /// <param name="options">Optional search options (<see cref="SearchOptions.Default"/> when null).</param>
    IReadOnlyList<SearchResult> Search(ReadOnlySpan<char> query, SearchOptions? options = null) =>
        Search(query.ToString(), options);
}