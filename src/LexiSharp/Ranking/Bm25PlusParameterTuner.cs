using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>One evaluated BM25+ configuration and the quality it achieved.</summary>
/// <param name="K1">The evaluated term-frequency saturation.</param>
/// <param name="B">The evaluated document-length normalization.</param>
/// <param name="Delta">The evaluated lower bound added to the term weight.</param>
/// <param name="MetricScore">Mean value of the tuned <see cref="TuningMetric"/> over the validation set.</param>
public sealed record Bm25PlusGridPoint(
    double K1,
    double B,
    double Delta,
    double MetricScore);

/// <summary>
/// The outcome of a <see cref="Bm25PlusParameterTuner.Tune"/> run: the best configuration found, the
/// quality it achieved, and the full evaluation grid for inspection.
/// </summary>
/// <param name="K1">The winning term-frequency saturation, for a <see cref="Bm25PlusScorer"/>.</param>
/// <param name="B">The winning document-length normalization, for a <see cref="Bm25PlusScorer"/>.</param>
/// <param name="Delta">The winning lower bound, for a <see cref="Bm25PlusScorer"/>.</param>
/// <param name="MetricScore">Mean metric value achieved by these parameters over the validation set.</param>
/// <param name="Metric">The metric that was optimized.</param>
/// <param name="TopK">The retrieval depth used while evaluating candidates.</param>
/// <param name="Grid">Every evaluated configuration, in ascending <c>(k1, b, delta)</c> order.</param>
/// <param name="UnflooredMetricScore">
/// The best score any <c>(k1, b)</c> reached with <b>no</b> lower bound — the baseline
/// <c>delta</c> has to beat. For BM25+ that baseline is BM25, exactly: <c>delta = 0</c> reduces the
/// term weight to <see cref="Bm25Scorer"/>'s. So on the same grids and the same queries this number
/// is what <see cref="Bm25ParameterTuner"/> reports. <see cref="double.NaN"/> when the delta grid
/// contained no zero, since then there is nothing to compare against.
/// </param>
/// <param name="EvaluatedConfigurations">How many configurations were run, for cost accounting.</param>
public sealed record Bm25PlusTuningResult(
    double K1,
    double B,
    double Delta,
    double MetricScore,
    TuningMetric Metric,
    int TopK,
    IReadOnlyList<Bm25PlusGridPoint> Grid,
    double UnflooredMetricScore,
    int EvaluatedConfigurations)
{
    /// <summary>
    /// Whether the lower bound actually beat no lower bound on this validation set. False means the
    /// search found no <c>delta</c> better than zero — a real answer, and the one to act on, not a
    /// failure of the search. Also false when <see cref="UnflooredMetricScore"/> is
    /// <see cref="double.NaN"/>, because nothing was compared.
    /// </summary>
    /// <remarks>
    /// <c>false</c> has two causes worth telling apart, and <see cref="Grid"/> distinguishes them.
    /// Either no configuration with a bound scored above the best one without, in which case the
    /// bound genuinely did not help — or many configurations tie at the maximum and
    /// <c>delta = 0</c> is merely the one the ascending-order tie-break reached first, in which case
    /// the validation set is too small or too flat to separate the formulas at all. Count the points
    /// where <see cref="MetricScore"/> recurs before reading <c>false</c> as a verdict on the bound:
    /// on this repository's 22-query reference corpus 46 of 125 points tie, and every
    /// <c>delta</c> from 0 to 1 is among them.
    /// </remarks>
    public bool DeltaHelped => MetricScore > UnflooredMetricScore;
}

