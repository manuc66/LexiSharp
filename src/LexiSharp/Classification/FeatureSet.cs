using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>
/// What a feature set knows about one feature: it is present, it is known to be absent, or the
/// source could not determine it. A binary question answers no for both absent and unknown; the
/// state is what lets a source route an unknown item deliberately instead of silently.
/// </summary>
public enum FeatureState
{
    /// <summary>The feature is known not to apply.</summary>
    Absent,

    /// <summary>The feature applies.</summary>
    Present,

    /// <summary>The source could not determine whether the feature applies.</summary>
    Unknown,
}

/// <summary>
/// The features of one item, as a question sees them: a set of names, no domain.
/// </summary>
/// <remarks>
/// A feature is an opaque, non-empty string. What it means — a term, a time bucket, a transition,
/// a process name, the fact that an item is generic or excluded — is decided entirely by the
/// <see cref="IFeatureSource{TItem}"/> that produced it. The classifier only asks whether a name
/// is present, and, when the name carries one, what its numeric value is.
/// </remarks>
public interface IFeatureSet
{
    /// <summary>Whether the set contains <paramref name="feature"/>.</summary>
    /// <param name="feature">The feature name to test.</param>
    /// <returns><c>true</c> when the feature is present.</returns>
    bool Contains(string feature);

    /// <summary>
    /// The numeric value attached to <paramref name="feature"/>, or null when the feature is
    /// absent or carries no value. A <see cref="double.NaN"/> value is treated as missing by the
    /// threshold question.
    /// </summary>
    /// <param name="feature">The feature name to read.</param>
    /// <returns>The value, or null.</returns>
    double? NumericValue(string feature);

    /// <summary>The feature names in the set. Order is not part of the contract.</summary>
    IReadOnlyCollection<string> Features { get; }

    /// <summary>
    /// What the set knows about <paramref name="feature"/>. <see cref="FeatureState.Present"/>
    /// implies <see cref="Contains"/>; <see cref="FeatureState.Unknown"/> does not.
    /// </summary>
    /// <param name="feature">The feature name to inspect.</param>
    /// <returns>The state of the feature.</returns>
    FeatureState State(string feature);

    /// <summary>The features the source could not determine, in no particular order.</summary>
    IReadOnlyCollection<string> UnknownFeatures { get; }
}

/// <summary>
/// The default <see cref="IFeatureSet"/>: a set of names with ordinal comparison, optionally with
/// a numeric value attached to some of them.
/// </summary>
/// <remarks>
/// Small sets — a text document's terms, typically — are stored as a deduplicated array and tested
/// with a linear scan; that is cheaper than a <see cref="HashSet{T}"/> for the sizes a document
/// produces, and it is what keeps the per-document allocation down in a tree build. A set above
/// <see cref="SmallSetThreshold"/> switches to a hash set, where the linear scan would stop paying.
/// </remarks>
public sealed class FeatureSet : IFeatureSet
{
    private const int SmallSetThreshold = 64;

    private readonly string[]? _small;
    private readonly HashSet<string>? _large;
    private readonly Dictionary<string, double>? _numeric;
    private readonly HashSet<string>? _unknown;

    /// <summary>Creates a binary feature set from names; duplicates collapse.</summary>
    /// <param name="features">The feature names. Each must be non-empty.</param>
    public FeatureSet(IEnumerable<string> features)
        : this(features, numericValues: null, unknownFeatures: null)
    {
    }

    /// <summary>
    /// Creates a feature set from names and, optionally, the numeric value of some of them. A
    /// numeric key is a feature too: it is added to the set even when it is absent from
    /// <paramref name="features"/>.
    /// </summary>
    /// <param name="features">The binary feature names. Each must be non-empty.</param>
    /// <param name="numericValues">The numeric values, keyed by feature name; null for a binary set.</param>
    public FeatureSet(
        IEnumerable<string> features,
        IReadOnlyDictionary<string, double>? numericValues)
        : this(features, numericValues, unknownFeatures: null)
    {
    }

