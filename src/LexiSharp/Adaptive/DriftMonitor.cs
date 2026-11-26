namespace LexiSharp.Adaptive;

/// <summary>Parameters of the Page–Hinkley drift monitor.</summary>
public sealed record DriftOptions
{
    /// <summary>The default options: 0.8 expected accuracy, a 10-unit threshold, 50 observations.</summary>
    public static DriftOptions Default { get; } = new();

    /// <summary>The accuracy the stream is expected to hold. Must be in <c>[0, 1]</c>. Default: <c>0.8</c>.</summary>
    public double ExpectedAccuracy { get; init; } = 0.8;

    /// <summary>The tolerance subtracted from each observation. Must be finite and non-negative. Default: <c>0.05</c>.</summary>
    public double Delta { get; init; } = 0.05;

    /// <summary>Excess-error mass that signals a drift. Must be finite and positive. Default: <c>10</c>.</summary>
    public double Threshold { get; init; } = 10;

    /// <summary>Observations ignored before a signal can fire. Default: <c>50</c>.</summary>
    public int MinObservations { get; init; } = 50;

    /// <summary>Lower bound of the suggested half-life. Default: 3 days.</summary>
    public TimeSpan MinHalfLife { get; init; } = TimeSpan.FromDays(3);

    /// <summary>Upper bound of the suggested half-life. Default: 365 days.</summary>
    public TimeSpan MaxHalfLife { get; init; } = TimeSpan.FromDays(365);

    /// <summary>Observations without a drift before the half-life may grow. Default: <c>200</c>.</summary>
    public int StableObservations { get; init; } = 200;
}

/// <summary>
/// A Page–Hinkley detector over a stream of correctness outcomes. It signals a drop in accuracy and
/// suggests a shorter half-life; it never changes a policy by itself.
/// </summary>
/// <remarks>
/// <para>
/// The statistic is <c>m_t = Σ (ExpectedAccuracy - x_t - Delta)</c> and its running minimum;
/// a drift fires when <c>m_t - min(m) &gt; Threshold</c> after <c>MinObservations</c>. A drop to
/// constant errors accumulates <c>ExpectedAccuracy - Delta</c> per observation, so the threshold is
/// expressed in excess-error units, not seconds.
/// </para>
/// <para>
/// <see cref="SuggestedHalfLife"/> is idempotent: after a drift it returns the halved value until
/// <see cref="Reset"/>, so calling it on every prediction cannot divide the half-life repeatedly.
/// The caller applies the suggestion, then resets the monitor.
/// </para>
/// </remarks>
public sealed class DriftMonitor
{
    private readonly DriftOptions _options;

    private DateTimeOffset? _lastAt;
    private double _statistic;
    private double _minimum;
    private int _observations;
    private bool _drifted;
    private TimeSpan? _suggested;

    /// <summary>Creates a monitor.</summary>
    /// <param name="options">The detector parameters; null uses <see cref="DriftOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its contract.</exception>
    public DriftMonitor(DriftOptions? options = null)
    {
        options ??= DriftOptions.Default;
        ValidateOptions(options);
        _options = options;
    }

    /// <summary>Whether a drift has been detected since the last <see cref="Reset"/>.</summary>
    public bool Drifted => _drifted;

    /// <summary>How many observations have been consumed since the last <see cref="Reset"/>.</summary>
    public int Observations => _observations;

    /// <summary>Records one correctness outcome.</summary>
    /// <param name="correct">Whether the last prediction was right.</param>
    /// <param name="at">The instant of the outcome; it must not run backwards.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="at"/> is before the previous observation.</exception>
    public void Observe(bool correct, DateTimeOffset at)
    {
        if (_lastAt is { } last && at < last)
        {
            throw new ArgumentOutOfRangeException(
                nameof(at),
                at,
                "Drift observations must be non-decreasing.");
        }

        _lastAt = at;

        if (_drifted)
        {
            return;
        }

        _observations++;

        double outcome = correct ? 1 : 0;
        _statistic += _options.ExpectedAccuracy - outcome - _options.Delta;
        _minimum = Math.Min(_minimum, _statistic);

        if (_observations >= _options.MinObservations
            && _statistic - _minimum > _options.Threshold)
        {
            _drifted = true;
            _suggested = null;
        }
    }

    /// <summary>Suggests the next half-life: halved on drift, grown by 5 % after stability, else unchanged.</summary>
    /// <param name="current">The current half-life. Must be positive.</param>
    /// <returns>The suggestion; idempotent until <see cref="Reset"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="current"/> is not positive.</exception>
    public TimeSpan SuggestedHalfLife(TimeSpan current)
    {
        if (current <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(current), current, "Must be positive.");
        }

        if (_suggested is { } suggestion)
        {
            return suggestion;
        }

        if (_drifted)
        {
            long ticks = Math.Max(_options.MinHalfLife.Ticks, current.Ticks / 2);
            var halved = TimeSpan.FromTicks(ticks);
            _suggested = halved;
            return halved;
        }

        if (_observations >= _options.StableObservations)
        {
            long ticks = Math.Min(
                _options.MaxHalfLife.Ticks,
                (long)Math.Round(current.Ticks * 1.05));
            var grown = TimeSpan.FromTicks(ticks);
            _suggested = grown;
            return grown;
        }

        return current;
    }

    /// <summary>Clears the detector and the suggestion; the next stream starts from zero.</summary>
    public void Reset()
    {
        _lastAt = null;
        _statistic = 0;
        _minimum = 0;
        _observations = 0;
        _drifted = false;
        _suggested = null;
    }

    private static void ValidateOptions(DriftOptions options)
    {
        if (!double.IsFinite(options.ExpectedAccuracy) || options.ExpectedAccuracy is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ExpectedAccuracy,
                "ExpectedAccuracy must be finite and in [0, 1].");
        }

        if (!double.IsFinite(options.Delta) || options.Delta < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Delta,
                "Delta must be finite and non-negative.");
        }

        if (!double.IsFinite(options.Threshold) || options.Threshold <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.Threshold,
                "Threshold must be finite and positive.");
        }

        if (options.MinObservations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinObservations,
                "MinObservations must be at least 1.");
        }

        if (options.StableObservations < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.StableObservations,
                "StableObservations must be at least 1.");
        }

        if (options.MinHalfLife <= TimeSpan.Zero
            || options.MaxHalfLife < options.MinHalfLife)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MinHalfLife,
                "The half-life bounds must be positive and ordered.");
        }
    }
}
