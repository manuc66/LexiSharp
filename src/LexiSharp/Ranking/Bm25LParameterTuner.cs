using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>One evaluated BM25L configuration and the quality it achieved.</summary>
/// <param name="K1">The evaluated term-frequency saturation.</param>
/// <param name="B">The evaluated document-length normalization.</param>
/// <param name="Delta">The evaluated lower bound on the compressed term frequency.</param>
/// <param name="MetricScore">Mean value of the tuned <see cref="TuningMetric"/> over the validation set.</param>
public sealed record Bm25LGridPoint(
    double K1,
    double B,
    double Delta,
    double MetricScore);

/// <summary>
/// The outcome of a <see cref="Bm25LParameterTuner.Tune"/> run: the best configuration found, the
/// quality it achieved, and the full evaluation grid for inspection.
/// </summary>
/// <param name="K1">The winning term-frequency saturation, for a <see cref="Bm25LScorer"/>.</param>
/// <param name="B">The winning document-length normalization, for a <see cref="Bm25LScorer"/>.</param>
/// <param name="Delta">The winning lower bound, for a <see cref="Bm25LScorer"/>.</param>
/// <param name="MetricScore">Mean metric value achieved by these parameters over the validation set.</param>
/// <param name="Metric">The metric that was optimized.</param>
/// <param name="TopK">The retrieval depth used while evaluating candidates.</param>
/// <param name="Grid">Every evaluated configuration, in ascending <c>(k1, b, delta)</c> order.</param>
/// <param name="UnflooredMetricScore">
/// The best score any <c>(k1, b)</c> reached with <b>no</b> lower bound, or <see cref="double.NaN"/>
/// when the delta grid contained no zero.
/// </param>
/// <remarks>
/// <para>
/// <b>That baseline is BM25, and exactly so.</b> BM25L's compressed numerator and its compressed
/// denominator cancel: <c>ctd / (k1 + ctd) = tf / (k1 &#183; norm + tf)</c>, which is
/// <see cref="Bm25Scorer"/>'s term weight. So at <c>delta = 0</c> BM25L <i>is</i> BM25, term for
/// term, and this number can be read straight against a <see cref="Bm25ParameterTuner"/> result on
/// the same grids. The same is true of <see cref="Bm25PlusTuningResult.UnflooredMetricScore"/>.
/// </para>
/// <para>
/// This is worth stating rather than leaving to algebra, because it is what makes
/// <see cref="DeltaHelped"/> answerable at all: without a δ=0 point in the grid there is no BM25 to
/// compare the bound against.
/// </para>
/// </remarks>
/// <param name="EvaluatedConfigurations">How many configurations were run, for cost accounting.</param>
public sealed record Bm25LTuningResult(
    double K1,
    double B,
    double Delta,
    double MetricScore,
    TuningMetric Metric,
    int TopK,
    IReadOnlyList<Bm25LGridPoint> Grid,
    double UnflooredMetricScore,
    int EvaluatedConfigurations)
{
    /// <summary>
    /// Whether the lower bound actually beat no lower bound for this scorer. False means the search
    /// found no <c>delta</c> better than zero — a real answer, and the one to act on. Also false when
    /// <see cref="UnflooredMetricScore"/> is <see cref="double.NaN"/>, because nothing was compared.
    /// </summary>
    /// <remarks>
    /// <c>false</c> has two causes worth telling apart, and <see cref="Grid"/> distinguishes them.
    /// Either no configuration with a bound scored above the best one without, in which case the
    /// bound genuinely did not help — or many configurations tie at the maximum and
    /// <c>delta = 0</c> is merely the one the ascending-order tie-break reached first, in which case
    /// the validation set is too small or too flat to separate the formulas at all. Count the points
    /// where <see cref="MetricScore"/> recurs before reading <c>false</c> as a verdict on the bound.
    /// </remarks>
    public bool DeltaHelped => MetricScore > UnflooredMetricScore;
}

