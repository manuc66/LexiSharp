using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// BM25F tuning knobs: term-frequency saturation (<c>k1</c>), document-length normalization
/// (<c>b</c>), and how much each field counts for.
/// </summary>
/// <param name="K1">
/// Term-frequency saturation; higher values let frequent terms contribute more. Must be
/// non-negative.
/// </param>
/// <param name="B">
/// Document-length normalization, in <c>[0, 1]</c>, applied to every field. <c>0</c> disables
/// normalization. The BM25F paper allows one <c>b</c> per field; this implementation uses a single
/// value for all of them.
/// </param>
/// <param name="FieldWeights">
/// Weight per field name, all non-negative. The weight multiplies a term's contribution in that
/// field, so a weight of <c>0</c> removes the field from the ranking entirely, and a field absent
/// from the map keeps the neutral weight of <c>1</c>. <see cref="TextFields.Default"/> is the
/// document's main text.
/// </param>
/// <remarks>
/// No default here is a claim that a weighting retrieves better. It is a starting point: a
/// field-weighted ranking has to be measured on your own corpus, with
/// <see cref="LexiSharp.Benchmarking.CorpusBenchmark"/>, the same way <c>k1</c> and <c>b</c> have
/// to be tuned rather than assumed.
/// </remarks>
public sealed record Bm25FParameters(
    double K1,
    double B,
    IReadOnlyDictionary<string, double>? FieldWeights = null)
{
    /// <summary>All fields weighted equally (k1 = 1.2, b = 0.75).</summary>
    public static Bm25FParameters Balanced { get; } = new(1.2, 0.75);

    /// <summary>Stronger length normalization (k1 = 2.0, b = 1.0).</summary>
    public static Bm25FParameters Aggressive { get; } = new(2.0, 1.0);

    /// <summary>Lighter normalization (k1 = 1.0, b = 0.5).</summary>
    public static Bm25FParameters Conservative { get; } = new(1.0, 0.5);

    /// <summary>
    /// A copy with one field's weight set, every other field left as it was. The usual shape is a
    /// chain from <see cref="Balanced"/>, favouring a title over the body:
    /// <c>Bm25FParameters.Balanced.WithWeight("title", 2.0)</c>.
    /// </summary>
    /// <param name="field">The field name; <see cref="TextFields.Default"/> for the main text.</param>
    /// <param name="weight">Its weight, non-negative. <c>0</c> removes the field from the ranking.</param>
    /// <exception cref="ArgumentException">The field name is null or blank.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The weight is negative or not finite.</exception>
    public Bm25FParameters WithWeight(string field, double weight)
    {
        TextFields.Validate(field, nameof(field));

        if (double.IsNaN(weight) || double.IsInfinity(weight) || weight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(weight), weight, "A field weight must be non-negative and finite.");
        }

        var weights = FieldWeights is null
            ? new Dictionary<string, double>(StringComparer.Ordinal)
            : new Dictionary<string, double>(FieldWeights, StringComparer.Ordinal);

        weights[field] = weight;
        return this with { FieldWeights = weights };
    }
}
