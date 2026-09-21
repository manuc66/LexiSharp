using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>
/// Multinomial Naive Bayes trained on labelled documents.
/// </summary>
/// <remarks>
/// Model parameters: class priors and per-class term probabilities
/// <c>P(t | c) = (count(c, t) + 1) / (size(c) + |V|)</c> — i.e. Laplace (add-1)
/// smoothing over the shared training vocabulary. Documents without a
/// <see cref="SearchDocument.Category"/> are ignored during training.
/// </remarks>
/// <remarks>
/// Categories with equal probability are ordered by category name, so the output of
/// <see cref="Predict"/> is deterministic for a given model.
/// </remarks>
public sealed class NaiveBayesClassifier : ITextClassifier
{
    private readonly ITokenizer _tokenizer;

    private readonly Dictionary<string, int> _classDocumentCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _classTokenCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _termCountsByClass = new(StringComparer.Ordinal);

    private int _documentCount;
    private int _classCount;
    private int _vocabularySize;

    public NaiveBayesClassifier(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        _classDocumentCounts.Clear();
        _classTokenCounts.Clear();
        _termCountsByClass.Clear();
        _documentCount = 0;
        _classCount = 0;
        _vocabularySize = 0;

        var vocabulary = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents)
        {
            if (string.IsNullOrEmpty(document.Category))
                continue;

            string category = document.Category;

            if (!_termCountsByClass.TryGetValue(category, out var classTerms))
            {
                classTerms = new Dictionary<string, int>(StringComparer.Ordinal);
                _termCountsByClass[category] = classTerms;
                _classCount++;
            }

            _classDocumentCounts.TryGetValue(category, out int documentCount);
            _classDocumentCounts[category] = documentCount + 1;
            _documentCount++;

            foreach (var term in _tokenizer.Tokenize(document.Text))
            {
                classTerms.TryGetValue(term, out int termCount);
                classTerms[term] = termCount + 1;
                vocabulary.Add(term);

                _classTokenCounts.TryGetValue(category, out int classTokenCount);
                _classTokenCounts[category] = classTokenCount + 1;
            }
        }

        _vocabularySize = vocabulary.Count;
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(string text, int limit = 3)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0 || _classCount == 0)
            return Array.Empty<ClassificationResult>();

        var terms = _tokenizer.Tokenize(text);
        var logProbabilities = new double[_classCount];
        var categories = new string[_classCount];

        int idx = 0;

        foreach (var pair in _termCountsByClass)
        {
            string category = pair.Key;
            var classTerms = pair.Value;
            int classTokenCount = 0;
            _classTokenCounts.TryGetValue(category, out classTokenCount);
            double smoothingDenominator = classTokenCount + _vocabularySize;

            _classDocumentCounts.TryGetValue(category, out int classDocumentCount);
            double logProbability = Math.Log((double)classDocumentCount / _documentCount);

            foreach (var term in terms)
            {
                classTerms.TryGetValue(term, out int termCount);

                if (smoothingDenominator > 0)
                    logProbability += Math.Log((termCount + 1.0) / smoothingDenominator);
            }

            categories[idx] = category;
            logProbabilities[idx] = logProbability;
            idx++;
        }

        return NormalizeAndRank(categories, logProbabilities, limit);
    }

    /// <inheritdoc />
    public string? PredictBest(string text)
    {
        var results = Predict(text, limit: 1);
        return results.Count > 0 ? results[0].Category : null;
    }

    private static IReadOnlyList<ClassificationResult> NormalizeAndRank(
        string[] categories, double[] logProbabilities, int limit)
    {
        double max = logProbabilities[0];
        for (int i = 1; i < logProbabilities.Length; i++)
            max = Math.Max(max, logProbabilities[i]);

        double sum = 0;
        var probabilities = new double[logProbabilities.Length];

        for (int i = 0; i < logProbabilities.Length; i++)
        {
            probabilities[i] = Math.Exp(logProbabilities[i] - max);
            sum += probabilities[i];
        }

        var ranked = new List<ClassificationResult>(categories.Length);

        for (int i = 0; i < categories.Length; i++)
        {
            ranked.Add(new ClassificationResult(categories[i], probabilities[i] / sum));
        }

        return ranked
            .OrderByDescending(r => r.Probability)
            .ThenBy(r => r.Category, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }
}