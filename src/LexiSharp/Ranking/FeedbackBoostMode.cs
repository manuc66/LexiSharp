namespace LexiSharp.Ranking;

/// <summary>
/// How a feedback channel's strength changes a candidate's base score.
/// </summary>
public enum FeedbackBoostMode
{
    /// <summary>
    /// Adds <c>strength * maxBoost</c> to the base score. The default, and the behaviour of every
    /// release before this mode existed.
    /// </summary>
    Additive = 0,

    /// <summary>
    /// Multiplies the base score by <c>1 + strength * maxBoost</c>, never below zero. The same
    /// <c>maxBoost</c> is then a relative change — <c>0.5</c> is at most +50 % at strength 1 — so
    /// it does not depend on the scale of the underlying scorer.
    /// </summary>
    Multiplicative = 1,
}
