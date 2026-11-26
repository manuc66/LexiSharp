using System.Globalization;
using System.Security.Cryptography;
using System.Text;

using LexiSharp.Classification;
using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>Options of an adaptive replay.</summary>
public sealed record AdaptiveEvaluationOptions
{
    /// <summary>Maximum rank counted as correct. Default: <c>1</c>.</summary>
    public int TopK { get; init; } = 1;

    /// <summary>Predictions after a correction in which to look for recovery. Default: <c>50</c>.</summary>
    public int RecoveryHorizon { get; init; } = 50;

    /// <summary>Predictions after a correction in which to count repeated bad offers. Default: <c>50</c>.</summary>
    public int RepetitionHorizon { get; init; } = 50;

    /// <summary>Block-bootstrap resamples; zero means a degenerate interval. Default: <c>1000</c>.</summary>
    public int BootstrapResamples { get; init; } = 1000;

    /// <summary>The bootstrap seed. Default: <c>7</c>.</summary>
    public int Seed { get; init; } = 7;

    /// <summary>Miscoverage level of the conformal sets. Default: <c>0.10</c>.</summary>
    public double Alpha { get; init; } = 0.10;

    /// <summary>Importance weights are clipped to this value. Default: <c>10</c>.</summary>
    public double MaxImportanceWeight { get; init; } = 10;
}

/// <summary>How quickly the policy recovers the accepted category after a correction.</summary>
/// <param name="Episodes">Corrections observed.</param>
/// <param name="Recovered">Episodes that recovered inside the horizon.</param>
/// <param name="Censored">Episodes that did not recover inside the horizon.</param>
/// <param name="Mean">Mean predictions to recovery over recovered episodes.</param>
/// <param name="Median">Median predictions to recovery over recovered episodes.</param>
/// <param name="P90">90th percentile over recovered episodes.</param>
public sealed record TimeToRecoveryReport(
    int Episodes,
    int Recovered,
    int Censored,
    double Mean,
    double Median,
    double P90);

/// <summary>How often a corrected bad offer was repeated before it disappeared.</summary>
/// <param name="Episodes">Corrections with a bad offer.</param>
/// <param name="RepeatedOffers">Total repeated bad offers.</param>
/// <param name="Censored">Episodes still repeating at the horizon.</param>
/// <param name="Mean">Mean repeats per episode.</param>
/// <param name="P90">90th percentile of repeats per episode.</param>
/// <param name="Max">Maximum repeats in one episode.</param>
public sealed record RepetitionRegretReport(
    int Episodes,
    int RepeatedOffers,
    int Censored,
    double Mean,
    double P90,
    int Max);

/// <summary>A self-normalized IPS estimate with its effective sample size.</summary>
/// <param name="Events">Events that carried a propensity.</param>
/// <param name="Value">The estimated acceptance value of the target policy.</param>
/// <param name="EffectiveSampleSize">The effective sample size of the importance weights.</param>
/// <param name="Clipped">How many weights were clipped.</param>
public sealed record ImportanceReport(
    int Events,
    double Value,
    double EffectiveSampleSize,
    int Clipped);

/// <summary>The outcome of one adaptive replay.</summary>
/// <param name="Events">Rows replayed.</param>
/// <param name="Labeled">Rows that carried a human label.</param>
/// <param name="Accepted">Accepted offers.</param>
/// <param name="Corrected">Corrected offers.</param>
/// <param name="Rejected">Rejected offers.</param>
/// <param name="Abstained">Abstentions.</param>
/// <param name="Ignored">Ignored offers.</param>
/// <param name="LabeledAccuracy">Top-k accuracy on labeled rows.</param>
/// <param name="LabeledAccuracy95">Block-bootstrap interval of the labeled accuracy.</param>
/// <param name="AcceptanceRate">Accepted / events.</param>
/// <param name="CorrectionRate">Corrected / events.</param>
/// <param name="AbstentionRate">Abstained / events.</param>
/// <param name="IgnoredRate">Ignored / events.</param>
/// <param name="LabeledShare">Labeled / events.</param>
/// <param name="Ece">Expected calibration error on the top-1 confidence.</param>
/// <param name="Brier">Multi-class Brier score.</param>
/// <param name="LogLoss">Log-loss of the human label.</param>
/// <param name="NominalCoverage">The conformal coverage target, or null when no set was produced.</param>
/// <param name="EmpiricalCoverage">The share of labels inside the set, or null.</param>
/// <param name="MeanSetSize">Mean conformal set size, or null.</param>
/// <param name="Recovery">Time-to-recovery episodes.</param>
/// <param name="Repetition">Repetition-regret episodes.</param>
/// <param name="Importance">IPS estimate, or null without propensities.</param>
/// <param name="Blocks">Distinct calendar days in the stream.</param>
/// <param name="OrderHash">SHA-256 of the replayed sequence; empty for an empty stream.</param>
public sealed record AdaptiveReport(
    int Events,
    int Labeled,
    int Accepted,
    int Corrected,
    int Rejected,
    int Abstained,
    int Ignored,
    double LabeledAccuracy,
    ClassificationInterval LabeledAccuracy95,
    double AcceptanceRate,
    double CorrectionRate,
    double AbstentionRate,
    double IgnoredRate,
    double LabeledShare,
    double Ece,
    double Brier,
    double LogLoss,
    double? NominalCoverage,
    double? EmpiricalCoverage,
    double? MeanSetSize,
    TimeToRecoveryReport Recovery,
    RepetitionRegretReport Repetition,
    ImportanceReport? Importance,
    int Blocks,
    string OrderHash);

