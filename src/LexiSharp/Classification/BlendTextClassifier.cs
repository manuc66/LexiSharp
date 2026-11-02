using LexiSharp.Core;

namespace LexiSharp.Classification;

/// <summary>One trained classifier and the weight its distribution carries in the blend.</summary>
/// <param name="Classifier">The model to ask for a distribution; never null.</param>
/// <param name="Weight">A finite, non-negative weight. Zero contributes nothing.</param>
public sealed record TextClassifierLane(ITextClassifier Classifier, double Weight);

/// <summary>One source of categories no model learned, and the weight of its evidence.</summary>
/// <param name="Source">The candidate source; never null.</param>
/// <param name="Weight">A finite, non-negative weight. Zero contributes nothing.</param>
public sealed record CandidateLane(ICandidateTextSource Source, double Weight);

/// <summary>One source of log-prior bonuses on categories a model already holds.</summary>
/// <param name="Source">The prior source; never null.</param>
/// <param name="Weight">A finite, non-negative weight. Zero contributes nothing.</param>
public sealed record PriorLane(ITextPriorSource Source, double Weight);

/// <summary>
/// Blends any number of text classifiers, then optionally injects candidate categories and
/// log-prior bonuses.
/// </summary>
/// <remarks>
/// <para>
/// The trained lanes are combined in probability space:
/// <c>score(c) = Σ_l weight_l · P_l(c)</c>, over the union of every lane's categories. A category
/// only one lane knows enters with a zero contribution from the others, exactly as
/// <see cref="FusedTextClassifier"/> does for two models. Candidate sources add a normalized
/// distribution of their own, and prior sources multiply the result by <c>exp(bonus(c))</c>;
/// because both are additive in log space, two prior sources compose by adding their bonuses.
/// </para>
/// <para>
/// A candidate source may introduce a category that no model was trained on. A prior source may
/// not: it only shifts categories already present. This is the difference between "the text names
/// this label" and "this label is more likely given the time or the neighbours".
/// </para>
/// <para>
/// The weights are not calibrations. They are tuned on a validation split, and a probability
/// average is not a calibrated posterior. The result is renormalized after exclusions; ties are
/// ordered by category name (ordinal); only the requested page becomes result objects.
/// </para>
/// <para>
/// Thread-safety: concurrent <see cref="Predict(string, int, IReadOnlySet{string})"/> calls are supported if the lanes and sources
/// are. <see cref="Train"/> must not overlap a prediction or another training; it trains every
/// lane on one materialized copy of the corpus.
/// </para>
/// </remarks>
public sealed class BlendTextClassifier : ITextClassifier, IIncrementalTextClassifier, IContextualTextClassifier
{
    private readonly IReadOnlyList<TextClassifierLane> _lanes;
    private readonly IReadOnlyList<CandidateLane> _candidates;
    private readonly IReadOnlyList<PriorLane> _priors;
    private readonly IReadOnlyList<IIncrementalTextClassifier>? _incrementalLanes;
    private readonly string? _nonIncrementalLane;

    /// <summary>Creates a blend over one or more trained lanes, with optional evidence sources.</summary>
    /// <param name="lanes">The trained lanes; at least one, each non-null with a finite non-negative weight.</param>
    /// <param name="candidates">Sources that may introduce categories no lane learned; null means none.</param>
    /// <param name="priors">Sources that shift categories already present; null means none.</param>
    /// <exception cref="ArgumentNullException">A list, lane or source is null.</exception>
    /// <exception cref="ArgumentException">A weight is negative or not finite, or there is no lane.</exception>
    public BlendTextClassifier(
        IReadOnlyList<TextClassifierLane> lanes,
        IReadOnlyList<CandidateLane>? candidates = null,
        IReadOnlyList<PriorLane>? priors = null)
    {
        ArgumentNullException.ThrowIfNull(lanes);

        if (lanes.Count == 0)
            throw new ArgumentException("A blend needs at least one trained lane.", nameof(lanes));

        foreach (TextClassifierLane lane in lanes)
        {
            ArgumentNullException.ThrowIfNull(lane);
            ArgumentNullException.ThrowIfNull(lane.Classifier);
            ValidateWeight(lane.Weight, nameof(lanes));
        }

        _candidates = candidates ?? Array.Empty<CandidateLane>();
        _priors = priors ?? Array.Empty<PriorLane>();

        foreach (CandidateLane lane in _candidates)
        {
            ArgumentNullException.ThrowIfNull(lane);
            ArgumentNullException.ThrowIfNull(lane.Source);
            ValidateWeight(lane.Weight, nameof(candidates));
        }

        foreach (PriorLane lane in _priors)
        {
            ArgumentNullException.ThrowIfNull(lane);
            ArgumentNullException.ThrowIfNull(lane.Source);
            ValidateWeight(lane.Weight, nameof(priors));
        }

        _lanes = lanes.ToArray();
        (_incrementalLanes, _nonIncrementalLane) = FindIncrementalLanes(_lanes);
    }

