using LexiSharp.Core;
using LexiSharp.Hybrid;

namespace LexiSharp.Classification;

/// <summary>
/// K-nearest-neighbour classifier over caller-supplied vectors.
/// </summary>
/// <remarks>
/// Stores one vector per labelled item and ranks a query by cosine similarity, with a configurable
/// vote. Vectors are already computed by the caller, so no embedding provider is required. This is
/// the vector counterpart of the dense path of <c>NearestNeighborClassifier</c>.
/// </remarks>
/// <typeparam name="TItem">The item type; the caller supplies the vector and the label.</typeparam>
public sealed class NearestNeighborClassifier<TItem>
{
    private readonly Func<TItem, ReadOnlyMemory<float>> _vectorOf;
    private readonly Func<TItem, string?> _categoryOf;
    private readonly NearestNeighborOptions _options;

    private readonly List<Entry> _entries = new();
    private int _dimension;

    /// <summary>Creates a classifier over a caller-supplied vector source and label.</summary>
    /// <param name="vectorOf">Produces the embedding of one item. Every vector must have the same length.</param>
    /// <param name="categoryOf">Returns the item's category, or null/empty to skip it during training.</param>
    /// <param name="options">Tunable knobs; when null, <see cref="NearestNeighborOptions.Default"/> applies.</param>
    /// <exception cref="ArgumentNullException">A delegate is null.</exception>
    /// <exception cref="ArgumentException">The options are not valid.</exception>
    public NearestNeighborClassifier(
        Func<TItem, ReadOnlyMemory<float>> vectorOf,
        Func<TItem, string?> categoryOf,
        NearestNeighborOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(vectorOf);
        ArgumentNullException.ThrowIfNull(categoryOf);

        _vectorOf = vectorOf;
        _categoryOf = categoryOf;
        _options = options ?? NearestNeighborOptions.Default;

        if (!_options.IsValid)
            throw new ArgumentException("The nearest-neighbour options are not valid.", nameof(options));
    }

    /// <summary>The options this classifier was created with.</summary>
    public NearestNeighborOptions Options => _options;

    /// <summary>Vector length the model was trained on, or <c>0</c> before training.</summary>
    public int Dimension => _dimension;

    /// <summary>Number of stored items, or <c>0</c> before training.</summary>
    public int Count => _entries.Count;

    /// <summary>Trains the model on labelled items, replacing any previous model.</summary>
    /// <param name="items">The training items. Unlabelled items are skipped.</param>
    /// <exception cref="ArgumentNullException"><paramref name="items"/> is null or contains null.</exception>
    /// <exception cref="ArgumentException">A non-empty vector is non-finite or has a length different from the first one.</exception>
    public void Train(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        _entries.Clear();
        _dimension = 0;

        foreach (TItem item in items)
        {
            if (item is null)
                throw new ArgumentNullException(nameof(items), "The training sequence contains a null item.");

            string? category = _categoryOf(item);
            if (string.IsNullOrEmpty(category)) continue;

            ReadOnlyMemory<float> vector = _vectorOf(item);
            if (vector.Length == 0) continue;

            ValidateShape(vector);
            ValidateFinite(vector, category);

            _entries.Add(new Entry(category!, vector.ToArray(), VectorSimilarity.Norm(vector.Span)));
            if (_dimension == 0) _dimension = vector.Length;
        }
    }

    /// <summary>Adds one labelled item to the model.</summary>
    /// <param name="item">The labelled item to learn.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    /// <exception cref="ArgumentException">A non-empty vector is non-finite or has the wrong length.</exception>
    public void Learn(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string? category = _categoryOf(item);
        if (string.IsNullOrEmpty(category)) return;

        ReadOnlyMemory<float> vector = _vectorOf(item);
        if (vector.Length == 0) return;

        if (_dimension == 0) _dimension = vector.Length;
        else if (vector.Length != _dimension)
            throw new ArgumentException($"The vector has {vector.Length} dimensions but the model was trained on {_dimension}.", nameof(item));

        ValidateFinite(vector, category);
        _entries.Add(new Entry(category!, vector.ToArray(), VectorSimilarity.Norm(vector.Span)));
    }

    /// <summary>Removes the first stored item whose category and vector match.</summary>
    /// <param name="item">The item to retract.</param>
    /// <exception cref="ArgumentNullException"><paramref name="item"/> is null.</exception>
    public void Unlearn(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        string? category = _categoryOf(item);
        if (string.IsNullOrEmpty(category)) return;

        ReadOnlyMemory<float> vector = _vectorOf(item);
        if (vector.Length == 0) return;

        for (int i = 0; i < _entries.Count; i++)
        {
            if (string.Equals(_entries[i].Category, category, StringComparison.Ordinal)
                && _entries[i].Vector.AsSpan().SequenceEqual(vector.Span))
            {
                _entries.RemoveAt(i);
                return;
            }
        }
    }

