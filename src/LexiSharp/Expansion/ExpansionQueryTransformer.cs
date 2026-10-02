using System.Text;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Expansion;

/// <summary>
/// A model-free <see cref="IQueryTransformer"/> that returns two variants — the caller's query
/// and the same query widened with the terms an <see cref="ITermExpander"/> derives — the
/// deterministic, offline cousin of a HyDE generator, and the case that makes this seam
/// testable without a model.
/// </summary>
/// <remarks>
/// The two-variant shape is what <see cref="ExpandingTextSearchEngine"/> does as one query,
/// split in two: the original query is searched on its own so a document that matches the
/// caller's exact terms keeps that (tight) score, and the widened variant adds the documents
/// that only the expansion terms reach. Fusing keeps each document's best score, so the exact
/// match is never diluted by the expansion terms — the difference between this seam and the
/// single-query widening it generalizes.
/// <para>
/// Queries carrying explicit syntax (<c>"</c>, <c>*</c>, <c>~</c>) are returned as their single
/// original, because those operators pin an exact meaning that a re-joined variant could not
/// preserve — the same guard <see cref="ExpandingTextSearchEngine"/> applies. An empty query or
/// a query the expander cannot widen is returned as its single original as well.
/// </para>
/// </remarks>
public sealed class ExpansionQueryTransformer : IQueryTransformer
{
    private static readonly System.Buffers.SearchValues<char> SyntaxCharacters =
        System.Buffers.SearchValues.Create("~*\"");

    private readonly ITermExpander _expander;
    private readonly ITokenizer _tokenizer;

    /// <param name="expander">Produces the related terms added to the widened variant.</param>
    /// <param name="tokenizer">
    /// Tokenizer used to split the raw query before expansion; should be the one the inner
    /// engine uses, otherwise the expanded terms will not line up with the indexed terms.
    /// </param>
    public ExpansionQueryTransformer(ITermExpander expander, ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(expander);

        _expander = expander;
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public string Name => "expansion-variants";

    /// <inheritdoc />
    public IReadOnlyList<string> Transform(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.AsSpan().ContainsAny(SyntaxCharacters))
            return [query];

        var terms = _tokenizer.Tokenize(query);

        if (terms.Count == 0)
            return [query];

        var expansion = _expander.Expand(terms);

        if (expansion.Count == 0)
            return [query];

        return [query, Widened(query, terms, expansion)];
    }

    /// <summary>
    /// The widened variant: the original terms first, then the distinct expansion terms — the
    /// same shape <see cref="ExpandingTextSearchEngine"/> builds, so the vocabulary it produces
    /// is comparable with that engine's.
    /// </summary>
    private static string Widened(
        string query,
        IReadOnlyList<string> terms,
        IReadOnlyCollection<Expansion.ExpandedTerm> expansion)
    {
        var seen = new HashSet<string>(terms, StringComparer.Ordinal);
        var builder = new StringBuilder(query.Length + (expansion.Count * 8));

        for (int i = 0; i < terms.Count; i++)
        {
            if (i > 0)
                builder.Append(' ');

            builder.Append(terms[i]);
        }

        foreach (var term in expansion)
        {
            if (string.IsNullOrEmpty(term.Term) || !seen.Add(term.Term))
                continue;

            builder.Append(' ');
            builder.Append(term.Term);
        }

        return builder.ToString();
    }
}