namespace LexiSharp.Expansion;

/// <summary>
/// Which statistic orders an input term's candidate neighbours before the expansion budget applies.
/// </summary>
/// <remarks>
/// The two orderings disagree, and the disagreement is the whole of it. A term present in a fifth
/// of a corpus's windows co-occurs with very nearly every other term, so it has a high count with
/// each of them and ranks first under <see cref="CoOccurrenceCount"/> while carrying almost no
/// information about any of them. Ranking by mutual information asks the opposite question — how much
/// more often does this pair appear than chance — and puts a rare, genuinely associated term above a
/// common one that merely appears nearby.
/// </remarks>
public enum ExpansionRanking
{
    /// <summary>Order by raw co-occurrence count, the classic expansion bias. The default.</summary>
    CoOccurrenceCount,

    /// <summary>Order by positive mutual information, with the count only as a tiebreak.</summary>
    PositiveMutualInformation,
}

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
    /// Which statistic orders the candidate neighbours. Default:
    /// <see cref="ExpansionRanking.CoOccurrenceCount"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="MaxWindowDensity"/> is the filter that keeps function words out of the
    /// neighbourhood, and it is a threshold: a common enough function word clears it and is then
    /// promoted to first place by the count. This is the other half — it cannot admit a neighbour
    /// the filter rejected, but it can decline to put that neighbour first.
    /// </remarks>
    public ExpansionRanking Ranking { get; set; } = ExpansionRanking.CoOccurrenceCount;

    /// <summary>
    /// How many neighbors, at most, a single input term can contribute. Default: <c>3</c>.
    /// </summary>
    public int MaxTermsPerInputTerm { get; set; } = 3;

    /// <summary>
    /// Overall cap on the expansion set per document. Default: <c>8</c>.
    /// </summary>
    public int MaxTotalTerms { get; set; } = 8;
}
