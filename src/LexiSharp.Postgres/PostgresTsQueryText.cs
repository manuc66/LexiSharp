namespace LexiSharp.Postgres;

/// <summary>
/// The two ways a raw query becomes a <c>tsquery</c> for the stock-PostgreSQL engines.
/// </summary>
/// <remarks>
/// <para>
/// <c>websearch_to_tsquery</c> is the safe, documented entry point, and it <b>ANDs</b> every term:
/// a document matches only if it contains all of them. That is the right rule for a search box
/// promising "all words", and the wrong one for a corpus where queries carry a dozen terms —
/// SciFact's average 13.1 terms and, at that rule, almost nothing matches. On this repository's
/// own BEIR run the difference is not a nuance: 0.0188 nDCG@10 for the AND rule against 0.662 for
/// BM25 on SciFact.
/// </para>
/// <para>
/// <see cref="AnyTerm"/> ORs the lexemes instead. The lexemes are taken from the <c>tsvector</c> of
/// the query, so they come from the same configuration as the indexed column, and each is emitted
/// through <c>quote_literal</c> because a lexeme is not necessarily a bare word: the <c>simple</c>
/// configuration keeps <c>a-b</c> as one lexeme, and an unquoted <c>a-b</c> in a <c>tsquery</c>
/// literal would be parsed as a phrase. An empty result (a query of stop words only) stays
/// <c>NULL</c>, so <c>tsv @@ NULL</c> matches nothing rather than raising.
/// </para>
/// <para>
/// AnyTerm is a bag of words: the websearch operators (<c>or</c>, <c>-term</c>, a trailing
/// <c>*</c>) are not read as operators, because the query goes through <c>to_tsvector</c>. Phrases
/// are unaffected — the engines split them out before they build a match expression.
/// </para>
/// </remarks>
internal static class PostgresTsQueryText
{
    /// <summary>
    /// <c>websearch_to_tsquery</c> over the parameter: every term must appear (AND).
    /// </summary>
    /// <param name="textSearchConfig">Validated as <c>[A-Za-z0-9_]</c> by the options.</param>
    /// <param name="parameter">An SQL parameter reference such as <c>@query</c>.</param>
    public static string AllTerms(string textSearchConfig, string parameter) =>
        $"websearch_to_tsquery('{textSearchConfig}', unaccent({parameter}))";

    /// <summary>
    /// A disjunction of the query's lexemes: any term may appear (OR).
    /// </summary>
    /// <param name="textSearchConfig">Validated as <c>[A-Za-z0-9_]</c> by the options.</param>
    /// <param name="parameter">An SQL parameter reference such as <c>@query</c>.</param>
    public static string AnyTerm(string textSearchConfig, string parameter) => $"""
        (SELECT string_agg(quote_literal(lexeme), ' | ')
         FROM unnest(tsvector_to_array(to_tsvector('{textSearchConfig}', unaccent({parameter})))) AS lexeme)::tsquery
        """;

    /// <summary>The expression a <see cref="PostgresMatchMode"/> selects.</summary>
    public static string For(PostgresMatchMode mode, string textSearchConfig, string parameter) =>
        mode == PostgresMatchMode.AnyTerm
            ? AnyTerm(textSearchConfig, parameter)
            : AllTerms(textSearchConfig, parameter);

    /// <summary>
    /// The query's lexemes, as the indexed column's configuration produced them.
    /// </summary>
    /// <remarks>
    /// The C# side needs the same terms the <c>tsvector</c> column holds — a BM25 computed over
    /// LexiSharp's tokenizer against term frequencies taken from PostgreSQL's analysis would sum
    /// over terms that never meet. This is the query-side counterpart of
    /// <see cref="PostgresSchema.TsvExpression"/>, and it is a separate statement because the term
    /// list is a value of the search, not of each returned row.
    /// </remarks>
    public static string QueryLexemes(string textSearchConfig) =>
        $"SELECT coalesce(tsvector_to_array(to_tsvector('{textSearchConfig}', unaccent(@query))), ARRAY[]::text[])";
}