/// <summary>
/// Finds the BM25L parameters (<c>k1</c>, <c>b</c> and the lower bound <c>delta</c>) that best satisfy
/// a set of labeled validation queries, by brute-force grid search.
/// </summary>
/// <remarks>
/// <para>
/// The BM25L half of the tuned-vs-tuned comparison; see <see cref="Bm25PlusParameterTuner"/> for why
/// the BM25+ side needed a <c>delta</c> search at all. Before this existed, both variants were
/// measured at the papers' starting <c>delta</c> against a BM25 whose <c>(k1, b)</c> had been fitted,
/// which made the published BEIR rows a comparison of two different amounts of tuning.
/// </para>
/// <para>
/// <b>Three parameters, searched as a full product.</b> Every <c>k1 × b × delta</c> combination is
/// scored, so a configuration that is only good jointly is found — 125 evaluations by default, five
/// times the cost of tuning BM25, which is why the run is capped and the count is reported rather
/// than silently trimmed.
/// </para>
/// <para>
/// Every grid point runs a <see cref="RankedTextSearchEngine"/> over the <b>same</b>
/// <see cref="ITextIndex"/> with a fresh <see cref="Bm25LScorer"/>. The index is never mutated and no
/// scorer state is shared, so tuning is side-effect free. Ties keep the first point in ascending
/// <c>(k1, b, delta)</c> order, making the result deterministic and resolving a tie towards the
/// least aggressive parameters.
/// </para>
/// <para>
/// <b>The result is an oracle, not a fair baseline.</b> The best of N configurations fitted on the
/// same N queries it is scored on is partly fitting noise, and 125 candidates fit more noise than
/// 25. Read the winner's margin as an upper bound, or re-score it on a held-out set.
/// </para>
/// </remarks>
public sealed class Bm25LParameterTuner
{
    private readonly ITextIndex _index;
    private readonly ITokenizer _tokenizer;
    private readonly IReadOnlyList<Bm25ValidationQuery> _validationQueries;

    /// <param name="index">The indexed corpus to tune against (read-only for the tuner).</param>
    /// <param name="validationQueries">
    /// Labeled queries with the ids of their relevant documents. Every query must list at least one
    /// relevant document, otherwise recall is undefined.
    /// </param>
    /// <param name="tokenizer">
    /// Tokenizer for queries; should be the same one the index was built with.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The validation set is empty, or a query lists no relevant document.
    /// </exception>
    public Bm25LParameterTuner(
        ITextIndex index,
        IEnumerable<Bm25ValidationQuery> validationQueries,
        ITokenizer? tokenizer = null)
    {
        ArgumentNullException.ThrowIfNull(index);

        _index = index;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _validationQueries = Bm25VariantTunerCore.ValidateValidationSet(
            validationQueries, nameof(validationQueries));
    }

    /// <summary>
    /// Runs the grid search and returns the best configuration together with the full grid.
    /// </summary>
    /// <param name="k1Values">Candidate saturation values; defaults to <c>0.5, 1.0, 1.2, 1.5, 2.0</c>.</param>
    /// <param name="bValues">Candidate length-normalization values; defaults to <c>0.0, 0.25, 0.5, 0.75, 1.0</c>.</param>
    /// <param name="deltaValues">
    /// Candidate lower bounds; defaults to <c>0.0, 0.25, 0.5, 0.75, 1.0</c>. The paper reports
    /// <c>0.5</c> as most effective; that is a starting point, not a tuned value, which is what this
    /// axis is for. <c>0.0</c> is in the default grid so the search can report whether the bound
    /// helped at all — see <see cref="Bm25LTuningResult.DeltaHelped"/>.
    /// </param>
    /// <param name="topK">Retrieval depth used to judge each candidate ranking.</param>
    /// <param name="metric">Quality metric maximized over the validation set.</param>
    /// <param name="maxConfigurations">
    /// Ceiling on evaluated configurations; defaults to 512, which fits the 125-point default grid
    /// with room to widen one axis. The run is refused above it rather than trimmed.
    /// </param>
    /// <returns>
    /// A <see cref="Bm25LTuningResult"/> whose <c>K1</c>, <c>B</c> and <c>Delta</c> can be fed
    /// straight into a <see cref="Bm25LScorer"/> constructor.
    /// </returns>
    /// <exception cref="ArgumentException">A grid is empty, or the search would exceed the cap.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A grid value is outside its valid range.</exception>
    public Bm25LTuningResult Tune(
        IEnumerable<double>? k1Values = null,
        IEnumerable<double>? bValues = null,
        IEnumerable<double>? deltaValues = null,
        int topK = 10,
        TuningMetric metric = TuningMetric.F1,
        int maxConfigurations = Bm25VariantTunerCore.DefaultMaxConfigurations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConfigurations);

        var (k1Grid, bGrid, deltaGrid) = Bm25VariantTunerCore.ResolveGrids(k1Values, bValues, deltaValues);

        var found = Bm25VariantTunerCore.Run(
            new Bm25VariantSearchRequest(_index, _tokenizer, _validationQueries, topK, metric, maxConfigurations),
            (k1Grid, bGrid, deltaGrid),
            (k1, b, delta) => new Bm25LScorer(k1, b, delta));

        return new Bm25LTuningResult(
            found.K1,
            found.B,
            found.Delta,
            found.MetricScore,
            metric,
            topK,
            found.Grid
                .Select(point => new Bm25LGridPoint(point.K1, point.B, point.Delta, point.MetricScore))
                .ToList(),
            found.UnflooredMetricScore,
            found.EvaluatedConfigurations);
    }
}
