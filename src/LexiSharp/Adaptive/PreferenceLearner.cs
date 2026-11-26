using System.Security.Cryptography;
using System.Text;

using LexiSharp.Classification;
using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Adaptive;

/// <summary>One logged choice among ranked candidates.</summary>
/// <param name="Text">The text the candidates were for.</param>
/// <param name="ContextKey">The caller's context key.</param>
/// <param name="Candidates">The logged distribution, ordered best-first.</param>
/// <param name="Chosen">The category the user chose; it must be in <paramref name="Candidates"/>.</param>
/// <param name="At">The instant of the choice.</param>
/// <param name="Propensity">The logging policy's probability of <paramref name="Chosen"/>; optional, for IPS.</param>
public sealed record PreferenceEvent(
    string Text,
    string ContextKey,
    IReadOnlyList<ClassificationResult> Candidates,
    string Chosen,
    DateTimeOffset At,
    double? Propensity = null);

/// <summary>One candidate and the text/context it is scored against.</summary>
/// <param name="Text">The text.</param>
/// <param name="ContextKey">The context key.</param>
/// <param name="Category">The candidate category; the default feature source ignores it.</param>
public sealed record PreferenceItem(
    string Text,
    string ContextKey,
    string Category);

/// <summary>Options of the pairwise preference learner.</summary>
public sealed record PreferenceOptions
{
    /// <summary>The default options: 10 epochs, learning rate 0.1, L2 1e-4, seed 7.</summary>
    public static PreferenceOptions Default { get; } = new();

    /// <summary>Training passes over the events. Default: <c>10</c>.</summary>
    public int Epochs { get; init; } = 10;

    /// <summary>The AdaGrad base step. Default: <c>0.1</c>.</summary>
    public double LearningRate { get; init; } = 0.1;

    /// <summary>The L2 penalty. Default: <c>1e-4</c>.</summary>
    public double L2 { get; init; } = 1e-4;

    /// <summary>The shuffle seed. Default: <c>7</c>.</summary>
    public int Seed { get; init; } = 7;

    /// <summary>Maximum vocabulary size in non-hashed mode. Default: <c>50_000</c>.</summary>
    public int MaxFeatures { get; init; } = 50_000;

    /// <summary>Maximum events in one training pass. Default: <c>100_000</c>.</summary>
    public int MaxEvents { get; init; } = 100_000;

    /// <summary>Whether features are hashed into a fixed space instead of a vocabulary. Default: <c>false</c>.</summary>
    public bool HashFeatures { get; init; }

    /// <summary>The hashed feature dimension; ignored when <see cref="HashFeatures"/> is false. Default: <c>65_536</c>.</summary>
    public int HashDimension { get; init; } = 1 << 16;
}

/// <summary>A re-ranker learned from relative choices.</summary>
public interface IPreferencePolicy
{
    /// <summary>Re-ranks a candidate distribution.</summary>
    /// <param name="text">The text the candidates are for.</param>
    /// <param name="contextKey">The caller's context key.</param>
    /// <param name="candidates">The candidate distribution.</param>
    IReadOnlyList<ClassificationResult> ReRank(
        string text,
        string contextKey,
        IReadOnlyList<ClassificationResult> candidates);
}

/// <summary>The default feature source: text unigrams plus one context feature.</summary>
public sealed class TextPreferenceFeatureSource : IFeatureSource<PreferenceItem>
{
    private readonly ITokenizer _tokenizer;

    /// <summary>Creates the source.</summary>
    /// <param name="tokenizer">The tokenizer; null uses <see cref="Tokenizer.Default"/>.</param>
    public TextPreferenceFeatureSource(ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? Tokenizer.Default;
    }

    /// <inheritdoc />
    public IFeatureSet FeaturesOf(PreferenceItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var features = new HashSet<string>(StringComparer.Ordinal);

        foreach (string token in _tokenizer.Tokenize(item.Text))
        {
            features.Add("t:" + token);
        }

        features.Add("c:" + item.ContextKey);

        return new FeatureSet(features);
    }
}

/// <summary>One weight in a persisted preference model.</summary>
/// <param name="Feature">The feature name in vocabulary mode; null in hashed mode.</param>
/// <param name="Index">The feature index in hashed mode; <c>-1</c> in vocabulary mode.</param>
/// <param name="Weight">The learned weight.</param>
public sealed record PreferenceWeightSnapshot(
    string? Feature,
    int Index,
    double Weight);

