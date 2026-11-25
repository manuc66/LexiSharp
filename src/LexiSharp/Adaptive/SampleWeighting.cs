using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>A document with the instant it was observed at, and how many times it was seen.</summary>
/// <param name="Document">The document to train on.</param>
/// <param name="At">The instant the document was observed at.</param>
/// <param name="Occurrences">
/// How many times the item was observed in the window. The first occurrence has full weight; later
/// ones are damped by the policy. Default: <c>1</c>.
/// </param>
public readonly record struct TimedDocument(
    SearchDocument Document,
    DateTimeOffset At,
    int Occurrences = 1);

/// <summary>
/// Expands timed documents into the whole copies a model without sample weights can consume.
/// </summary>
/// <remarks>
/// This is the fallback for models that cannot take a fractional weight (Naive Bayes, centroids,
/// nearest neighbours). For a model that accepts a weight, pass
/// <see cref="RecencyPolicy.Weight"/> through its <c>weightOf</c> parameter instead: copies inflate
/// the document counts and interact with priors and unlearning.
/// </remarks>
public static class SampleWeighting
{
    /// <summary>Yields each document once per copy the policy assigns it.</summary>
    /// <param name="samples">The timed documents to expand.</param>
    /// <param name="policy">The recency policy; it must be in <see cref="SampleWeightMode.Quantized"/> mode.</param>
    /// <param name="now">The instant the weights are evaluated at.</param>
    /// <returns>The documents in input order, repeated by their copy count; an expired sample is omitted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="samples"/> or <paramref name="policy"/> is null.</exception>
    /// <exception cref="ArgumentException">A sample carries a null document.</exception>
    /// <exception cref="InvalidOperationException">The policy is not in <see cref="SampleWeightMode.Quantized"/> mode.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A sample instant is after <paramref name="now"/>.</exception>
    public static IEnumerable<SearchDocument> Expand(
        IEnumerable<TimedDocument> samples,
        RecencyPolicy policy,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(policy);

        foreach (TimedDocument sample in samples)
        {
            if (sample.Document is null)
            {
                throw new ArgumentException(
                    "A timed document cannot carry a null document.",
                    nameof(samples));
            }

            int copies = policy.Copies(now, sample.At, sample.Occurrences);

            for (int i = 0; i < copies; i++)
            {
                yield return sample.Document;
            }
        }
    }
}