    /// <summary>Predicts the most likely categories for a vector, by decreasing probability.</summary>
    /// <param name="vector">The vector to classify; its length must match the training vectors.</param>
    /// <param name="limit">Maximum number of categories to return; a non-positive value returns nothing.</param>
    /// <param name="excludedCategories">Optional set of categories to hide; probabilities are renormalized over the rest.</param>
    /// <exception cref="ArgumentException">A non-empty vector is non-finite or has the wrong length.</exception>
    public IReadOnlyList<ClassificationResult> Predict(
        ReadOnlyMemory<float> vector,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        if (limit <= 0 || _options.K <= 0 || _entries.Count == 0) return Array.Empty<ClassificationResult>();
        if (vector.Length == 0) return Array.Empty<ClassificationResult>();
        if (_dimension != 0 && vector.Length != _dimension)
            throw new ArgumentException($"The query vector has {vector.Length} dimensions but the model was trained on {_dimension}.", nameof(vector));

        ValidateFinite(vector, null);

        ReadOnlySpan<float> query = vector.Span;
        float queryNorm = VectorSimilarity.Norm(query);

        var neighbors = new List<(string Category, double Score)>(_entries.Count);
        foreach (Entry entry in _entries)
        {
            if (excludedCategories?.Contains(entry.Category) == true) continue;
            double cosine = VectorSimilarity.CosineSimilarity(query, entry.Vector, queryNorm, entry.Norm);
            neighbors.Add((entry.Category, cosine));
        }

        if (neighbors.Count == 0) return Array.Empty<ClassificationResult>();

        neighbors.Sort(static (a, b) =>
        {
            int byScore = b.Score.CompareTo(a.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(a.Category, b.Category);
        });

        int takeK = Math.Min(_options.K, neighbors.Count);
        var top = neighbors.GetRange(0, takeK);

        int n = top.Count;
        double[] weights = new double[n];
        double total = 0;

        switch (_options.Vote)
        {
            case NeighborVote.Rank:
                for (int i = 0; i < n; i++) { weights[i] = 1.0 / (i + 1); total += weights[i]; }
                break;
            case NeighborVote.Temperature:
                double highest = double.NegativeInfinity;
                for (int i = 0; i < n; i++) highest = Math.Max(highest, top[i].Score);
                for (int i = 0; i < n; i++) { weights[i] = Math.Exp((top[i].Score - highest) / _options.Temperature); total += weights[i]; }
                break;
            default:
                for (int i = 0; i < n; i++) { weights[i] = Math.Max(0, top[i].Score); total += weights[i]; }
                break;
        }

        if (total <= 0) { for (int i = 0; i < n; i++) weights[i] = 1.0; total = n; }

        var aggregated = new Dictionary<string, double>(StringComparer.Ordinal);
        for (int i = 0; i < n; i++)
        {
            double weighted = weights[i] / total;
            aggregated.TryGetValue(top[i].Category, out double existing);
            aggregated[top[i].Category] = existing + weighted;
        }

        var ranked = aggregated.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();

        if (ranked.Count == 0 || ranked[0].Value < _options.MinProbability)
            return Array.Empty<ClassificationResult>();

        int take = Math.Min(limit, ranked.Count);
        var results = new ClassificationResult[take];
        for (int i = 0; i < take; i++) results[i] = new ClassificationResult(ranked[i].Key, ranked[i].Value);
        return results;
    }

    /// <summary>Returns the single most likely category, or null when nothing was trained.</summary>
    /// <param name="vector">The vector to classify.</param>
    /// <param name="excludedCategories">Optional set of categories to hide.</param>
    public string? PredictBest(ReadOnlyMemory<float> vector, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(vector, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private void ValidateShape(ReadOnlyMemory<float> vector)
    {
        if (_dimension == 0) return;
        if (vector.Length != _dimension)
            throw new ArgumentException($"All vectors must have the same length (got {vector.Length} after {_dimension}).", nameof(vector));
    }

    private static void ValidateFinite(ReadOnlyMemory<float> vector, string? category)
    {
        ReadOnlySpan<float> span = vector.Span;
        int index = VectorSimilarity.IndexOfNonFinite(span);
        if (index < 0) return;
        string subject = category is null ? "query" : $"category '{category}'";
        throw new ArgumentOutOfRangeException(nameof(vector), $"The vector source returned a non-finite value ({span[index]}) at index {index} for {subject}.");
    }

    private readonly record struct Entry(string Category, float[] Vector, float Norm);
}
