using LexiSharp.Linguistics;

namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextClassifier"/> that can explain a prediction:
/// the predicted categories, the contribution of each underlying lane (for blended models)
/// and the terms that drove the decision.
/// </summary>
public interface IExplainableTextClassifier : ITextClassifier
{
    /// <summary>
    /// Explains the prediction for <paramref name="text"/>, or returns <c>null</c> when the
    /// model is untrained or cannot produce an explanation.
    /// </summary>
    ClassificationExplanation? Explain(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null);

    /// <summary>
    /// Returns the effect of removing each distinct term of <paramref name="text"/> on the
    /// predicted category, most impactful first.
    /// </summary>
    IReadOnlyList<Counterfactual> Counterfactuals(string text, int top = 5)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (top <= 0)
        {
            return Array.Empty<Counterfactual>();
        }

        IReadOnlyList<ClassificationResult> before = Predict(text, limit: 1);
        string? bestBefore = before.Count > 0 ? before[0].Category : null;
        double scoreBefore = before.Count > 0 ? before[0].Probability : 0;

        var tokens = Tokenizer.Default.Tokenize(text).Distinct(StringComparer.Ordinal).ToList();

        var results = new List<Counterfactual>();
        foreach (string term in tokens)
        {
            string afterText = string.Join(' ', tokens.Where(t => !string.Equals(t, term, StringComparison.Ordinal)));
            IReadOnlyList<ClassificationResult> after = string.IsNullOrEmpty(afterText)
                ? Array.Empty<ClassificationResult>()
                : Predict(afterText, limit: 1);
            string? bestAfter = after.Count > 0 ? after[0].Category : null;
            double scoreAfter = after.Count > 0 ? after[0].Probability : 0;

            if (!string.Equals(bestBefore, bestAfter, StringComparison.Ordinal) || Math.Abs(scoreBefore - scoreAfter) > 1e-12)
            {
                results.Add(new Counterfactual(term, scoreBefore, scoreAfter, bestAfter));
            }
        }

        return results
            .OrderByDescending(c => Math.Abs(c.ScoreBefore - c.ScoreAfter))
            .Take(top)
            .ToList();
    }
}

/// <summary>The contribution of one underlying lane (classifier) to a blended prediction.</summary>
/// <param name="Lane">Name of the lane (e.g. <c>NaiveBayes</c>, <c>NearestNeighbor</c>).</param>
/// <param name="Weight">The lane's blend weight in <c>[0, 1]</c>.</param>
/// <param name="Results">The lane's own prediction.</param>
public sealed record LaneContribution(
    string Lane,
    double Weight,
    IReadOnlyList<ClassificationResult> Results);

/// <summary>A transparent breakdown of a classification prediction.</summary>
/// <param name="Results">The final predicted categories.</param>
/// <param name="Lanes">Per-lane contributions (one entry for a single classifier).</param>
/// <param name="Terms">The (normalized) terms that drove the decision, with their aggregate weight.</param>
public sealed record ClassificationExplanation(
    IReadOnlyList<ClassificationResult> Results,
    IReadOnlyList<LaneContribution> Lanes,
    IReadOnlyList<TermContribution> Terms);
