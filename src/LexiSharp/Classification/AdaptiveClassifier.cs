using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>
/// A thread-safe holder for an <see cref="ITextClassifier"/> that can be swapped atomically
/// after an online update or a retrain. It forwards <see cref="Predict"/> to the current model and
/// provides an opt-in validation gate so a candidate is only published when it does not regress.
/// </summary>
/// <remarks>
/// <para>
/// The policy stays with the caller: when to collect feedback, what counts as a regression, and
/// when to retrain. This type only makes the swap safe and the comparison explicit.
/// </para>
/// <para>
/// For continuous adaptation prefer <see cref="TrySwapSignificant"/>: <see cref="TrySwap"/> accepts
/// a candidate whose accuracy merely ties the current model (a difference of zero is "no
/// regression"), so on a small validation set it can swap on noise. The significance gate requires
/// a paired difference that is statistically distinguishable from chance.
/// </para>
/// </remarks>
public sealed class AdaptiveClassifier : ITextClassifier
{
    private ITextClassifier _current;

    /// <summary>Wraps <paramref name="initial"/> as the current model.</summary>
    public AdaptiveClassifier(ITextClassifier initial)
    {
        ArgumentNullException.ThrowIfNull(initial);
        _current = initial;
    }

    /// <summary>The model currently serving predictions.</summary>
    public ITextClassifier Current => Volatile.Read(ref _current);

    /// <summary>Raised after a successful swap, with the previous and new models.</summary>
    public event Action<ITextClassifier, ITextClassifier>? Swapped;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents) => Current.Train(documents);

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null) =>
        Current.Predict(text, limit, excludedCategories);

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null) =>
        Current.PredictBest(text, excludedCategories);

    /// <summary>
    /// Applies one labelled example incrementally when the current model supports it.
    /// </summary>
    /// <returns><c>true</c> when the model learned the example; <c>false</c> when it is not incremental.</returns>
    public bool Learn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Current is not IIncrementalTextClassifier incremental)
        {
            return false;
        }

        incremental.Learn(document);
        return true;
    }

    /// <summary>
    /// Removes one labelled example incrementally when the current model supports it.
    /// </summary>
    /// <returns><c>true</c> when the model unlearned the example; <c>false</c> when it is not incremental.</returns>
    public bool Unlearn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (Current is not IIncrementalTextClassifier incremental)
        {
            return false;
        }

        incremental.Unlearn(document);
        return true;
    }

    /// <summary>
    /// Publishes <paramref name="candidate"/> only when its accuracy on <paramref name="validation"/>
    /// is at least the current model's accuracy plus <paramref name="minimumGain"/>.
    /// </summary>
    /// <remarks>
    /// An accuracy tie is accepted when <paramref name="minimumGain"/> is zero: "no regression"
    /// includes "no change". This is deliberate for deterministic retrains, but it means a noisy
    /// validation set can trigger a swap without a real gain. For continuous adaptation use
    /// <see cref="TrySwapSignificant"/> instead.
    /// </remarks>
    /// <param name="candidate">The model trained on the new data.</param>
    /// <param name="validation">Labelled documents used to compare the two models.</param>
    /// <param name="minimumGain">Required accuracy gain; 0 means "do not regress".</param>
    /// <returns><c>true</c> when the candidate was published.</returns>
    public bool TrySwap(
        ITextClassifier candidate,
        IEnumerable<SearchDocument> validation,
        double minimumGain = 0.0)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(validation);
        if (double.IsNaN(minimumGain) || minimumGain < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumGain), minimumGain, "Must be non-negative.");
        }

        var documents = validation
            .Where(document => document is not null && !string.IsNullOrEmpty(document.Category))
            .ToList();

        if (documents.Count == 0)
        {
            return false;
        }

        ITextClassifier current = Current;
        double currentAccuracy = Accuracy(current, documents);
        double candidateAccuracy = Accuracy(candidate, documents);

        if (candidateAccuracy + 1e-12 < currentAccuracy + minimumGain)
        {
            return false;
        }

        Interlocked.Exchange(ref _current, candidate);
        Swapped?.Invoke(current, candidate);
        return true;
    }

    /// <summary>
    /// Publishes <paramref name="candidate"/> only when it beats the current model on
    /// <paramref name="validation"/> and the paired difference is significant (McNemar exact test).
    /// </summary>
    /// <param name="candidate">The model trained on the new data.</param>
    /// <param name="validation">Labelled documents used to compare the two models.</param>
    /// <param name="alpha">Significance level (default 0.05).</param>
    /// <returns><c>true</c> when the candidate was published.</returns>
    public bool TrySwapSignificant(
        ITextClassifier candidate,
        IEnumerable<SearchDocument> validation,
        double alpha = 0.05)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(validation);
        if (!double.IsFinite(alpha) || alpha is <= 0 or >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be finite and in (0, 1).");
        }

        var documents = validation
            .Where(document => document is not null && !string.IsNullOrEmpty(document.Category))
            .ToList();

        if (documents.Count == 0)
        {
            return false;
        }

        ITextClassifier current = Current;
        int currentCorrect = 0;
        int candidateCorrect = 0;
        int currentOnly = 0;
        int candidateOnly = 0;

        foreach (SearchDocument document in documents)
        {
            bool currentHit = string.Equals(current.PredictBest(document.Text), document.Category, StringComparison.Ordinal);
            bool candidateHit = string.Equals(candidate.PredictBest(document.Text), document.Category, StringComparison.Ordinal);

            if (currentHit) currentCorrect++;
            if (candidateHit) candidateCorrect++;

            if (currentHit && !candidateHit) currentOnly++;
            if (!currentHit && candidateHit) candidateOnly++;
        }

        double currentAccuracy = currentCorrect / (double)documents.Count;
        double candidateAccuracy = candidateCorrect / (double)documents.Count;

        if (candidateAccuracy <= currentAccuracy + 1e-12)
        {
            return false;
        }

        int discordant = currentOnly + candidateOnly;
        if (discordant == 0)
        {
            return false;
        }

        double pValue = McNemarExact(currentOnly, candidateOnly);
        if (pValue >= alpha)
        {
            return false;
        }

        Interlocked.Exchange(ref _current, candidate);
        Swapped?.Invoke(current, candidate);
        return true;
    }

    private static double McNemarExact(int currentOnly, int candidateOnly)
    {
        int n = currentOnly + candidateOnly;
        if (n == 0)
        {
            return 1.0;
        }

        int tail = Math.Min(currentOnly, candidateOnly);
        double sum = 0;
        for (int k = 0; k <= tail; k++)
        {
            sum += BinomialCoefficient(n, k);
        }

        double p = 2.0 * sum / Math.Pow(2.0, n);
        return Math.Min(1.0, p);
    }

    private static double BinomialCoefficient(int n, int k)
    {
        if (k < 0 || k > n)
        {
            return 0;
        }

        k = Math.Min(k, n - k);
        double result = 1;
        for (int i = 1; i <= k; i++)
        {
            result *= (n - k + i) / (double)i;
        }

        return result;
    }

    private static double Accuracy(ITextClassifier classifier, IReadOnlyList<SearchDocument> documents)
    {
        int correct = 0;
        foreach (SearchDocument document in documents)
        {
            string? predicted = classifier.PredictBest(document.Text);
            if (string.Equals(predicted, document.Category, StringComparison.Ordinal))
            {
                correct++;
            }
        }

        return correct / (double)documents.Count;
    }
}
