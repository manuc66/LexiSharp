using LexiSharp.Linguistics;

namespace LexiSharp.Core;

/// <summary>
/// Detects and validates the LexiSharp query syntax a raw query carries, so an engine that does
/// not interpret a feature fails fast instead of silently treating the operators as plain text.
/// </summary>
/// <remarks>
/// Detection uses <see cref="Tokenizer.Default"/>, the canonical query tokenizer: an operator
/// whose base does not tokenize to a term (e.g. <c>a*</c> with the default single-char rule) is
/// plain text, exactly as <see cref="QueryParser"/> decides it.
/// </remarks>
public static class QuerySyntax
{
    /// <summary>The features <paramref name="query"/> uses, as <see cref="QueryParser"/> would parse it.</summary>
    public static QueryFeatures Detect(string query)
    {
        ArgumentNullException.ThrowIfNull(query);

        var parsed = QueryParser.Parse(query, Tokenizer.Default);
        var features = QueryFeatures.None;

        if (parsed.HasPhrases)
            features |= QueryFeatures.Phrases;

        if (parsed.HasExpansions)
            features |= QueryFeatures.Expansions;

        return features;
    }

    /// <summary>
    /// Throws <see cref="NotSupportedException"/> when <paramref name="query"/> uses a feature
    /// not in <paramref name="supported"/>.
    /// </summary>
    /// <param name="query">The raw query about to be searched.</param>
    /// <param name="supported">The features the target engine honors.</param>
    /// <param name="engineName">Engine name used in the error message.</param>
    public static void EnsureSupported(string query, QueryFeatures supported, string engineName)
    {
        ArgumentNullException.ThrowIfNull(query);

        var unsupported = Detect(query) & ~supported;

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

        var parts = new List<string>(2);

        if ((feature & QueryFeatures.Phrases) != 0)
            parts.Add("phrase queries");

        if ((feature & QueryFeatures.Expansions) != 0)
            parts.Add("prefix/fuzzy operators");

        return string.Join(" and ", parts);
    }
}
