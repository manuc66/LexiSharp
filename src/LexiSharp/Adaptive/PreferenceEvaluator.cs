using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>Options of a preference replay.</summary>
public sealed record PreferenceEvaluationOptions
{
    /// <summary>The bootstrap seed. Default: <c>7</c>.</summary>
    public int Seed { get; init; } = 7;

    /// <summary>Importance weights are clipped to this value. Default: <c>10</c>.</summary>
    public double MaxImportanceWeight { get; init; } = 10;
}

/// <summary>Agreement metrics for one logged rank.</summary>
/// <param name="LoggedRank">The rank of the chosen category in the log, 1-based.</param>
/// <param name="Events">Events at this rank.</param>
/// <param name="Top1Agreement">Share where the re-ranked top-1 is the chosen category.</param>
/// <param name="MeanReciprocalRank">Mean reciprocal rank of the chosen category.</param>
public sealed record RankSlice(
    int LoggedRank,
    int Events,
    double Top1Agreement,
    double MeanReciprocalRank);

/// <summary>The outcome of one preference replay.</summary>
/// <param name="Events">Events replayed.</param>
/// <param name="WithPropensity">Events that carried a propensity.</param>
/// <param name="Top1Agreement">Share where the re-ranked top-1 is the chosen category.</param>
/// <param name="MeanReciprocalRank">Mean reciprocal rank of the chosen category.</param>
/// <param name="IpsTop1">Self-normalized IPS estimate of the top-1 agreement, or null.</param>
/// <param name="EffectiveSampleSize">The effective sample size of the importance weights.</param>
/// <param name="Clipped">How many weights were clipped.</param>
/// <param name="ByLoggedRank">Metrics sliced by the chosen category's logged rank.</param>
/// <param name="OrderHash">SHA-256 of the replayed sequence; empty for an empty stream.</param>
public sealed record PreferenceReport(
    int Events,
    int WithPropensity,
    double Top1Agreement,
    double MeanReciprocalRank,
    double? IpsTop1,
    double EffectiveSampleSize,
    int Clipped,
    IReadOnlyList<RankSlice> ByLoggedRank,
    string OrderHash);

/// <summary>Replays logged choices through a fixed preference policy.</summary>
/// <remarks>
/// <para>
/// <see cref="Replay"/> evaluates a <b>fixed</b> policy: it does not train it. To evaluate an online
/// learner, train on a warmup, then re-rank before observing each remaining event; training on the
/// stream being evaluated makes the result in-sample.
/// </para>
/// <para>
/// <see cref="PreferenceReport.IpsTop1"/> is a self-normalized estimate of the probability that the
/// target top-1 is the logged choice, under the logging distribution. It requires the logged
/// propensity of the chosen category and assumes positivity and ignorability. It is not a measure
/// of user satisfaction.
/// </para>
/// </remarks>
public static class PreferenceEvaluator
{
    /// <summary>Replays an ordered preference stream.</summary>
    /// <param name="events">The events, in the order they happened.</param>
    /// <param name="policy">The fixed policy to evaluate.</param>
    /// <param name="options">Clipping and seed options; null uses the defaults.</param>
    /// <returns>Agreement, MRR, per-rank slices and the IPS estimate.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its contract.</exception>
    public static PreferenceReport Replay(
        IEnumerable<PreferenceEvent> events,
        IPreferencePolicy policy,
        PreferenceEvaluationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(policy);

        options ??= new PreferenceEvaluationOptions();
        ValidateOptions(options);

        var list = events.ToList();

        if (list.Count == 0)
        {
            return new PreferenceReport(
                0,
                0,
                0,
                0,
                null,
                0,
                0,
                Array.Empty<RankSlice>(),
                string.Empty);
        }

        int agreements = 0;
        int withPropensity = 0;
        int clipped = 0;
        double reciprocalSum = 0;
        double numerator = 0;
        double denominator = 0;
        double sumWeights = 0;
        double sumSquares = 0;

        var slices = new Dictionary<int, (int Events, int Agreements, double Reciprocal)>();

        foreach (PreferenceEvent preference in list)
        {
            ArgumentNullException.ThrowIfNull(preference);

            int loggedRank = IndexOf(preference.Candidates, preference.Chosen) + 1;

            if (loggedRank == 0)
            {
                throw new ArgumentException(
                    "The chosen category is not among the logged candidates.",
                    nameof(events));
            }

            IReadOnlyList<ClassificationResult> reranked = policy.ReRank(
                preference.Text,
                preference.ContextKey,
                preference.Candidates);

            string? top1 = reranked.Count > 0 ? reranked[0].Category : null;
            bool agreement = string.Equals(top1, preference.Chosen, StringComparison.Ordinal);
            int rank = IndexOf(reranked, preference.Chosen) + 1;
            double reciprocal = rank > 0 ? 1.0 / rank : 0;

            if (agreement)
            {
                agreements++;
            }

            reciprocalSum += reciprocal;

            if (!slices.TryGetValue(loggedRank, out (int Events, int Agreements, double Reciprocal) slice))
            {
                slice = (0, 0, 0);
            }

            slices[loggedRank] = (
                slice.Events + 1,
                slice.Agreements + (agreement ? 1 : 0),
                slice.Reciprocal + reciprocal);

            if (preference.Propensity is { } propensity)
            {
                double weight = 1 / propensity;

                if (weight > options.MaxImportanceWeight)
                {
                    weight = options.MaxImportanceWeight;
                    clipped++;
                }

                numerator += (agreement ? 1 : 0) * weight;
                denominator += weight;
                sumWeights += weight;
                sumSquares += weight * weight;
                withPropensity++;
            }
        }

        var byRank = new List<RankSlice>(slices.Count);

        foreach ((int rank, (int count, int correct, double reciprocal)) in slices.OrderBy(pair => pair.Key))
        {
            byRank.Add(new RankSlice(
                rank,
                count,
                correct / (double)count,
                reciprocal / count));
        }

        return new PreferenceReport(
            list.Count,
            withPropensity,
            agreements / (double)list.Count,
            reciprocalSum / list.Count,
            withPropensity > 0 ? numerator / denominator : null,
            sumSquares > 0 ? (sumWeights * sumWeights) / sumSquares : 0,
            clipped,
            byRank,
            OrderHash(list));
    }

    private static int IndexOf(IReadOnlyList<ClassificationResult> candidates, string category)
    {
        for (int i = 0; i < candidates.Count; i++)
        {
            if (string.Equals(candidates[i].Category, category, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static string OrderHash(IReadOnlyList<PreferenceEvent> events)
    {
        var builder = new StringBuilder();

        foreach (PreferenceEvent preference in events)
        {
            builder.Append(HashContext(preference.ContextKey)).Append('|')
                .Append(preference.At.UtcTicks).Append('|')
                .Append(preference.Chosen).Append('|');

            foreach (ClassificationResult candidate in preference.Candidates)
            {
                builder.Append(candidate.Category).Append(':')
                    .Append(candidate.Probability.ToString("R", CultureInfo.InvariantCulture))
                    .Append(',');
            }

            builder.Append('|')
                .Append(preference.Propensity is { } propensity
                    ? BitConverter.DoubleToInt64Bits(propensity).ToString(CultureInfo.InvariantCulture)
                    : string.Empty)
                .Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static string HashContext(string context)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(context));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static void ValidateOptions(PreferenceEvaluationOptions options)
    {
        if (!double.IsFinite(options.MaxImportanceWeight) || options.MaxImportanceWeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxImportanceWeight,
                "MaxImportanceWeight must be finite and positive.");
        }
    }
}
