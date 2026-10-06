namespace LexiSharp.Core;

/// <summary>
/// A signed score adjustment: a multiplicative factor plus an additive offset. Both can go up
/// or down, so positive <b>and</b> negative boosts are expressed the same way.
/// </summary>
/// <remarks>
/// The adjusted score is <c>score * Multiply + Add</c>.
/// <list type="bullet">
/// <item><b>Positive boost</b>: <see cref="Factor"/> &gt; 1, or a positive <see cref="Offset"/>.</item>
/// <item><b>Negative boost (damp)</b>: <see cref="Factor"/> in (0, 1) — down-weights a match.</item>
/// <item><b>Negative boost (penalty)</b>: a negative <see cref="Offset"/> — subtracts from a match.</item>
/// <item><see cref="Factor"/> = 0 zeroes the score (document excluded, score-0 convention).</item>
/// <item><see cref="None"/> leaves the score untouched.</item>
/// </list>
/// <para>
/// <b>Write <see cref="None"/>, not <c>default</c>, to mean "no change".</b> The optional
/// constructor parameters give <c>new ScoreBoost()</c> a <see cref="Multiply"/> of 1, but
/// <c>default(ScoreBoost)</c> does not go through the constructor: it is all zeroes, so its factor
/// is 0 — which is the *exclude* case above. A boost function returning <c>default</c> therefore
/// drops every document it is asked about, which is the opposite of what the expression reads as.
/// </para>
/// </remarks>
/// <param name="Add">Additive adjustment, added after the multiplication (default: 0).</param>
/// <param name="Multiply">Multiplicative factor (default: 1). Negative would invert the ranking and is rejected.</param>
public readonly record struct ScoreBoost(double Add = 0, double Multiply = 1)
{
    /// <summary>
    /// No adjustment: the candidate keeps the score the inner engine gave it.
    /// </summary>
    /// <remarks>
    /// This is what a boost returns when it declines to act. It is a distinct value from
    /// <c>default(ScoreBoost)</c>, which excludes the document — see the type remarks.
    /// </remarks>
    public static ScoreBoost None => new(0, 1);

    /// <summary>A pure multiplicative boost: &gt;1 boosts, &lt;1 damps, 0 excludes.</summary>
    public static ScoreBoost Factor(double factor) => new(0, factor);

    /// <summary>A pure additive boost: positive bonus, negative penalty.</summary>
    public static ScoreBoost Offset(double amount) => new(amount, 1);

    /// <summary>Implicitly treats a plain double as a multiplicative <see cref="Factor"/>.</summary>
    public static implicit operator ScoreBoost(double factor) => Factor(factor);
}