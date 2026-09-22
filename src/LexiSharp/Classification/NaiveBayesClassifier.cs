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
    /// <c>temperature</c> that sharpens or flattens the posterior, an <see cref="IdfMode"/> that
    /// discounts corpus-wide vocabulary, an <see cref="NaiveBayesOptions.Alpha"/> smoothing
    /// coefficient (optionally applied to the priors), a mode that skips out-of-vocabulary
    /// query tokens instead of letting Laplace smoothing penalize them, and a
    /// <see cref="NaiveBayesOptions.Complement"/> mode that learns each class from its
    /// complement — use it when the training classes are severely imbalanced.
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
    private readonly Dictionary<string, int> _globalTermCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _termDocumentFrequencies = new(StringComparer.Ordinal);

    private int _documentCount;
    private int _totalTokenCount;
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
        _globalTermCounts.Clear();
        _termDocumentFrequencies.Clear();
        _documentCount = 0;
        _totalTokenCount = 0;
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

                _globalTermCounts.TryGetValue(term, out int globalTermCount);
                _globalTermCounts[term] = globalTermCount + 1;
                _totalTokenCount++;

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

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
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
            if (excludedCategories is not null && excludedCategories.Contains(pair.Key))
                continue;

            logProbabilities[idx] = LogProbability(pair.Key, pair.Value, tokenList) / _options.Temperature;
            categories[idx] = pair.Key;
            idx++;
        }

        return NormalizeAndRank(categories, logProbabilities, idx, limit);
    }

    private double LogProbability(
        string category,
        Dictionary<string, int> classTerms,
        List<WeightedToken> tokenList)
    {
        _classTokenCounts.TryGetValue(category, out int classTokenCount);
        _classDocumentCounts.TryGetValue(category, out int classDocumentCount);

        return _options.Complement
            ? ComplementLogProbability(classTerms, classTokenCount, tokenList)
            : ClassicLogProbability(classTerms, classTokenCount, classDocumentCount, tokenList);
    }

    private double ComplementLogProbability(
        Dictionary<string, int> classTerms,
        int classTokenCount,
        List<WeightedToken> tokenList)
    {
        // A query is scored as the negative log-likelihood of its tokens in the complement
        // distribution, so the class whose complement explains the query least is the best pick
        // (Rennie et al., 2003 as implemented by scikit-learn's ComplementNB, which also leaves
        // the priors out). Complement statistics: term and token totals over the other classes
        // are smoothed by Alpha over the vocabulary.
        int complementTokenCount = _totalTokenCount - classTokenCount;
        double complementDenominator = complementTokenCount + _options.Alpha * _vocabularySize;
        double logProbability = 0.0;

        foreach (var token in tokenList)
        {
            if (_options.SkipOutOfVocabularyTokens && !IsInVocabulary(token.Token))
                continue;

            _globalTermCounts.TryGetValue(token.Token, out int globalTermCount);
            classTerms.TryGetValue(token.Token, out int termCount);
            int complementTermCount = globalTermCount - termCount;

            if (complementDenominator > 0)
            {
                double idf = IdfWeight(token.Token);
                logProbability += token.Weight * idf
                    * -Math.Log((complementTermCount + _options.Alpha) / complementDenominator);
            }
        }

        return logProbability;
    }

    private double ClassicLogProbability(
        Dictionary<string, int> classTerms,
        int classTokenCount,
        int classDocumentCount,
        List<WeightedToken> tokenList)
    {
        double smoothingDenominator = classTokenCount + _options.Alpha * _vocabularySize;

        double logProbability = _options.SmoothPriors
            ? Math.Log((classDocumentCount + _options.Alpha)
                       / (_documentCount + _options.Alpha * _classCount))
            : Math.Log((double)classDocumentCount / _documentCount);

        foreach (var token in tokenList)
        {
            if (_options.SkipOutOfVocabularyTokens && !IsInVocabulary(token.Token))
                continue;

            classTerms.TryGetValue(token.Token, out int termCount);

            if (smoothingDenominator > 0)
            {
                double idf = IdfWeight(token.Token);
                logProbability += token.Weight * idf
                    * Math.Log((termCount + _options.Alpha) / smoothingDenominator);
            }
        }

        return logProbability;
    }

    private bool IsInVocabulary(string term) =>
        _termDocumentFrequencies.TryGetValue(term, out int docFrequency) && docFrequency > 0;

    /// <summary>
    /// Inverse-document-frequency weight for a term, per <see cref="NaiveBayesOptions.IdfMode"/>:
    /// <c>DocumentCount</c> uses <c>log(1 + N / df)</c>, <c>ClassCount</c> uses
    /// <c>max(0, log(C / df))</c>, <c>None</c> returns 1. Tokens never seen in the corpus are
    /// corner cases only reachable when <see cref="NaiveBayesOptions.SkipOutOfVocabularyTokens"/>
    /// is off; they are weighted like an unseen term (1.0 / 0.0 respectively).
    /// </summary>
    private double IdfWeight(string term)
    {
        switch (_options.IdfMode)
        {
            case IdfMode.ClassCount:
            {
                if (!IsInVocabulary(term) || _classCount <= 0)
                    return 0.0;

                return Math.Max(0.0, Math.Log((double)_classCount / _termDocumentFrequencies[term]));
            }

            case IdfMode.DocumentCount:
            {
                if (_documentCount == 0 || !IsInVocabulary(term))
                    return 1.0;

                return Math.Log(1.0 + _documentCount / (double)_termDocumentFrequencies[term]);
            }

            default:
                return 1.0;
        }
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