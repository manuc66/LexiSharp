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
/// <remarks>
/// Two optional capabilities sit on top of retraining, and they are deliberately different things.
/// <see cref="Learn"/> and <see cref="Unlearn"/> move the corpus itself: a model fed one document at
/// a time is indistinguishable from one trained on the whole corpus at once, under every options
/// setting, because <c>Train</c> and <c>Learn</c> write through the same code path. See
/// <see cref="IIncrementalTextClassifier"/>.
/// <para/>
/// <see cref="Reinforce"/> and <see cref="Unreinforce"/> instead record user feedback in a ledger
/// beside those counts, never in them, weighted and signed so that a user can say "this is that
/// category", "this is not", or change their mind, with no retrain. Keeping the two apart is what
/// makes cancelling feedback exact and keeps the corpus model reachable only through
/// <see cref="Train"/>, <see cref="Learn"/> and <see cref="Unlearn"/>. See
/// <see cref="IReinforceableTextClassifier"/> for the contract.
/// </remarks>
public sealed class NaiveBayesClassifier
    : ITextClassifier, IWeightedPredictor, IIncrementalTextClassifier, IReinforceableTextClassifier
{
    private readonly ITokenizer _tokenizer;
    private readonly NaiveBayesOptions _options;

    private readonly Dictionary<string, int> _classDocumentCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _classTokenCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _termCountsByClass = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _globalTermCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _termDocumentFrequencies = new(StringComparer.Ordinal);

    // Category -> term -> signed mass. Deliberately kept apart from the counts above: Train is the
    // only writer of the corpus model, which is what makes Unreinforce an exact inverse and
    // ForgetReinforcement a clean reset rather than a reconstruction.
    private readonly Dictionary<string, Dictionary<string, double>> _reinforcementByClass = new(StringComparer.Ordinal);

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

        ResetModel();

        foreach (var document in documents)
            Accumulate(document);
    }

    /// <inheritdoc />
    public void Learn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        Accumulate(document);
    }

    /// <inheritdoc />
    public void Unlearn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (string.IsNullOrEmpty(document.Category))
            return;

        // Same guard as Train: a category the model does not hold has no counts to subtract from,
        // and inventing one here is how a stray call would leave a phantom label behind.
        if (!_termCountsByClass.TryGetValue(document.Category, out var classTerms))
            return;

        // document.Category is non-empty here, checked above.
        Decumulate(document.Category, document.Text, classTerms);
    }

    private void ResetModel()
    {
        _classDocumentCounts.Clear();
        _classTokenCounts.Clear();
        _termCountsByClass.Clear();
        _globalTermCounts.Clear();
        _termDocumentFrequencies.Clear();
        _documentCount = 0;
        _totalTokenCount = 0;
        _classCount = 0;
        _vocabularySize = 0;
    }

    /// <summary>
    /// Adds one document to the corpus counts. This is the only place the per-class term counts,
    /// the corpus-wide counts, the document frequencies and the priors are written, and
    /// <see cref="Train"/> and <see cref="Learn"/> both go through it — which is what makes learning
    /// one document at a time identical to training on the whole corpus, rather than merely close.
    /// </summary>
    private void Accumulate(SearchDocument document)
    {
        if (string.IsNullOrEmpty(document.Category))
            return;

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

        // Derived from the counts rather than tracked separately, so it cannot drift out of step the
        // way an independent counter would.
        _vocabularySize = _globalTermCounts.Count;
    }

    /// <summary>
    /// The exact inverse of <see cref="Accumulate"/> for one document, on a category already known
    /// to exist.
    /// </summary>
    /// <remarks>
    /// Two invariants are restored unconditionally rather than conditionally, because they describe
    /// the document count and not its content: the class loses a document, and the corpus loses one.
    /// A text whose terms are absent from the class therefore still costs the class a document of
    /// prior. That is the documented arithmetic contract, and it is what makes this the exact inverse
    /// of <see cref="Learn"/> for the case that matters — a document that really was learned.
    /// </remarks>
    private void Decumulate(string category, string text, Dictionary<string, int> classTerms)
    {
        var documentTerms = new HashSet<string>(StringComparer.Ordinal);

        foreach (var term in _tokenizer.Tokenize(text))
        {
            // A term this class never counted is not this class's to subtract: it may well belong to
            // another class, where its corpus-wide count is still owed.
            if (!classTerms.TryGetValue(term, out int termCount) || termCount <= 0)
                continue;

            if (termCount <= 1)
                classTerms.Remove(term);
            else
                classTerms[term] = termCount - 1;

            _totalTokenCount--;

            _classTokenCounts.TryGetValue(category, out int classTokenCount);
            _classTokenCounts[category] = classTokenCount - 1;

            if (_globalTermCounts.TryGetValue(term, out int globalTermCount))
            {
                if (globalTermCount <= 1)
                    _globalTermCounts.Remove(term);
                else
                    _globalTermCounts[term] = globalTermCount - 1;
            }

            // Once per distinct term, mirroring how Accumulate raised it.
            if (documentTerms.Add(term) &&
                _termDocumentFrequencies.TryGetValue(term, out int documentFrequency))
            {
                if (documentFrequency <= 1)
                    _termDocumentFrequencies.Remove(term);
                else
                    _termDocumentFrequencies[term] = documentFrequency - 1;
            }
        }

        if (_classDocumentCounts.TryGetValue(category, out int documents))
        {
            if (documents <= 1)
            {
                // The last document of this class: retire it entirely, rather than leave behind the
                // zero-count entries a fresh Train over the remaining corpus would never produce.
                _classDocumentCounts.Remove(category);
                _classTokenCounts.Remove(category);
                _termCountsByClass.Remove(category);
                _classCount--;
            }
            else
            {
                _classDocumentCounts[category] = documents - 1;
            }
        }

        if (_documentCount > 0)
            _documentCount--;

        _vocabularySize = _globalTermCounts.Count;
    }

    /// <inheritdoc />
    public void Reinforce(string text, string category, double weight = 1.0) =>
        AdjustReinforcement(text, category, weight, add: true);

    /// <inheritdoc />
    public void Unreinforce(string text, string category, double weight = 1.0) =>
        AdjustReinforcement(text, category, weight, add: false);

    /// <inheritdoc />
    public void ForgetReinforcement() => _reinforcementByClass.Clear();

    /// <summary>
    /// Moves <paramref name="weight"/> units of evidence for every distinct term of
    /// <paramref name="text"/> in or out of the ledger entry for <paramref name="category"/>.
    /// </summary>
    /// <param name="add">
    /// True to reinforce, false to unreinforce — which is the same as reinforcing by
    /// <c>-weight</c>, because the ledger is additive and keeps no history.
    /// </param>
    /// <param name="text">The text the feedback is about.</param>
    /// <param name="category">The category whose ledger entry is adjusted.</param>
    /// <param name="weight">Signed strength; validated here so the reported parameter name matches the public one.</param>
    private void AdjustReinforcement(string text, string category, double weight, bool add)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (double.IsNaN(weight) || double.IsInfinity(weight))
            throw new ArgumentOutOfRangeException(
                nameof(weight), weight, "Reinforcement weight must be a finite number.");

        double massChange = add ? weight : -weight;

        if (massChange == 0 || string.IsNullOrEmpty(category))
            return;

        // The label set is the corpus's. A category that was never trained has neither a prior nor
        // a class-conditional distribution, so there is nothing here for evidence to adjust.
        if (!_termCountsByClass.ContainsKey(category))
            return;

        if (!_reinforcementByClass.TryGetValue(category, out var ledger))
        {
            ledger = new Dictionary<string, double>(StringComparer.Ordinal);
            _reinforcementByClass[category] = ledger;
        }

        // Distinct terms only: a user repeating a word in a search box is not asserting it twice,
        // and letting repetition scale the correction would make the weight parameter redundant.
        var counted = new HashSet<string>(StringComparer.Ordinal);

        foreach (string term in _tokenizer.Tokenize(text))
        {
            if (!counted.Add(term))
                continue;

            ledger.TryGetValue(term, out double mass);
            mass += massChange;

            // Pruning at zero is what makes a cancelled reinforcement indistinguishable from one
            // that never happened, right down to the scoring path, which pays for the ledger only
            // while the ledger holds something.
            if (mass == 0)
                ledger.Remove(term);
            else
                ledger[term] = mass;
        }

        if (ledger.Count == 0)
            _reinforcementByClass.Remove(category);
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
    /// per class; the corpus count, the vocabulary flag and the reinforcement weights are resolved
    /// only when <see cref="NaiveBayesOptions.Complement"/>,
    /// <see cref="NaiveBayesOptions.SkipOutOfVocabularyTokens"/> or a non-empty reinforcement
    /// ledger calls for them, so a default model on a default classifier pays for one array per
    /// prediction. Vocabulary membership itself is always looked up (see the loop below) because the
    /// idf weight needs it whatever the options say.
    /// </remarks>
    private QueryTerms ResolveQueryTerms(List<WeightedToken> tokens)
    {
        bool needsGlobalCounts = _options.Complement;
        bool needsVocabulary = _options.SkipOutOfVocabularyTokens;

        // The ledger is usually empty, and the caller reads one array per active feature per
        // prediction, so it is only asked for when there is something in it to weight.
        var query = new QueryTerms(
            tokens, needsGlobalCounts, needsVocabulary, _reinforcementByClass.Count > 0);

        // Local, non-nullable handles: the arrays only exist when the matching option is on, and
        // holding them here keeps the loop free of null checks.
        var globalCounts = query.GlobalCounts;
        var inVocabularyFlags = query.InVocabulary;
        var reinforcementWeights = query.ReinforcementWeights;

        for (int i = 0; i < tokens.Count; i++)
        {
            string term = tokens[i].Token;

            if (globalCounts is not null)
                globalCounts[i] = _globalTermCounts.TryGetValue(term, out int global) ? global : 0;

            // Resolved for every query token, whatever the options are. The idf weight below is a
            // function of the term alone and is read from the document-frequency table, so a term
            // the corpus never saw has to be recognized as such here — gating this on
            // SkipOutOfVocabularyTokens reported corpus-unknown terms as in-vocabulary, and the
            // idf lookup then indexed a key that was not there.
            bool inVocabulary = _termDocumentFrequencies.TryGetValue(term, out int df) && df > 0;

            if (inVocabularyFlags is not null)
                inVocabularyFlags[i] = inVocabulary;

            // A function of the term alone, so it is identical for every class: resolved once here
            // rather than inside the class loop.
            query.IdfWeights[i] = _options.IdfMode == IdfMode.None ? 1.0 : IdfWeight(term, inVocabulary);

            if (reinforcementWeights is not null)
            {
                // A term the corpus never saw has no document frequency to be discounted by, and a
                // user asserting "this text means that category" outranks whatever the corpus
                // happened to contain. So such a term counts at full strength for reinforcement,
                // even under ClassCount, where the likelihood path zeroes it. Deliberate, and
                // pinned by a test: a correction made entirely of unfamiliar words still has to
                // say something.
                reinforcementWeights[i] = inVocabulary ? query.IdfWeights[i] : 1.0;
            }
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

        // Parenthesized deliberately: `?:` binds looser than `+`, so without them the reinforcement
        // would only ever be added on the classic path.
        return _options.Complement
            ? ComplementLogProbability(classTerms, classTokenCount, query)
              + ReinforcedEvidence(category, query)
            : ClassicLogProbability(classTerms, classTokenCount, classDocumentCount, query)
              + ReinforcedEvidence(category, query);
    }

    /// <summary>
    /// The reinforcement ledger's contribution to one category's log score: the sum over the query's
    /// terms of the query weight, the term's reinforcement weight, and the mass recorded for this
    /// category. Added identically under both scoring variants, so what a user asserted does not
    /// depend on which variant they configured.
    /// </summary>
    /// <remarks>
    /// Guarded by the per-category lookup, so a classifier nobody has reinforced pays one failed
    /// dictionary probe per category and nothing else — and when the ledger is empty the caller
    /// never reaches the token loop at all.
    /// </remarks>
    private double ReinforcedEvidence(string category, QueryTerms query)
    {
        var weights = query.ReinforcementWeights;

        if (weights is null || !_reinforcementByClass.TryGetValue(category, out var ledger))
            return 0.0;

        var tokens = query.Tokens;
        double total = 0.0;

        for (int i = 0; i < query.Count; i++)
        {
            if (ledger.TryGetValue(tokens[i].Token, out double mass))
                total += tokens[i].Weight * weights[i] * mass;
        }

        return total;
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
        public QueryTerms(
            List<WeightedToken> tokens,
            bool needsGlobalCounts,
            bool needsVocabulary,
            bool hasReinforcement)
        {
            Tokens = tokens;
            IdfWeights = new double[tokens.Count];
            GlobalCounts = needsGlobalCounts ? new int[tokens.Count] : null;
            InVocabulary = needsVocabulary ? new bool[tokens.Count] : null;
            ReinforcementWeights = hasReinforcement ? new double[tokens.Count] : null;
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

        /// <summary>
        /// Per-token weight applied to the reinforcement ledger, identical across classes; null when
        /// the ledger is empty, which is how the whole feature stays off the hot path by default.
        /// </summary>
        public double[]? ReinforcementWeights { get; }
    }

    /// <summary>
    /// Inverse-document-frequency weight for a term, per <see cref="NaiveBayesOptions.IdfMode"/>:
    /// <c>DocumentCount</c> uses <c>log(1 + N / df)</c>, <c>ClassCount</c> uses
    /// <c>max(0, log(C / df))</c>, <c>None</c> returns 1. A term the corpus never saw carries no
    /// document frequency, so it is weighted as an unseen term: 1.0 under <c>DocumentCount</c> and
    /// under <c>None</c>, 0.0 under <c>ClassCount</c>.
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