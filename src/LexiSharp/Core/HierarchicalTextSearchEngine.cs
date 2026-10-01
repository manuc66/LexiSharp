namespace LexiSharp.Core;

/// <summary>
/// A decorator that layers a <see cref="DocumentHierarchy"/> over an existing engine: searches
/// and writes are forwarded unchanged — flat retrieval still finds the precise node —
/// and the <see cref="IStructureAwareSearchEngine"/> capability answers the navigation
/// question that follows a hit: which ancestors (sections, chapters, books) does it belong to?
/// </summary>
/// <remarks>
/// <para>
/// The hierarchy is <b>app-declared</b>: the caller indexes whatever units it searches (the
/// leaf chunks) and declares the tree those units live in through the
/// <see cref="DocumentHierarchy"/>. This engine neither chunks documents nor changes what is
/// scored — the « overview to precise node » navigation is a second surface
/// (<see cref="GetAncestors"/>) on top of an unchanged ranking.
/// </para>
/// <para>
/// Resolving ancestor ids to <see cref="SearchDocument"/>s needs the caller's ledger, because
/// no <see cref="ITextSearchEngine"/> contract returns a document by id. Pass a document
/// resolver when the documents should be resolvable; without one,
/// <see cref="GetAncestors"/> throws rather than returning stubs, and
/// <see cref="GetAncestorIds"/> remains available.
/// </para>
/// </remarks>
public sealed class HierarchicalTextSearchEngine : IStructureAwareSearchEngine
{
    private readonly ITextSearchEngine _inner;
    private readonly DocumentHierarchy _hierarchy;
    private readonly Func<string, SearchDocument?>? _resolveDocument;

    /// <param name="inner">The engine that actually indexes and searches the documents.</param>
    /// <param name="hierarchy">The parent/child forest the documents live in.</param>
    /// <param name="resolveDocument">
    /// Optional resolver turning an ancestor id into its document (the caller's own store —
    /// the ledger <c>LexiSharpIndex</c> keeps for its hits). Pass <c>null</c> and
    /// <see cref="GetAncestors"/> throws, leaving <see cref="GetAncestorIds"/> available.
    /// </param>
    public HierarchicalTextSearchEngine(
        ITextSearchEngine inner,
        DocumentHierarchy hierarchy,
        Func<string, SearchDocument?>? resolveDocument = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(hierarchy);

        _inner = inner;
        _hierarchy = hierarchy;
        _resolveDocument = resolveDocument;
    }

    /// <summary>The hierarchy this engine navigates.</summary>
    public DocumentHierarchy Hierarchy => _hierarchy;

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
    public bool Remove(string documentId) => _inner.Remove(documentId);

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
    public IReadOnlyList<string> GetAncestorIds(string documentId) =>
        _hierarchy.GetAncestors(documentId);

    /// <inheritdoc />
    public IReadOnlyList<SearchDocument> GetAncestors(string documentId)
    {
        if (_resolveDocument is null)
        {
            throw new NotSupportedException(
                $"{nameof(HierarchicalTextSearchEngine)} cannot resolve ancestor documents: no " +
                $"document resolver was provided to the constructor. Call " +
                $"{nameof(GetAncestorIds)} for the ids, or construct with resolveDocument.");
        }

        var ancestors = _hierarchy.GetAncestors(documentId);

        if (ancestors.Count == 0)
            return Array.Empty<SearchDocument>();

        var documents = new List<SearchDocument>(ancestors.Count);

        foreach (string ancestorId in ancestors)
        {
            // The caller's ledger is authoritative; an ancestor it cannot produce is simply
            // absent from the breadcrumb rather than represented by a fabricated document.
            if (_resolveDocument(ancestorId) is { } document)
                documents.Add(document);
        }

        return documents;
    }
}