namespace LexiSharp.Core;

/// <summary>How a query atom expands against the index vocabulary.</summary>
public enum QueryExpansionKind
{
    /// <summary><c>term*</c> — every vocabulary term starting with the base (ordinal prefix).</summary>
    Prefix,

    /// <summary><c>term~</c> / <c>term~N</c> — vocabulary terms within N edits of the base.</summary>
    Fuzzy,
}

/// <summary>What a query atom asks of the documents that contain it.</summary>
public enum QueryTermOperator
{
    /// <summary>No leading operator: the atom only contributes to scoring.</summary>
    None,

    /// <summary>A leading <c>+</c>: the document must contain the atom, and it scores.</summary>
    Required,

    /// <summary>A leading <c>-</c>: the document must not contain the atom, and it does not score.</summary>
    Prohibited,
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
    int MaxEdits = 1)
{
    /// <summary>
    /// The leading <c>+</c>/<c>-</c> operator that qualified this atom, or
    /// <see cref="QueryTermOperator.None"/> when it carried none. A
    /// <see cref="QueryTermOperator.Required"/> atom gates on at least one expanded term being
    /// present and contributes its expanded terms to scoring; a
    /// <see cref="QueryTermOperator.Prohibited"/> atom gates on none of them being present and
    /// contributes nothing to scoring.
    /// </summary>
    public QueryTermOperator Operator { get; init; } = QueryTermOperator.None;

    /// <summary>
    /// The field the atom is scoped to (<c>title:foo*</c>), or <c>null</c> when it is unscoped.
    /// Each expanded term inherits the scope: it is matched and scored from that field only.
    /// </summary>
    public string? Field { get; init; }
}
