using LexiSharp.Core;

namespace LexiSharp;

/// <summary>
/// Per-search options for <see cref="LexiSharpIndex{TDocument}.Search"/> and friends.
/// </summary>
/// <param name="Limit">Maximum number of results to return (default: 10).</param>
/// <param name="MinimumScore">Results with a score below this value are discarded.</param>
/// <param name="Offset">Number of top-ranked results to skip, for deep pagination.</param>
/// <param name="Highlight">
/// Wrap query-term occurrences in the returned <see cref="LexiSharpHit{TDocument}.HighlightedText"/>
/// (default: <c>false</c>). Requires a span-capable tokenizer, which the default one is.
/// </param>
public sealed record LexiSharpQueryOptions(
    int Limit = 10,
    double MinimumScore = double.NegativeInfinity,
    int Offset = 0,
    bool Highlight = false)
{
    /// <summary>A default configuration: <c>Limit = 10</c>, no filters, no highlighting.</summary>
    public static readonly LexiSharpQueryOptions Default = new();

    /// <summary>Converts these options to the engine-level <see cref="SearchOptions"/>.</summary>
    internal SearchOptions ToCore() => new(Limit, MinimumScore, Offset: Offset);
}