/// <summary>
/// Finds the BM25+ parameters (<c>k1</c>, <c>b</c> and the lower bound <c>delta</c>) that best satisfy
/// a set of labeled validation queries, by brute-force grid search.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes the BM25+ comparison in <c>docs/ranking.md</c> tuned-vs-tuned rather than
/// tuned-vs-default. <see cref="Bm25ParameterTuner"/> searches <c>(k1, b)</c> only, so a BM25+ row
/// next to a tuned BM25 row was really measuring BM25+ at the papers' starting <c>delta</c> against
/// BM25 at its best — a comparison no BM25+ result could be expected to win, and not the one a reader
/// assumes it is.
/// </para>
/// <para>
/// <b>Three parameters, searched as a full product.</b> Every <c>k1 × b × delta</c> combination is
/// scored, so a configuration that is only good jointly is found — 125 evaluations by default. That
/// is 5× the cost of tuning BM25, which is why the run is capped and the count is reported rather
/// than silently trimmed.
/// </para>
/// <para>
/// Every grid point runs a <see cref="RankedTextSearchEngine"/> over the <b>same</b>
/// <see cref="ITextIndex"/> with a fresh <see cref="Bm25PlusScorer"/>. The index is never mutated and
/// no scorer state is shared, so tuning is side-effect free. Ties keep the first point in ascending
/// <c>(k1, b, delta)</c> order, which makes the result deterministic and resolves a tie towards the
/// least aggressive parameters.
/// </para>
/// <para>
/// <b>The result is an oracle, not a fair baseline.</b> The best of N configurations fitted on the
/// same N queries it is scored on is partly fitting noise, and the more configurations were tried the
/// more of the reported gain is that. That caveat applies here with more force than to
/// <see cref="Bm25ParameterTuner"/>, which searches 25 points rather than 125: the two are only
/// comparable as tuned-vs-tuned when both are read as the same kind of number. Score the winner on a
/// held-out set, or the number is an upper bound.
/// </para>
/// <para>
/// For BM25L the same search exists as <see cref="Bm25LParameterTuner"/>, and it is a separate class
/// rather than a flag on this one: <c>delta</c> means a different thing in each formula, so a shared
/// result type would let a BM25+ <c>delta</c> be read as a BM25L one.
/// </para>
/// </remarks>
public sealed class Bm25PlusParameterTuner
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
    public Bm25PlusParameterTuner(
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
    /// Candidate lower bounds; defaults to <c>0.0, 0.25, 0.5, 0.75, 1.0</c>, which spans the range
    /// Lv &amp; Zhai report around <c>0.5</c>–<c>1.0</c> and keeps the no-bound case in the search.
    /// Keeping <c>0.0</c> matters: it is the only point at which the search can conclude that the
    /// bound earns nothing, which is <see cref="Bm25PlusTuningResult.DeltaHelped"/>'s question. Omit
    /// it and <see cref="Bm25PlusTuningResult.UnflooredMetricScore"/> is <see cref="double.NaN"/>.
    /// </param>
    /// <param name="topK">Retrieval depth used to judge each candidate ranking.</param>
    /// <param name="metric">Quality metric maximized over the validation set.</param>
    /// <param name="maxConfigurations">
    /// Ceiling on evaluated configurations; defaults to 512, which fits the 125-point default grid
    /// with room to widen one axis. The run is refused above it rather than trimmed.
    /// </param>
    /// <returns>
    /// A <see cref="Bm25PlusTuningResult"/> whose <c>K1</c>, <c>B</c> and <c>Delta</c> can be fed
    /// straight into a <see cref="Bm25PlusScorer"/> constructor.
    /// </returns>
    /// <exception cref="ArgumentException">A grid is empty, or the search would exceed the cap.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A grid value is outside its valid range.</exception>
    public Bm25PlusTuningResult Tune(
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
            (k1, b, delta) => new Bm25PlusScorer(k1, b, delta));

        return new Bm25PlusTuningResult(
            found.K1,
            found.B,
            found.Delta,
            found.MetricScore,
            metric,
            topK,
            found.Grid
                .Select(point => new Bm25PlusGridPoint(point.K1, point.B, point.Delta, point.MetricScore))
                .ToList(),
            found.UnflooredMetricScore,
            found.EvaluatedConfigurations);
    }
}
