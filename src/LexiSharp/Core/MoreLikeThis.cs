using LexiSharp.Linguistics;

namespace LexiSharp.Core;

/// <summary>
/// Builds a query from one already-indexed document and re-runs it through an engine, so the
/// caller gets the documents most like it — the classic "more like this" affordance.
/// </summary>
/// <remarks>
/// <para>
/// The query is the document's highest-frequency terms (after tokenization), joined by
/// whitespace, with the source document excluded from the results. It is a deliberately
/// simple, dependency-free approximation: no IDF weighting, no term vectors, no per-field
/// boost. It reuses whatever scorer the engine already holds, so the ranking stays
/// consistent with normal searches.
/// </para>
/// <para>
/// Tokenization uses <see cref="Tokenizer"/> by default; pass a configured tokenizer
/// (stemming, stop words, ...) to match the one the index was built with.
/// </para>
/// </remarks>
public static class MoreLikeThis
{
    /// <summary>
    /// Returns the documents most similar to <paramref name="documentId"/>, excluding the
    /// document itself.
    /// </summary>
    /// <param name="engine">Engine to search with.</param>
    /// <param name="index">Index holding the document and its corpus statistics.</param>
    /// <param name="documentId">Id of the source document.</param>
    /// <param name="options">Search options; <see cref="SearchOptions.ExcludedDocumentIds"/> is
    /// extended to include the source document.</param>
    /// <param name="maxQueryTerms">Maximum number of terms taken from the source document.</param>
    /// <param name="tokenizer">Tokenizer used to extract terms; defaults to <see cref="Tokenizer"/>.</param>
    public static IReadOnlyList<SearchResult> Search(
        ITextSearchEngine engine,
        IReadOnlyTextIndex index,
        string documentId,
        SearchOptions? options = null,
        int maxQueryTerms = 25,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(index);
        ArgumentException.ThrowIfNullOrWhiteSpace(documentId);

        if (maxQueryTerms <= 0)
            return Array.Empty<SearchResult>();

        SearchDocument? source = null;

        foreach (var document in index.Documents)
        {
            if (string.Equals(document.Id, documentId, StringComparison.Ordinal))
            {
                source = document;
                break;
            }
        }

        if (source is null)
            return Array.Empty<SearchResult>();

        tokenizer ??= new Tokenizer();

        var terms = tokenizer.Tokenize(source.Text);

        // Term frequency, then take the most frequent terms. Ties broken by term ordinal so
        // the result is deterministic regardless of corpus order.
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var term in terms)
        {
            if (counts.TryGetValue(term, out int count))
                counts[term] = count + 1;
            else
                counts[term] = 1;
        }

        var queryTerms = counts
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(maxQueryTerms)
            .Select(pair => pair.Key)
            .ToArray();

        if (queryTerms.Length == 0)
            return Array.Empty<SearchResult>();

        string query = string.Join(' ', queryTerms);

        var baseOptions = options ?? SearchOptions.Default;

        IReadOnlySet<string> excluded;

        if (baseOptions.ExcludedDocumentIds is { Count: > 0 })
        {
            var set = new HashSet<string>(baseOptions.ExcludedDocumentIds, StringComparer.Ordinal)
            {
                documentId,
            };
            excluded = set;
        }
        else
        {
            excluded = new HashSet<string>(StringComparer.Ordinal) { documentId };
        }

        var searchOptions = baseOptions with { ExcludedDocumentIds = excluded };

        return engine.Search(query, searchOptions);
    }
}