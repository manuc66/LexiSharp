using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// A temporal prior built from past assignments: how often each category was accepted at a given
/// weekday and block of the day, with Dirichlet smoothing for rare buckets.
/// </summary>
public class TemporalPrior : ITemporalPriorSource
{
    private readonly Dictionary<(DayOfWeek Weekday, int Block), Dictionary<string, int>> _counts;
    private readonly Dictionary<(DayOfWeek Weekday, int Block), int> _totals;
    private readonly HashSet<string> _categories;
    private readonly int _blockHours;
    private readonly int _minSupport;
    private readonly double _alpha;

    /// <summary>A prior that never proposes anything.</summary>
    public static TemporalPrior Empty { get; } = new(Array.Empty<ObservedAssignment>());

    private TemporalPrior(
        Dictionary<(DayOfWeek, int), Dictionary<string, int>> counts,
        Dictionary<(DayOfWeek, int), int> totals,
        HashSet<string> categories,
        int blockHours,
        int minSupport,
        double alpha)
    {
        if (!double.IsFinite(alpha))
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be finite.");
        }

        _counts = counts;
        _totals = totals;
        _categories = categories;
        _blockHours = blockHours <= 0 ? 3 : blockHours;
        _minSupport = minSupport < 0 ? 0 : minSupport;
        _alpha = alpha <= 0 ? 0.5 : alpha;
    }

    /// <summary>Builds a prior from past assignments.</summary>
    public TemporalPrior(IEnumerable<ObservedAssignment> history)
        : this(
            BuildCounts(history, 3),
            BuildTotals(history, 3),
            BuildCategories(history),
            3,
            3,
            0.5)
    {
    }

    /// <summary>Builds a prior from past assignments.</summary>
    public TemporalPrior(IEnumerable<ObservedAssignment> history, int blockHours, int minSupport = 3, double alpha = 0.5)
        : this(
            BuildCounts(history, blockHours <= 0 ? 3 : blockHours),
            BuildTotals(history, blockHours <= 0 ? 3 : blockHours),
            BuildCategories(history),
            blockHours,
            minSupport,
            alpha)
    {
    }

    /// <summary>Counts the assignments per weekday and block.</summary>
    public static TemporalPrior Build(IEnumerable<ObservedAssignment> history) =>
        new(history);

    /// <summary>Counts the assignments per weekday and block.</summary>
    public static TemporalPrior Build(IEnumerable<ObservedAssignment> history, int blockHours, int minSupport = 3, double alpha = 0.5) =>
        new(history, blockHours, minSupport, alpha);

    /// <summary>Hours per block.</summary>
    public int BlockHours => _blockHours;
    /// <summary>Minimum support.</summary>
    public int MinSupport => _minSupport;
    /// <summary>Dirichlet alpha.</summary>
    public double Alpha => _alpha;

    /// <summary>Share of each category in the block.</summary>
    public IReadOnlyDictionary<string, double> Probabilities(DayOfWeek weekday, int hour)
    {
        if (hour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(hour));
        }

        int block = hour / _blockHours;
        if (!_totals.TryGetValue((weekday, block), out int total) || total < _minSupport)
        {
            return new Dictionary<string, double>(StringComparer.Ordinal);
        }

        int k = _categories.Count == 0 ? 1 : _categories.Count;
        var probabilities = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string category in _categories)
        {
            _counts.TryGetValue((weekday, block), out Dictionary<string, int>? bucket);
            int count = bucket is not null && bucket.TryGetValue(category, out int c) ? c : 0;
            probabilities[category] = (count + _alpha) / (total + k * _alpha);
        }

        return probabilities;
    }

    /// <inheritdoc />
    public IReadOnlyList<TextEvidence> Prior(DateTimeOffset when) =>
        Probabilities(when.DayOfWeek, when.Hour)
            .Select(kv => new TextEvidence(kv.Key, Math.Log(kv.Value <= 0 ? 1e-12 : kv.Value)))
            .ToList();

    private static Dictionary<(DayOfWeek, int), Dictionary<string, int>> BuildCounts(IEnumerable<ObservedAssignment> history, int blockHours)
    {
        var counts = new Dictionary<(DayOfWeek, int), Dictionary<string, int>>();
        foreach (ObservedAssignment assignment in history ?? [])
        {
            if (string.IsNullOrWhiteSpace(assignment.Category))
            {
                continue;
            }

            int block = assignment.Hour / blockHours;
            if (!counts.TryGetValue((assignment.Weekday, block), out Dictionary<string, int>? categories))
            {
                categories = new Dictionary<string, int>(StringComparer.Ordinal);
                counts.Add((assignment.Weekday, block), categories);
            }

            categories[assignment.Category] = categories.TryGetValue(assignment.Category, out int count)
                ? count + 1
                : 1;
        }

        return counts;
    }

    private static Dictionary<(DayOfWeek, int), int> BuildTotals(IEnumerable<ObservedAssignment> history, int blockHours)
    {
        var totals = new Dictionary<(DayOfWeek, int), int>();
        foreach (ObservedAssignment assignment in history ?? [])
        {
            if (string.IsNullOrWhiteSpace(assignment.Category))
            {
                continue;
            }

            int block = assignment.Hour / blockHours;
            totals[(assignment.Weekday, block)] = totals.TryGetValue((assignment.Weekday, block), out int total)
                ? total + 1
                : 1;
        }

        return totals;
    }

    private static HashSet<string> BuildCategories(IEnumerable<ObservedAssignment> history)
    {
        var categories = new HashSet<string>(StringComparer.Ordinal);
        foreach (ObservedAssignment assignment in history ?? [])
        {
            if (!string.IsNullOrWhiteSpace(assignment.Category))
            {
                categories.Add(assignment.Category);
            }
        }

        return categories;
    }
}

/// <summary>Alias for <see cref="TemporalPrior"/>.</summary>
public class TimeOfDayPrior : TemporalPrior
{
    /// <summary>Builds a prior from past assignments.</summary>
    public TimeOfDayPrior(IEnumerable<ObservedAssignment> history)
        : base(history)
    {
    }

    /// <summary>Builds a prior from past assignments.</summary>
    public TimeOfDayPrior(IEnumerable<ObservedAssignment> history, int blockHours, int minSupport = 3, double alpha = 0.5)
        : base(history, blockHours, minSupport, alpha)
    {
    }

    /// <summary>A prior that never proposes anything.</summary>
    public new static TimeOfDayPrior Empty { get; } = new(Array.Empty<ObservedAssignment>());

    /// <summary>Counts the assignments per weekday and block.</summary>
    public new static TimeOfDayPrior Build(IEnumerable<ObservedAssignment> history) =>
        new(history);

    /// <summary>Counts the assignments per weekday and block.</summary>
    public new static TimeOfDayPrior Build(IEnumerable<ObservedAssignment> history, int blockHours, int minSupport = 3, double alpha = 0.5) =>
        new(history, blockHours, minSupport, alpha);

}

/// <summary>
/// One past assignment, with the local time it happened at.
/// </summary>
public readonly record struct ObservedAssignment(DayOfWeek Weekday, int Hour, string Category)
{
    /// <summary>Alias for <see cref="Category"/>, matching WindowTracker's vocabulary.</summary>
    public string SubjectKey => Category;
}
