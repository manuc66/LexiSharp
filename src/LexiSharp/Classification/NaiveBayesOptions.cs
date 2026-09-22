namespace LexiSharp.Classification;

/// <summary>
/// Tunable knobs for <see cref="NaiveBayesClassifier"/>; the defaults reproduce the classic
/// Laplace-smoothed, unweighted multinomial Naive Bayes.
/// </summary>
public sealed record NaiveBayesOptions
{
    /// <summary>Default options: no IDF weighting, temperature 1.</summary>
    public static readonly NaiveBayesOptions Default = new();

    /// <summary>
    /// Softmax temperature applied to the per-class log-odds before normalization.
    /// <c>Temperature &lt; 1</c> sharpens the posterior, <c>&gt; 1</c> flattens it towards uniform.
    /// Always positive. Default: <c>1</c> (no effect).
    /// </summary>
    public double Temperature { get; init; } = 1.0;

    /// <summary>
    /// When true, each term's evidence is scaled by an inverse-document-frequency weight
    /// <c>log(1 + N / df(term))</c> over the training corpus — rarer terms weigh more, constraining
    /// the plasticity of very frequent vocabulary. Default: <c>false</c>.
    /// </summary>
    public bool IdfWeighting { get; init; }

    internal bool IsValid => Temperature > 0 && !double.IsNaN(Temperature) && !double.IsInfinity(Temperature);
}