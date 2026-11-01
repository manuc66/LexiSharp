using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// Blends two text classifiers by probability: <c>(1 - w) · P(first) + w · P(second)</c>.
/// </summary>
/// <remarks>
/// <para>
/// The intended pair is <see cref="NaiveBayesClassifier"/> and <see cref="CentroidClassifier"/>:
/// the lexical model is strong on exact terms, names and identifiers, and the centroid model sees
/// paraphrases the lexical model cannot. <c>w</c> is the second model's weight and has to be tuned
/// on a validation split — this class blends probabilities, it does not calibrate them, and the
/// two models' notions of confidence are not comparable by construction.
/// </para>
/// <para>
/// A category present in only one model's output enters the blend with a contribution of zero for
/// the other, so a category the lexical model alone considered can still win. Each model is asked
/// for every category it holds (not just <c>limit</c>), because a category outside one model's top
/// few can be inside the final top few after the blend. Excluded categories are passed to both
/// models and the blended result is renormalized over the survivors; ties are ordered by category
/// name (ordinal).
/// </para>
/// <para>
/// <see cref="Train"/> trains both models. If either was already trained, its model is replaced —
/// the same contract as every other <see cref="ITextClassifier"/>.
/// </para>
/// </remarks>
public sealed class FusedTextClassifier : ITextClassifier
{
    private readonly ITextClassifier _first;
    private readonly ITextClassifier _second;
    private readonly double _secondWeight;

    /// <summary>Creates a blended classifier over two text classifiers.</summary>
    /// <param name="first">The lexical or primary model.</param>
    /// <param name="second">The semantic or secondary model.</param>
    /// <param name="secondWeight">
    /// The weight of <paramref name="second"/> in the blend, in <c>[0, 1]</c>. <c>0</c> returns the
    /// first model's distribution unchanged, <c>1</c> the second's. Default: <c>0.5</c>.
    /// </param>
    /// <exception cref="ArgumentNullException">A classifier is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="secondWeight"/> is not a finite number in <c>[0, 1]</c>.
    /// </exception>
    public FusedTextClassifier(
        ITextClassifier first,
        ITextClassifier second,
        double secondWeight = 0.5)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        if (!double.IsFinite(secondWeight) || secondWeight < 0 || secondWeight > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secondWeight),
                "The second model's weight must be a finite number in [0, 1].");
        }

        _first = first;
        _second = second;
        _secondWeight = secondWeight;
    }

    /// <summary>The weight of the second model in the blend.</summary>
    public double SecondWeight => _secondWeight;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        // Materialize once: an IEnumerable may be a one-pass query, and training two models from
        // the same enumerator would otherwise hand the second an empty corpus.
        var corpus = documents as IReadOnlyList<SearchDocument> ?? documents.ToList();

        _first.Train(corpus);
        _second.Train(corpus);
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0)
            return Array.Empty<ClassificationResult>();

        var first = _first.Predict(text, limit: int.MaxValue, excludedCategories);
        var second = _second.Predict(text, limit: int.MaxValue, excludedCategories);

        if (first.Count == 0 && second.Count == 0)
            return Array.Empty<ClassificationResult>();

        var blended = new Dictionary<string, double>(first.Count + second.Count, StringComparer.Ordinal);

        foreach (var result in first)
            blended[result.Category] = (1 - _secondWeight) * result.Probability;

        foreach (var result in second)
        {
            blended.TryGetValue(result.Category, out double current);
            blended[result.Category] = current + (_secondWeight * result.Probability);
        }

        double total = 0;

        foreach (double probability in blended.Values)
            total += probability;

        if (total <= 0)
            return Array.Empty<ClassificationResult>();

        var ranked = new List<(string Category, double Probability)>(blended.Count);

        foreach (var (category, probability) in blended)
            ranked.Add((category, probability / total));

        ranked.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);
            return byProbability != 0
                ? byProbability
                : string.CompareOrdinal(left.Category, right.Category);
        });

        // Only the returned categories become result objects. The union can be wider than the
        // page, and materializing every category only to discard it is the allocation this avoids.
        int take = Math.Min(limit, ranked.Count);
        var results = new ClassificationResult[take];

        for (int i = 0; i < take; i++)
            results[i] = new ClassificationResult(ranked[i].Category, ranked[i].Probability);

        return results;
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }
}
