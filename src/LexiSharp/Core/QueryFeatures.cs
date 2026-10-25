namespace LexiSharp.Core;

/// <summary>
/// The optional LexiSharp query-syntax features a raw query can carry. Engines declare which
/// ones they interpret; using an unsupported one is a hard error rather than silent drift.
/// </summary>
[Flags]
public enum QueryFeatures
{
    /// <summary>A plain query: free terms only, no special syntax.</summary>
    None = 0,

    /// <summary>Double-quoted segments gate on consecutive document positions.</summary>
    Phrases = 1,

    /// <summary>Prefix (<c>term*</c>) and fuzzy (<c>term~</c>/<c>term~N</c>) atoms.</summary>
    Expansions = 2,

    /// <summary>
    /// A leading <c>+</c> on a whitespace-delimited token (<c>+term</c>, <c>+"a phrase"</c>,
    /// <c>+term*</c>): the document must contain it.
    /// </summary>
    RequiredTerms = 4,

    /// <summary>
    /// A leading <c>-</c> on a whitespace-delimited token (<c>-term</c>, <c>-"a phrase"</c>,
    /// <c>-term~N</c>): the document must not contain it.
    /// </summary>
    ProhibitedTerms = 8,

    /// <summary>
    /// A known field name followed by a colon scopes the atom to that field:
    /// <c>title:term</c>, <c>title:"a phrase"</c>, and either with a leading <c>+</c>/<c>-</c>.
    /// </summary>
    /// <remarks>
    /// A prefix is a field only when the name is known to the query — the engine's index fields by
    /// default, or <see cref="SearchOptions.KnownFields"/> — so <c>http://x</c>, <c>12:30</c> and
    /// <c>key:value</c> stay ordinary text unless the corpus really has a field of that name.
    /// </remarks>
    FieldScopes = 16,
}
