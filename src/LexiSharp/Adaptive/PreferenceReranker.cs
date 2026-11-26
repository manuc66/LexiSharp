using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Adaptive;

/// <summary>One candidate as the reranker sees it: the query, the document and its base rank.</summary>
/// <param name="Query">The query text.</param>
/// <param name="ContextKey">The caller's context key.</param>
/// <param name="DocumentId">The candidate document id.</param>
/// <param name="DocumentText">The candidate document text.</param>
/// <param name="Rank">The candidate's zero-based rank in the base list.</param>
public sealed record RerankItem(
    string Query,
    string ContextKey,
    string DocumentId,
    string DocumentText,
    int Rank);

/// <summary>The default query–document features: query terms, document terms, context and base rank.</summary>
public sealed class RerankFeatureSource : IFeatureSource<RerankItem>
{
    private readonly ITokenizer _tokenizer;

    /// <summary>Creates the source.</summary>
    /// <param name="tokenizer">The tokenizer; null uses <see cref="Tokenizer.Default"/>.</param>
    public RerankFeatureSource(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public IFeatureSet FeaturesOf(RerankItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var features = new HashSet<string>(StringComparer.Ordinal);

        foreach (string token in _tokenizer.Tokenize(item.Query))
        {
            features.Add("q:" + token);
        }

        foreach (string token in _tokenizer.Tokenize(item.DocumentText))
        {
            features.Add("d:" + token);
            features.Add("cx:" + item.ContextKey + "|d:" + token);
        }

        features.Add("c:" + item.ContextKey);
        features.Add("r:" + Math.Clamp(item.Rank, 0, 9));

        return new FeatureSet(features);
    }
}

/// <summary>
/// A learned re-ranker over a shortlist: one shared weight vector over query–document features,
/// trained from logged choices with a pairwise loss and exposed as an <see cref="IReranker"/>.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="PreferenceLearner"/>, which learns one weight vector per category, this
/// scorer is shared across candidates: it generalizes to documents the log never contained. The
/// base order is a feature (<c>r:</c>), so the model can learn to preserve or overturn it.
/// </para>
/// <para>
/// <see cref="Rerank(string, string, IReadOnlyList{SearchResult})"/> returns a new list, never extends it, and leaves the input untouched. Without
/// weights it returns the candidate list unchanged. The feature source and the document resolver are
/// runtime configuration, not snapshot data.
/// </para>
/// </remarks>
public sealed class PreferenceReranker : IReranker, IDisposable
{
    private const double ProbabilityFloor = 1e-12;
    private const double AdaGradEpsilon = 1e-8;

    private readonly Func<string, SearchDocument?>? _resolveDocument;
    private readonly IFeatureSource<RerankItem> _features;
    private readonly PreferenceOptions _options;
    private readonly Dictionary<string, int> _featureIds = new(StringComparer.Ordinal);
    private readonly List<string> _featureNames = [];
    private readonly Dictionary<int, double> _weights = [];
    private readonly Dictionary<int, double> _accumulators = [];
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    private bool _disposed;

    /// <summary>Creates a reranker.</summary>
    /// <param name="resolveDocument">Resolves a candidate id to its document for training; null uses an empty document text.</param>
    /// <param name="features">The feature source; null uses <see cref="RerankFeatureSource"/>.</param>
    /// <param name="options">The pairwise training options; null uses <see cref="PreferenceOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its contract.</exception>
    public PreferenceReranker(
        Func<string, SearchDocument?>? resolveDocument = null,
        IFeatureSource<RerankItem>? features = null,
        PreferenceOptions? options = null)
    {
        options ??= PreferenceOptions.Default;
        ValidateOptions(options);

        _resolveDocument = resolveDocument;
        _features = features ?? new RerankFeatureSource();
        _options = options;
    }

    /// <inheritdoc />
    public string Name => "Preference";

