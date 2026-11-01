using LexiSharp.Core;
using LexiSharp.Ranking;

namespace LexiSharp.Classification;

/// <summary>How a neighbour's similarity becomes a vote.</summary>
public enum NeighborVote
{
    /// <summary>Weight the neighbour by its (non-negative) similarity.</summary>
    Score,

    /// <summary>Weight the neighbour by <c>1 / (rank + 1)</c>, ignoring the score gap.</summary>
    Rank,

    /// <summary>Weight the neighbour by a softmax over its similarity divided by <see cref="NearestNeighborOptions.Temperature"/>.</summary>
    Temperature,
}

/// <summary>
/// Tunable knobs for <see cref="NearestNeighborClassifier"/>: how many neighbours vote, how their
/// scores become weights, the BM25 parameters of the lexical path, and the embedding role of the
/// dense path.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="K"/> is the number of neighbours retrieved, not the number of categories returned.
/// A non-positive <see cref="K"/> makes prediction return nothing rather than silently choosing
/// one neighbour.
/// </para>
/// <para>
/// <see cref="NeighborVote.Score"/> is the natural choice for BM25, whose scores are already
/// non-negative and additive. <see cref="NeighborVote.Rank"/> ignores the score scale, which is
/// useful when the distance is only ordinal. <see cref="NeighborVote.Temperature"/> is the choice
/// for cosine similarities, which live in a narrow band and need a scale to become a posterior.
/// The temperature changes confidence and the vote mix, never the neighbour order.
/// </para>
/// <para>
/// <see cref="TrainingUse"/> and <see cref="PredictionUse"/> only apply to the dense constructor.
/// The default is <see cref="EmbeddingUse.Query"/> on both sides, because nearest-neighbour
/// classification is symmetric: the labelled examples and the text being classified play the same
/// role. Set both to <see cref="EmbeddingUse.Passage"/> for a model that expects its passage
/// convention on the examples instead.
/// </para>
/// </remarks>
public sealed record NearestNeighborOptions
{
    /// <summary>Default options: K = 5, score vote, temperature 0.1, balanced BM25.</summary>
    public static readonly NearestNeighborOptions Default = new();

    /// <summary>Number of neighbours retrieved. Default: <c>5</c>.</summary>
    public int K { get; init; } = 5;

    /// <summary>How a neighbour's score becomes a vote. Default: <see cref="NeighborVote.Score"/>.</summary>
    public NeighborVote Vote { get; init; } = NeighborVote.Score;

    /// <summary>Softmax temperature for <see cref="NeighborVote.Temperature"/>. Default: <c>0.1</c>.</summary>
    public double Temperature { get; init; } = 0.1;

    /// <summary>Okapi BM25 parameters for the lexical path. Default: <see cref="Bm25Parameters.Balanced"/>.</summary>
    public Bm25Parameters Bm25 { get; init; } = Bm25Parameters.Balanced;

    /// <summary>
    /// When the winning probability is below this, prediction returns nothing instead of an
    /// answer. <c>0</c> never abstains. Default: <c>0</c>.
    /// </summary>
    public double MinProbability { get; init; }

    /// <summary>The role given to training documents by the dense path. Default: <see cref="EmbeddingUse.Query"/>.</summary>
    public EmbeddingUse TrainingUse { get; init; } = EmbeddingUse.Query;

    /// <summary>The role given to the text being classified by the dense path. Default: <see cref="EmbeddingUse.Query"/>.</summary>
    public EmbeddingUse PredictionUse { get; init; } = EmbeddingUse.Query;

    internal bool IsValid =>
        K >= 0
        && Temperature > 0
        && double.IsFinite(Temperature)
        && MinProbability >= 0
        && MinProbability <= 1
        && double.IsFinite(MinProbability)
        && Bm25 is not null
        && Bm25.K1 >= 0
        && double.IsFinite(Bm25.K1)
        && Bm25.B >= 0
        && Bm25.B <= 1
        && double.IsFinite(Bm25.B);
}
