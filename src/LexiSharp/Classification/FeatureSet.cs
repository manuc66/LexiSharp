using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Classification;

/// <summary>
/// The features of one item, as a question sees them: a set of names, no domain.
/// </summary>
/// <remarks>
/// A feature is an opaque, non-empty string. What it means — a term, a time bucket, a transition,
/// a process name, the fact that an item is generic or excluded — is decided entirely by the
/// <see cref="IFeatureSource{TItem}"/> that produced it. The classifier only asks whether a name
/// is present.
/// </remarks>
public interface IFeatureSet
{
    /// <summary>Whether the set contains <paramref name="feature"/>.</summary>
    /// <param name="feature">The feature name to test.</param>
    /// <returns><c>true</c> when the feature is present.</returns>
    bool Contains(string feature);

    /// <summary>The feature names in the set. Order is not part of the contract.</summary>
    IReadOnlyCollection<string> Features { get; }
}

/// <summary>
/// The default <see cref="IFeatureSet"/>: a set of names with ordinal comparison.
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

    /// <summary>Creates a feature set from names; duplicates collapse.</summary>
    /// <param name="features">The feature names. Each must be non-empty.</param>
    public FeatureSet(IEnumerable<string> features)
    {
        ArgumentNullException.ThrowIfNull(features);

        // A tokenizer already hands us a list; materializing only when it is not one avoids a
        // second copy of the document's terms.
        IReadOnlyList<string> buffer = features as IReadOnlyList<string> ?? features.ToList();

        if (buffer.Count <= SmallSetThreshold)
        {
            var small = new string[buffer.Count];
            int count = 0;

            foreach (string feature in buffer)
            {
                if (string.IsNullOrEmpty(feature))
                    throw new ArgumentException("Feature names must be non-empty strings.", nameof(features));

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

            _small = count == small.Length ? small : small[..count];
            return;
        }

        var large = new HashSet<string>(buffer.Count, StringComparer.Ordinal);

        foreach (string feature in buffer)
        {
            if (string.IsNullOrEmpty(feature))
                throw new ArgumentException("Feature names must be non-empty strings.", nameof(features));

            large.Add(feature);
        }

        _large = large;
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
}

/// <summary>
/// Extracts the features of an item — the seam that keeps the question trees domain-agnostic.
/// </summary>
/// <remarks>
/// Implement this once per domain and compose it with <see cref="CompositeFeatureSource{TItem}"/>
/// when several dimensions have to count together. Every dimension is expressed the same way: a
/// name the caller chose. A time is a bucket name, a transition is a pair name, a process is an
/// identifier, and a derived flag is simply another name.
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
/// feature another source added; if two dimensions collide on a name, they are the same feature.
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

        foreach (var source in _sources)
        {
            foreach (string feature in source.FeaturesOf(item).Features)
                union.Add(feature);
        }

        return new FeatureSet(union);
    }
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