/// <summary>Replays a feedback stream through an adaptive policy: predict first, then observe.</summary>
/// <remarks>
/// <para>
/// The contract is the same as <see cref="OnlineClassificationEvaluator"/>: the policy is asked for
/// a ranking <b>before</b> the event is observed, so an event never helps classify itself. The
/// metrics are therefore measured on the policy that produced them, not on a policy trained on the
/// whole stream.
/// </para>
/// <para>
/// <see cref="AdaptiveReport.LabeledAccuracy"/> is conditional on feedback: it compares the
/// policy's ranking with the human label on the rows that carried one. It is not accuracy over the
/// unlabeled rows, and the report always carries <see cref="AdaptiveReport.LabeledShare"/> beside
/// it. IPS corrects the estimate only when the log carries propensities and the positivity and
/// ignorability assumptions hold; it is a diagnostic, not a deployment gate.
/// </para>
/// </remarks>
public static class AdaptiveEvaluator
{
    /// <summary>Replays an ordered feedback stream.</summary>
    /// <param name="events">The events, in the order they happened.</param>
    /// <param name="policy">The policy to evaluate; it is observed after each prediction.</param>
    /// <param name="options">Top-k, horizons, bootstrap and clipping; null uses the defaults.</param>
    /// <returns>The counts, metrics, episodes and importance estimate.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its contract.</exception>
    public static AdaptiveReport Replay<TContext>(
        IEnumerable<FeedbackEvent<TContext>> events,
        IAdaptiveFeedbackPolicy<TContext> policy,
        AdaptiveEvaluationOptions? options = null)
        where TContext : notnull
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(policy);

        options ??= new AdaptiveEvaluationOptions();
        ValidateOptions(options);

        var list = events.ToList();

        if (list.Count == 0)
        {
            return EmptyReport();
        }

        int count = list.Count;
        var top1 = new string?[count];
        var labels = new string?[count];
        var contexts = new string[count];
        var correct = new bool[count];
        var dates = new DateTime[count];

        int accepted = 0;
        int corrected = 0;
        int rejected = 0;
        int abstained = 0;
        int ignored = 0;
        int labeled = 0;
        int correctCount = 0;

        var binCounts = new int[10];
        var binCorrect = new int[10];
        var binConfidence = new double[10];

        double brierSum = 0;
        double logLossSum = 0;
        int setCount = 0;
        int setCovered = 0;
        int setSizeSum = 0;