    /// <summary>Trains the shared scorer on logged choices.</summary>
    /// <param name="events">The events; the first <see cref="PreferenceOptions.MaxEvents"/> are used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is null.</exception>
    /// <exception cref="ArgumentException">An event is malformed.</exception>
    public void Train(IEnumerable<PreferenceEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();

            var rows = new List<Row>(Math.Min(_options.MaxEvents, 1024));

            foreach (PreferenceEvent preference in events)
            {
                if (rows.Count >= _options.MaxEvents)
                {
                    break;
                }

                rows.Add(Materialize(preference));
            }

            for (int epoch = 0; epoch < _options.Epochs; epoch++)
            {
                var order = new int[rows.Count];

                for (int i = 0; i < order.Length; i++)
                {
                    order[i] = i;
                }

                Shuffle(order, _options.Seed + epoch);

                foreach (int index in order)
                {
                    Update(rows[index]);
                }
            }
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>Folds one logged choice into the scorer.</summary>
    /// <param name="preference">The choice to learn.</param>
    /// <exception cref="ArgumentNullException"><paramref name="preference"/> is null.</exception>
    /// <exception cref="ArgumentException">The event is malformed.</exception>
    public void Observe(PreferenceEvent preference)
    {
        _lock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            Update(Materialize(preference));
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The <see cref="IReranker"/> contract has no context, so this overload uses the empty context.
    /// Callers that have one should use
    /// <see cref="Rerank(string, string, IReadOnlyList{SearchResult})"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="candidates"/> is null.</exception>
    public IReadOnlyList<SearchResult> Rerank(
        string query,
        IReadOnlyList<SearchResult> candidates) =>
        Rerank(query, string.Empty, candidates);

    /// <summary>Re-ranks a candidate list with an explicit context key.</summary>
    /// <param name="query">The query text.</param>
    /// <param name="contextKey">The context the choice was made in; empty means none.</param>
    /// <param name="candidates">Candidates as produced by a search, ordered best-first.</param>
    /// <returns>The re-ranked list; the input is not mutated and no document is added.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/>, <paramref name="contextKey"/> or <paramref name="candidates"/> is null.</exception>
    public IReadOnlyList<SearchResult> Rerank(
        string query,
        string contextKey,
        IReadOnlyList<SearchResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(contextKey);
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            return [];
        }

        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            if (_weights.Count == 0)
            {
                return candidates.ToList();
            }

            var reranked = new List<SearchResult>(candidates.Count);

            for (int i = 0; i < candidates.Count; i++)
            {
                SearchResult candidate = candidates[i];
                double linear = Math.Clamp(
                    Dot(FeatureIds(
                        new RerankItem(query, contextKey, candidate.DocumentId, candidate.Document.Text, i),
                        add: false)),
                    -50,
                    50);

                reranked.Add(new SearchResult(
                    candidate.DocumentId,
                    1.0 / (1.0 + Math.Exp(-linear)),
                    candidate.Document));
            }

            return reranked
                .OrderByDescending(candidate => candidate.Score)
                .ToList();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Projects the shared weights into the common preference snapshot.</summary>
    /// <param name="now">The instant the snapshot is taken at; it is recorded for the caller.</param>
    /// <returns>A snapshot with one special category, <c>"*"</c>, holding the shared vector.</returns>
    public PreferenceSnapshot Save(DateTimeOffset now)
    {
        _lock.EnterReadLock();
        try
        {
            ThrowIfDisposed();

            var weights = new List<PreferenceWeightSnapshot>(_weights.Count);

            foreach ((int index, double weight) in _weights.OrderBy(pair => pair.Key))
            {
                weights.Add(new PreferenceWeightSnapshot(
                    _options.HashFeatures ? null : _featureNames[index],
                    _options.HashFeatures ? index : -1,
                    weight));
            }

            var categories = new List<PreferenceCategorySnapshot>
            {
                new("*", weights),
            };

            string signature = PreferenceLearner.SignatureOf(
                _options,
                _options.HashFeatures,
                _featureNames,
                categories);

            return new PreferenceSnapshot(
                PreferenceSnapshot.CurrentVersion,
                _options,
                _options.HashFeatures,
                _options.HashFeatures ? [] : _featureNames.ToList(),
                categories,
                signature);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>Restores a reranker from a shared-weight preference snapshot.</summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <param name="resolveDocument">Resolves a candidate id to its document for training.</param>
    /// <param name="features">The feature source; null uses <see cref="RerankFeatureSource"/>.</param>
    /// <returns>A reranker with the snapshot's weights.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidDataException">The version, signature or body is invalid.</exception>
    public static PreferenceReranker Load(
        PreferenceSnapshot snapshot,
        Func<string, SearchDocument?>? resolveDocument = null,
        IFeatureSource<RerankItem>? features = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion != PreferenceSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported preference snapshot version {snapshot.SchemaVersion}; " +
                $"this build reads {PreferenceSnapshot.CurrentVersion}.");
        }

        string signature = PreferenceLearner.SignatureOf(
            snapshot.Options,
            snapshot.Hashed,
            snapshot.Features,
            snapshot.Categories);

        if (!string.Equals(signature, snapshot.Signature, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The preference snapshot signature does not match its body.");
        }

        var reranker = new PreferenceReranker(resolveDocument, features, snapshot.Options);

        if (!snapshot.Hashed)
        {
            if (snapshot.Features.Count > snapshot.Options.MaxFeatures)
            {
                throw new InvalidDataException("The preference snapshot carries too many features.");
            }

            for (int i = 0; i < snapshot.Features.Count; i++)
            {
                reranker._featureIds.Add(snapshot.Features[i], i);
                reranker._featureNames.Add(snapshot.Features[i]);
            }
        }

        var sharedCategories = snapshot.Categories
            .Where(category => string.Equals(category.Category, "*", StringComparison.Ordinal))
            .ToList();

        if (sharedCategories.Count != 1)
        {
            throw new InvalidDataException("The preference snapshot must have exactly one shared '*' category.");
        }

        PreferenceCategorySnapshot shared = sharedCategories[0];

        foreach (PreferenceWeightSnapshot row in shared.Weights)
        {
            if (!double.IsFinite(row.Weight))
            {
                throw new InvalidDataException("The preference snapshot carries a non-finite weight.");
            }

            int index;

            if (snapshot.Hashed)
            {
                if (row.Index < 0 || row.Index >= snapshot.Options.HashDimension)
                {
                    throw new InvalidDataException("The preference snapshot carries an invalid feature index.");
                }

                index = row.Index;
            }
            else if (row.Feature is null
                     || !reranker._featureIds.TryGetValue(row.Feature, out index))
            {
                throw new InvalidDataException("The preference snapshot references an unknown feature.");
            }

            reranker._weights[index] = row.Weight;
        }

        return reranker;
    }

    /// <summary>Disposes the reranker's lock.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lock.Dispose();
    }

    private Row Materialize(PreferenceEvent preference)
    {
        ArgumentNullException.ThrowIfNull(preference);
        ValidateCandidates(preference.Candidates);

        var categories = new List<string>(preference.Candidates.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (ClassificationResult candidate in preference.Candidates)
        {
            if (!seen.Add(candidate.Category))
            {
                throw new ArgumentException("A candidate appears more than once.", nameof(preference));
            }

            categories.Add(candidate.Category);
        }

        if (!seen.Contains(preference.Chosen))
        {
            throw new ArgumentException("The chosen category is not among the candidates.", nameof(preference));
        }

        if (preference.Propensity is { } propensity
            && (!double.IsFinite(propensity) || propensity <= 0 || propensity > 1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(preference),
                propensity,
                "Propensity must be in (0, 1].");
        }

        var features = new List<IReadOnlyList<int>>(categories.Count);

        for (int i = 0; i < categories.Count; i++)
        {
            SearchDocument? document = _resolveDocument?.Invoke(categories[i]);

            features.Add(FeatureIds(
                new RerankItem(
                    preference.Text,
                    preference.ContextKey,
                    categories[i],
                    document?.Text ?? string.Empty,
                    i),
                add: true));
        }

        int chosenIndex = categories.IndexOf(preference.Chosen);

        return new Row(features, chosenIndex);
    }

    private void Update(Row row)
    {
        if (row.Features.Count < 2)
        {
            return;
        }

        IReadOnlyList<int> chosen = row.Features[row.ChosenIndex];

        for (int i = 0; i < row.Features.Count; i++)
        {
            if (i == row.ChosenIndex)
            {
                continue;
            }

            IReadOnlyList<int> rejected = row.Features[i];
            double delta = Dot(chosen) - Dot(rejected);
            double gradient = 1 - Sigmoid(delta);

            foreach (int feature in chosen)
            {
                Apply(feature, gradient);
            }

            foreach (int feature in rejected)
            {
                Apply(feature, -gradient);
            }
        }
    }

    private void Apply(int feature, double gradient)
    {
        if (gradient == 0)
        {
            return;
        }

        _accumulators.TryGetValue(feature, out double accumulated);
        accumulated += gradient * gradient;
        _accumulators[feature] = accumulated;

        _weights.TryGetValue(feature, out double weight);
        weight += (_options.LearningRate * gradient) / Math.Sqrt(accumulated + AdaGradEpsilon);
        weight -= _options.LearningRate * _options.L2 * weight;

        if (Math.Abs(weight) < 1e-12)
        {
            _weights.Remove(feature);
        }
        else
        {
            _weights[feature] = weight;
        }
    }

    private IReadOnlyList<int> FeatureIds(RerankItem item, bool add)
    {
        IFeatureSet featureSet = _features.FeaturesOf(item);
        var ids = new List<int>(featureSet.Features.Count);
        var seen = new HashSet<int>();

        foreach (string feature in featureSet.Features)
        {
            int id = FeatureId(feature, add);

            if (id >= 0 && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private int FeatureId(string feature, bool add)
    {
        if (_options.HashFeatures)
        {
            return (int)(Fnv1a64(feature) % (ulong)_options.HashDimension);
        }

        if (_featureIds.TryGetValue(feature, out int existing))
        {
            return existing;
        }

        if (!add || _featureIds.Count >= _options.MaxFeatures)
        {
            return -1;
        }

        int id = _featureNames.Count;
        _featureIds.Add(feature, id);
        _featureNames.Add(feature);

        return id;
    }

    private double Dot(IReadOnlyList<int> features)
    {
        double sum = 0;

        foreach (int feature in features)
        {
            if (_weights.TryGetValue(feature, out double weight))
            {
                sum += weight;
            }
        }

        return sum;
    }

    private static void ValidateCandidates(IReadOnlyList<ClassificationResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        if (candidates.Count == 0)
        {
            throw new ArgumentException("The candidate list cannot be empty.", nameof(candidates));
        }

        double sum = 0;

        foreach (ClassificationResult candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);

            if (!double.IsFinite(candidate.Probability) || candidate.Probability is < 0 or > 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(candidates),
                    candidate.Probability,
                    "Probabilities must be in [0, 1].");
            }

            sum += candidate.Probability;
        }

        if (Math.Abs(sum - 1.0) > 1e-6)
        {
            throw new ArgumentException(
                $"The candidates must sum to 1; they sum to {sum}.",
                nameof(candidates));
        }
    }

    private static void ValidateOptions(PreferenceOptions options)
    {
        if (options.Epochs < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Epochs, "Epochs must be at least 1.");
        }

        if (!double.IsFinite(options.LearningRate) || options.LearningRate <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.LearningRate,
                "LearningRate must be finite and positive.");
        }

        if (!double.IsFinite(options.L2) || options.L2 < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.L2, "L2 must be finite and non-negative.");
        }

        if (options.MaxFeatures < 1 || options.MaxEvents < 1 || options.HashDimension < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Caps and HashDimension must be positive.");
        }

        if (options.Seed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Seed, "Seed must be non-negative.");
        }
    }

    private static void Shuffle(int[] values, int seed)
    {
        var random = new Random(seed);

        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static double Sigmoid(double value) =>
        value >= 0
            ? 1.0 / (1.0 + Math.Exp(-value))
            : Math.Exp(value) / (1.0 + Math.Exp(value));

    private static ulong Fnv1a64(string value)
    {
        ulong hash = 14695981039346656037UL;

        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
    }

    private readonly record struct Row(
        IReadOnlyList<IReadOnlyList<int>> Features,
        int ChosenIndex);
}
