namespace LexiSharp.Core;

/// <summary>
/// Rewrites a raw query into one or more searchable variants <b>before</b> retrieval — the
/// « pre-retrieval transformation » seam. Rewriting, decomposing a question into sub-queries,
/// generating lexical variants and HyDE (a hypothetical answer searched as a query) all
/// produce one or more variant strings; the consuming
/// <see cref="Expansion.TransformingTextSearchEngine"/> searches each against the same inner
/// engine and fuses the rankings. <b>LexiSharp never runs a model itself</b>: implementing
/// this interface is up to the consumer — an LLM call, a rule, a thesaurus — and the engine
/// only reads the returned strings.
/// </summary>
/// <remarks>
/// A transformer returns <b>several</b> variants so every use has one home: rewriting returns
/// one, decomposition one per sub-query, a HyDE generator the generated answer text. Because
/// every variant runs on the same inner engine, one scorer's scale compares them all, and the
/// consumer engine keeps each document's best score across the variants.
/// <para>
/// A transformer that returns nothing or throws does not break a search: the consumer falls
/// back to the untransformed query, the same way a broken <see cref="IQueryRouter"/> falls
/// back to its route. Variants must be searchable text — a null or blank entry is ignored —
/// and identical variants are searched once.
/// </para>
/// </remarks>
public interface IQueryTransformer
{
    /// <summary>Human readable name of the strategy, e.g. <c>"hyde"</c>.</summary>
    string Name { get; }

    /// <summary>
    /// Returns the variants to search for <paramref name="query"/>, in caller order. Searchable
    /// text only; the caller's own conventions (tokenizer, query syntax) apply to each variant.
    /// </summary>
    /// <param name="query">The raw query about to be searched.</param>
    IReadOnlyList<string> Transform(string query);
}