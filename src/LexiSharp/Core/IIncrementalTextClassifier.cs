namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextClassifier"/>: add or retract single labelled
/// documents without a retraining pass over the whole corpus. Detected with pattern matching, like
/// <see cref="IWeightedPredictor"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is corpus state, and it is the same state <see cref="ITextClassifier.Train"/> builds. A
/// classifier that learned documents one at a time is indistinguishable from one handed the whole
/// corpus at once — the vocabulary, the per-class term counts, the document frequencies and the
/// class priors all match. That parity is the whole contract, and it holds under every
/// <c>NaiveBayesOptions</c> setting, not only the defaults.
/// </para>
/// <para>
/// Contrast with <see cref="IReinforceableTextClassifier"/>, which adjusts a trained model from
/// user feedback without touching the corpus counts. The two are independent: learning a document
/// leaves reinforcement alone and forgetting reinforcement leaves learning alone.
/// </para>
/// <para>
/// <see cref="Unlearn"/> is arithmetic, not bookkeeping. It removes one document from the category
/// and subtracts that text's contribution, and it does not verify that the text was ever learned —
/// tracking which documents went in would cost memory proportional to the corpus, where the model
/// itself is proportional to the vocabulary. The consequence is that unlearning something the model
/// never saw is a well-defined operation rather than an error: it removes a document from the
/// category, and subtracts nothing else, because there is nothing else to subtract. Callers that
/// mean "retract exactly what I added" should keep track of it themselves; callers that mean "this
/// category has one document fewer" can just call it.
/// </para>
/// <para>
/// Thread-safety: the same contract as <see cref="ITextClassifier"/> — these must not run
/// concurrently with each other or with
/// <see cref="ITextClassifier.Predict(string, int, IReadOnlySet{string})"/>.
/// </para>
/// </remarks>
public interface IIncrementalTextClassifier : ITextClassifier
{
    /// <summary>
    /// Adds one labelled document to the model, as though it had been included in the last
    /// <see cref="ITextClassifier.Train"/> call alongside everything already learned. A document
    /// with no <see cref="SearchDocument.Category"/> is ignored, exactly as in
    /// <see cref="ITextClassifier.Train"/>.
    /// </summary>
    /// <remarks>
    /// Learning the same document twice weighs twice as much, matching two copies in the training
    /// corpus. Learning a document with empty text still adds the category and its document to the
    /// priors, with no vocabulary contribution — also matching <see cref="ITextClassifier.Train"/>.
    /// </remarks>
    /// <param name="document">The labelled document to learn.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    void Learn(SearchDocument document);

    /// <summary>
    /// Removes one document's contribution: the class loses a document from its prior, the given
    /// text's terms lose one count each, the document frequencies fall for every distinct term, and
    /// a category is retired once it holds no documents left. A document with no
    /// <see cref="SearchDocument.Category"/>, or one whose category the model does not hold, is
    /// ignored.
    /// </summary>
    /// <remarks>
    /// Exactly undoes a <see cref="Learn"/> of the same document. It does not check that the document
    /// was learned — see the remarks on <see cref="IIncrementalTextClassifier"/> for what that means.
    /// </remarks>
    /// <param name="document">The document to retract.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    void Unlearn(SearchDocument document);
}
