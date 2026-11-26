using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>
/// Wraps a text classifier with a <see cref="FeedbackMemory{TContext}"/> and an optional
/// <see cref="WeightedConformalCalibrator"/>: the model ranks, the memory reweights by context, the
/// calibrator decides when to abstain.
/// </summary>
/// <remarks>
/// <para>
/// The memory reweights the categories the model already proposes; it never introduces one. A
/// category the model does not return is absent from the ranking even when the memory holds
/// evidence for it — use a candidate source or retrain to add a label.
/// </para>
/// <para>
/// <b>Identity.</b> With an empty memory and no calibrator, the prediction is the model's own
/// distribution, in the model's order, with the same probabilities. The ranker has a fast path for
/// that case so the equality is exact, not merely close.
/// </para>
/// <para>
/// <b>Threading.</b> <see cref="Predict"/> and <see cref="Explain"/> take a reader lock;
/// <see cref="Observe"/> and <see cref="Rebase"/> take the writer lock, which covers the memory and
/// the calibrator together. One writer, concurrent readers: the same contract as
/// <see cref="Classification.SynchronizedTextClassifier"/>.
/// </para>
/// <para>
/// The ranker does not update the text model. Learning a correction into the text and into the
/// memory on the same event would double the signal and make the evaluation impossible to attribute.
/// </para>
/// </remarks>
/// <typeparam name="TContext">The caller's context key.</typeparam>
public sealed class AdaptiveFeedbackRanker<TContext> : IAdaptiveFeedbackPolicy<TContext>, IDisposable
    where TContext : notnull
{
    private readonly ITextClassifier _model;
    private readonly FeedbackMemory<TContext> _memory;
    private readonly AdaptiveOptions _options;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private readonly DriftMonitor? _drift;
    private WeightedConformalCalibrator? _calibrator;
    private long _missingOfferedScores;
    private bool _disposed;

    /// <summary>Creates a ranker over a trained model and a feedback memory.</summary>
    /// <param name="model">The text model; not owned by the ranker.</param>
    /// <param name="memory">The feedback memory; not owned by the ranker.</param>
    /// <param name="options">The multiplier weights; null uses <see cref="AdaptiveOptions.Default"/>.</param>
    /// <param name="calibrator">The optional conformal calibrator; null disables abstention sets.</param>
    /// <exception cref="ArgumentNullException"><paramref name="model"/> or <paramref name="memory"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its contract.</exception>
    public AdaptiveFeedbackRanker(
        ITextClassifier model,
        FeedbackMemory<TContext> memory,
        AdaptiveOptions? options = null,
        WeightedConformalCalibrator? calibrator = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(memory);

        options ??= AdaptiveOptions.Default;
        ValidateOptions(options);

        _model = model;
        _memory = memory;
        _options = options;
        _calibrator = calibrator;
        _drift = options.AutoTuneHalfLife ? new DriftMonitor() : null;
    }

    /// <summary>The wrapped text model.</summary>
    public ITextClassifier Model => _model;

    /// <summary>The feedback memory.</summary>
    public FeedbackMemory<TContext> Memory => _memory;

    /// <summary>The multiplier weights.</summary>
    public AdaptiveOptions Options => _options;

    /// <summary>The conformal calibrator, or null.</summary>
    public WeightedConformalCalibrator? Calibrator => Volatile.Read(ref _calibrator);

    /// <summary>How many accepted or corrected events arrived without the shown distribution.</summary>
    public long MissingOfferedScores => Interlocked.Read(ref _missingOfferedScores);

    /// <inheritdoc />
    public AdaptiveRanking Predict(string text, TContext context, DateTimeOffset now, int limit = 6)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0)
        {
            return Empty(now);
        }

        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            return Compute(text, context, now, limit).Ranking;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Ranks the categories and returns the contributions and the context prior.</summary>
    /// <param name="text">The text to classify.</param>
    /// <param name="context">The caller's context key.</param>
    /// <param name="now">The instant the prediction is made at.</param>
    /// <param name="limit">Maximum number of returned categories.</param>
    /// <returns>The ranking, its prior and the model's own explanation when available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The ranker has been disposed.</exception>
    public AdaptiveExplanation Explain(string text, TContext context, DateTimeOffset now, int limit = 6)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(context);

        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            Computation computation = Compute(text, context, now, Math.Max(1, limit));

            ClassificationExplanation? textExplanation = _model is IExplainableTextClassifier explainable
                ? explainable.Explain(text, limit)
                : null;

            return new AdaptiveExplanation(
                text,
                now,
                computation.Prior,
                computation.Ranking.AllScores,
                textExplanation);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="feedback"/> is null.</exception>
    /// <exception cref="ArgumentException">The outcome and categories are inconsistent.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The instant runs backwards, or a propensity is invalid.</exception>
    /// <exception cref="ObjectDisposedException">The ranker has been disposed.</exception>
    public void Observe(FeedbackEvent<TContext> feedback)
    {
        ArgumentNullException.ThrowIfNull(feedback);

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            // The memory validates the event and its offered distribution first, so a rejected
            // event never reaches the calibrator.
            _memory.Observe(feedback);

            if (feedback.Outcome is FeedbackOutcome.Accepted or FeedbackOutcome.Corrected)
            {
                if (feedback.Offered is { Count: > 0 } offered)
                {
                    _calibrator?.Observe(feedback.At, offered, feedback.AcceptedCategory!);
                }
                else
                {
                    Interlocked.Increment(ref _missingOfferedScores);
                }
            }

            UpdateDrift(feedback);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Projects the ranker's memory and calibration into a signed adaptive snapshot.</summary>
    /// <param name="now">The instant to project to.</param>
    /// <param name="modelSignature">The caller's signature of the wrapped model.</param>
    /// <returns>The snapshot, with its signature computed.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="modelSignature"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The ranker has been disposed.</exception>
    public AdaptiveSnapshot Save(DateTimeOffset now, string modelSignature)
    {
        ArgumentNullException.ThrowIfNull(modelSignature);

        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            return AdaptiveSnapshotSerializer.Create(
                modelSignature,
                _options,
                _memory.Save(now),
                _calibrator?.Save(now));
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Restores a ranker from a snapshot and a trained text model.</summary>
    /// <param name="snapshot">The snapshot to restore.</param>
    /// <param name="model">The text model; not owned by the ranker.</param>
    /// <param name="codec">The context codec; null uses the hashed codec.</param>
    /// <returns>A ranker with the snapshot's options, memory and calibration.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidDataException">The snapshot version is unknown.</exception>
    public static AdaptiveFeedbackRanker<TContext> Load(
        AdaptiveSnapshot snapshot,
        ITextClassifier model,
        IContextCodec<TContext>? codec = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(model);

        if (snapshot.SchemaVersion != AdaptiveSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported adaptive snapshot version {snapshot.SchemaVersion}; " +
                $"this build reads {AdaptiveSnapshot.CurrentVersion}.");
        }

        if (snapshot.Memory is null || snapshot.Options is null)
        {
            throw new InvalidDataException("The adaptive snapshot is incomplete.");
        }

        FeedbackMemory<TContext> memory = FeedbackMemory<TContext>.Load(snapshot.Memory, codec);
        WeightedConformalCalibrator? calibrator = snapshot.Calibration is null
            ? null
            : WeightedConformalCalibrator.Load(snapshot.Calibration);

        return new AdaptiveFeedbackRanker<TContext>(model, memory, snapshot.Options, calibrator);
    }

    /// <summary>Materializes the memory at <paramref name="now"/> and replaces its policy.</summary>
    /// <param name="now">The instant to materialize at.</param>
    /// <param name="policy">The new policy; the calibrator is rebased to it too.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The ranker has been disposed.</exception>
    public void Rebase(DateTimeOffset now, RecencyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            _memory.Rebase(now, policy);
            _calibrator?.Rebase(policy);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Disposes the ranker's lock. The model, memory and calibrator are not owned.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
    }

    private Computation Compute(string text, TContext context, DateTimeOffset now, int limit)
    {
        IReadOnlyList<ClassificationResult> textResults = _model.Predict(text, limit: int.MaxValue);
        ContextPrior prior = _memory.Prior(context, now);

        if (textResults.Count == 0)
        {
            return new Computation(Empty(now), prior);
        }

        IReadOnlySet<string> vetoed = _memory.Vetoed(context, now);

        if (prior.Categories.Count == 0
            && vetoed.Count == 0
            && _calibrator is null)
        {
            var identityScores = new List<AdaptiveScore>(textResults.Count);

            foreach (ClassificationResult result in textResults)
            {
                identityScores.Add(new AdaptiveScore(
                    result.Category,
                    result.Probability,
                    0,
                    0,
                    1,
                    result.Probability,
                    false,
                    false));
            }

            var identityResults = identityScores.Take(limit).ToList();
            bool identityAbstained = identityScores[0].FinalProbability < _options.MinScore;

            return new Computation(
                new AdaptiveRanking(
                    identityResults,
                    identityScores,
                    null,
                    identityAbstained,
                    false,
                    prior.Evidence,
                    now),
                prior);
        }

        var byCategory = new Dictionary<string, CategoryPrior>(
            prior.Categories.Count,
            StringComparer.Ordinal);

        foreach (CategoryPrior category in prior.Categories)
        {
            byCategory[category.Category] = category;
        }

        var raw = new List<RawScore>(textResults.Count);
        bool anyVetoed = false;
        double total = 0;

        foreach (ClassificationResult result in textResults)
        {
            if (!double.IsFinite(result.Probability) || result.Probability < 0)
            {
                throw new InvalidOperationException(
                    $"The model returned an invalid probability for '{result.Category}'.");
            }

            byCategory.TryGetValue(result.Category, out CategoryPrior? categoryPrior);

            double acceptanceShare = categoryPrior?.AcceptanceShare ?? 0;
            double refusalMass = categoryPrior?.RefusalMass ?? 0;
            double multiplier = (1 + (_options.AcceptanceWeight * acceptanceShare))
                * Math.Exp(-_options.RefusalWeight * refusalMass);

            bool isVetoed = vetoed.Contains(result.Category);
            anyVetoed |= isVetoed;

            var score = new RawScore(
                result.Category,
                result.Probability,
                acceptanceShare,
                refusalMass,
                multiplier,
                result.Probability * multiplier,
                isVetoed);

            raw.Add(score);

            if (!isVetoed)
            {
                total += score.Score;
            }
        }

        bool vetoFallback = anyVetoed && total <= 0;

        if (vetoFallback)
        {
            total = 0;

            foreach (RawScore score in raw)
            {
                total += score.Score;
            }
        }

        if (total <= 0)
        {
            total = 0;

            for (int i = 0; i < raw.Count; i++)
            {
                RawScore score = raw[i] with { Score = raw[i].TextProbability };
                raw[i] = score;
                total += score.Score;
            }
        }

        var all = new List<AdaptiveScore>(raw.Count);

        foreach (RawScore score in raw)
        {
            if (!vetoFallback && score.Vetoed)
            {
                continue;
            }

            all.Add(new AdaptiveScore(
                score.Category,
                score.TextProbability,
                score.AcceptanceShare,
                score.RefusalMass,
                score.Multiplier,
                total > 0 ? score.Score / total : 0,
                score.Vetoed,
                false));
        }

        if (all.Count == 0)
        {
            return new Computation(Empty(now), prior);
        }

        all = all
            .OrderByDescending(score => score.FinalProbability)
            .ToList();

        var results = all.Take(limit).ToList();
        PredictionSet? set = null;
        bool abstained;

        if (_calibrator is { } calibrator)
        {
            var distribution = new List<ClassificationResult>(all.Count);

            foreach (AdaptiveScore score in all)
            {
                distribution.Add(new ClassificationResult(score.Category, score.FinalProbability));
            }

            set = calibrator.PredictSet(distribution, now, topK: limit);

            var included = new HashSet<string>(set.Categories, StringComparer.Ordinal);

            for (int i = 0; i < all.Count; i++)
            {
                all[i] = all[i] with { InPredictionSet = included.Contains(all[i].Category) };
            }

            results = all.Take(limit).ToList();
            abstained = set.IsEmpty || all[0].FinalProbability < _options.MinScore;
        }
        else
        {
            abstained = all[0].FinalProbability < _options.MinScore;
        }

        return new Computation(
            new AdaptiveRanking(
                results,
                all,
                set,
                abstained,
                vetoFallback,
                prior.Evidence,
                now),
            prior);
    }

    private void UpdateDrift(FeedbackEvent<TContext> feedback)
    {
        if (_drift is null)
        {
            return;
        }

        bool? correct = feedback.Outcome switch
        {
            FeedbackOutcome.Accepted => true,
            FeedbackOutcome.Corrected or FeedbackOutcome.Rejected => false,
            _ => null,
        };

        if (correct is not { } outcome)
        {
            return;
        }

        _drift.Observe(outcome, feedback.At);

        if (!_drift.Drifted)
        {
            return;
        }

        RecencyPolicy policy = _memory.Policy;

        if (policy.HalfLife != Timeout.InfiniteTimeSpan)
        {
            TimeSpan suggested = _drift.SuggestedHalfLife(policy.HalfLife);

            if (suggested != policy.HalfLife)
            {
                var rebased = new RecencyPolicy(
                    suggested,
                    policy.MaxAge,
                    policy.FrequencySaturation,
                    policy.Mode,
                    policy.MaxCopies);

                _memory.Rebase(feedback.At, rebased);
                _calibrator?.Rebase(rebased);
            }
        }

        _drift.Reset();
    }

    private static AdaptiveRanking Empty(DateTimeOffset now) =>
        new(
            Array.Empty<AdaptiveScore>(),
            Array.Empty<AdaptiveScore>(),
            null,
            true,
            false,
            0,
            now);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }

    private static void ValidateOptions(AdaptiveOptions options)
    {
        if (!double.IsFinite(options.AcceptanceWeight) || options.AcceptanceWeight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.AcceptanceWeight,
                "AcceptanceWeight must be finite and non-negative.");
        }

        if (!double.IsFinite(options.RefusalWeight) || options.RefusalWeight < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RefusalWeight,
                "RefusalWeight must be finite and non-negative.");
        }

        if (!double.IsFinite(options.MinScore) || options.MinScore is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinScore,
                "MinScore must be finite and in [0, 1].");
        }
    }

    private readonly record struct RawScore(
        string Category,
        double TextProbability,
        double AcceptanceShare,
        double RefusalMass,
        double Multiplier,
        double Score,
        bool Vetoed);

    private sealed record Computation(AdaptiveRanking Ranking, ContextPrior Prior);
}
