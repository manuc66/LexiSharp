namespace LexiSharp.Hybrid;

/// <summary>
/// What a hybrid knows about one source engine's scores beyond the result list itself: the query's
/// theoretical upper bound, when the engine can report one.
/// </summary>
/// <remarks>
/// <para>
/// The normalizers that existed before this type measured the list they were given. That is enough
/// to put one page on a comparable scale, and not enough to make a score independent of how many
/// candidates the page happened to contain. The bound travels separately because the merger receives
/// only scores, and a normalizer cannot ask an engine a question it no longer has a reference to.
/// </para>
/// <para>
/// A context with no bound is the honest answer for an engine that cannot produce one; a contextual
/// normalizer falls back to its list-measuring behaviour rather than inventing a scale. That is why the
/// property is nullable rather than a zero: zero would be a scale, and every contribution would become
/// zero with it.
/// </para>
/// </remarks>
/// <param name="QueryUpperBound">
/// No score this engine returns for the query can exceed this value, or <c>null</c> when the engine
/// could not report one — an unsupported scorer, a syntax-bearing query, or an index that cannot
/// answer the posting statistics.
/// </param>
/// <param name="ScorerName">
/// The scorer that produced the bound, for a trace or a diagnostic; <c>null</c> when no bound exists.
/// </param>
public sealed record ScoreNormalizationContext(double? QueryUpperBound, string? ScorerName = null)
{
    /// <summary>A context for an engine that reported no bound.</summary>
    public static readonly ScoreNormalizationContext Empty = new((double?)null);
}
