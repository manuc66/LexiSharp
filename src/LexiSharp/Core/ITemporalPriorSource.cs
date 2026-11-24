namespace LexiSharp.Core;

/// <summary>
/// Produces additive log-prior evidence for categories based on a temporal context.
/// </summary>
public interface ITemporalPriorSource
{
    /// <summary>Returns the log-prior bonus for the categories at the given instant.</summary>
    IReadOnlyList<TextEvidence> Prior(DateTimeOffset when);
}
