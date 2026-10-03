using System.Globalization;
using System.Text;
using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A scorer built from other scorers: the weighted sum of their scores, <c>Σ wᵢ · componentᵢ</c>,
/// computed in one pass over one candidate set.
/// </summary>
/// <remarks>
/// <para>
/// <b>What this is for.</b> Blending scores <i>inside</i> one scoring pass, in one scale, so the
/// weights are the only thing a caller tunes. The alternative the library already offers —
/// <see cref="Hybrid.WeightedScoreResultMerger"/> over
/// <see cref="Hybrid.HybridTextSearchEngine"/> — blends scores <i>across</i> finished rankings, and
/// normalizes each one by its own maximum first. The two are not interchangeable: normalization by
/// a per-list maximum makes the blend depend on how many documents each list happened to return,
/// which is why it is right for fusing sources whose scores are not commensurable (a
/// <c>ts_rank</c> and a cosine similarity) and wrong for composing one score out of parts of
/// itself.
/// </para>
/// <para>
/// <b>Weights may be negative; that is the point of half of them.</b> A negative weight is how a
/// component becomes a penalty — <see cref="DocumentLengthRatioScorer"/> at <c>-λ</c> is a length
/// prior, exactly as in any weighted-sum scorer.
/// <see cref="Hybrid.WeightedScoreResultMerger"/> refuses negative weights for a different reason:
/// it blends normalized non-negative similarities, where a negative weight has no reading. The
/// difference is deliberate, and both types say so. Only non-finite weights are rejected here.
/// </para>
/// <para>
/// <b>A zero weight removes the component</b>, and the component is then not called at all: not
/// cheaper as an optimisation, but because <c>0 × NaN</c> is <c>NaN</c>, and a component that
/// returns a non-finite score for some document must not be able to poison a weight that says it
/// does not participate. A composite whose weights are all zero scores <c>0</c> for every document,
/// which the engine reads as « no match » — it returns nothing, which is what "no component
/// participates" means.
/// </para>
/// <para>
/// <b>A non-finite component score propagates</b>, and the engine rejects the document. Dropping
/// the component instead would yield a score computed from a subset of the parts — a
/// plausible-looking wrong number, which is harder to notice than a document that is simply
/// missing.
/// </para>
/// <para>
/// <b>Not an <see cref="ITermOverlapScorer"/>, and the consequence is worth reading.</b> A prior
/// component scores a document that shares no query term with the query, because a length is a
/// property of the document whatever the query asked for. Such a document therefore receives a
/// non-zero total and <b>can reach the page</b>. This scorer cannot promise the marker interface's
/// « score is exactly 0 without shared terms », so the engine will not skip non-matching
/// candidates on its behalf — it scores every candidate it enumerates, and scans the corpus when
/// nothing else narrows the set. Two ways to keep the result set clean, both the caller's call:
/// keep a matching component's weight above the priors', or compose this inside a cascade stage, so
/// the candidates arrive from a retrieval stage that already decided what matched.
/// </para>
/// <para>
/// <b>No <see cref="IScoreExplainer"/>.</b> The parts explain themselves, and an explanation whose
/// per-term contributions belong to another object would not be one. Composite the explanations of
/// its components instead — or reach for the explanation of the component that did the matching.
/// </para>
/// <para>
/// <b>Cost.</b> One weighted sum per component per candidate, on top of the work the components
/// themselves do. The components are consulted in the order they were given, which is the
/// summation order and therefore the only thing that decides the last bits of the total.
/// </para>
/// </remarks>
public sealed class WeightedCompositeScorer : ITextScorer
{
    private readonly ITextScorer[] _components;
    private readonly double[] _weights;
    private readonly string _name;

    /// <summary>Builds a composite from weighted components.</summary>
    /// <param name="components">
    /// The weighted parts, summed in the order given. At least one is required; a part at weight
    /// <c>0</c> is kept out of the sum entirely, and a part at a non-finite weight is rejected.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="components"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">
    /// The list is empty, or one of the parts is <c>null</c>.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">A weight is NaN or infinite.</exception>
    public WeightedCompositeScorer(params (ITextScorer Component, double Weight)[] components)
    {
        ArgumentNullException.ThrowIfNull(components);

        if (components.Length == 0)
            throw new ArgumentException("At least one component is required.", nameof(components));

        // Every part is validated before anything reads one — including the name builder, which
        // asks each part for its name. Validating afterwards left a null part reaching the name
        // builder and surfacing as a NullReferenceException instead of the argument exception the
        // signature promises.
        for (int i = 0; i < components.Length; i++)
        {
            if (components[i].Component is null)
                throw new ArgumentException("Components must not contain null.", nameof(components));

            if (double.IsNaN(components[i].Weight) || double.IsInfinity(components[i].Weight))
                throw new ArgumentOutOfRangeException(
                    nameof(components), components[i].Weight, "A weight must be finite.");
        }

        _name = BuildName(components);
        _components = new ITextScorer[components.Length];
        _weights = new double[components.Length];

        int kept = 0;

        for (int i = 0; i < components.Length; i++)
        {
            (ITextScorer component, double weight) = components[i];

            // Skipping rather than multiplying by zero: a zero weight is a statement that the
            // component does not participate, and a component that answers with a non-finite score
            // must not reach the total at all.
            if (weight == 0)
                continue;

            _components[kept] = component;
            _weights[kept] = weight;
            kept++;
        }

        Array.Resize(ref _components, kept);
        Array.Resize(ref _weights, kept);
    }

    /// <inheritdoc />
    public string Name => _name;

    /// <summary>
    /// The weighted parts, in summation order, with the zero-weight ones already dropped.
    /// </summary>
    public IReadOnlyList<(ITextScorer Component, double Weight)> Components
    {
        get
        {
            var components = new (ITextScorer, double)[_components.Length];

            for (int i = 0; i < _components.Length; i++)
                components[i] = (_components[i], _weights[i]);

            return components;
        }
    }

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        double total = 0;

        for (int i = 0; i < _components.Length; i++)
            total += _weights[i] * _components[i].Score(documentId, queryTerms, index);

        return total;
    }

    /// <summary>
    /// A name that identifies the configuration rather than the shape: the same three components at
    /// three different weights are three different scorers, and a log line or a cost sheet that
    /// cannot tell them apart has lost the thing it was for.
    /// </summary>
    private static string BuildName((ITextScorer Component, double Weight)[] components)
    {
        var builder = new StringBuilder("Composite(");

        for (int i = 0; i < components.Length; i++)
        {
            if (i > 0)
                builder.Append(" + ");

            // Invariant, or a French locale reads a different score out of the same name.
            _ = builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"{components[i].Component.Name}*{components[i].Weight:0.###}"));
        }

        return builder.Append(')').ToString();
    }
}