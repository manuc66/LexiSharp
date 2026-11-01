using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// Tunable knobs for <see cref="CentroidClassifier"/> and
/// <see cref="CentroidClassifier{TItem}"/>.
/// </summary>
/// <remarks>
/// <para>
/// The centroids are class means in the embedding space, and a prediction is a softmax over the
/// cosine between the input vector and each centroid. Cosine similarities live in a much narrower
/// band than the logits of a trained classifier — two unrelated E5 vectors often sit around
/// <c>0.75</c> and two related ones around <c>0.9</c> — so a temperature of <c>1</c> would return
/// an almost uniform distribution. The default is <c>0.1</c>, which turns a cosine gap of
/// <c>0.05</c> into roughly an <c>e^0.5</c> ratio between two categories. Tune it on a validation
/// split; it changes the confidence, never the ranking (the cosine order is the same at every
/// positive temperature).
/// </para>
/// <para>
/// <see cref="TrainingUse"/> and <see cref="PredictionUse"/> only apply to the text wrapper, which
/// needs to tell an asymmetric provider (E5 and friends) which side of the model a text belongs
/// to. The generic classifier already receives vectors and ignores both. The defaults are
/// <see cref="EmbeddingUse.Query"/> on both sides: centroid classification is symmetric — the
/// labelled examples and the text being classified play the same role — and E5's model card
/// recommends its query prefix for symmetric tasks. Set both to
/// <see cref="EmbeddingUse.Passage"/> for a model that expects its passage convention on the
/// examples instead.
/// </para>
/// </remarks>
public sealed record CentroidClassifierOptions
{
    /// <summary>Default options: temperature <c>0.1</c>, query embeddings on both sides.</summary>
    public static readonly CentroidClassifierOptions Default = new();

    /// <summary>
    /// Softmax temperature applied to the cosine similarities. Must be finite and greater than
    /// zero; smaller values sharpen the posterior. Default: <c>0.1</c>.
    /// </summary>
    public double Temperature { get; init; } = 0.1;

    /// <summary>
    /// The role given to training documents when the text wrapper embeds them. Default:
    /// <see cref="EmbeddingUse.Query"/>.
    /// </summary>
    public EmbeddingUse TrainingUse { get; init; } = EmbeddingUse.Query;

    /// <summary>
    /// The role given to the text being classified. Default: <see cref="EmbeddingUse.Query"/>.
    /// </summary>
    public EmbeddingUse PredictionUse { get; init; } = EmbeddingUse.Query;

    internal bool IsValid => double.IsFinite(Temperature) && Temperature > 0;
}
