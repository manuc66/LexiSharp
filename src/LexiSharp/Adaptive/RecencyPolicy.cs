namespace LexiSharp.Adaptive;

/// <summary>How a sample weight is applied when a model consumes timed observations.</summary>
public enum SampleWeightMode
{
    /// <summary>The model consumes the weight directly. This is the default.</summary>
    Exact,

    /// <summary>
    /// The caller expands a sample into whole copies for a model that cannot consume a fractional
    /// weight. An approximation: it inflates the document counts and interacts with priors.
    /// </summary>
    Quantized,
}

/// <summary>
/// Describes how quickly a sample loses weight as it ages, and how repeated observations of the
/// same item are damped.
/// </summary>
/// <remarks>
/// <para>
/// The policy is data: it carries no clock. Every member takes the instant it should be evaluated
/// at, so a replay and a live run produce the same weights. A weight of zero means the sample is
/// outside <see cref="MaxAge"/> and must not be trained on.
/// </para>
/// <para>
/// The weight is <c>0.5 ^ (age / HalfLife)</c> multiplied by a frequency damping
/// <c>1 / (1 + (occurrences - 1) / FrequencySaturation)</c>. The first occurrence has full weight;
/// the eighth, with the default saturation of 7, has half.
/// </para>
/// <para>
/// <see cref="Weight"/> and <see cref="Copies"/> are deterministic and allocation-free. They are
/// safe to call from concurrent readers; the policy is immutable.
/// </para>
/// </remarks>
public sealed record RecencyPolicy
{
    /// <summary>A policy with no decay and no expiry: every weight is <c>1</c>.</summary>
    public static RecencyPolicy None { get; } = new(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

    /// <summary>Creates a policy.</summary>
    /// <param name="halfLife">
    /// Age at which a sample's weight falls to <c>0.5</c>. <see cref="Timeout.InfiniteTimeSpan"/>
    /// disables decay; otherwise it must be positive.
    /// </param>
    /// <param name="maxAge">
    /// Age beyond which a sample is dropped. <see cref="Timeout.InfiniteTimeSpan"/> disables
    /// expiry; otherwise it must be positive.
    /// </param>
    /// <param name="frequencySaturation">
    /// Occurrence count at which a repeatedly seen item is damped to half weight. Must be finite and
    /// positive. Default: <c>7</c>.
    /// </param>
    /// <param name="mode">Whether the weight is consumed exactly or approximated by copies. Default: <see cref="SampleWeightMode.Exact"/>.</param>
    /// <param name="maxCopies">Upper bound on the copies produced in <see cref="SampleWeightMode.Quantized"/> mode. Default: <c>4</c>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A parameter is outside its contract.</exception>
    public RecencyPolicy(
        TimeSpan halfLife,
        TimeSpan maxAge,
        double frequencySaturation = 7,
        SampleWeightMode mode = SampleWeightMode.Exact,
        int maxCopies = 4)
    {
        if (halfLife != Timeout.InfiniteTimeSpan && halfLife <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(halfLife),
                halfLife,
                "Must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (maxAge != Timeout.InfiniteTimeSpan && maxAge <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxAge),
                maxAge,
                "Must be positive or Timeout.InfiniteTimeSpan.");
        }

        if (!double.IsFinite(frequencySaturation) || frequencySaturation <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frequencySaturation),
                frequencySaturation,
                "Must be finite and positive.");
        }

        if (mode is not (SampleWeightMode.Exact or SampleWeightMode.Quantized))
        {
            throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown sample-weight mode.");
        }

        if (maxCopies < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCopies), maxCopies, "Must be at least 1.");
        }

        HalfLife = halfLife;
        MaxAge = maxAge;
        FrequencySaturation = frequencySaturation;
        Mode = mode;
        MaxCopies = maxCopies;
    }

    /// <summary>Age at which a sample's weight falls to <c>0.5</c>; infinite means no decay.</summary>
    public TimeSpan HalfLife { get; }

    /// <summary>Age beyond which a sample is dropped; infinite means no expiry.</summary>
    public TimeSpan MaxAge { get; }

    /// <summary>Occurrence count at which a repeatedly seen item is damped to half weight.</summary>
    public double FrequencySaturation { get; }

    /// <summary>Whether the weight is consumed exactly or approximated by copies.</summary>
    public SampleWeightMode Mode { get; }

    /// <summary>Upper bound on the copies produced in <see cref="SampleWeightMode.Quantized"/> mode.</summary>
    public int MaxCopies { get; }

    /// <summary>Whether <paramref name="at"/> is outside <see cref="MaxAge"/> at <paramref name="now"/>.</summary>
    /// <param name="now">The instant the policy is evaluated at.</param>
    /// <param name="at">The instant the sample was observed at.</param>
    /// <returns><c>true</c> when the sample is older than the maximum age.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="at"/> is after <paramref name="now"/>.</exception>
    public bool IsExpired(DateTimeOffset now, DateTimeOffset at)
    {
        TimeSpan age = Age(now, at);

        return MaxAge != Timeout.InfiniteTimeSpan && age > MaxAge;
    }

    /// <summary>Returns the sample weight in <c>[0, 1]</c>; <c>0</c> means the sample is expired.</summary>
    /// <param name="now">The instant the policy is evaluated at.</param>
    /// <param name="at">The instant the sample was observed at.</param>
    /// <param name="occurrences">
    /// How many times this item has been observed in the window. The first occurrence has full
    /// weight; later ones are damped. Must be at least <c>1</c>. Default: <c>1</c>.
    /// </param>
    /// <returns>The weight, in <c>[0, 1]</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="occurrences"/> is less than <c>1</c>, or <paramref name="at"/> is after <paramref name="now"/>.</exception>
    public double Weight(DateTimeOffset now, DateTimeOffset at, int occurrences = 1)
    {
        if (occurrences < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(occurrences), occurrences, "Must be at least 1.");
        }

        TimeSpan age = Age(now, at);

        if (MaxAge != Timeout.InfiniteTimeSpan && age > MaxAge)
        {
            return 0;
        }

        double decay = HalfLife == Timeout.InfiniteTimeSpan
            ? 1.0
            : Math.Pow(0.5, age.TotalSeconds / HalfLife.TotalSeconds);

        double damping = 1.0 / (1.0 + ((occurrences - 1) / FrequencySaturation));

        return decay * damping;
    }

    /// <summary>Returns how many whole copies <see cref="Weight"/> represents.</summary>
    /// <param name="now">The instant the policy is evaluated at.</param>
    /// <param name="at">The instant the sample was observed at.</param>
    /// <param name="occurrences">How many times this item has been observed; see <see cref="Weight"/>.</param>
    /// <returns>A count in <c>[0, MaxCopies]</c>; <c>0</c> means the sample is expired.</returns>
    /// <exception cref="InvalidOperationException">The policy is not in <see cref="SampleWeightMode.Quantized"/> mode.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="occurrences"/> is less than <c>1</c>, or <paramref name="at"/> is after <paramref name="now"/>.</exception>
    public int Copies(DateTimeOffset now, DateTimeOffset at, int occurrences = 1)
    {
        if (Mode != SampleWeightMode.Quantized)
        {
            throw new InvalidOperationException(
                "Copies is only meaningful when Mode is SampleWeightMode.Quantized.");
        }

        double weight = Weight(now, at, occurrences);

        if (weight <= 0)
        {
            return 0;
        }

        int copies = (int)Math.Round(weight * MaxCopies, MidpointRounding.AwayFromZero);

        return Math.Clamp(copies, 1, MaxCopies);
    }

    private static TimeSpan Age(DateTimeOffset now, DateTimeOffset at)
    {
        if (at > now)
        {
            throw new ArgumentOutOfRangeException(
                nameof(at),
                at,
                "The sample instant cannot be after the evaluation instant.");
        }

        return now - at;
    }
}
