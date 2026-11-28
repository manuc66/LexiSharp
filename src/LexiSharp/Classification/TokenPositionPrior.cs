using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>One past text and the category it was assigned to.</summary>
/// <param name="Text">The text whose leading or trailing tokens should be counted.</param>
/// <param name="Category">The category that text was assigned to.</param>
public readonly record struct ObservedText(string Text, string Category);

/// <summary>Which end of a text a <see cref="TokenPositionPrior"/> reads.</summary>
public enum TokenPosition
{
    /// <summary>The first tokens, before the rest of the text.</summary>
    Leading,

    /// <summary>The last tokens, after the rest of the text.</summary>
    Trailing,
}

/// <summary>
/// A log-prior over categories derived from the tokens at one end of the text: how often a token
/// at that position was seen with each category in the observations, with Dirichlet smoothing.
/// </summary>
/// <remarks>
/// <para>
/// This is a <see cref="ITextPriorSource"/>, not a classifier: it only shifts the probability of
/// categories a trained model already holds. Feed it to <see cref="BlendTextClassifier"/> through a
/// <see cref="PriorLane"/> with a weight tuned on a validation split. Two sources compose by
/// adding their log bonuses, so a leading and a trailing prior can be used together.
/// </para>
/// <para>
/// The bonus is <c>log P(category | tokens at this position)</c>, not a measure of how much the
/// text "looks like" the category. It is deliberately cheap and explainable: the tokens that fired
/// are the evidence, and a model that already uses position well will need a small weight.
/// </para>
/// <para>
/// Instances are immutable after construction and safe for concurrent prediction. Observations are
/// consumed once at construction, so a one-shot sequence is supported.
/// </para>
/// </remarks>
public abstract class TokenPositionPrior : ITextPriorSource
{
    private readonly Dictionary<string, int[]> _counts;
    private readonly string[] _categories;
    private readonly TokenPosition _position;
    private readonly int _tokenCount;
    private readonly double _alpha;

    /// <summary>Builds a prior from observed texts and their categories.</summary>
    /// <param name="observations">Past texts and assignments; consumed once.</param>
    /// <param name="position">Which end of the text to read.</param>
    /// <param name="tokenCount">How many tokens to read at that end. Must be positive.</param>
    /// <param name="alpha">Dirichlet smoothing, strictly positive. <c>1</c> is Laplace smoothing.</param>
    protected TokenPositionPrior(
        IEnumerable<ObservedText> observations,
        TokenPosition position,
        int tokenCount = 3,
        double alpha = 1.0)
    {
        ArgumentNullException.ThrowIfNull(observations);

        if (tokenCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(tokenCount), tokenCount, "TokenCount must be positive.");
        if (!double.IsFinite(alpha) || alpha <= 0)
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "Alpha must be finite and positive.");

        _position = position;
        _tokenCount = tokenCount;
        _alpha = alpha;
        ObservedText[] materialized = observations.ToArray();
        _categories = materialized
            .Select(observation => observation.Category)
            .Where(category => !string.IsNullOrEmpty(category))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(category => category, StringComparer.Ordinal)
            .ToArray();
        _counts = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var index = _categories.Select((category, i) => (category, i))
            .ToDictionary(pair => pair.category, pair => pair.i, StringComparer.Ordinal);

        // One pass: a caller may hand us a sequence that cannot be enumerated twice.
        foreach (ObservedText observation in materialized)
        {
            if (!index.TryGetValue(observation.Category, out int category))
                continue;

            foreach (string token in Tokens(observation.Text, position, tokenCount))
            {
                if (!_counts.TryGetValue(token, out int[]? row))
                {
                    row = new int[_categories.Length];
                    _counts[token] = row;
                }
                row[category]++;
            }
        }
    }

    /// <summary>Categories this prior can shift, in ordinal order.</summary>
    public IReadOnlyList<string> Categories => Array.AsReadOnly(_categories);

    /// <summary>Which end of the text is read.</summary>
    public TokenPosition Position => _position;

    /// <summary>How many tokens are read at that end.</summary>
    public int TokenCount => _tokenCount;

    /// <summary>Dirichlet smoothing used for every token.</summary>
    public double Alpha => _alpha;

    /// <inheritdoc />
    public IReadOnlyList<TextEvidence> Prior(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (_categories.Length == 0 || _counts.Count == 0)
            return Array.Empty<TextEvidence>();

        var totals = new double[_categories.Length];
        foreach (string token in Tokens(text, _position, _tokenCount))
        {
            if (!_counts.TryGetValue(token, out int[]? row))
                continue;
            for (int i = 0; i < row.Length; i++)
                totals[i] += row[i];
        }

        double total = totals.Sum();
        var evidence = new TextEvidence[_categories.Length];
        for (int i = 0; i < _categories.Length; i++)
        {
            double probability = (totals[i] + _alpha) / (total + _categories.Length * _alpha);
            evidence[i] = new TextEvidence(_categories[i], Math.Log(probability));
        }
        return Array.AsReadOnly(evidence);
    }

    private static IEnumerable<string> Tokens(string text, TokenPosition position, int count)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        IEnumerable<string> selected = position == TokenPosition.Leading
            ? words.Take(count)
            : words.TakeLast(count);
        return selected.Select(word => word.ToLowerInvariant());
    }
}

/// <summary>A log-prior from the first tokens of a text.</summary>
public sealed class LeadingTokenPrior : TokenPositionPrior
{
    /// <summary>A prior that never proposes anything.</summary>
    public static LeadingTokenPrior Empty { get; } = new(Array.Empty<ObservedText>());

    /// <summary>Builds a prior from observed texts and their categories.</summary>
    public LeadingTokenPrior(
        IEnumerable<ObservedText> observations,
        int tokenCount = 3,
        double alpha = 1.0)
        : base(observations, TokenPosition.Leading, tokenCount, alpha)
    {
    }

    /// <summary>Builds a prior from observed texts and their categories.</summary>
    public static LeadingTokenPrior Build(
        IEnumerable<ObservedText> observations,
        int tokenCount = 3,
        double alpha = 1.0) =>
        new(observations, tokenCount, alpha);
}

/// <summary>A log-prior from the last tokens of a text.</summary>
public sealed class TrailingTokenPrior : TokenPositionPrior
{
    /// <summary>A prior that never proposes anything.</summary>
    public static TrailingTokenPrior Empty { get; } = new(Array.Empty<ObservedText>());

    /// <summary>Builds a prior from observed texts and their categories.</summary>
    public TrailingTokenPrior(
        IEnumerable<ObservedText> observations,
        int tokenCount = 3,
        double alpha = 1.0)
        : base(observations, TokenPosition.Trailing, tokenCount, alpha)
    {
    }

    /// <summary>Builds a prior from observed texts and their categories.</summary>
    public static TrailingTokenPrior Build(
        IEnumerable<ObservedText> observations,
        int tokenCount = 3,
        double alpha = 1.0) =>
        new(observations, tokenCount, alpha);
}
