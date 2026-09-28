namespace LexiSharp.Core;

/// <summary>
/// Optional capability of an <see cref="ITextClassifier"/>: adjust a trained model from user
/// feedback, one text-to-category association at a time, without a retraining pass over the
/// corpus. Detected with pattern matching, like <see cref="IWeightedPredictor"/>.
/// </summary>
/// <remarks>
/// <para>
/// Reinforcement is an additive offset on a category's log score, kept in a ledger beside the
/// corpus model rather than folded into it. That is what makes <see cref="Unreinforce"/> an exact
/// inverse and <see cref="ForgetReinforcement"/> a clean reset: the corpus model is never written
/// to, so no sequence of feedback can leave it in a state a retrain would not have produced.
/// </para>
/// <para>
/// The label set is still the corpus's. A category the classifier was never trained on has no
/// prior and no class-conditional distribution, so reinforcing it does nothing — use
/// <see cref="ITextClassifier.Train"/> to introduce a category.
/// </para>
/// <para>
/// <see cref="ITextClassifier.Train"/> never writes the ledger. Reinforcing a category that the
/// current corpus does not contain is a no-op for as long as that is true, and the recorded
/// evidence applies again if a later retrain brings the category back. <see cref="ForgetReinforcement"/>
/// is how a caller says the evidence should not come back.
/// </para>
/// <para>
/// Thread-safety: the same contract as <see cref="ITextClassifier"/> — the mutating members and
/// <see cref="ITextClassifier.Predict(string, int, IReadOnlySet{string})"/> must not overlap.
/// <see cref="Classification.SynchronizedTextClassifier"/> in the <c>LexiSharp.Classification</c>
/// namespace wraps a classifier to provide that synchronization.
/// </para>
/// </remarks>
public interface IReinforceableTextClassifier : ITextClassifier
{
    /// <summary>
    /// Adjusts the model towards <paramref name="category"/> for <paramref name="text"/>, by
    /// <paramref name="weight"/> units of evidence per distinct term. A negative weight adjusts it
    /// away from the category, which is how a user says "this text is not that category" without
    /// having to guess the category it is instead.
    /// </summary>
    /// <remarks>
    /// Callers can depend on three properties, and should not need to care how they are achieved:
    /// the effect is proportional to <paramref name="weight"/>; it accumulates over repeated calls;
    /// and each distinct term counts once however often the text repeats it. A term the corpus has
    /// never seen is reinforced at full strength, including under
    /// <see cref="Classification.IdfMode.ClassCount"/> where the likelihood path discards it — an
    /// explicit correction built entirely from unfamiliar words still has to say something.
    /// </remarks>
    /// <param name="text">The text the feedback is about; tokenized internally.</param>
    /// <param name="category">The category to reinforce. Ignored when null or empty.</param>
    /// <param name="weight">
    /// Signed strength. Zero does nothing; positive reinforces, negative penalizes. Must be finite.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is NaN or infinite.</exception>
    void Reinforce(string text, string category, double weight = 1.0);

    /// <summary>
    /// Removes <paramref name="weight"/> units of reinforcement, exactly undoing a
    /// <see cref="Reinforce"/> of the same weight. Because the ledger is additive, this is the same
    /// operation as reinforcing by <c>-weight</c> and nothing more: it is not an error to
    /// unreinforce text that was never reinforced, the category is simply pushed further away. Use
    /// <see cref="ForgetReinforcement"/> to return to the trained model.
    /// </summary>
    /// <param name="text">The text the feedback is about; tokenized internally.</param>
    /// <param name="category">The category to remove reinforcement from.</param>
    /// <param name="weight">Signed strength to remove; see <see cref="Reinforce"/>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="weight"/> is NaN or infinite.</exception>
    void Unreinforce(string text, string category, double weight = 1.0);

    /// <summary>
    /// Drops every reinforcement recorded so far, returning the model to exactly the state
    /// <see cref="ITextClassifier.Train"/> left it in. The corpus model is untouched, so this is
    /// always a clean reset rather than an approximation of one.
    /// </summary>
    void ForgetReinforcement();
}
