using LexiSharp.Core;
using LexiSharp.Linguistics;
using System.Text;

namespace LexiSharp.Expansion;

/// <summary>
/// A decorator engine that corrects query terms against the index vocabulary before searching —
/// the consuming side of <see cref="Core.ITermCorrector"/>, proving the seam is usable end to end.
/// </summary>
/// <remarks>
/// <para>
/// The raw query is tokenized, each term is handed to the corrector, and the original terms plus
/// the corrected candidates are joined into one bag-of-words query for the inner engine. A query
/// such as <c>helo</c> becomes <c>helo hello</c>, so a document that spells the term correctly is
/// still reached. Writes are forwarded to the inner engine unchanged.
/// </para>
/// <para>
/// The decorator is deliberately thin: it does not score the corrections differently from the
/// originals, and it does not remove the original term. Correction here is widening, not
/// replacement, which is the safe default on short text where a forced correction changes meaning.
/// </para>
/// </remarks>
public sealed class CorrectingTextSearchEngine : ITextSearchEngine, IQueryCostProbe
{
    private static readonly char Separator = ' ';

    private readonly ITextSearchEngine _inner;
    private readonly ITermCorrector _corrector;
    private readonly ITokenizer _tokenizer;

    /// <param name="inner">The engine that ultimately scores the search.</param>
    /// <param name="corrector">Produces the corrected candidate terms.</param>
    /// <param name="tokenizer">Tokenizer used to split the raw query; defaults to <see cref="Tokenizer.Default"/>.</param>
    public CorrectingTextSearchEngine(ITextSearchEngine inner, ITermCorrector corrector, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(corrector);

        _inner = inner;
        _corrector = corrector;
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <summary>The corrector applied to every query.</summary>
    public ITermCorrector Corrector => _corrector;

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
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _inner.Search(BuildCorrectedQuery(query), options, cancellationToken);
    }

    /// <inheritdoc />
    public long EstimateCandidateCount(ReadOnlySpan<char> query, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return _inner is IQueryCostProbe probe
            ? probe.EstimateCandidateCount(BuildCorrectedQuery(query.ToString()), options)
            : long.MaxValue;
    }

    private string BuildCorrectedQuery(string query)
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

            foreach (var (candidate, _) in _corrector.Correct(term))
            {
                if (!string.Equals(candidate, term, StringComparison.Ordinal))
                {
                    builder.Append(Separator);
                    builder.Append(candidate);
                }
            }
        }

        return builder.ToString();
    }
}
