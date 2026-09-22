namespace LexiSharp.Core;

/// <summary>
/// Declares which <see cref="QueryFeature"/>s an <see cref="ITextSearchEngine"/> interprets, so
/// callers can discover the supported query syntax instead of discovering it by getting wrong
/// results. A query using a feature an engine does not declare is rejected with
/// <see cref="NotSupportedException"/> by <see cref="QuerySyntax.EnsureSupported"/>.
/// </summary>
public interface IQuerySyntaxSupport
{
    /// <summary>The query-syntax features this engine honors.</summary>
    QueryFeature SupportedQueryFeatures { get; }
}