    private static (IReadOnlyList<IIncrementalTextClassifier>? Lanes, string? Missing) FindIncrementalLanes(
        IReadOnlyList<TextClassifierLane> lanes)
    {
        var incremental = new List<IIncrementalTextClassifier>(lanes.Count);
        string? missing = null;

        foreach (TextClassifierLane lane in lanes)
        {
            if (lane.Classifier is IIncrementalTextClassifier incrementalLane)
                incremental.Add(incrementalLane);
            else
                missing ??= lane.Classifier.GetType().Name;
        }

        return (missing is null ? incremental : null, missing);
    }

    /// <summary>The trained lanes, in the order they were supplied.</summary>
    public IReadOnlyList<TextClassifierLane> Lanes => _lanes;

    /// <summary>The candidate sources, in the order they were supplied.</summary>
    public IReadOnlyList<CandidateLane> Candidates => _candidates;

    /// <summary>The prior sources, in the order they were supplied.</summary>
    public IReadOnlyList<PriorLane> Priors => _priors;

    /// <inheritdoc />
    public void Train(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        // Materialize once: an IEnumerable may be a one-pass query, and every lane must see the
        // same corpus rather than the first lane consuming the enumerator.
        var corpus = documents as IReadOnlyList<SearchDocument> ?? documents.ToList();

        foreach (TextClassifierLane lane in _lanes)
            lane.Classifier.Train(corpus);
    }