/// <summary>One category's weights in a persisted preference model.</summary>
/// <param name="Category">The category.</param>
/// <param name="Weights">The weights, sorted by feature or index.</param>
public sealed record PreferenceCategorySnapshot(
    string Category,
    IReadOnlyList<PreferenceWeightSnapshot> Weights);

/// <summary>A versioned view of a <see cref="PreferenceLearner"/>.</summary>
/// <param name="SchemaVersion">The format version; only <see cref="CurrentVersion"/> is read.</param>
/// <param name="Options">The learner options.</param>
/// <param name="Hashed">Whether features are hashed.</param>
/// <param name="Features">The vocabulary in non-hashed mode; empty otherwise.</param>
/// <param name="Categories">The category weight vectors, sorted by category.</param>
/// <param name="Signature">SHA-256 of the canonical body.</param>
public sealed record PreferenceSnapshot(
    int SchemaVersion,
    PreferenceOptions Options,
    bool Hashed,
    IReadOnlyList<string> Features,
    IReadOnlyList<PreferenceCategorySnapshot> Categories,
    string Signature)
{
    /// <summary>The only preference-snapshot version this build reads.</summary>
    public const int CurrentVersion = 1;
}

/// <summary>
/// A linear pairwise re-ranker trained from logged choices: the chosen category is pushed above each
/// rejected one, with AdaGrad and a seeded shuffle.
/// </summary>
/// <remarks>
/// <para>
/// The model is one sparse weight vector per category over <c>φ(text, context)</c>; the score is
/// <c>log p(c) + w_c · φ</c>, normalized. It never introduces a category and, without weights, it
/// returns the candidate distribution unchanged.
/// </para>
/// <para>
/// <see cref="Train"/> is the deterministic batch reference; <see cref="Observe"/> is the online
/// convenience. The feature source is not stored in a snapshot, like the tokenizer in
/// <see cref="ClassificationModelSerializer"/>: load with the same configuration.
/// </para>
/// </remarks>
public sealed class PreferenceLearner : IPreferencePolicy
{
    private const double ProbabilityFloor = 1e-12;
    private const double AdaGradEpsilon = 1e-8;

    private readonly IFeatureSource<PreferenceItem> _features;
    private readonly PreferenceOptions _options;
    private readonly Dictionary<string, int> _featureIds = new(StringComparer.Ordinal);
    private readonly List<string> _featureNames = [];
    private readonly Dictionary<string, Dictionary<int, double>> _weights = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<int, double>> _accumulators = new(StringComparer.Ordinal);

    /// <summary>Creates a learner.</summary>
    /// <param name="features">The feature source; null uses <see cref="TextPreferenceFeatureSource"/>.</param>
    /// <param name="options">The training options; null uses <see cref="PreferenceOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is outside its contract.</exception>
    public PreferenceLearner(
        IFeatureSource<PreferenceItem>? features = null,
        PreferenceOptions? options = null)
    {
        options ??= PreferenceOptions.Default;
        ValidateOptions(options);

        _features = features ?? new TextPreferenceFeatureSource();
        _options = options;
    }

