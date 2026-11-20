using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// Weights a query's terms by a saturating function of how many times each one was written:
/// <c>w(t) = (k3 + 1) · qtf(t) / (k3 + qtf(t))</c>, normalized so the heaviest term carries
/// <c>1</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The other half of BM25's saturation.</b> BM25 saturates the <i>document</i> side of a term with
/// its <c>k1</c>: a word occurring twenty times in a document is not twenty times as relevant as a
/// word occurring once. The formula is normally written over distinct query terms, so the
/// <i>query</i> side is scored linearly — a word the caller wrote five times counts exactly five
/// times. That was a fine assumption for a three-word query typed by a human. It stops being one
/// when the query is a paragraph produced by a model, where the same concept is repeated because the
/// prompt repeated it, and where a term that appears once in a generated expansion gets no weight at
/// all against one that appears six times.
/// </para>
/// <para>
/// <c>k3</c> is the saturation constant for that side, and it behaves like <c>k1</c> where it is
/// applied: a term written twice is worth less than twice a term written once, and the ratio
/// converges to <c>k3 + 1</c> however many times the word is repeated. With the default
/// <c>k3 = 8</c>, two occurrences weigh <c>1.8×</c> one, five weigh <c>3.46×</c>, and the curve
/// flattens towards <c>9×</c> — a slow saturation, chosen because a query that repeats a term is
/// usually making a deliberate emphasis rather than a mistake.
/// </para>
/// <para>
/// <b><c>k3 = 0</c> is the no-op.</b> The formula then returns <c>1</c> for every term whatever its
/// count, which is the "ignore query frequency" setting the literature describes and the control a
/// measurement needs: the same code path, one number away from the treatment.
/// </para>
/// <para>
/// <b>Use it with <see cref="QueryTermWeighting.Distinct"/></b> (the <see cref="Bm25Scorer"/>
/// default). <see cref="QueryTermWeighting.QueryFrequency"/> also multiplies a term's idf by its
/// occurrence count, so combining the two counts the repetition twice — this weighter is the
/// <i>saturating</i> alternative to that setting, not an addition to it. The two produce the same
/// ranking when every term occurs once, and diverge where a query repeats one.
/// </para>
/// <para>
/// <b>How to apply it.</b> A weight belongs to one query, so the scorer cannot hold it:
/// <see cref="LexiSharp.Expansion.ReweightingTextSearchEngine"/> asks this weighter for the weights,
/// rebuilds the scorer for that query and searches. Only a scorer implementing
/// <see cref="IWeightedQueryScorer"/> can consume weights, which is <see cref="Bm25Scorer"/> — the
/// variants that do not carry per-query weights (BM25+, BM25L, BM25VA) cannot be driven by any
/// weighter, this one included.
/// </para>
/// <para>
/// <b>Custom weights, without a weighter.</b> A caller that already has one weight per term — an
/// agent's own expansion weights, a learned query encoder — does not need a class here: it calls
/// <see cref="IWeightedQueryScorer.WithTermWeights"/> on its scorer and ranks with the copy, which
/// is the same seam this weighter goes through.
/// </para>
/// </remarks>
public sealed class SaturatingQueryTermWeighter : IQueryTermWeighter
{
    /// <summary>
    /// The saturation constant used when the caller names none: <c>8</c>.
    /// </summary>
    /// <remarks>
    /// Deliberately slow. The value decides how quickly repetition stops paying: at <c>k3 = 8</c> a
    /// second occurrence is worth <c>+80 %</c> and a fifth <c>+15 %</c>, so an expansion that repeats
    /// a term three times still counts for more than one written once, while a prompt that repeats it
    /// thirty times cannot drown the rest of the query. A smaller constant saturates sooner and treats
    /// repetition as noise.
    /// </remarks>
    public const double DefaultSaturation = 8.0;

    private readonly double _k3;

    /// <summary>Creates a weighter with the given query-side saturation constant.</summary>
    /// <param name="k3">
    /// The saturation constant. <c>0</c> disables the effect — every term weighs <c>1</c> — and no
    /// finite value ever returns a weight above <c>1</c> after normalization. Must be finite and
    /// non-negative; <c>8</c> is <see cref="DefaultSaturation"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="k3"/> is negative, NaN or infinite.</exception>
    public SaturatingQueryTermWeighter(double k3 = DefaultSaturation)
    {
        if (!double.IsFinite(k3) || k3 < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(k3), k3, "The query saturation constant must be finite and non-negative.");
        }

        _k3 = k3;
    }

    /// <summary>The saturation constant this weighter applies.</summary>
    public double Saturation => _k3;

    /// <inheritdoc />
    /// <remarks>
    /// The weights are relative to the query's own heaviest term, so the scale BM25's corpus
    /// statistics put a score on is unchanged: a query whose terms all occur once weighs every term
    /// <c>1</c> and scores exactly as it does without this weighter.
    /// </remarks>
    public IReadOnlyDictionary<string, double> Weigh(IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0 || _k3 == 0)
        {
            // No term at all, or saturation switched off: the map that says "every weight is 1" is the
            // empty one, which lets the decorator keep ranking with the prototype.
            return new Dictionary<string, double>(0, StringComparer.Ordinal);
        }

        var counts = new Dictionary<string, int>(terms.Count, StringComparer.Ordinal);
        int heaviest = 0;

        for (int i = 0; i < terms.Count; i++)
        {
            int count = counts.TryGetValue(terms[i], out int seen) ? seen + 1 : 1;
            counts[terms[i]] = count;

            if (count > heaviest)
                heaviest = count;
        }

        var weights = new Dictionary<string, double>(counts.Count, StringComparer.Ordinal);

        if (heaviest <= 1)
        {
            // Every term occurs once, so every weight is 1 and the map can say nothing: an empty map
            // means "all 1" by the interface's contract, and the decorator then scores with the
            // prototype instead of copying it per query. Most queries are this case, and it is the
            // one where the weighter must cost nothing at all.
            return weights;
        }

        double heaviestWeight = Saturate(heaviest);

        foreach (var (term, count) in counts)
            weights[term] = Saturate(count) / heaviestWeight;

        return weights;
    }

    /// <summary>The unnormalized <c>(k3 + 1) · qtf / (k3 + qtf)</c>.</summary>
    private double Saturate(int occurrences) =>
        (_k3 + 1.0) * occurrences / (_k3 + occurrences);
}
