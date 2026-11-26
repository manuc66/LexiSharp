using LexiSharp.Core;

namespace LexiSharp.Adaptive;

/// <summary>One non-conformity score and the instant it was observed at.</summary>
/// <param name="Score">The non-conformity score, <c>1 - p(actual)</c>.</param>
/// <param name="At">The instant the observation was made.</param>
public readonly record struct CalibrationEntry(double Score, DateTimeOffset At);

/// <summary>
/// Split-conformal calibration whose quantile is weighted by a <see cref="RecencyPolicy"/>: an old
/// score counts less, so the coverage follows a change in habits instead of averaging the window.
/// </summary>
/// <remarks>
/// <para>
/// With every weight equal to <c>1</c>, the weighted quantile reduces exactly to the split-conformal
/// index <c>ceil((n + 1)(1 - alpha)) - 1</c> used by
/// <see cref="Classification.ConformalClassifier{TInner}"/>: the target is
/// <c>(1 - alpha)(W + 1)</c>, where the <c>+ 1</c> is the test point's weight.
/// </para>
/// <para>
/// Entries are stored with their instant, not with a fixed weight; <see cref="PredictSet"/> recomputes
/// the weights at the prediction instant. The threshold is cached and refreshed after an observation,
/// every <c>refreshEvery</c> calls, or after <c>maxThresholdAge</c>: between refreshes the temporal
/// decay is ignored, which bounds the approximation.
/// </para>
/// <para>
/// This type is not thread-safe by itself; an adaptive ranker serializes its writers and readers
/// with its own lock.
/// </para>
/// </remarks>
public sealed class WeightedConformalCalibrator
{
    private readonly double _alpha;
    private RecencyPolicy _policy;
    private readonly int _minSamples;
    private readonly int _maxSamples;
    private readonly int _refreshEvery;
    private readonly TimeSpan _maxThresholdAge;
    private readonly List<CalibrationEntry> _entries = [];

    private bool _dirty = true;
    private double _threshold = double.NaN;
    private DateTimeOffset _thresholdAt = DateTimeOffset.MinValue;
    private int _predictionsSinceRefresh;

