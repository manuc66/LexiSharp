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
/// <para>
/// The behavior is tunable through <see cref="NaiveBayesOptions"/>: a softmax
/// <c>temperature</c> that sharpens or flattens the posterior, and an optional term
/// <c>idf</c> weighting <c>log(1 + N / df(t))</c> that lets rarer vocabulary weigh more.
/// </para>
/// </remarks>
/// <remarks>
/// Categories with equal probability are ordered by category name, so the output of
/// <see cref="Predict(string, int, IReadOnlySet{string})"/> is deterministic for a given model.
/// </remarks>
public sealed class NaiveBayesClassifier : ITextClassifier, IWeightedPredictor
{
    private readonly ITokenizer _tokenizer;
    private readonly NaiveBayesOptions _options;

    private readonly Dictionary<string, int> _classDocumentCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _classTokenCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _termCountsByClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _termDocumentFrequencies = new(StringComparer.Ordinal);

    private int _documentCount;
    private int _classCount;
    private int _vocabularySize;

    /// <param name="tokenizer">Tokenizer used both at training and prediction time (default: <see cref="Tokenizer.Default"/>).</param>
    /// <param name="options">Tunable knobs; when null, <see cref="NaiveBayesOptions.Default"/> (classic behavior) applies.</param>
    public NaiveBayesClassifier(ITokenizer? tokenizer = null, NaiveBayesOptions? options = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _options = options ?? NaiveBayesOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("Temperature must be a finite number greater than 0.", nameof(options));
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        _classDocumentCounts.Clear();
        _classTokenCounts.Clear();
        _termCountsByClass.Clear();
        _termDocumentFrequencies.Clear();
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

            var documentTerms = new HashSet<string>(StringComparer.Ordinal);

            foreach (var term in _tokenizer.Tokenize(document.Text))
            {
                classTerms.TryGetValue(term, out int termCount);
                classTerms[term] = termCount + 1;
                vocabulary.Add(term);

                _classTokenCounts.TryGetValue(category, out int classTokenCount);
                _classTokenCounts[category] = classTokenCount + 1;

                if (documentTerms.Add(term))
                {
                    _termDocumentFrequencies.TryGetValue(term, out int docFrequency);
                    _termDocumentFrequencies[term] = docFrequency + 1;
                }
            }
        }

        _vocabularySize = vocabulary.Count;
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0 || _classCount == 0)
            return Array.Empty<ClassificationResult>();

        var tokens = _tokenizer.Tokenize(text).Select(WeightedToken.Full);

        return PredictCore(tokens, limit, excludedCategories);
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        IEnumerable<WeightedToken> tokens,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(tokens);

        if (limit <= 0 || _classCount == 0)
            return Array.Empty<ClassificationResult>();

        return PredictCore(tokens, limit, excludedCategories);
    }

    private IReadOnlyList<ClassificationResult> PredictCore(
        IEnumerable<WeightedToken> tokens,
        int limit,
        IReadOnlySet<string>? excludedCategories)
    {
        var tokenList = tokens.ToList();

        var logProbabilities = new double[_classCount];
        var categories = new string[_classCount];

        int idx = 0;

        foreach (var pair in _termCountsByClass)
        {
            string category = pair.Key;

            if (excludedCategories is not null && excludedCategories.Contains(category))
                continue;

            var classTerms = pair.Value;
            int classTokenCount = 0;
            _classTokenCounts.TryGetValue(category, out classTokenCount);
            double smoothingDenominator = classTokenCount + _vocabularySize;

            _classDocumentCounts.TryGetValue(category, out int classDocumentCount);
            double logProbability = Math.Log((double)classDocumentCount / _documentCount);

            foreach (var token in tokenList)
            {
                classTerms.TryGetValue(token.Token, out int termCount);

                if (smoothingDenominator > 0)
                {
                    double idf = IdfWeight(token.Token);
                    logProbability += token.Weight * idf * Math.Log((termCount + 1.0) / smoothingDenominator);
                }
            }

            categories[idx] = category;
            logProbabilities[idx] = logProbability / _options.Temperature;
            idx++;
        }

        return NormalizeAndRank(categories, logProbabilities, idx, limit);
    }

    /// <summary><c>log(1 + N / df(term))</c> over the training corpus, or 1 when unweighted.</summary>
    private double IdfWeight(string term)
    {
        if (!_options.IdfWeighting || _documentCount == 0)
            return 1.0;

        _termDocumentFrequencies.TryGetValue(term, out int docFrequency);

        if (docFrequency <= 0)
            return 1.0;

        return Math.Log(1.0 + _documentCount / (double)docFrequency);
    }

    private static IReadOnlyList<ClassificationResult> NormalizeAndRank(
        string[] categories, double[] logProbabilities, int count, int limit)
    {
        if (count <= 0)
            return Array.Empty<ClassificationResult>();

        double max = logProbabilities[0];
        for (int i = 1; i < count; i++)
            max = Math.Max(max, logProbabilities[i]);

        double sum = 0;
        var probabilities = new double[count];

        for (int i = 0; i < count; i++)
        {
            probabilities[i] = Math.Exp(logProbabilities[i] - max);
            sum += probabilities[i];
        }

        var ranked = new List<ClassificationResult>(count);

        for (int i = 0; i < count; i++)
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