    /// <summary>
    /// Learns one document in every lane. The lanes are checked before the first mutation, so a
    /// blend that contains a non-incremental model refuses the whole call instead of leaving half
    /// of its lanes updated.
    /// </summary>
    /// <param name="document">The labelled document to learn.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// At least one lane is not an <see cref="IIncrementalTextClassifier"/>.
    /// </exception>
    public void Learn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (IIncrementalTextClassifier lane in IncrementalLanes())
            lane.Learn(document);
    }

    /// <summary>Removes one document from every lane, with the same all-or-nothing check as <see cref="Learn"/>.</summary>
    /// <param name="document">The document to retract.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// At least one lane is not an <see cref="IIncrementalTextClassifier"/>.
    /// </exception>
    public void Unlearn(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        foreach (IIncrementalTextClassifier lane in IncrementalLanes())
            lane.Unlearn(document);
    }

    private IReadOnlyList<IIncrementalTextClassifier> IncrementalLanes()
    {
        if (_incrementalLanes is not null)
            return _incrementalLanes;

        throw new NotSupportedException(
            $"{_nonIncrementalLane} does not implement {nameof(IIncrementalTextClassifier)}; " +
            "the blend cannot learn one lane without the others.");
    }

    /// <inheritdoc />
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null) =>
        PredictCore(text, context: null, limit, excludedCategories);

    /// <summary>
    /// Predicts with evidence supplied for this request only, instead of a context configured on
    /// the blend. The context is read once and never stored.
    /// </summary>
    /// <param name="text">The text being classified.</param>
    /// <param name="context">Request-scoped candidates and priors; <see cref="BlendContext.Empty"/> means none.</param>
    /// <param name="limit">Maximum number of categories to return.</param>
    /// <param name="excludedCategories">Optional set of categories to hide.</param>
    /// <exception cref="ArgumentNullException"><paramref name="text"/> or <paramref name="context"/> is null.</exception>
    /// <exception cref="ArgumentException">A context weight or evidence entry is invalid.</exception>
    public IReadOnlyList<ClassificationResult> Predict(
        string text,
        BlendContext context,
        int limit = 3,
        IReadOnlySet<string>? excludedCategories = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        return PredictCore(text, context, limit, excludedCategories);
    }

    private IReadOnlyList<ClassificationResult> PredictCore(
        string text,
        BlendContext? context,
        int limit,
        IReadOnlySet<string>? excludedCategories)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (limit <= 0)
            return Array.Empty<ClassificationResult>();

        if (context is not null)
        {
            ValidateWeight(context.CandidateWeight, nameof(context));
            ValidateWeight(context.PriorWeight, nameof(context));
        }

        var probability = new Dictionary<string, double>(StringComparer.Ordinal);
        var trainedCategories = new HashSet<string>(StringComparer.Ordinal);

        CollectLaneProbabilities(text, excludedCategories, probability, trainedCategories);
        AddCandidateProbabilities(text, excludedCategories, probability);

        if (context?.Candidates is { Count: > 0 } candidates)
            AddCandidates(candidates, context.CandidateWeight, excludedCategories, probability, nameof(context));

        if (probability.Count == 0)
            return Array.Empty<ClassificationResult>();

        if (_priors.Count > 0 || context?.Priors is { Count: > 0 })
            ApplyPriors(text, context, probability, trainedCategories);

        return Rank(probability, excludedCategories, limit);
    }

    private void CollectLaneProbabilities(
        string text,
        IReadOnlySet<string>? excludedCategories,
        Dictionary<string, double> probability,
        HashSet<string> trainedCategories)
    {
        foreach (TextClassifierLane lane in _lanes)
        {
            if (lane.Weight == 0)
                continue;

            foreach (ClassificationResult result in lane.Classifier.Predict(
                         text, limit: int.MaxValue, excludedCategories))
            {
                if (excludedCategories?.Contains(result.Category) == true)
                    continue;

                trainedCategories.Add(result.Category);
                probability.TryGetValue(result.Category, out double current);
                probability[result.Category] = current + (lane.Weight * result.Probability);
            }
        }
    }

    private void AddCandidateProbabilities(
        string text,
        IReadOnlySet<string>? excludedCategories,
        Dictionary<string, double> probability)
    {
        foreach (CandidateLane lane in _candidates)
        {
            if (lane.Weight == 0)
                continue;

            IReadOnlyList<TextEvidence> evidence = lane.Source.Candidates(text);
            AddCandidates(evidence, lane.Weight, excludedCategories, probability, nameof(lane));
        }
    }

    private static void AddCandidates(
        IReadOnlyList<TextEvidence> evidence,
        double weight,
        IReadOnlySet<string>? excludedCategories,
        Dictionary<string, double> probability,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(evidence, parameterName);

        if (weight == 0)
            return;

        double total = 0;
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);

        foreach (TextEvidence candidate in evidence)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            ValidateEvidence(candidate, parameterName);

            if (excludedCategories?.Contains(candidate.Category) == true)
                continue;

            weights.TryGetValue(candidate.Category, out double current);
            weights[candidate.Category] = current + candidate.Score;
            total += candidate.Score;
        }

        if (total <= 0)
            return;

        foreach (var (category, candidateWeight) in weights)
        {
            probability.TryGetValue(category, out double current);
            probability[category] = current + (weight * candidateWeight / total);
        }
    }

    private static IReadOnlyList<ClassificationResult> Rank(
        Dictionary<string, double> probability,
        IReadOnlySet<string>? excludedCategories,
        int limit)
    {
        double mass = 0;

        foreach (double value in probability.Values)
            mass += value;

        if (!(mass > 0) || double.IsNaN(mass) || double.IsInfinity(mass))
            return Array.Empty<ClassificationResult>();

        var ranked = new List<(string Category, double Probability)>(probability.Count);

        foreach (var (category, value) in probability)
        {
            if (excludedCategories?.Contains(category) == true)
                continue;

            ranked.Add((category, value / mass));
        }

        ranked.Sort(static (left, right) =>
        {
            int byProbability = right.Probability.CompareTo(left.Probability);
            return byProbability != 0
                ? byProbability
                : string.CompareOrdinal(left.Category, right.Category);
        });

        int take = Math.Min(limit, ranked.Count);
        var results = new ClassificationResult[take];

        for (int i = 0; i < take; i++)
            results[i] = new ClassificationResult(ranked[i].Category, ranked[i].Probability);

        return results;
    }

    /// <inheritdoc />
    public string? PredictBest(string text, IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(text, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    /// <summary>Predicts the single best category with request-scoped evidence.</summary>
    /// <param name="text">The text being classified.</param>
    /// <param name="context">Request-scoped candidates and priors.</param>
    /// <param name="excludedCategories">Optional set of categories to hide.</param>
    public string? PredictBestWithContext(
        string text,
        BlendContext context,
        IReadOnlySet<string>? excludedCategories = null)
    {
        var results = Predict(text, context, limit: 1, excludedCategories);
        return results.Count > 0 ? results[0].Category : null;
    }

    private void ApplyPriors(
        string text,
        BlendContext? context,
        Dictionary<string, double> probability,
        HashSet<string> trainedCategories)
    {
        var bonus = new Dictionary<string, double>(StringComparer.Ordinal);

        AccumulateConfiguredPriors(text, bonus, trainedCategories);

        if (context?.Priors is { Count: > 0 } priors)
            AccumulatePriors(priors, context.PriorWeight, bonus, trainedCategories, nameof(context));

        if (bonus.Count == 0)
            return;

        ApplyBonus(probability, bonus);
    }

    private void AccumulateConfiguredPriors(
        string text,
        Dictionary<string, double> bonus,
        HashSet<string> trainedCategories)
    {
        foreach (PriorLane lane in _priors)
        {
            if (lane.Weight == 0)
                continue;

            IReadOnlyList<TextEvidence> evidence = lane.Source.Prior(text);
            AccumulatePriors(evidence, lane.Weight, bonus, trainedCategories, nameof(lane));
        }
    }

    private static void AccumulatePriors(
        IReadOnlyList<TextEvidence> evidence,
        double weight,
        Dictionary<string, double> bonus,
        HashSet<string> trainedCategories,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(evidence, parameterName);

        if (weight == 0)
            return;

        foreach (TextEvidence prior in evidence)
        {
            ArgumentNullException.ThrowIfNull(prior);
            ValidateEvidence(prior, parameterName, allowNegative: true);

            // A prior is about the model's labels, not about evidence the text injected. A
            // candidate that no lane produced is not eligible, and a prior cannot create it:
            // exp(bonus) times zero is still zero.
            if (!trainedCategories.Contains(prior.Category))
                continue;

            bonus.TryGetValue(prior.Category, out double current);
            bonus[prior.Category] = current + (weight * prior.Score);
        }
    }

    private static void ApplyBonus(
        Dictionary<string, double> probability,
        Dictionary<string, double> bonus)
    {
        // Work in log space: exp(bonus) can overflow for a large but finite bonus, while
        // exp(log(p) + bonus - max) is the same renormalized distribution without the overflow.
        double highest = double.NegativeInfinity;

        foreach (var (category, value) in probability)
        {
            if (value <= 0)
                continue;

            double logScore = Math.Log(value) + bonus.GetValueOrDefault(category);
            highest = Math.Max(highest, logScore);
        }

        if (double.IsNegativeInfinity(highest))
        {
            probability.Clear();
            return;
        }

        double total = 0;
        var updated = new Dictionary<string, double>(probability.Count, StringComparer.Ordinal);

        foreach (var (category, value) in probability)
        {
            if (value <= 0)
            {
                updated[category] = 0;
                continue;
            }

            double logScore = Math.Log(value) + bonus.GetValueOrDefault(category);
            double score = Math.Exp(logScore - highest);
            updated[category] = score;
            total += score;
        }

        probability.Clear();

        foreach (var (category, value) in updated)
            probability[category] = total > 0 ? value / total : 0;
    }

    private static void ValidateWeight(double weight, string parameterName)
    {
        if (!double.IsFinite(weight) || weight < 0)
            throw new ArgumentException("A lane weight must be finite and non-negative.", parameterName);
    }

    private static void ValidateEvidence(TextEvidence evidence, string parameterName, bool allowNegative = false)
    {
        if (string.IsNullOrEmpty(evidence.Category))
            throw new ArgumentException("Evidence must name a category.", parameterName);

        if (!double.IsFinite(evidence.Score) || (!allowNegative && evidence.Score < 0))
        {
            throw new ArgumentException(
                allowNegative
                    ? "A prior score must be finite."
                    : "A candidate score must be finite and non-negative.",
                parameterName);
        }
    }
}