    /// <summary>Creates an empty calibrator.</summary>
    /// <param name="alpha">Miscoverage level in <c>(0, 1)</c>; coverage is <c>1 - alpha</c>.</param>
    /// <param name="policy">The recency policy used to weight the calibration scores.</param>
    /// <param name="minSamples">Minimum number of entries before conformal mode; below it the probability threshold is used. Default: <c>20</c>.</param>
    /// <param name="maxSamples">Maximum number of entries kept; the oldest is dropped. Default: <c>10_000</c>.</param>
    /// <param name="refreshEvery">Maximum number of predictions between threshold recomputations. Default: <c>64</c>.</param>
    /// <param name="maxThresholdAge">Maximum age of the cached threshold; null means five minutes.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its contract.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public WeightedConformalCalibrator(
        double alpha,
        RecencyPolicy policy,
        int minSamples = 20,
        int maxSamples = 10_000,
        int refreshEvery = 64,
        TimeSpan? maxThresholdAge = null)
    {
        if (!double.IsFinite(alpha) || alpha is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "Must be finite and in (0, 1).");
        }

        ArgumentNullException.ThrowIfNull(policy);

        if (minSamples < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minSamples), minSamples, "Must be non-negative.");
        }

        if (maxSamples < 1 || minSamples > maxSamples)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxSamples),
                maxSamples,
                "Must be at least 1 and at least minSamples.");
        }

        if (refreshEvery < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshEvery), refreshEvery, "Must be at least 1.");
        }

        TimeSpan age = maxThresholdAge ?? TimeSpan.FromMinutes(5);

        if (age < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maxThresholdAge), age, "Must be non-negative.");
        }

        _alpha = alpha;
        _policy = policy;
        _minSamples = minSamples;
        _maxSamples = maxSamples;
        _refreshEvery = refreshEvery;
        _maxThresholdAge = age;
    }

    /// <summary>The miscoverage level.</summary>
    public double Alpha => _alpha;

    /// <summary>The recency policy applied to the calibration scores.</summary>
    public RecencyPolicy Policy => _policy;

    /// <summary>Whether at least <c>minSamples</c> entries have been observed.</summary>
    /// <remarks>
    /// This is a count, not a guarantee that the entries still carry weight at a given instant; use
    /// <see cref="PredictSet"/> for the instantaneous decision.
    /// </remarks>
    public bool IsCalibrated => _entries.Count >= _minSamples;

    /// <summary>The last computed probability threshold, <c>1 - q</c>; NaN before the first prediction.</summary>
    public double Threshold => _threshold;

    /// <summary>How many calibration entries are stored.</summary>
    public int Count => _entries.Count;

    /// <summary>Replaces the recency policy; the next threshold is recomputed with it.</summary>
    /// <param name="policy">The new policy.</param>
    /// <exception cref="ArgumentNullException"><paramref name="policy"/> is null.</exception>
    public void Rebase(RecencyPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
        _dirty = true;
    }

    /// <summary>Records the distribution shown and the category that was actually right.</summary>
    /// <param name="at">The instant the interaction happened.</param>
    /// <param name="offered">The distribution that was shown; an empty list records nothing.</param>
    /// <param name="actual">The category that was actually right; it may be absent from the distribution.</param>
    /// <exception cref="ArgumentNullException"><paramref name="offered"/> or <paramref name="actual"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A probability is outside <c>[0, 1]</c>.</exception>
    public void Observe(
        DateTimeOffset at,
        IReadOnlyList<ClassificationResult> offered,
        string actual)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(actual);

        if (offered.Count == 0)
        {
            return;
        }

        double probability = 0;

        foreach (ClassificationResult result in offered)
        {
            ArgumentNullException.ThrowIfNull(result);

            if (!double.IsFinite(result.Probability) || result.Probability is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(offered),
                    result.Probability,
                    "Probabilities must be in [0, 1].");
            }

            if (string.Equals(result.Category, actual, StringComparison.Ordinal))
            {
                probability = result.Probability;
            }
        }

        _entries.Add(new CalibrationEntry(1 - probability, at));

        if (_entries.Count > _maxSamples)
        {
            int oldest = 0;

            for (int i = 1; i < _entries.Count; i++)
            {
                if (_entries[i].At < _entries[oldest].At)
                {
                    oldest = i;
                }
            }

            _entries.RemoveAt(oldest);
        }

        _dirty = true;
    }

    /// <summary>Returns the categories whose score reaches the calibrated threshold.</summary>
    /// <param name="offered">The distribution to filter.</param>
    /// <param name="now">The instant the threshold is evaluated at.</param>
    /// <param name="topK">Optional cap on the returned categories; null or zero means no cap.</param>
    /// <returns>The prediction set; empty means abstention.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="offered"/> is null.</exception>
    public PredictionSet PredictSet(
        IReadOnlyList<ClassificationResult> offered,
        DateTimeOffset now,
        int? topK = null)
    {
        ArgumentNullException.ThrowIfNull(offered);

        if (offered.Count == 0)
        {
            return new PredictionSet(Array.Empty<string>(), Array.Empty<ClassificationResult>());
        }

        EnsureThreshold(now);

        int cap = topK is > 0 ? Math.Min(topK.Value, offered.Count) : offered.Count;
        var included = new List<ClassificationResult>(cap);

        foreach (ClassificationResult result in offered)
        {
            if (result.Probability >= _threshold - 1e-12)
            {
                included.Add(result);

                if (included.Count == cap)
                {
                    break;
                }
            }
        }

        var categories = new List<string>(included.Count);

        foreach (ClassificationResult result in included)
        {
            categories.Add(result.Category);
        }

        return new PredictionSet(categories, included);
    }

    /// <summary>Drops entries older than the policy's maximum age.</summary>
    /// <param name="now">The instant the purge is evaluated at.</param>
    public void Purge(DateTimeOffset now)
    {
        int removed = _entries.RemoveAll(entry => entry.At <= now && _policy.IsExpired(now, entry.At));

        if (removed > 0)
        {
            _dirty = true;
        }
    }

    /// <summary>Drops every entry and the cached threshold.</summary>
    public void Reset()
    {
        _entries.Clear();
        _dirty = true;
        _threshold = double.NaN;
        _thresholdAt = DateTimeOffset.MinValue;
        _predictionsSinceRefresh = 0;
    }

    /// <summary>Projects the calibration entries into a versioned snapshot.</summary>
    /// <param name="now">The instant the snapshot is taken at; it is recorded for the caller.</param>
    /// <returns>The snapshot; entries are kept with their instants, not with fixed weights.</returns>
    public WeightedConformalSnapshot Save(DateTimeOffset now)
    {
        var entries = _entries
            .OrderBy(entry => entry.At)
            .ThenBy(entry => entry.Score)
            .ToList();

        return new WeightedConformalSnapshot(
            WeightedConformalSnapshot.CurrentVersion,
            now,
            _alpha,
            _policy,
            _minSamples,
            _maxSamples,
            _refreshEvery,
            _maxThresholdAge,
            entries);
    }

    /// <summary>Restores a calibrator from a snapshot.</summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <returns>A calibrator with the snapshot's entries; its threshold is recomputed on the next prediction.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidDataException">The version or an entry is invalid.</exception>
    public static WeightedConformalCalibrator Load(WeightedConformalSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion != WeightedConformalSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported conformal snapshot version {snapshot.SchemaVersion}; " +
                $"this build reads {WeightedConformalSnapshot.CurrentVersion}.");
        }

        if (snapshot.Policy is null || snapshot.Entries is null)
        {
            throw new InvalidDataException("The conformal snapshot is incomplete.");
        }

        if (snapshot.Entries.Count > snapshot.MaxSamples)
        {
            throw new InvalidDataException("The conformal snapshot carries more entries than its cap.");
        }

        WeightedConformalCalibrator calibrator;

        try
        {
            calibrator = new WeightedConformalCalibrator(
                snapshot.Alpha,
                snapshot.Policy,
                snapshot.MinSamples,
                snapshot.MaxSamples,
                snapshot.RefreshEvery,
                snapshot.MaxThresholdAge);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new InvalidDataException("The conformal snapshot carries invalid parameters.", exception);
        }

        foreach (CalibrationEntry entry in snapshot.Entries)
        {
            if (!double.IsFinite(entry.Score) || entry.Score is < 0 or > 1)
            {
                throw new InvalidDataException("The conformal snapshot carries an invalid score.");
            }

            calibrator._entries.Add(entry);
        }

        calibrator._dirty = true;

        return calibrator;
    }

    private void EnsureThreshold(DateTimeOffset now)
    {
        if (!_dirty
            && _predictionsSinceRefresh < _refreshEvery
            && now - _thresholdAt <= _maxThresholdAge)
        {
            _predictionsSinceRefresh++;
            return;
        }

        var weighted = new List<(double Score, double Weight)>(_entries.Count);
        double total = 0;

        foreach (CalibrationEntry entry in _entries)
        {
            double weight = _policy.Weight(now, entry.At);

            if (weight <= 0)
            {
                continue;
            }

            weighted.Add((entry.Score, weight));
            total += weight;
        }

        if (weighted.Count < _minSamples || total <= 0)
        {
            _threshold = 1 - _alpha;
            _thresholdAt = now;
            _predictionsSinceRefresh = 0;
            _dirty = false;
            return;
        }

        weighted.Sort(static (left, right) => left.Score.CompareTo(right.Score));

        double target = (1 - _alpha) * (total + 1);
        double q = weighted[^1].Score;
        double cumulative = 0;

        foreach ((double score, double weight) in weighted)
        {
            cumulative += weight;

            if (cumulative >= target)
            {
                q = score;
                break;
            }
        }

        _threshold = 1 - q;
        _thresholdAt = now;
        _predictionsSinceRefresh = 0;
        _dirty = false;
    }
}

/// <summary>A versioned view of a <see cref="WeightedConformalCalibrator"/>.</summary>
/// <param name="SchemaVersion">The format version; only <see cref="CurrentVersion"/> is read.</param>
/// <param name="SavedAt">The instant the snapshot was taken at.</param>
/// <param name="Alpha">The miscoverage level.</param>
/// <param name="Policy">The recency policy.</param>
/// <param name="MinSamples">The conformal-mode floor.</param>
/// <param name="MaxSamples">The entry cap.</param>
/// <param name="RefreshEvery">The prediction-count refresh interval.</param>
/// <param name="MaxThresholdAge">The time-based refresh interval.</param>
/// <param name="Entries">The non-conformity scores and their instants, sorted by instant.</param>
public sealed record WeightedConformalSnapshot(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    double Alpha,
    RecencyPolicy Policy,
    int MinSamples,
    int MaxSamples,
    int RefreshEvery,
    TimeSpan MaxThresholdAge,
    IReadOnlyList<CalibrationEntry> Entries)
{
    /// <summary>The only conformal-snapshot version this build reads.</summary>
    public const int CurrentVersion = 1;
}