    /// <summary>Trains the pairwise model on logged choices.</summary>
    /// <param name="events">The events; the first <see cref="PreferenceOptions.MaxEvents"/> are used.</param>
    /// <exception cref="ArgumentNullException"><paramref name="events"/> is null.</exception>
    /// <exception cref="ArgumentException">An event is malformed.</exception>
    public void Train(IEnumerable<PreferenceEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

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
                Update(rows[index], _options.LearningRate);
            }
        }
    }

    /// <summary>Folds one logged choice into the model.</summary>
    /// <param name="preference">The choice to learn.</param>
    /// <exception cref="ArgumentNullException"><paramref name="preference"/> is null.</exception>
    /// <exception cref="ArgumentException">The event is malformed.</exception>
    public void Observe(PreferenceEvent preference)
    {
        Update(Materialize(preference), _options.LearningRate);
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentNullException"><paramref name="text"/>, <paramref name="contextKey"/> or <paramref name="candidates"/> is null.</exception>
    /// <exception cref="ArgumentException">The candidates are empty, duplicated or not normalized.</exception>
    public IReadOnlyList<ClassificationResult> ReRank(
        string text,
        string contextKey,
        IReadOnlyList<ClassificationResult> candidates)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(contextKey);
        ValidateCandidates(candidates);

        if (_weights.Count == 0)
        {
            return candidates.ToList();
        }

        IReadOnlyList<int> features = FeatureIds(text, contextKey);
        var weighted = new List<ClassificationResult>(candidates.Count);
        double total = 0;

        foreach (ClassificationResult candidate in candidates)
        {
            double linear = Math.Clamp(Dot(candidate.Category, features), -50, 50);
            double value = Math.Max(candidate.Probability, ProbabilityFloor) * Math.Exp(linear);
            weighted.Add(new ClassificationResult(candidate.Category, value));
            total += value;
        }

        if (total <= 0)
        {
            return candidates.ToList();
        }

        var result = new List<ClassificationResult>(weighted.Count);

        foreach (ClassificationResult candidate in weighted)
        {
            result.Add(new ClassificationResult(candidate.Category, candidate.Probability / total));
        }

        result.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);

            return byProbability != 0
                ? byProbability
                : string.CompareOrdinal(left.Category, right.Category);
        });

        return result;
    }

    /// <summary>Projects the model into a versioned snapshot.</summary>
    /// <param name="now">The instant the snapshot is taken at; it is recorded for the caller.</param>
    /// <returns>The snapshot with a canonical signature.</returns>
    public PreferenceSnapshot Save(DateTimeOffset now)
    {
        var categories = new List<PreferenceCategorySnapshot>(_weights.Count);

        foreach ((string category, Dictionary<int, double> weights) in _weights
                     .OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var rows = new List<PreferenceWeightSnapshot>(weights.Count);

            foreach ((int index, double weight) in weights.OrderBy(pair => pair.Key))
            {
                string? feature = _options.HashFeatures ? null : _featureNames[index];
                rows.Add(new PreferenceWeightSnapshot(feature, _options.HashFeatures ? index : -1, weight));
            }

            categories.Add(new PreferenceCategorySnapshot(category, rows));
        }

        string signature = SignatureOf(_options, _options.HashFeatures, _featureNames, categories);

        return new PreferenceSnapshot(
            PreferenceSnapshot.CurrentVersion,
            _options,
            _options.HashFeatures,
            _options.HashFeatures ? [] : _featureNames.ToList(),
            categories,
            signature);
    }

    /// <summary>Restores a learner from a snapshot.</summary>
    /// <param name="snapshot">The snapshot to read.</param>
    /// <param name="features">The feature source; null uses <see cref="TextPreferenceFeatureSource"/>.</param>
    /// <returns>A learner with the snapshot's weights.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="snapshot"/> is null.</exception>
    /// <exception cref="InvalidDataException">The version, signature or a weight is invalid.</exception>
    public static PreferenceLearner Load(
        PreferenceSnapshot snapshot,
        IFeatureSource<PreferenceItem>? features = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.SchemaVersion != PreferenceSnapshot.CurrentVersion)
        {
            throw new InvalidDataException(
                $"Unsupported preference snapshot version {snapshot.SchemaVersion}; " +
                $"this build reads {PreferenceSnapshot.CurrentVersion}.");
        }

        ValidateOptions(snapshot.Options);

        string signature = SignatureOf(
            snapshot.Options,
            snapshot.Hashed,
            snapshot.Features,
            snapshot.Categories);

        if (!string.Equals(signature, snapshot.Signature, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The preference snapshot signature does not match its body.");
        }

        var learner = new PreferenceLearner(features, snapshot.Options);

        if (!snapshot.Hashed)
        {
            if (snapshot.Features.Count > snapshot.Options.MaxFeatures)
            {
                throw new InvalidDataException("The preference snapshot carries too many features.");
            }

            for (int i = 0; i < snapshot.Features.Count; i++)
            {
                learner._featureIds.Add(snapshot.Features[i], i);
                learner._featureNames.Add(snapshot.Features[i]);
            }
        }

        foreach (PreferenceCategorySnapshot category in snapshot.Categories)
        {
            var weights = new Dictionary<int, double>();

            foreach (PreferenceWeightSnapshot row in category.Weights)
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
                         || !learner._featureIds.TryGetValue(row.Feature, out index))
                {
                    throw new InvalidDataException("The preference snapshot references an unknown feature.");
                }

                weights[index] = row.Weight;
            }

            learner._weights[category.Category] = weights;
        }

        return learner;
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

        return new Row(FeatureIds(preference.Text, preference.ContextKey), categories, preference.Chosen);
    }

    private void Update(Row row, double learningRate)
    {
        if (row.Candidates.Count < 2)
        {
            return;
        }

        double chosenScore = Dot(row.Chosen, row.Features);

        foreach (string rejected in row.Candidates)
        {
            if (string.Equals(rejected, row.Chosen, StringComparison.Ordinal))
            {
                continue;
            }

            double delta = chosenScore - Dot(rejected, row.Features);
            double gradient = 1 - Sigmoid(delta);

            foreach (int feature in row.Features)
            {
                Apply(row.Chosen, feature, gradient, learningRate);
                Apply(rejected, feature, -gradient, learningRate);
            }
        }
    }

    private void Apply(string category, int feature, double gradient, double learningRate)
    {
        if (gradient == 0)
        {
            return;
        }

        Dictionary<int, double> weights = WeightsFor(category);
        Dictionary<int, double> accumulators = AccumulatorsFor(category);

        accumulators.TryGetValue(feature, out double accumulated);
        accumulated += gradient * gradient;
        accumulators[feature] = accumulated;

        weights.TryGetValue(feature, out double weight);
        weight += (learningRate * gradient) / Math.Sqrt(accumulated + AdaGradEpsilon);
        weight -= learningRate * _options.L2 * weight;

        if (Math.Abs(weight) < 1e-12)
        {
            weights.Remove(feature);
        }
        else
        {
            weights[feature] = weight;
        }
    }

    private IReadOnlyList<int> FeatureIds(string text, string contextKey)
    {
        IFeatureSet featureSet = _features.FeaturesOf(new PreferenceItem(text, contextKey, string.Empty));
        var ids = new List<int>(featureSet.Features.Count);
        var seen = new HashSet<int>();

        foreach (string feature in featureSet.Features)
        {
            int id = FeatureId(feature);

            if (id >= 0 && seen.Add(id))
            {
                ids.Add(id);
            }
        }

        return ids;
    }

    private int FeatureId(string feature)
    {
        if (_options.HashFeatures)
        {
            return (int)(Fnv1a64(feature) % (ulong)_options.HashDimension);
        }

        if (_featureIds.TryGetValue(feature, out int existing))
        {
            return existing;
        }

        if (_featureIds.Count >= _options.MaxFeatures)
        {
            return -1;
        }

        int id = _featureNames.Count;
        _featureIds.Add(feature, id);
        _featureNames.Add(feature);

        return id;
    }

    private double Dot(string category, IReadOnlyList<int> features)
    {
        if (!_weights.TryGetValue(category, out Dictionary<int, double>? weights))
        {
            return 0;
        }

        double sum = 0;

        foreach (int feature in features)
        {
            if (weights.TryGetValue(feature, out double weight))
            {
                sum += weight;
            }
        }

        return sum;
    }

    private Dictionary<int, double> WeightsFor(string category)
    {
        if (!_weights.TryGetValue(category, out Dictionary<int, double>? weights))
        {
            _weights[category] = weights = [];
        }

        return weights;
    }

    private Dictionary<int, double> AccumulatorsFor(string category)
    {
        if (!_accumulators.TryGetValue(category, out Dictionary<int, double>? accumulators))
        {
            _accumulators[category] = accumulators = [];
        }

        return accumulators;
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

    internal static string SignatureOf(
        PreferenceOptions options,
        bool hashed,
        IReadOnlyList<string> features,
        IReadOnlyList<PreferenceCategorySnapshot> categories)
    {
        var builder = new StringBuilder();
        builder.Append(options.Epochs).Append('|')
            .Append(options.LearningRate.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(options.L2.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).Append('|')
            .Append(options.Seed).Append('|')
            .Append(options.MaxFeatures).Append('|')
            .Append(options.MaxEvents).Append('|')
            .Append(hashed ? 1 : 0).Append('|')
            .Append(options.HashDimension).Append('\n');

        foreach (string feature in features)
        {
            builder.Append('f').Append(feature).Append('\n');
        }

        foreach (PreferenceCategorySnapshot category in categories)
        {
            builder.Append('c').Append(category.Category).Append('\n');

            foreach (PreferenceWeightSnapshot row in category.Weights)
            {
                builder.Append(row.Feature ?? "#").Append('|')
                    .Append(row.Index).Append('|')
                    .Append(row.Weight.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                    .Append('\n');
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(hash);
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

        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }

        return hash;
    }

    private readonly record struct Row(
        IReadOnlyList<int> Features,
        IReadOnlyList<string> Candidates,
        string Chosen);
}
