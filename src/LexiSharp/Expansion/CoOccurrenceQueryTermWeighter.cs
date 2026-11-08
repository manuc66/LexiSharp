using LexiSharp.Core;

namespace LexiSharp.Expansion;

/// <summary>
/// Which direction the co-occurrence graph moves a query term's weight.
/// </summary>
public enum QueryTermWeightingMode
{
    /// <summary>
    /// Down-weight a term tied to many of the other query terms: it describes a concept the query
    /// already carries, and BM25's independent sum over-rewards documents that repeat it.
    /// </summary>
    Redundancy,

    /// <summary>
    /// Up-weight a term tied to many of the other query terms: it is central to what the query is
    /// about, while an isolated term is more likely to be noise. The opposite reading of the same
    /// statistic, and the two are not interchangeable — which is why both are measured.
    /// </summary>
    Centrality,
}

/// <summary>
/// Weights the terms of a query by how <b>redundant</b> each one is with the rest of the query in a
/// corpus-derived co-occurrence graph: a term tied to many of the other terms is describing a
/// concept the query already contains, and BM25's sum over independent terms over-rewards documents
/// that repeat it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The statistic.</b> For a query <c>Q</c> and a term <c>t</c>,
/// <c>relation(t) = Σ_{u ∈ Q, u ≠ t} max(0, PPMI(t, u))</c>. <see cref="QueryTermWeightingMode"/>
/// decides which way it moves the weight: <c>1 / (1 + strength · relation(t))</c> for
/// <see cref="QueryTermWeightingMode.Redundancy"/> and <c>1 + strength · relation(t)</c> for
/// <see cref="QueryTermWeightingMode.Centrality"/>. Both are normalized so the most important term
/// carries <c>1</c>. The graph is the one <see cref="PmiTermExpander"/> learns over a sliding
/// window, so the association is « these terms habitually appear in the same context », not « these
/// terms are near-synonyms » — a distinction the normalization above makes explicit.
/// </para>
/// <para>
/// <b>Why it is a query-level weight and not a query rewrite.</b> The engine enumerates candidates
/// from every term, and it must keep doing so: a term down-weighted to <c>0.2</c> can still be the
/// one that finds the answer. The weight belongs to the scoring sum, which is why it is applied by
/// the scorer (<see cref="IWeightedQueryScorer"/>) and not by dropping terms from the query.
/// </para>
/// <para>
/// <b>The hypothesis, and the reading.</b> The published reweighting work this is closest to
/// (Aklouche, Bounhas and Slimani, 2019/2021) measures a term's discriminative power from a
/// co-occurrence graph; this class implements the two readings of that idea with one tunable
/// <c>strength</c>, so <c>0</c> is a no-op and the effect can be measured as a direction rather than
/// as a single point. A query whose terms are all equally tied to each other is unchanged: only the
/// <i>relative</i> relation moves the ranking.
/// </para>
/// </remarks>
public sealed class CoOccurrenceQueryTermWeighter : IQueryTermWeighter
{
    private readonly PmiTermExpander _graph;
    private readonly double _strength;
    private readonly QueryTermWeightingMode _mode;
    private readonly int _minimumQueryTerms;

    /// <param name="graph">
    /// The learned association graph. <see cref="PmiTermExpander.LearnFrom"/> builds it from a
    /// corpus in one pass; the same instance can serve every query.
    /// </param>
    /// <param name="strength">
    /// How hard the relation moves the weight. <c>0</c> returns every weight as <c>1</c> — the
    /// no-op that makes the decorator disappear — and larger values push the term further from
    /// <c>1</c>. Must be finite and non-negative.
    /// </param>
    /// <param name="mode">
    /// Whether a term tied to the rest of the query is penalized
    /// (<see cref="QueryTermWeightingMode.Redundancy"/>, the default) or promoted
    /// (<see cref="QueryTermWeightingMode.Centrality"/>).
    /// </param>
    /// <param name="minimumQueryTerms">
    /// Do nothing below this many distinct query terms. The relation is an estimate, and a query
    /// with three terms gives it three data points; the measurements behind this class lose on
    /// short queries and gain only on long ones, so the floor is the line between the two rather
    /// than a tuning knob. <c>0</c> (the default) applies the weighter to every query.
    /// </param>
    public CoOccurrenceQueryTermWeighter(
        PmiTermExpander graph,
        double strength = 1.0,
        QueryTermWeightingMode mode = QueryTermWeightingMode.Redundancy,
        int minimumQueryTerms = 0)
    {
        ArgumentNullException.ThrowIfNull(graph);

        if (!double.IsFinite(strength) || strength < 0)
            throw new ArgumentOutOfRangeException(nameof(strength), strength, "strength must be finite and non-negative.");

        ArgumentOutOfRangeException.ThrowIfNegative(minimumQueryTerms);

        _graph = graph;
        _strength = strength;
        _mode = mode;
        _minimumQueryTerms = minimumQueryTerms;
    }

    /// <inheritdoc />
    public IReadOnlyDictionary<string, double> Weigh(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        var distinct = new HashSet<string>(terms, StringComparer.Ordinal);
        var weights = new Dictionary<string, double>(distinct.Count, StringComparer.Ordinal);

        if (distinct.Count == 0)
            return weights;

        // One term has nothing to be redundant with, strength 0 is defined as the no-op, and a query
        // below the floor is where the estimate is too thin to act on.
        if (distinct.Count <= 1 || _strength == 0 || distinct.Count < _minimumQueryTerms)
        {
            foreach (string term in distinct)
                weights[term] = 1.0;

            return weights;
        }

        double maximum = 0;

        foreach (string term in distinct)
        {
            double relation = 0;

            foreach (var association in _graph.AssociationsWith(term, distinct))
                relation += Math.Max(0, association.Value);

            double weight = _mode == QueryTermWeightingMode.Redundancy
                ? 1.0 / (1.0 + (_strength * relation))
                : 1.0 + (_strength * relation);

            weights[term] = weight;

            if (weight > maximum)
                maximum = weight;
        }

        // Normalize so the most important term keeps weight 1: the weights are meant to change the
        // relative contributions, not to scale every score of the query by a constant that depends
        // on how redundant or how central the query happens to be.
        if (maximum > 0)
        {
            foreach (string term in distinct)
                weights[term] /= maximum;
        }

        return weights;
    }
}
