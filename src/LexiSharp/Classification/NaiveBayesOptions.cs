namespace LexiSharp.Classification;

/// <summary>
/// How term evidence is scaled by corpus rarity during classification. Term frequency is
/// always counted from the training corpus; this knob decides whether (and how) the raw
/// class-conditional counts are discounted for terms that appear across many classes or
/// documents.
/// </summary>
public enum IdfMode
{
    /// <summary>No inverse-document-frequency weighting: every term's evidence counts as-is.</summary>
    None,

    /// <summary>
    /// Scale each term by <c>log(1 + N / df)</c> where N is the number of training documents
    /// and df the number of documents containing the term. Rarer terms weigh more, constraining
    /// the plasticity of very frequent vocabulary.
    /// </summary>
    DocumentCount,

    /// <summary>
    /// Scale each term by <c>max(0, log(C / df))</c> where C is the number of classes and df the
    /// number of documents containing the term. Terms present in every class drop out entirely
    /// (idf = 0), so only class-discriminative vocabulary feeds the likelihood.
    /// </summary>
    ClassCount,
}

/// <summary>
/// Tunable knobs for <see cref="NaiveBayesClassifier"/>; the defaults reproduce the classic
/// Laplace-smoothed, unweighted multinomial Naive Bayes.
/// </summary>
public sealed record NaiveBayesOptions
{
    /// <summary>Default options: add-1 smooth an unweighted multinomial, softmax temperature 1.</summary>
    public static readonly NaiveBayesOptions Default = new();

    /// <summary>
    /// Softmax temperature applied to the per-class log-odds before normalization.
    /// <c>Temperature &lt; 1</c> sharpens the posterior, <c>&gt; 1</c> flattens it towards uniform.
    /// Always positive. Default: <c>1</c> (no effect).
    /// </summary>
    public double Temperature { get; init; } = 1.0;

    /// <summary>
    /// Inverse-document-frequency weighting applied to each term's evidence. Default:
    /// <see cref="IdfMode.None"/> (classic behavior). See <see cref="IdfMode"/> for the formulas.
    /// </summary>
    public IdfMode IdfMode { get; init; } = IdfMode.None;

    /// <summary>
    /// Additive smoothing coefficient applied to the class-conditional term probabilities and,
    /// when <see cref="SmoothPriors"/> is set, to the class priors. The classic add-1 Laplace
    /// smoothing is <c>Alpha = 1</c> (the default); smaller values sharpen the likelihood.
    /// Always positive.
    /// </summary>
    public double Alpha { get; init; } = 1.0;

    /// <summary>
    /// When true, the class prior is smoothed by <see cref="Alpha"/>:
    /// <c>log((N_c + Alpha) / (N + Alpha * C))</c> instead of the maximum-likelihood
    /// <c>log(N_c / N)</c>. Prevents an empty class from ever getting a zero probability.
    /// Default: <c>false</c>.
    /// </summary>
    public bool SmoothPriors { get; init; }

    /// <summary>
    /// When true, a query token absent from the training vocabulary contributes no evidence at
    /// all — it is skipped instead of dragging every class down through the Laplace smoothing
    /// term. Use with a pre-filtered vocabulary (e.g. a spell-corrector that only ever yields
    /// known terms). Default: <c>false</c>.
    /// </summary>
    public bool SkipOutOfVocabularyTokens { get; init; }

    internal bool IsValid =>
        Temperature > 0 && !double.IsNaN(Temperature) && !double.IsInfinity(Temperature)
        && Alpha > 0 && !double.IsNaN(Alpha) && !double.IsInfinity(Alpha);
}