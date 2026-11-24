using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Expansion;

/// <summary>
/// <see cref="ITextClassifier"/> decorator that corrects unknown terms before classifying,
/// predicting on both the original and the corrected text and keeping the best result.
/// </summary>
public sealed class CorrectingSubjectRanker : ITextClassifier
{
    private readonly ITextClassifier _inner;
    private readonly ITermCorrector _corrector;
    private readonly ITokenizer _tokenizer;
    private readonly IReadOnlySet<string> _vocabulary;

    /// <summary>Wraps <paramref name="inner"/> with correction over <paramref name="vocabulary"/>.</summary>
    public CorrectingSubjectRanker(
        ITextClassifier inner,
        ITermCorrector corrector,
        IEnumerable<string> vocabulary,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(corrector);
        ArgumentNullException.ThrowIfNull(vocabulary);

        _inner = inner;
        _corrector = corrector;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _vocabulary = new HashSet<string>(vocabulary, StringComparer.Ordinal);
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

        (string corrected, bool changed) = Correct(text);
        if (!changed)
        {
            return _inner.Predict(text, limit, excludedCategories);
        }

        IReadOnlyList<ClassificationResult> originalResults = _inner.Predict(text, limit, excludedCategories);
        IReadOnlyList<ClassificationResult> correctedResults = _inner.Predict(corrected, limit, excludedCategories);

        if (correctedResults.Count == 0)
        {
            return originalResults;
        }

        if (originalResults.Count == 0)
        {
            return correctedResults;
        }

        return correctedResults[0].Probability >= originalResults[0].Probability
            ? correctedResults
            : originalResults;
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        IReadOnlyList<ClassificationResult> results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private (string Text, bool Changed) Correct(string text)
    {
        IReadOnlyList<string> tokens = _tokenizer.Tokenize(text);
        var corrected = new List<string>(tokens.Count);
        bool changed = false;

        foreach (string token in tokens)
        {
            if (_vocabulary.Contains(token))
            {
                corrected.Add(token);
                continue;
            }

            IReadOnlyList<(string Term, double Weight)> candidates = _corrector.Correct(token);
            string best = candidates.Count > 0 ? candidates[0].Term : token;
            changed |= !string.Equals(best, token, StringComparison.Ordinal);
            corrected.Add(best);
        }

        return (string.Join(' ', corrected), changed);
    }
}
