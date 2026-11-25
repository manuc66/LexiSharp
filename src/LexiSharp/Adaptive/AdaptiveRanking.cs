using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>Tunable weights of the adaptive ranker.</summary>
public sealed record AdaptiveOptions
{
    /// <summary>The default options: λ = 0.5, γ = ln 2, no score floor.</summary>
    public static AdaptiveOptions Default { get; } = new();

    /// <summary>Weight of the acceptance share in the multiplier. Must be finite and non-negative. Default: <c>0.5</c>.</summary>
    public double AcceptanceWeight { get; init; } = 0.5;

    /// <summary>Weight of one decayed refusal in the exponent. Must be finite and non-negative. Default: <c>ln 2</c>.</summary>
    public double RefusalWeight { get; init; } = Math.Log(2.0);

    /// <summary>A top score below this abstains even without a conformal set. In <c>[0, 1]</c>. Default: <c>0</c>.</summary>
    public double MinScore { get; init; }
}

/// <summary>One category's three contributions in an adaptive ranking.</summary>
/// <param name="Category">The category label.</param>
/// <param name="TextProbability">The wrapped model's probability.</param>
/// <param name="AcceptanceShare">The shrunk acceptance share from the memory.</param>
/// <param name="RefusalMass">The decayed refusal mass from the memory.</param>
/// <param name="ContextMultiplier">The multiplier applied to the text probability.</param>
/// <param name="FinalProbability">The normalized final probability.</param>
/// <param name="Vetoed">Whether the memory vetoed the category; retained only on a fallback.</param>
/// <param name="InPredictionSet">Whether the conformal set contains the category; false without a calibrator.</param>
public sealed record AdaptiveScore(
    string Category,
    double TextProbability,
    double AcceptanceShare,
    double RefusalMass,
    double ContextMultiplier,
    double FinalProbability,
    bool Vetoed,
    bool InPredictionSet);

/// <summary>The outcome of one adaptive prediction.</summary>
/// <param name="Results">The top <c>limit</c> scores, best-first.</param>
/// <param name="AllScores">Every surviving score, best-first, for the log and the calibrator.</param>
/// <param name="PredictionSet">The conformal set, or null when no calibrator is configured.</param>
/// <param name="Abstained">Whether the caller should not show an offer.</param>
/// <param name="VetoFallback">Whether every category was vetoed and the un-vetoed list was used instead.</param>
/// <param name="ContextEvidence">The context's evidence mass, in <c>[0, 1)</c>.</param>
/// <param name="At">The instant the prediction was made at.</param>
public sealed record AdaptiveRanking(
    IReadOnlyList<AdaptiveScore> Results,
    IReadOnlyList<AdaptiveScore> AllScores,
    PredictionSet? PredictionSet,
    bool Abstained,
    bool VetoFallback,
    double ContextEvidence,
    DateTimeOffset At);

/// <summary>A prediction with the contributions that produced it.</summary>
/// <param name="Text">The text that was classified.</param>
/// <param name="At">The instant the prediction was made at.</param>
/// <param name="Prior">The context evidence used.</param>
/// <param name="Scores">Every surviving score, best-first.</param>
/// <param name="TextExplanation">The wrapped model's own explanation, when it supports one.</param>
public sealed record AdaptiveExplanation(
    string Text,
    DateTimeOffset At,
    ContextPrior Prior,
    IReadOnlyList<AdaptiveScore> Scores,
    ClassificationExplanation? TextExplanation);

/// <summary>A policy that predicts and learns from feedback; the seam an evaluator replays.</summary>
/// <typeparam name="TContext">The context key.</typeparam>
public interface IAdaptiveFeedbackPolicy<TContext> where TContext : notnull
{
    /// <summary>Ranks the categories for a text and context.</summary>
    /// <param name="text">The text to classify.</param>
    /// <param name="context">The caller's context key.</param>
    /// <param name="now">The instant the prediction is made at.</param>
    /// <param name="limit">Maximum number of returned categories.</param>
    AdaptiveRanking Predict(string text, TContext context, DateTimeOffset now, int limit = 6);

    /// <summary>Records one interaction.</summary>
    /// <param name="feedback">The feedback to record.</param>
    void Observe(FeedbackEvent<TContext> feedback);
}
