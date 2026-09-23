namespace LexiSharp.Expansion;

/// <summary>
/// Configuration of a <see cref="PmiTermExpander"/>: how the corpus is analyzed and how many
/// expansion terms each document gets.
/// </summary>
public sealed class PmiTermExpanderOptions
{
    /// <summary>
    /// Size of the sliding token window in which co-occurrence is counted (the standard
    /// distributional-semantics context: LSA/PMI windows are typically 8–10 tokens). Default:
    /// <c>8</c>.
    /// </summary>
    public int ContextWindowSize { get; set; } = 8;

    /// <summary>
    /// A term pair must co-occur in at least this many windows before it is considered a
    /// candidate association. Default: <c>2</c>.
    /// </summary>
    public int MinimumCoOccurrence { get; set; } = 2;

    /// <summary>
    /// A candidate neighbor must itself appear in at least this many windows corpus-wide. This
    /// floor cuts the PPMI « rarity bias »: words that happen to appear once, always alongside
    /// an input term, would otherwise rank above genuinely selective associates. Default:
    /// <c>2</c>.
    /// </summary>
    public int MinimumNeighborWindowFrequency { get; set; } = 2;

    /// <summary>
    /// Terms appearing in more than this share of the corpus windows are treated as functional
    /// stop words: they co-occur with everything by chance (near-zero PPMI but high counts), so
    /// they would otherwise dominate the count-based ranking without adding any discriminative
    /// signal. Default: <c>0.5</c>.
    /// </summary>
    public double MaxWindowDensity { get; set; } = 0.5;

    /// <summary>
    /// Positive pointwise mutual information (PPMI) floor: pairs below this value are never
    /// considered associated. Default: <c>0</c> (every genuinely positive association is a
    /// candidate, rank is what matters).
    /// </summary>
    public double MinimumPmi { get; set; }

    /// <summary>
    /// How many neighbors, at most, a single input term can contribute. Default: <c>3</c>.
    /// </summary>
    public int MaxTermsPerInputTerm { get; set; } = 3;

    /// <summary>
    /// Overall cap on the expansion set per document. Default: <c>8</c>.
    /// </summary>
    public int MaxTotalTerms { get; set; } = 8;
}