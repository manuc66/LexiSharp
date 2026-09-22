namespace LexiSharp.Core;

/// <summary>
/// The optional LexiSharp query-syntax features a raw query can carry. Engines declare which
/// ones they interpret; using an unsupported one is a hard error rather than silent drift.
/// </summary>
[Flags]
public enum QueryFeature
{
    /// <summary>A plain query: free terms only, no special syntax.</summary>
    None = 0,

    /// <summary>Double-quoted segments gate on consecutive document positions.</summary>
    Phrases = 1,

    /// <summary>Prefix (<c>term*</c>) and fuzzy (<c>term~</c>/<c>term~N</c>) atoms.</summary>
    Expansions = 2,
}
