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

    /// <summary>
    /// When true, <see cref="NaiveBayesClassifier.Predict(string, int, IReadOnlySet{string})"/>
    /// returns no results for text that shares no token with the training vocabulary, instead of
    /// answering from the class priors. Default: <c>false</c> — existing behaviour, unchanged.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Without this, a text the model has never seen is scored as if its words meant something.
    /// Under Laplace smoothing an unseen token contributes <c>log(alpha / (N_c + alpha*V))</c> to
    /// class <c>c</c>, where <c>N_c</c> is that class's token count and <c>V</c> the shared
    /// vocabulary — so the class with <b>less</b> training text has the smaller denominator and
    /// gains the most evidence. A paragraph of out-of-vocabulary words therefore reads as
    /// evidence for whichever class was trained on the least, and the confidence rises with the
    /// amount of text the model cannot interpret: measured on a 7/3 document split, French prose
    /// with no shared token returns the minority class at <c>0.97</c>, and nonsense at
    /// <c>0.88</c>. Neither number is a probability of anything.
    /// </para>
    /// <para>
    /// The prior-dominant case is different and less dramatic: with no tokens at all — whitespace,
    /// an empty string — the likelihood contributes nothing and the answer is exactly the class
    /// prior, which on a 7/3 split is <c>0.70</c>. Correct arithmetic, and still not an answer to
    /// "what is this text".
    /// </para>
    /// <para>
    /// Setting this does not repair those scores; it declines to report them. Use
    /// <see cref="NaiveBayesClassifier.HasAnyVocabularyOverlap(string)"/> to inspect the condition
    /// without paying for a prediction, or with the option off, when a caller wants to fall back
    /// to something other than nothing.
    /// </para>
    /// <para>
    /// Note that <see cref="SkipOutOfVocabularyTokens"/> is not this. It stops unseen tokens from
    /// contributing evidence, which removes the 0.97 artifact — but an input with no tokens left
    /// to contribute still falls through to the prior, so the answer stays <c>0.70</c> rather than
    /// becoming an abstention.
    /// </para>
    /// </remarks>
    public bool AbstainWithoutVocabularyOverlap { get; init; }

    /// <summary>
    /// When true, the classifier learns each class from the documents of the *other* classes
    /// (Complement Naive Bayes, Rennie et al., ICML 2003) and scores a query through the
    /// likelihood of its terms under every class's complement. The crop with the smallest
    /// complement likelihood wins, so a query is attributed to the class whose training data is
    /// <em>least</em> compatible with it — which resists skewed priors better than standard
    /// multinomial scoring. Semantics match scikit-learn's <c>ComplementNB</c>: the per-term
    /// smoothing uses <see cref="Alpha"/> over the whole vocabulary, the winner maximizes
    /// <c>score(c) = Σ_t count(t) · w(t) · (−log P(t | c̄))</c>, probabilities are the softmax of
    /// that score, and class priors do not participate (as in the reference implementation).
    /// <see cref="IdfMode"/> and <see cref="SkipOutOfVocabularyTokens"/> apply as usual. Default:
    /// <c>false</c> (classic multinomial behavior).
    /// </summary>
    public bool Complement { get; init; }

    internal bool IsValid =>
        Temperature > 0 && !double.IsNaN(Temperature) && !double.IsInfinity(Temperature)
        && Alpha > 0 && !double.IsNaN(Alpha) && !double.IsInfinity(Alpha);
}