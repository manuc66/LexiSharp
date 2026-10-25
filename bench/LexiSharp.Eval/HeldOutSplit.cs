namespace LexiSharp.Eval;

/// <summary>
/// The partition of a run's validation queries into a set the tuners fit on and a set they are
/// scored on, decided by a stable hash of the query id alone.
/// </summary>
/// <remarks>
/// The hash is FNV-1a (64-bit) over the id's UTF-16 code units. It is written out here rather than
/// taken from <see cref="string.GetHashCode()"/>, which is randomized per process and would make the
/// split depend on the run rather than on the command. The id is the only input: the partition does
/// not depend on the order the corpus arrives in, on the file system, or on the query text, so two
/// runs of the same corpus against the same fraction see the same two sets.
/// </remarks>
internal static class HeldOutSplit
{
    /// <summary>
    /// Resolution of the held-out share. One bucket is one millionth of the validation set; the
    /// comparison is against this integer resolution rather than against a floating-point threshold,
    /// so the same fraction and the same ids cannot round onto different sides between runs.
    /// </summary>
    public const int Buckets = 1_000_000;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    /// <summary>FNV-1a 64 over the id's UTF-16 code units.</summary>
    public static ulong Hash(string queryId)
    {
        ArgumentNullException.ThrowIfNull(queryId);

        ulong hash = FnvOffsetBasis;

        foreach (char character in queryId)
        {
            hash ^= character;
            hash *= FnvPrime;
        }

        return hash;
    }

    /// <summary>
    /// The bucket an id falls in, in <c>[0, Buckets)</c>. A bucket below the held-out threshold
    /// belongs to the score set.
    /// </summary>
    public static int Bucket(string queryId) => (int)(Hash(queryId) % Buckets);

    /// <summary>
    /// Whether this id is in the score set — the part withheld from fitting — for a held-out share
    /// of <paramref name="fraction"/>.
    /// </summary>
    public static bool IsHeldOut(string queryId, double fraction) =>
        Bucket(queryId) < Threshold(fraction);

    /// <summary>
    /// Splits the ids into a fit set and a score set. Both may be empty when the set is small
    /// relative to the fraction; the caller reports that rather than moving a query to the other
    /// side, because a moved query would make the partition depend on the rule's edge case rather
    /// than on its hash.
    /// </summary>
    public static (List<T> Fit, List<T> Score) Partition<T>(
        IEnumerable<T> queries,
        Func<T, string> queryId,
        double fraction)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(queryId);

        int threshold = Threshold(fraction);

        var fit = new List<T>();
        var score = new List<T>();

        foreach (T query in queries)
        {
            if (Bucket(queryId(query)) < threshold)
                score.Add(query);
            else
                fit.Add(query);
        }

        return (fit, score);
    }

    /// <summary>
    /// The bucket cut for a held-out share. The share is exclusive of both endpoints: 0 would leave
    /// no score set and 1 no fit set, so neither is a split at all.
    /// </summary>
    public static int Threshold(double fraction)
    {
        if (!double.IsFinite(fraction) || fraction <= 0 || fraction >= 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fraction), fraction,
                "The held-out share must be greater than 0 and less than 1.");
        }

        return (int)Math.Round(fraction * Buckets, MidpointRounding.AwayFromZero);
    }
}
