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

        // Everything that depends only on the query token is resolved once here, not once per
        // class: the class loop below is the outer one, so leaving these lookups inside it
        // re-hashed every term once per class.
        var query = ResolveQueryTerms(tokenList);

        var logProbabilities = new double[_classCount];
        var categories = new string[_classCount];

        int idx = 0;

        foreach (var pair in _termCountsByClass)
        {
            if (excludedCategories is not null && excludedCategories.Contains(pair.Key))
                continue;

            logProbabilities[idx] = LogProbability(pair.Key, pair.Value, query) / _options.Temperature;
            categories[idx] = pair.Key;
            idx++;
        }

        return NormalizeAndRank(categories, logProbabilities, idx, limit);
    }

    /// <summary>
    /// The per-token quantities a prediction needs, resolved in a single pass over the query.
    /// Building this up front turns the per-class scoring loop into arithmetic over an array
    /// instead of a dictionary lookup per (class, token) pair.
    /// </summary>
    /// <remarks>
    /// Only the lookups the active options actually use are materialized. The idf weight is always
    /// resolved because it is a function of the term alone and would otherwise be recomputed once
    /// per class; the corpus count and the vocabulary flag are resolved only when
    /// <see cref="NaiveBayesOptions.Complement"/> or
    /// <see cref="NaiveBayesOptions.SkipOutOfVocabularyTokens"/> is on, so a default model pays for
    /// one array per prediction rather than five.
    /// </remarks>
    private QueryTerms ResolveQueryTerms(List<WeightedToken> tokens)
    {
        bool needsGlobalCounts = _options.Complement;
        bool needsVocabulary = _options.SkipOutOfVocabularyTokens;

        var query = new QueryTerms(tokens, needsGlobalCounts, needsVocabulary);

        // Local, non-nullable handles: the arrays only exist when the matching option is on, and
        // holding them here keeps the loop free of null checks.
        var globalCounts = query.GlobalCounts;
        var inVocabularyFlags = query.InVocabulary;

        for (int i = 0; i < tokens.Count; i++)
        {
            string term = tokens[i].Token;

            if (globalCounts is not null)
                globalCounts[i] = _globalTermCounts.TryGetValue(term, out int global) ? global : 0;

            bool inVocabulary = inVocabularyFlags is null
                                || (_termDocumentFrequencies.TryGetValue(term, out int df) && df > 0);

            if (inVocabularyFlags is not null)
                inVocabularyFlags[i] = inVocabulary;

            // A function of the term alone, so it is identical for every class: resolved once here
            // rather than inside the class loop.
            query.IdfWeights[i] = _options.IdfMode == IdfMode.None ? 1.0 : IdfWeight(term, inVocabulary);
        }

        return query;
    }

    private double LogProbability(
        string category,
        Dictionary<string, int> classTerms,
        QueryTerms query)
    {
        _classTokenCounts.TryGetValue(category, out int classTokenCount);
        _classDocumentCounts.TryGetValue(category, out int classDocumentCount);

        return _options.Complement
            ? ComplementLogProbability(classTerms, classTokenCount, query)
            : ClassicLogProbability(classTerms, classTokenCount, classDocumentCount, query);
    }

    private double ComplementLogProbability(
        Dictionary<string, int> classTerms,
        int classTokenCount,
        QueryTerms query)
    {
        // A query is scored as the negative log-likelihood of its tokens in the complement
        // distribution, so the class whose complement explains the query least is the best pick
        // (Rennie et al., 2003 as implemented by scikit-learn's ComplementNB, which also leaves
        // the priors out). Complement statistics: term and token totals over the other classes
        // are smoothed by Alpha over the vocabulary.
        int complementTokenCount = _totalTokenCount - classTokenCount;
        double complementDenominator = complementTokenCount + _options.Alpha * _vocabularySize;
        double logProbability = 0.0;

        if (complementDenominator <= 0)
            return logProbability;

        var tokens = query.Tokens;
        var globalCounts = query.GlobalCounts!;

        for (int i = 0; i < query.Count; i++)
        {
            if (query.InVocabulary is not null && !query.InVocabulary[i])
                continue;

            classTerms.TryGetValue(tokens[i].Token, out int termCount);
            int complementTermCount = globalCounts[i] - termCount;

            logProbability += tokens[i].Weight * query.IdfWeights[i]
                * -Math.Log((complementTermCount + _options.Alpha) / complementDenominator);
        }

        return logProbability;
    }

    private double ClassicLogProbability(
        Dictionary<string, int> classTerms,
        int classTokenCount,
        int classDocumentCount,
        QueryTerms query)
    {
        double smoothingDenominator = classTokenCount + _options.Alpha * _vocabularySize;

        double logProbability = _options.SmoothPriors
            ? Math.Log((classDocumentCount + _options.Alpha)
                       / (_documentCount + _options.Alpha * _classCount))
            : Math.Log((double)classDocumentCount / _documentCount);

        if (smoothingDenominator <= 0)
            return logProbability;

        var tokens = query.Tokens;

        for (int i = 0; i < query.Count; i++)
        {
            if (query.InVocabulary is not null && !query.InVocabulary[i])
                continue;

            classTerms.TryGetValue(tokens[i].Token, out int termCount);

            logProbability += tokens[i].Weight * query.IdfWeights[i]
                * Math.Log((termCount + _options.Alpha) / smoothingDenominator);
        }

        return logProbability;
    }

    /// <summary>
    /// A prediction's query tokens with every term-only lookup the active options use already
    /// resolved. Holds the caller's token list rather than copying it.
    /// </summary>
    private sealed class QueryTerms
    {
        public QueryTerms(List<WeightedToken> tokens, bool needsGlobalCounts, bool needsVocabulary)
        {
            Tokens = tokens;
            IdfWeights = new double[tokens.Count];
            GlobalCounts = needsGlobalCounts ? new int[tokens.Count] : null;
            InVocabulary = needsVocabulary ? new bool[tokens.Count] : null;
        }

        /// <summary>The caller's tokens, in query order.</summary>
        public List<WeightedToken> Tokens { get; }

        /// <summary>Number of tokens.</summary>
        public int Count => Tokens.Count;

        /// <summary>Per-token idf weight, identical across classes.</summary>
        public double[] IdfWeights { get; }

        /// <summary>Corpus-wide occurrence count, for the complement model; null when unused.</summary>
        public int[]? GlobalCounts { get; }

        /// <summary>Vocabulary membership, for the out-of-vocabulary skip; null when unused.</summary>
        public bool[]? InVocabulary { get; }
    }

    /// <summary>
    /// Inverse-document-frequency weight for a term, per <see cref="NaiveBayesOptions.IdfMode"/>:
    /// <c>DocumentCount</c> uses <c>log(1 + N / df)</c>, <c>ClassCount</c> uses
    /// <c>max(0, log(C / df))</c>, <c>None</c> returns 1. Tokens never seen in the corpus are
    /// corner cases only reachable when <see cref="NaiveBayesOptions.SkipOutOfVocabularyTokens"/>
    /// is off; they are weighted like an unseen term (1.0 / 0.0 respectively).
    /// </summary>
    /// <param name="term">The term to weight.</param>
    /// <param name="inVocabulary">
    /// Whether the term is in the corpus vocabulary, resolved by the caller so this stays a pure
    /// computation on the scoring path.
    /// </param>
    private double IdfWeight(string term, bool inVocabulary)
    {
        switch (_options.IdfMode)
        {
            case IdfMode.ClassCount:
            {
                if (!inVocabulary || _classCount <= 0)
                    return 0.0;

                return Math.Max(0.0, Math.Log((double)_classCount / _termDocumentFrequencies[term]));
            }

            case IdfMode.DocumentCount:
            {
                if (_documentCount == 0 || !inVocabulary)
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