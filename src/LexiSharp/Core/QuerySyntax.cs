using LexiSharp.Linguistics;

namespace LexiSharp.Core;

/// <summary>
/// Detects and validates the LexiSharp query syntax a raw query carries, so an engine that does
/// not interpret a feature fails fast instead of silently treating the operators as plain text.
/// </summary>
/// <remarks>
/// Detection uses <see cref="Tokenizer.Default"/>, the canonical query tokenizer: an operator
/// whose base does not tokenize to a term (e.g. <c>a*</c> with the default single-char rule) is
/// plain text, exactly as <see cref="QueryParser"/> decides it. The same rule is why a bare
/// <c>+</c>/<c>-</c> or a <c>-</c> inside a word (<c>e-mail</c>) is not reported: it is not an
/// operator to <see cref="QueryParser"/> either.
/// </remarks>
public static class QuerySyntax
{
    /// <summary>The features <paramref name="query"/> uses, as <see cref="QueryParser"/> would parse it.</summary>
    /// <param name="query">The raw query.</param>
    /// <param name="fields">
    /// The field names a <c>name:</c> prefix may name, or <c>null</c> when the caller has no field
    /// vocabulary — in which case no prefix is a field and <c>Decimal:comma</c> is ordinary text.
    /// </param>
    public static QueryFeatures Detect(string query, IReadOnlySet<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parsed = QueryParser.Parse(query, Tokenizer.Default, fields);
        var features = QueryFeatures.None;

        if (parsed.HasPhrases)
            features |= QueryFeatures.Phrases;

        if (parsed.HasExpansions)
            features |= QueryFeatures.Expansions;

        if (parsed.HasRequired)
            features |= QueryFeatures.RequiredTerms;

        if (parsed.HasProhibited)
            features |= QueryFeatures.ProhibitedTerms;

        if (parsed.HasFieldScopes)
            features |= QueryFeatures.FieldScopes;

        return features;
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> when <paramref name="query"/> uses a feature
    /// not in <paramref name="supported"/>.
    /// </summary>
    /// <param name="query">The raw query about to be searched.</param>
    /// <param name="supported">The features the target engine honors.</param>
    /// <param name="engineName">Engine name used in the error message.</param>
    /// <param name="fields">
    /// The field names a <c>name:</c> prefix may name, or <c>null</c> when the engine has no field
    /// vocabulary. A backend asked to scope a query to a field it cannot honor refuses here, before
    /// opening a connection, rather than forwarding the prefix as text.
    /// </param>
    public static void EnsureSupported(
        string query,
        QueryFeatures supported,
        string engineName,
        IReadOnlySet<string>? fields = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var unsupported = Detect(query, fields) & ~supported;

        if (unsupported != QueryFeatures.None)
        {
            throw new NotSupportedException(
                $"{engineName} does not support {Describe(unsupported)} (it supports {Describe(supported)}). " +
                "Remove the unsupported syntax or use an engine that declares it via IQuerySyntaxSupport.");
        }
    }

    private static string Describe(QueryFeatures feature)
    {
        if (feature == QueryFeatures.None)
            return "plain queries only";

        var parts = new List<string>(4);

        if ((feature & QueryFeatures.Phrases) != 0)
            parts.Add("phrase queries");

        if ((feature & QueryFeatures.Expansions) != 0)
            parts.Add("prefix/fuzzy operators");

        if ((feature & QueryFeatures.RequiredTerms) != 0)
            parts.Add("required (+) terms");

        if ((feature & QueryFeatures.ProhibitedTerms) != 0)
            parts.Add("prohibited (-) terms");

        if ((feature & QueryFeatures.FieldScopes) != 0)
            parts.Add("field-scoped terms");

        return string.Join(" and ", parts);
    }
}