        for (int i = 0; i < count; i++)
        {
            FeedbackEvent<TContext> feedback = list[i];
            ArgumentNullException.ThrowIfNull(feedback);

            contexts[i] = HashContext(feedback.Context);
            dates[i] = feedback.At.Date;

            AdaptiveRanking ranking = policy.Predict(
                feedback.Text,
                feedback.Context,
                feedback.At,
                limit: Math.Max(1, options.TopK));

            top1[i] = ranking.Results.Count > 0 ? ranking.Results[0].Category : null;

            switch (feedback.Outcome)
            {
                case FeedbackOutcome.Accepted:
                    accepted++;
                    break;

                case FeedbackOutcome.Corrected:
                    corrected++;
                    break;

                case FeedbackOutcome.Rejected:
                    rejected++;
                    break;

                case FeedbackOutcome.Abstained:
                    abstained++;
                    break;

                case FeedbackOutcome.Ignored:
                    ignored++;
                    break;
            }

            string? label = LabelOf(feedback);
            labels[i] = label;

            if (label is not null)
            {
                labeled++;

                bool isCorrect = ranking.Results
                    .Take(options.TopK)
                    .Any(score => string.Equals(score.Category, label, StringComparison.Ordinal));

                correct[i] = isCorrect;

                if (isCorrect)
                {
                    correctCount++;
                }

                double confidence = ranking.Results.Count > 0
                    ? Math.Clamp(ranking.Results[0].FinalProbability, 0, 1)
                    : 0;

                int bin = Math.Min(9, (int)(confidence * 10));
                binCounts[bin]++;
                binConfidence[bin] += confidence;

                if (isCorrect)
                {
                    binCorrect[bin]++;
                }

                double brier = 0;
                bool found = false;

                foreach (AdaptiveScore score in ranking.AllScores)
                {
                    double expected = string.Equals(score.Category, label, StringComparison.Ordinal)
                        ? 1
                        : 0;

                    if (expected > 0)
                    {
                        found = true;
                    }

                    double difference = score.FinalProbability - expected;
                    brier += difference * difference;
                }

                if (!found)
                {
                    brier += 1;
                }

                brierSum += brier;

                double labelProbability = 0;

                foreach (AdaptiveScore score in ranking.AllScores)
                {
                    if (string.Equals(score.Category, label, StringComparison.Ordinal))
                    {
                        labelProbability = score.FinalProbability;
                        break;
                    }
                }

                logLossSum -= Math.Log(Math.Max(labelProbability, 1e-12));

                if (ranking.PredictionSet is { } set)
                {
                    setCount++;
                    setSizeSum += set.Count;

                    if (set.Categories.Contains(label, StringComparer.Ordinal))
                    {
                        setCovered++;
                    }
                }
            }

            policy.Observe(feedback);
        }

        double ece = 0;

        if (labeled > 0)
        {
            for (int bin = 0; bin < binCounts.Length; bin++)
            {
                if (binCounts[bin] == 0)
                {
                    continue;
                }

                double accuracy = binCorrect[bin] / (double)binCounts[bin];
                double confidence = binConfidence[bin] / binCounts[bin];
                ece += (binCounts[bin] / (double)labeled) * Math.Abs(accuracy - confidence);
            }
        }

        TimeToRecoveryReport recovery = MeasureRecovery(
            list,
            top1,
            contexts,
            options.RecoveryHorizon);

        RepetitionRegretReport repetition = MeasureRepetition(
            list,
            top1,
            contexts,
            options.RepetitionHorizon);

        ImportanceReport? importance = MeasureImportance(
            list,
            top1,
            options.MaxImportanceWeight);

        double accuracyAtK = labeled > 0 ? correctCount / (double)labeled : 0;

