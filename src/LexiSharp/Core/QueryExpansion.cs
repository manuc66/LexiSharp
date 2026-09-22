namespace LexiSharp.Core;

/// <summary>How a query atom expands against the index vocabulary.</summary>
public enum QueryExpansionKind
{
    /// <summary><c>term*</c> — every vocabulary term starting with the base (ordinal prefix).</summary>
    Prefix,

    /// <summary><c>term~</c> / <c>term~N</c> — vocabulary terms within N edits of the base.</summary>
    Fuzzy,
}

/// <summary>
/// A free-text query atom carrying a search-time expansion operator: the base term (already
/// tokenized to exactly one term) is matched against the index vocabulary instead of being
/// looked up literally. Never produced inside a quoted phrase segment.
/// </summary>
/// <param name="BaseTerm">The normalized base term, operator stripped.</param>
/// <param name="Kind">Prefix (<c>term*</c>) or fuzzy (<c>term~N</c>).</param>
/// <param name="MaxEdits">
/// Fuzzy only: edit-distance budget, clamped to <c>[0, 2]</c> (default 1 when no count is
/// written). Ignored for prefix expansions.
/// </param>
public sealed record QueryExpansion(
    string BaseTerm,
    QueryExpansionKind Kind,
    int MaxEdits = 1);
