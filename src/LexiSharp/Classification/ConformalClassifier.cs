using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// Wraps an <see cref="ITextClassifier"/> with split-conformal calibration: at prediction time
/// it returns the smallest set of categories whose confidence reaches the requested coverage
/// level, and abstains (empty set) when no category is confident enough.
/// </summary>
/// <typeparam name="TInner">The wrapped classifier type.</typeparam>
/// <remarks>
/// <para>
/// Calibration uses a held-out set that must not have been used to train the wrapped classifier.
/// For each calibration example the non-conformity score <c>1 - p(y | x)</c> is computed; the
/// quantile of these scores at level <c>ceil((n+1)(1-α))/n</c> becomes the inclusion threshold.
/// A category is included iff <c>p(c | x) ≥ 1 - q</c>.
/// </para>
/// <para>
/// When fewer than <c>minCalibrationSize</c> calibration examples are available the wrapper falls
/// back to a probability threshold of <c>1 - α</c> instead of throwing.
/// </para>
/// </remarks>
public sealed class ConformalClassifier<TInner> : ITextClassifier, IExplainableTextClassifier
    where TInner : ITextClassifier
{
    private readonly TInner _inner;
    private int _minCalibrationSize;
    private double _alpha;

    private double[] _scores = [];
    private double _quantile = double.NaN;
    private bool _calibrated;

    /// <summary>Wraps <paramref name="inner"/> and calibrates on <paramref name="calibrationDocuments"/>.</summary>
    /// <param name="inner">A trained classifier. Not owned by this wrapper.</param>
    /// <param name="calibrationDocuments">Held-out labelled examples used for calibration; may be null to calibrate later.</param>
    /// <param name="alpha">Miscoverage level in (0, 1); coverage is 1 - alpha.</param>
    /// <param name="minCalibrationSize">Minimum number of calibration examples required for conformal mode.</param>
    public ConformalClassifier(
        TInner inner,
        IEnumerable<SearchDocument>? calibrationDocuments = null,
        double alpha = 0.10,
        int minCalibrationSize = 20)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!double.IsFinite(alpha) || alpha is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be finite and in (0, 1).");
        }

        if (minCalibrationSize < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minCalibrationSize), minCalibrationSize, "Must be non-negative.");
        }

        _inner = inner;
        _alpha = alpha;
        _minCalibrationSize = minCalibrationSize;

        if (calibrationDocuments is not null)
        {
            Calibrate(calibrationDocuments);
        }
    }

    /// <summary>The wrapped classifier.</summary>
    public TInner Inner => _inner;

    /// <summary>The miscoverage level.</summary>
    public double Alpha => _alpha;

    /// <summary>Whether conformal calibration succeeded.</summary>
    public bool IsCalibrated => _calibrated;

    /// <summary>The calibrated quantile, or <see cref="double.NaN"/> when not calibrated.</summary>
    public double Quantile => _quantile;

    /// <summary>The calibration non-conformity scores, sorted ascending.</summary>
    public IReadOnlyList<double> NonConformityScores => Array.AsReadOnly(_scores);

    /// <summary>Recomputes the calibration from <paramref name="calibrationDocuments"/>.</summary>
    public void Calibrate(IEnumerable<SearchDocument> calibrationDocuments)
    {
        ArgumentNullException.ThrowIfNull(calibrationDocuments);

        var scores = new List<double>();
        foreach (SearchDocument document in calibrationDocuments)
        {
            if (string.IsNullOrWhiteSpace(document.Category))
            {
                continue;
            }

            IReadOnlyList<ClassificationResult> predicted = _inner.Predict(document.Text, limit: int.MaxValue);
            double probability = 0;
            foreach (ClassificationResult result in predicted)
            {
                if (string.Equals(result.Category, document.Category, StringComparison.Ordinal))
                {
                    probability = result.Probability;
                    break;
                }
            }

            scores.Add(1.0 - probability);
        }

        scores.Sort();
        _scores = scores.ToArray();

        if (scores.Count == 0 || scores.Count < _minCalibrationSize)
        {
            _calibrated = false;
            _quantile = double.NaN;
            return;
        }

        int index = (int)Math.Ceiling((scores.Count + 1) * (1.0 - _alpha)) - 1;
        if (index < 0)
        {
            index = 0;
        }

        if (index >= scores.Count)
        {
            index = scores.Count - 1;
        }

        _quantile = scores[index];
        _calibrated = true;
    }

    /// <summary>Writes the fitted calibration (alpha, minimum size and non-conformity scores).</summary>
    public void SaveCalibration(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        using var writer = new BinaryWriter(destination, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(_alpha);
        writer.Write(_minCalibrationSize);
        writer.Write(_scores.Length);
        foreach (double score in _scores)
        {
            writer.Write(score);
        }
    }

    /// <summary>Reads a calibration written by <see cref="SaveCalibration"/>.</summary>
    public void LoadCalibration(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new BinaryReader(source, System.Text.Encoding.UTF8, leaveOpen: true);
        double alpha = reader.ReadDouble();
        int minSize = reader.ReadInt32();
        int count = reader.ReadInt32();
        if (count < 0)
        {
            throw new InvalidDataException("The calibration stream is corrupt.");
        }

        var scores = new double[count];
        for (int i = 0; i < count; i++)
        {
            scores[i] = reader.ReadDouble();
        }

        ApplyCalibration(alpha, minSize, scores);
    }

    private void ApplyCalibration(double alpha, int minSize, double[] scores)
    {
        if (alpha is <= 0 or >= 1 || minSize < 0)
        {
            throw new InvalidDataException("The calibration stream carries invalid parameters.");
        }

        Array.Sort(scores);
        _scores = scores;
        _alpha = alpha;
        _minCalibrationSize = minSize;

        if (scores.Length == 0 || scores.Length < minSize)
        {
            _calibrated = false;
            _quantile = double.NaN;
            return;
        }

        int index = (int)Math.Ceiling((scores.Length + 1) * (1.0 - alpha)) - 1;
        index = Math.Clamp(index, 0, scores.Length - 1);
        _quantile = scores[index];
        _calibrated = true;
    }

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => _inner.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null) =>
        _inner.Predict(text, limit, excludedCategories);

    /// <summary>
    /// Returns the conformal prediction set for <paramref name="text"/>: the categories whose
    /// confidence is high enough to reach the configured coverage level. Empty means abstention.
    /// </summary>
    /// <param name="text">Text to classify.</param>
    /// <param name="topK">Optional cap on the number of returned categories.</param>
    public PredictionSet PredictSet(string text, int? topK = null)
    {
        ArgumentNullException.ThrowIfNull(text);

        IReadOnlyList<ClassificationResult> predicted = _inner.Predict(text, limit: int.MaxValue);
        double threshold = _calibrated
            ? 1.0 - _quantile
            : 1.0 - _alpha;

        var included = new List<ClassificationResult>();
        foreach (ClassificationResult result in predicted)
        {
            if (result.Probability >= threshold - 1e-12)
            {
                included.Add(result);
            }
        }

        if (topK.HasValue && topK.Value > 0 && included.Count > topK.Value)
        {
            included = included.Take(topK.Value).ToList();
        }

        return new PredictionSet(
            included.Select(result => result.Category).ToList(),
            included);
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
        _inner.PredictBest(text, excludedCategories);

    /// <inheritdoc />
    public ClassificationExplanation? Explain(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        if (_inner is IExplainableTextClassifier explainable)
        {
            return explainable.Explain(text, limit, excludedCategories);
        }

        return null;
    }
}
