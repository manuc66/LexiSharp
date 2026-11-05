using LexiSharp.Core;
using LexiSharp.Linguistics;
using System.Text;

namespace LexiSharp.Expansion;

/// <summary>
/// A decorator engine that widens the query with vocabulary terms within a small edit distance
/// of each query term — in-memory fuzzy matching, the counterpart of the Postgres <c>pg_trgm</c>
/// lane. Handles typos and near-misses without a model or a backend.
/// </summary>
/// <remarks>
/// <para>
/// The raw query is tokenized; each term is matched against the supplied vocabulary, and every
/// vocabulary term within a small edit distance (Damerau-Levenshtein, including
/// adjacent transposition) is added to the query. A query such as <c>helo</c> becomes
/// <c>helo hello</c>, so a document spelling the term correctly is still reached. Writes are
/// forwarded to the inner engine unchanged.
/// </para>
/// <para>
/// Widening, not replacement: the original term is always kept, because on short text a forced
/// correction changes the meaning. The vocabulary is held as a snapshot taken at construction.
/// </para>
/// </remarks>
public sealed class FuzzyTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    private static readonly char Separator = ' ';

    private readonly ITextSearchEngine _inner;
    private readonly IReadOnlyList<string> _vocabulary;
    private readonly int _maxDistance;
    private readonly ITokenizer _tokenizer;

    /// <param name="inner">The engine that ultimately scores the search.</param>
    /// <param name="vocabulary">The terms the index holds.</param>
    /// <param name="maxDistance">Maximum edit distance to consider (default 2).</param>
    /// <param name="tokenizer">Tokenizer used to split the raw query; defaults to <see cref="Tokenizer.Default"/>.</param>
    public FuzzyTextSearchEngine(
        ITextSearchEngine inner,
        IEnumerable<string> vocabulary,
        int maxDistance = 2,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(vocabulary);

        _inner = inner;
        _vocabulary = vocabulary.Distinct(StringComparer.Ordinal).ToList();
        _maxDistance = Math.Max(1, maxDistance);
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

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
    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _inner.Search(BuildFuzzyQuery(query), options, cancellationToken);
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _inner is IQueryCostProbe probe
            ? probe.EstimateCandidateCount(BuildFuzzyQuery(query.ToString()), options)
            : long.MaxValue;
    }

    private string BuildFuzzyQuery(string query)
    {
        var terms = _tokenizer.Tokenize(query);

        if (terms.Count == 0)
            return query;

        var builder = new StringBuilder();

        foreach (var term in terms)
        {
            if (builder.Length > 0)
                builder.Append(Separator);

            builder.Append(term);

            foreach (var candidate in _vocabulary)
            {
                if (string.Equals(candidate, term, StringComparison.Ordinal))
                    continue;

                if (DamerauLevenshtein(term, candidate) <= _maxDistance)
                {
                    builder.Append(Separator);
                    builder.Append(candidate);
                }
            }
        }

        return builder.ToString();
    }

    private static int DamerauLevenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        int[,] d = new int[a.Length + 1, b.Length + 1];

        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;

                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);

                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
