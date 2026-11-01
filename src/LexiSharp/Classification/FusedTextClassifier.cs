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
/// <para>
/// This is the two-lane case of <see cref="BlendTextClassifier"/>: <c>FusedTextClassifier(a, b, w)</c>
/// produces exactly the same distribution as
/// <c>BlendTextClassifier([(a, 1-w), (b, w)])</c>. Use the latter when the blend has more than two
/// models or when candidate/prior evidence has to be injected.
/// </para>
/// </remarks>
public sealed class FusedTextClassifier : ITextClassifier
{
    private readonly BlendTextClassifier _blend;
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

        _secondWeight = secondWeight;
        _blend = new BlendTextClassifier(new[]
        {
            new TextClassifierLane(first, 1 - secondWeight),
            new TextClassifierLane(second, secondWeight),
        });
    }

    /// <summary>The weight of the second model in the blend.</summary>
    public double SecondWeight => _secondWeight;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _blend.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null) =>
        _blend.Predict(text, limit, excludedCategories);

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
        _blend.PredictBest(text, excludedCategories);
}
