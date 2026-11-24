using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>
/// <see cref="ITextClassifier"/> decorator that drops categories sharing no token with the query:
/// a model may still return a distribution for an unseen text, and a strong prior alone must not
/// propose an unrelated category.
/// </summary>
public class VocabularyOverlapGate : ITextClassifier
{
    private readonly ITextClassifier _inner;
    private readonly ITokenizer _tokenizer;
    private readonly Dictionary<string, HashSet<string>> _vocabulary;

    /// <summary>Wraps <paramref name="inner"/> and filters its predictions by vocabulary overlap.</summary>
    public VocabularyOverlapGate(
        ITextClassifier inner,
        ITokenizer tokenizer,
        IReadOnlyDictionary<string, HashSet<string>> vocabulary)
        : this(inner, tokenizer, ToDictionary(vocabulary))
    {
    }


    /// <summary>Wraps <paramref name="inner"/> and filters its predictions by vocabulary overlap.</summary>
    public VocabularyOverlapGate(
        ITextClassifier inner,
        ITokenizer tokenizer,
        IReadOnlyDictionary<string, IReadOnlySet<string>> vocabulary)
        : this(inner, tokenizer, ToDictionary(vocabulary.ToDictionary(kv => kv.Key, kv => (IEnumerable<string>)kv.Value)))
    {
    }

    /// <summary>Wraps <paramref name="inner"/> and filters its predictions by vocabulary overlap.</summary>
    public VocabularyOverlapGate(
        ITextClassifier inner,
        ITokenizer tokenizer,
        IReadOnlyDictionary<string, IEnumerable<string>> vocabulary)
        : this(inner, tokenizer, ToDictionary(vocabulary))
    {
    }

    private VocabularyOverlapGate(
        ITextClassifier inner,
        ITokenizer tokenizer,
        Dictionary<string, HashSet<string>> vocabulary)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentNullException.ThrowIfNull(vocabulary);

        _inner = inner;
        _tokenizer = tokenizer;
        _vocabulary = vocabulary;
    }

    private static Dictionary<string, HashSet<string>> ToDictionary<T>(
        IEnumerable<KeyValuePair<string, T>> vocabulary)
        where T : IEnumerable<string>
    {
        ArgumentNullException.ThrowIfNull(vocabulary);

        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (KeyValuePair<string, T> pair in vocabulary)
        {
            result[pair.Key] = pair.Value is null
                ? new HashSet<string>(StringComparer.Ordinal)
                : new HashSet<string>(pair.Value, StringComparer.Ordinal);
        }

        return result;
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        IReadOnlyList<string> tokens = _tokenizer.Tokenize(text);
        if (tokens.Count == 0)
        {
            return [];
        }

        IReadOnlyList<ClassificationResult> predicted = _inner.Predict(text, limit: int.MaxValue, excludedCategories);
        return predicted
            .Where(result => _vocabulary.TryGetValue(result.Category, out HashSet<string>? terms)
                && terms.Overlaps(tokens))
            .Take(limit)
            .ToList();
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }
}

/// <summary>
/// Backwards-friendly alias for <see cref="VocabularyOverlapGate"/>, matching the name used by
/// WindowTracker.
/// </summary>
public sealed class VocabularyGate : VocabularyOverlapGate
{
    /// <summary>Wraps <paramref name="inner"/> and filters its predictions by vocabulary overlap.</summary>
    public VocabularyGate(ITextClassifier inner, ITokenizer tokenizer, IReadOnlyDictionary<string, HashSet<string>> vocabulary)
        : base(inner, tokenizer, vocabulary)
    {
    }


    /// <summary>Wraps <paramref name="inner"/> and filters its predictions by vocabulary overlap.</summary>
    public VocabularyGate(ITextClassifier inner, ITokenizer tokenizer, IReadOnlyDictionary<string, IEnumerable<string>> vocabulary)
        : base(inner, tokenizer, vocabulary)
    {
    }
}

/// <summary>
/// <see cref="ITextClassifier"/> decorator that remembers the last prediction of the wrapped
/// classifier, so a result can be attributed to its strongest signal.
/// </summary>
public sealed class RecordingClassifier : ITextClassifier
{
    private readonly ITextClassifier _inner;

    /// <summary>Wraps <paramref name="inner"/>.</summary>
    public RecordingClassifier(ITextClassifier inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>The wrapped classifier.</summary>
    public ITextClassifier Inner => _inner;

    /// <summary>The last prediction produced by <see cref="Predict"/>.</summary>
    public IReadOnlyList<ClassificationResult> Last { get; private set; } = [];

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        Last = _inner.Predict(text, limit, excludedCategories);
        return Last;
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }
}
