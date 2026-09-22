namespace LexiSharp.Core;

/// <summary>
/// A token paired with a fractional relevance weight, for classifiers that can consume
/// pre-weightened tokens (e.g. a token corrected by a spell-corrector should carry less
/// evidence than an exact match).
/// </summary>
/// <param name="Token">A single token, in the tokenizer's normalized form.</param>
/// <param name="Weight">How much this token counts toward the prediction (fractional allowed).</param>
public sealed record WeightedToken(string Token, double Weight)
{
    /// <summary>A token with full (1.0) weight — the default for unconverted tokens.</summary>
    public static WeightedToken Full(string token) => new(token, 1.0);
}

/// <summary>
/// Optional capability of a <see cref="ITextClassifier"/> that accepts
/// <see cref="WeightedToken"/>s instead of raw text, bypassing internal tokenization.
/// Detected with pattern matching (<c>classifier is IWeightedPredictor</c>).
/// </summary>
public interface IWeightedPredictor
{
    /// <summary>
    /// Predicts the most likely categories for a pre-tokenized, possibly weighted, sequence of
    /// tokens, by decreasing probability.
    /// </summary>
    /// <param name="tokens">Tokens of the input, already normalized, each with a weight in <c>(0, 1]</c>.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional set of categories to hide; probabilities are renormalized over the rest.</param>
    IReadOnlyList<ClassificationResult> Predict(
        IEnumerable<WeightedToken> tokens,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null);
}