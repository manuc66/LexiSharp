using LexiSharp.Core;
using LexiSharp.Linguistics;

namespace LexiSharp.Ranking;

/// <summary>One evaluated <c>(k1, b)</c> combination of a <see cref="Bm25VaParameterTuner"/>.</summary>
/// <param name="K1">The evaluated term-frequency saturation.</param>
/// <param name="B">The evaluated balance between verboseness (<c>0</c>) and length (<c>1</c>).</param>
/// <param name="MetricScore">Mean value of the tuned <see cref="TuningMetric"/> over the validation set.</param>
public sealed record Bm25VaGridPoint(double K1, double B, double MetricScore);

/// <summary>
/// The outcome of a <see cref="Bm25VaParameterTuner"/> run: the best <c>(k1, b)</c> found, the
/// quality it achieved and the full grid for inspection.
/// </summary>
/// <param name="K1">The winning term-frequency saturation.</param>
/// <param name="B">
/// The winning balance. <b><c>1</c> means the search collapsed onto BM25</b>: at that point the
/// verboseness term carries a zero weight, so a winner at <c>b = 1</c> is the corpus saying the
/// extra statistic bought nothing.
/// </param>
/// <param name="MetricScore">Mean <see cref="Metric"/> of the winning point over the validation set.</param>
/// <param name="Metric">The metric the grid maximized.</param>
/// <param name="TopK">Retrieval depth the candidates were judged at.</param>
/// <param name="EvaluatedConfigurations">How many grid points were scored.</param>
/// <param name="Grid">Every evaluated point, in ascending <c>(k1, b)</c> order.</param>
public sealed record Bm25VaTuningResult(
    double K1,
    double B,
    double MetricScore,
    TuningMetric Metric,
    int TopK,
    int EvaluatedConfigurations,
    IReadOnlyList<Bm25VaGridPoint> Grid);

/// <summary>
/// Finds the <c>(k1, b)</c> of <see cref="Bm25VaScorer"/> that best satisfy a set of labeled
/// validation queries, by the same brute-force grid search as <see cref="Bm25ParameterTuner"/>.
/// </summary>
/// <remarks>
/// <para>
/// The point of this tuner is the <c>b</c> it returns. BM25VA's <c>b</c> is not BM25's: it
/// interpolates between verboseness (<c>0</c>) and length (<c>1</c>), so <b>a winner at <c>b = 1</c>
/// is a null result</b> — the search tried the verboseness component and the corpus preferred the
/// length component it already had. That is the answer the evaluation harness exists to produce,
/// and it is why the winning <c>b</c> is reported next to the score rather than buried in a grid.
/// </para>
/// <para>
/// The search reuses <see cref="Bm25VariantTunerCore"/> with a one-point delta grid, so the
/// tie-break, the configuration cap and the scoring loop are the ones every other tuner uses. A
/// fresh <see cref="Bm25VaScorer"/> per point, over the same read-only index: tuning mutates
/// nothing, and the verboseness table is rebuilt per scorer because it is cached against the
/// scorer, not shared.
/// </para>
/// </remarks>
public sealed class Bm25VaParameterTuner
{
    private readonly ITextIndex _index;
    private readonly ITokenizer _tokenizer;
    private readonly IReadOnlyList<Bm25ValidationQuery> _validationQueries;
    private readonly SearchOptions? _searchOptions;
    private readonly QueryTermWeighting _queryTerms;

    /// <param name="index">The indexed corpus to tune against (read-only for the tuner).</param>
    /// <param name="validationQueries">Labeled queries with the ids of their relevant documents.</param>
    /// <param name="tokenizer">Tokenizer for queries; should be the one the index was built with.</param>
    /// <param name="searchOptions">
    /// Options every candidate search starts from, or <c>null</c> for the library defaults. Used as a
    /// template exactly as in <see cref="Bm25ParameterTuner"/>: the depth and the query's exclusions
    /// are replaced per query, everything else is kept.
    /// </param>
    /// <param name="queryTermWeighting">
    /// How a term repeated in the query is treated, passed to every candidate scorer. Default
    /// <see cref="QueryTermWeighting.Distinct"/>.
    /// </param>
    public Bm25VaParameterTuner(
        ITextIndex index,
        IEnumerable<Bm25ValidationQuery> validationQueries,
        ITokenizer? tokenizer = null,
        SearchOptions? searchOptions = null,
        QueryTermWeighting queryTermWeighting = QueryTermWeighting.Distinct)
    {
        ArgumentNullException.ThrowIfNull(index);

        _index = index;
        _tokenizer = tokenizer ?? Tokenizer.Default;
        _searchOptions = searchOptions;
        _queryTerms = queryTermWeighting;
        _validationQueries = Bm25VariantTunerCore.ValidateValidationSet(validationQueries, nameof(validationQueries));
    }

    /// <summary>
    /// Runs the grid search and returns the best pair together with the full grid.
    /// </summary>
    /// <param name="k1Values">Candidate saturation values; defaults to the library's five.</param>
    /// <param name="bValues">
    /// Candidate balances; defaults to <c>0.0, 0.25, 0.5, 0.75, 1.0</c>. Include <c>1.0</c>, or the
    /// search cannot report the null result that says verboseness bought nothing.
    /// </param>
    /// <param name="topK">Retrieval depth used to judge each candidate ranking.</param>
    /// <param name="metric">Quality metric maximized over the validation set.</param>
    /// <param name="maxConfigurations">
    /// Ceiling on evaluated points, as in the variant tuners. The default grid is 25 points; the cap
    /// exists so a caller cannot quietly fit a thousand points to the same queries.
    /// </param>
    public Bm25VaTuningResult Tune(
        IEnumerable<double>? k1Values = null,
        IEnumerable<double>? bValues = null,
        int topK = 10,
        TuningMetric metric = TuningMetric.F1,
        int maxConfigurations = Bm25VariantTunerCore.DefaultMaxConfigurations)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(topK);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConfigurations);

        // The delta axis does not exist here, and the core's grid is three-dimensional: one point is
        // passed so the shared loop runs, and nothing is inferred from that axis.
        var grids = Bm25VariantTunerCore.ResolveGrids(k1Values, bValues, [0.0]);

        var request = new Bm25VariantSearchRequest(
            _index,
            _tokenizer,
            _validationQueries,
            topK,
            metric,
            maxConfigurations,
            _searchOptions);

        Bm25VariantSearchResult result = Bm25VariantTunerCore.Run(
            request,
            grids,
            (k1, b, _) => new Bm25VaScorer(k1, b, _queryTerms));

        return new Bm25VaTuningResult(
            result.K1,
            result.B,
            result.MetricScore,
            metric,
            topK,
            result.EvaluatedConfigurations,
            result.Grid.Select(point => new Bm25VaGridPoint(point.K1, point.B, point.MetricScore)).ToArray());
    }
}