    /// <summary>
    /// Creates a feature set from names, numeric values and the features the source could not
    /// determine. A feature cannot be both present and unknown.
    /// </summary>
    /// <param name="features">The present feature names. Each must be non-empty.</param>
    /// <param name="numericValues">The numeric values, keyed by feature name; null for a binary set.</param>
    /// <param name="unknownFeatures">The features whose state is unknown; null when every feature is decided.</param>
    public FeatureSet(
        IEnumerable<string> features,
        IReadOnlyDictionary<string, double>? numericValues,
        IEnumerable<string>? unknownFeatures)
    {
        ArgumentNullException.ThrowIfNull(features);

        IReadOnlyList<string> buffer = features as IReadOnlyList<string> ?? features.ToList();

        if (numericValues is null || numericValues.Count == 0)
        {
            (_small, _large) = BuildNames(buffer);
        }
        else
        {
            var combined = new string[buffer.Count + numericValues.Count];
            int count = 0;

            foreach (string feature in buffer)
                combined[count++] = feature;

            foreach (string feature in numericValues.Keys)
                combined[count++] = feature;

            (_small, _large) = BuildNames(combined);

            _numeric = new Dictionary<string, double>(numericValues.Count, StringComparer.Ordinal);

            foreach (var pair in numericValues)
                _numeric[pair.Key] = pair.Value;
        }

        if (unknownFeatures is null)
            return;

        var unknown = new HashSet<string>(StringComparer.Ordinal);

        foreach (string feature in unknownFeatures)
        {
            if (string.IsNullOrEmpty(feature))
                throw new ArgumentException("Feature names must be non-empty strings.", nameof(unknownFeatures));

            if (Contains(feature))
                throw new ArgumentException("A feature cannot be both present and unknown.", nameof(unknownFeatures));

            unknown.Add(feature);
        }

        _unknown = unknown.Count == 0 ? null : unknown;
    }

    /// <summary>An empty feature set.</summary>
    public static FeatureSet Empty { get; } = new(Array.Empty<string>());

    /// <inheritdoc />
    public IReadOnlyCollection<string> Features => _large is not null ? _large : _small!;