        return new AdaptiveReport(
            count,
            labeled,
            accepted,
            corrected,
            rejected,
            abstained,
            ignored,
            accuracyAtK,
            BootstrapAccuracy(correct, labels, dates, options, accuracyAtK),
            accepted / (double)count,
            corrected / (double)count,
            abstained / (double)count,
            ignored / (double)count,
            labeled / (double)count,
            ece,
            labeled > 0 ? brierSum / labeled : 0,
            labeled > 0 ? logLossSum / labeled : 0,
            setCount > 0 ? 1 - options.Alpha : null,
            setCount > 0 ? setCovered / (double)setCount : null,
            setCount > 0 ? setSizeSum / (double)setCount : null,
            recovery,
            repetition,
            importance,
            dates.Distinct().Count(),
            OrderHash(list, contexts));
    }

    private static TimeToRecoveryReport MeasureRecovery<TContext>(
        IReadOnlyList<FeedbackEvent<TContext>> events,
        IReadOnlyList<string?> top1,
        IReadOnlyList<string> contexts,
        int horizon)
        where TContext : notnull
    {
        var recoveries = new List<double>();
        int episodes = 0;
        int censored = 0;

        for (int i = 0; i < events.Count; i++)
        {
            FeedbackEvent<TContext> feedback = events[i];

            if (feedback.Outcome != FeedbackOutcome.Corrected
                || feedback.AcceptedCategory is null)
            {
                continue;
            }

            episodes++;
            bool found = false;
            int seen = 0;

            for (int j = i + 1; j < events.Count && seen < horizon; j++)
            {
                if (!string.Equals(contexts[j], contexts[i], StringComparison.Ordinal))
                {
                    continue;
                }

                seen++;

                if (string.Equals(top1[j], feedback.AcceptedCategory, StringComparison.Ordinal))
                {
                    recoveries.Add(seen);
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                censored++;
            }
        }

        return new TimeToRecoveryReport(
            episodes,
            recoveries.Count,
            censored,
            recoveries.Count > 0 ? recoveries.Average() : 0,
            Percentile(recoveries, 0.5),
            Percentile(recoveries, 0.9));
    }

    private static RepetitionRegretReport MeasureRepetition<TContext>(
        IReadOnlyList<FeedbackEvent<TContext>> events,
        IReadOnlyList<string?> top1,
        IReadOnlyList<string> contexts,
        int horizon)
        where TContext : notnull
    {
        var active = new Dictionary<(string Context, string Offered), Episode>();
        var repeats = new List<double>();
        int episodes = 0;
        int repeated = 0;
        int censored = 0;
        int max = 0;

        for (int i = 0; i < events.Count; i++)
        {
            FeedbackEvent<TContext> feedback = events[i];
            string context = contexts[i];

            foreach ((string Context, string Offered) key in active.Keys
                         .Where(key => string.Equals(key.Context, context, StringComparison.Ordinal))
                         .ToList())
            {
                Episode episode = active[key];
                episode.Seen++;

                if (string.Equals(top1[i], key.Offered, StringComparison.Ordinal))
                {
                    episode.Repeats++;

                    if (episode.Seen >= horizon)
                    {
                        Close(episode, isCensored: true);
                        active.Remove(key);
                    }
                }
                else
                {
                    Close(episode, isCensored: false);
                    active.Remove(key);
                }
            }

            if (feedback.Outcome != FeedbackOutcome.Corrected
                || feedback.OfferedCategory is null
                || string.Equals(feedback.OfferedCategory, feedback.AcceptedCategory, StringComparison.Ordinal))
            {
                continue;
            }

            var newKey = (context, feedback.OfferedCategory);

            if (!active.ContainsKey(newKey))
            {
                active[newKey] = new Episode();
                episodes++;
            }
        }

        foreach (Episode episode in active.Values)
        {
            Close(episode, isCensored: true);
        }

        return new RepetitionRegretReport(
            episodes,
            repeated,
            censored,
            episodes > 0 ? repeated / (double)episodes : 0,
            Percentile(repeats, 0.9),
            max);

        void Close(Episode episode, bool isCensored)
        {
            repeats.Add(episode.Repeats);
            repeated += episode.Repeats;
            max = Math.Max(max, episode.Repeats);

            if (isCensored)
            {
                censored++;
            }
        }
    }

    private static ImportanceReport? MeasureImportance<TContext>(
        IReadOnlyList<FeedbackEvent<TContext>> events,
        IReadOnlyList<string?> top1,
        double maxWeight)
        where TContext : notnull
    {
        int count = 0;
        int clipped = 0;
        double numerator = 0;
        double denominator = 0;
        double sum = 0;
        double sumSquares = 0;

        for (int i = 0; i < events.Count; i++)
        {
            FeedbackEvent<TContext> feedback = events[i];

            if (feedback.Propensity is not { } propensity
                || feedback.OfferedCategory is null
                || feedback.Outcome is not (FeedbackOutcome.Accepted
                    or FeedbackOutcome.Corrected
                    or FeedbackOutcome.Rejected))
            {
                continue;
            }

            double weight = 1 / propensity;

            if (weight > maxWeight)
            {
                weight = maxWeight;
                clipped++;
            }

            double reward = feedback.Outcome == FeedbackOutcome.Accepted ? 1 : 0;
            double match = string.Equals(top1[i], feedback.OfferedCategory, StringComparison.Ordinal)
                ? 1
                : 0;

            numerator += match * reward * weight;
            denominator += match * weight;
            sum += weight;
            sumSquares += weight * weight;
            count++;
        }

        if (count == 0)
        {
            return null;
        }

        return new ImportanceReport(
            count,
            denominator > 0 ? numerator / denominator : 0,
            sumSquares > 0 ? (sum * sum) / sumSquares : 0,
            clipped);
    }

    private static ClassificationInterval BootstrapAccuracy(
        IReadOnlyList<bool> correct,
        IReadOnlyList<string?> labels,
        IReadOnlyList<DateTime> dates,
        AdaptiveEvaluationOptions options,
        double point)
    {
        if (options.BootstrapResamples <= 0)
        {
            return new ClassificationInterval(point, point);
        }

        var blocks = new Dictionary<DateTime, List<int>>();

        for (int i = 0; i < correct.Count; i++)
        {
            if (labels[i] is null)
            {
                continue;
            }

            if (!blocks.TryGetValue(dates[i], out List<int>? block))
            {
                blocks[dates[i]] = block = [];
            }

            block.Add(i);
        }

        if (blocks.Count < 2)
        {
            return new ClassificationInterval(point, point);
        }

        var keys = blocks.Keys.Order().ToList();
        var random = new Random(options.Seed);
        var values = new double[options.BootstrapResamples];

        for (int resample = 0; resample < values.Length; resample++)
        {
            int hits = 0;
            int total = 0;

            for (int block = 0; block < keys.Count; block++)
            {
                List<int> indices = blocks[keys[random.Next(keys.Count)]];

                foreach (int index in indices)
                {
                    total++;

                    if (correct[index])
                    {
                        hits++;
                    }
                }
            }

            values[resample] = total > 0 ? hits / (double)total : 0;
        }

        Array.Sort(values);

        return new ClassificationInterval(
            values[(int)Math.Floor(0.025 * values.Length)],
            values[Math.Min(values.Length - 1, (int)Math.Ceiling(0.975 * values.Length) - 1)]);
    }

    private static string? LabelOf<TContext>(FeedbackEvent<TContext> feedback)
        where TContext : notnull =>
        feedback.Outcome switch
        {
            FeedbackOutcome.Accepted => feedback.AcceptedCategory,
            FeedbackOutcome.Corrected => feedback.AcceptedCategory,
            FeedbackOutcome.Abstained => feedback.AcceptedCategory,
            _ => null,
        };

    private static string HashContext<TContext>(TContext context)
        where TContext : notnull
    {
        string text = Convert.ToString(context, CultureInfo.InvariantCulture) ?? string.Empty;
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash.AsSpan(0, 16));
    }

    private static string OrderHash<TContext>(
        IReadOnlyList<FeedbackEvent<TContext>> events,
        IReadOnlyList<string> contexts)
        where TContext : notnull
    {
        var builder = new StringBuilder();

        for (int i = 0; i < events.Count; i++)
        {
            FeedbackEvent<TContext> feedback = events[i];

            builder.Append(feedback.At.UtcTicks)
                .Append('|')
                .Append((int)feedback.Outcome)
                .Append('|')
                .Append(feedback.OfferedCategory)
                .Append('|')
                .Append(feedback.AcceptedCategory)
                .Append('|')
                .Append(feedback.Propensity is { } propensity
                    ? BitConverter.DoubleToInt64Bits(propensity).ToString(CultureInfo.InvariantCulture)
                    : string.Empty)
                .Append('|')
                .Append(contexts[i])
                .Append('\n');
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        int index = (int)Math.Ceiling(percentile * sorted.Count) - 1;

        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }

    private sealed class Episode
    {
        public int Seen { get; set; }

        public int Repeats { get; set; }
    }

    private static AdaptiveReport EmptyReport() =>
        new(
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new ClassificationInterval(0, 0),
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            null,
            null,
            null,
            new TimeToRecoveryReport(0, 0, 0, 0, 0, 0),
            new RepetitionRegretReport(0, 0, 0, 0, 0, 0),
            null,
            0,
            string.Empty);

    private static void ValidateOptions(AdaptiveEvaluationOptions options)
    {
        if (options.TopK < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.TopK, "TopK must be at least 1.");
        }

        if (options.RecoveryHorizon < 0 || options.RepetitionHorizon < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Horizons must be non-negative.");
        }

        if (options.BootstrapResamples < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.BootstrapResamples,
                "BootstrapResamples must be non-negative.");
        }

        if (!double.IsFinite(options.Alpha) || options.Alpha is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Alpha, "Alpha must be in (0, 1).");
        }

        if (!double.IsFinite(options.MaxImportanceWeight) || options.MaxImportanceWeight <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxImportanceWeight,
                "MaxImportanceWeight must be finite and positive.");
        }
    }
}
