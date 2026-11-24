namespace LexiSharp.Core;

/// <summary>
/// The effect of removing one term from a text on the predicted category.
/// </summary>
/// <param name="RemovedTerm">The term that was removed.</param>
/// <param name="ScoreBefore">Score of the winning category before removal.</param>
/// <param name="ScoreAfter">Score of the winning category after removal (0 if it disappeared).</param>
/// <param name="NewBestCategory">The winning category after removal, when it differs.</param>
public sealed record Counterfactual(
    string RemovedTerm,
    double ScoreBefore,
    double ScoreAfter,
    string? NewBestCategory);