    /// <inheritdoc />
    public bool Contains(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        if (_large is not null)
            return _large.Contains(feature);

        string[] small = _small!;

        for (int i = 0; i < small.Length; i++)
        {
            if (string.Equals(small[i], feature, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <inheritdoc />
    public double? NumericValue(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        return _numeric is not null && _numeric.TryGetValue(feature, out double value) ? value : null;
    }

    /// <inheritdoc />
    public FeatureState State(string feature)
    {
        ArgumentNullException.ThrowIfNull(feature);

        if (Contains(feature))
            return FeatureState.Present;

        return _unknown is not null && _unknown.Contains(feature)
            ? FeatureState.Unknown
            : FeatureState.Absent;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> UnknownFeatures =>
        _unknown is not null ? (IReadOnlyCollection<string>)_unknown : Array.Empty<string>();

    private static (string[]? Small, HashSet<string>? Large) BuildNames(IReadOnlyList<string> names)
    {
        if (names.Count <= SmallSetThreshold)
        {
            var small = new string[names.Count];
            int count = 0;

            foreach (string feature in names)
            {
                if (string.IsNullOrEmpty(feature))
                    throw new ArgumentException("Feature names must be non-empty strings.", nameof(names));

                bool duplicate = false;

                for (int i = 0; i < count; i++)
                {
                    if (string.Equals(small[i], feature, StringComparison.Ordinal))
                    {
                        duplicate = true;
                        break;
                    }
                }

                if (!duplicate)
                    small[count++] = feature;
            }

            return (count == small.Length ? small : small[..count], null);
        }

        var large = new HashSet<string>(names.Count, StringComparer.Ordinal);

        foreach (string feature in names)
        {
            if (string.IsNullOrEmpty(feature))
                throw new ArgumentException("Feature names must be non-empty strings.", nameof(names));

            large.Add(feature);
        }

        return (null, large);
    }
}

/// <summary>
/// Extracts the features of an item — the seam that keeps the question trees domain-agnostic.
/// </summary>
/// <remarks>
/// Implement this once per domain and compose it with <see cref="CompositeFeatureSource{TItem}"/>
/// when several dimensions have to count together. Every dimension is expressed the same way: a
/// name the caller chose, with an optional numeric value. A time is a bucket name or a number, a
/// transition is a pair name, a process is an identifier, and a derived flag is simply another
/// name.
/// </remarks>
/// <typeparam name="TItem">The item type the classifier or divider is built over.</typeparam>
public interface IFeatureSource<in TItem>
{
    /// <summary>Returns the features of one item.</summary>
    /// <param name="item">The item to reduce.</param>
    /// <returns>The item's features.</returns>
    IFeatureSet FeaturesOf(TItem item);
}

/// <summary>
/// The text feature space: the normalized terms of <see cref="SearchDocument.Text"/>.
/// </summary>
/// <remarks>
/// This is the default source behind the non-generic <see cref="DecisionTreeClassifier"/> and
/// <see cref="CorpusDivider"/>. Use it directly when text is one dimension among several and the
/// other dimensions come from another source.
/// </remarks>
public sealed class TextFeatureSource : IFeatureSource<SearchDocument>
{
    private readonly ITokenizer _tokenizer;

    /// <param name="tokenizer">Tokenizer used to reduce text to terms (default: <see cref="Tokenizer.Default"/>).</param>
    public TextFeatureSource(ITokenizer? tokenizer = null) => _tokenizer = tokenizer ?? Tokenizer.Default;

    /// <inheritdoc />
    public IFeatureSet FeaturesOf(SearchDocument item)
    {
        ArgumentNullException.ThrowIfNull(item);

        return new FeatureSet(_tokenizer.Tokenize(item.Text));
    }
}

/// <summary>
/// Combines several feature sources by union: an item carries every feature any source reports.
/// </summary>
/// <remarks>
/// The composition point for multi-dimensional models — text plus derived flags, text plus
/// bucketed time, several independent encodings of the same event. A source cannot remove a
/// feature another source added; if two dimensions collide on a name, they are the same feature
/// and the later source's numeric value wins.
/// </remarks>
/// <typeparam name="TItem">The item type the sources read.</typeparam>
public sealed class CompositeFeatureSource<TItem> : IFeatureSource<TItem>
{
    private readonly IReadOnlyList<IFeatureSource<TItem>> _sources;

    /// <summary>Creates a source that unions the features of <paramref name="sources"/>.</summary>
    /// <param name="sources">At least one feature source.</param>
    public CompositeFeatureSource(params IFeatureSource<TItem>[] sources)
    {
        ArgumentNullException.ThrowIfNull(sources);

        if (sources.Length == 0)
            throw new ArgumentException("At least one feature source is required.", nameof(sources));

        foreach (var source in sources)
            ArgumentNullException.ThrowIfNull(source);

        // Copied so a caller mutating the array it passed cannot change the model's feature space.
        _sources = [.. sources];
    }

    /// <inheritdoc />
    public IFeatureSet FeaturesOf(TItem item)
    {
        var union = new HashSet<string>(StringComparer.Ordinal);
        var unknown = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, double>? numeric = null;

        foreach (var source in _sources)
        {
            IFeatureSet features = source.FeaturesOf(item);

            foreach (string feature in features.Features)
            {
                union.Add(feature);

                if (features.NumericValue(feature) is not { } value)
                    continue;

                numeric ??= new Dictionary<string, double>(StringComparer.Ordinal);
                numeric[feature] = value;
            }

            foreach (string feature in features.UnknownFeatures)
                unknown.Add(feature);
        }

        // A source that knows the feature wins over one that could not decide it.
        foreach (string feature in union)
            unknown.Remove(feature);

        return new FeatureSet(union, numeric, unknown);
    }
}

/// <summary>
/// Suppresses an inner feature source for the items a mask predicate selects, and reports the
/// mask's own features instead.
/// </summary>
/// <remarks>
/// This is the generic « excluded » / « generic » seam: the caller decides what the mask means,
/// the library only knows that when it is true the inner dimensions are not trusted and the mask
/// features are. It is not a filter over individual features — a masked item carries the mask
/// features and nothing from the inner source, so the tree learns a separate branch rather than
/// a split that mixes a generic item's text with its flag.
/// </remarks>
/// <typeparam name="TItem">The item type the sources read.</typeparam>
public sealed class MaskedFeatureSource<TItem> : IFeatureSource<TItem>
{
    private readonly IFeatureSource<TItem> _inner;
    private readonly Func<TItem, bool> _isMasked;
    private readonly string[] _maskFeatures;

    /// <summary>Creates a source that replaces the inner features with the mask's when masked.</summary>
    /// <param name="inner">The source whose features are suppressed.</param>
    /// <param name="isMasked">Returns true for an item whose inner features must not be used.</param>
    /// <param name="maskFeatures">The feature names reported for a masked item. Each must be non-empty.</param>
    public MaskedFeatureSource(
        IFeatureSource<TItem> inner,
        Func<TItem, bool> isMasked,
        params string[] maskFeatures)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(isMasked);
        ArgumentNullException.ThrowIfNull(maskFeatures);

        foreach (string feature in maskFeatures)
        {
            if (string.IsNullOrEmpty(feature))
                throw new ArgumentException("Feature names must be non-empty strings.", nameof(maskFeatures));
        }

        _inner = inner;
        _isMasked = isMasked;
        _maskFeatures = [.. maskFeatures];
    }

    /// <inheritdoc />
    public IFeatureSet FeaturesOf(TItem item) =>
        _isMasked(item) ? new FeatureSet(_maskFeatures) : _inner.FeaturesOf(item);
}

/// <summary>
/// A feature source built from a delegate, for the one-off projection that does not deserve a
/// class.
/// </summary>
/// <typeparam name="TItem">The item type the delegate reads.</typeparam>
public sealed class DelegateFeatureSource<TItem> : IFeatureSource<TItem>
{
    private readonly Func<TItem, IEnumerable<string>> _features;

    /// <summary>Creates a source from a projection.</summary>
    /// <param name="features">Returns the feature names of one item.</param>
    public DelegateFeatureSource(Func<TItem, IEnumerable<string>> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        _features = features;
    }

    /// <inheritdoc />
    public IFeatureSet FeaturesOf(TItem item)
    {
        IEnumerable<string> features = _features(item);

        ArgumentNullException.ThrowIfNull(features);

        return new FeatureSet(features);
    }
}
