using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// BM25 whose term frequency is a <b>spatial spread</b> rather than a count: how many distinct
/// windows of the document contain the term, instead of how many times it occurs.
/// </summary>
/// <remarks>
/// <para>
/// <b>The formula.</b> For a document <c>d</c>, a term <c>t</c> and a window width <c>W</c>,
/// <c>spread(t, d) = |{ ⌊p / W⌋ : p ∈ positions(t, d) }|</c> — occurrences in the same window count
/// once. The scorer then applies BM25's saturation to the effective frequency
/// <c>tf_eff = tf^(1 − α) · spread^α</c>: <c>α = 0</c> is the raw count and <b>exactly</b>
/// <see cref="Bm25Scorer"/>; <c>α = 1</c> is the spread alone. The length normalization and the
/// rest of the formula are BM25's, unchanged.
/// </para>
/// <para>
/// <b>The hypothesis, and the one it is tested against.</b> BM25 rewards repetition as if it were
/// evidence, and a term repeated ten times in one paragraph is weaker evidence than the same term
/// spread across ten paragraphs. If spread is the better statistic, then length normalization
/// (<c>b</c>) should become less necessary — the spatial statistic already knows that a long
/// document is not automatically a better match. That interaction is the measurement this scorer
/// exists for: <c>α = 1</c> at <c>b = 0</c> against tuned BM25.
/// </para>
/// <para>
/// <b>Not the verboseness of BM25VA.</b> <see cref="Bm25VaScorer"/> measures repetition at the
/// document level (<c>|d| / distinct terms</c>) and leaves the term frequency raw; this measures
/// it per term and in space. The two are related but not substitutes, and the measurements in
/// <c>docs/ranking.md</c> report them separately.
/// </para>
/// <para>
/// <b>The cost is a positions read per candidate, and it is deliberate.</b> The term-at-a-time and
/// posting-array passes carry a term frequency and a length, not the positions a spread needs, so
/// the scorer declines them and the engine walks candidates document by document — the same trade
/// <see cref="WindowBm25Scorer"/> makes. On a corpus whose queries reach most documents that is
/// the slow row of the table, not a defect.
/// </para>
/// </remarks>
public sealed class SpreadBm25Scorer : ITextScorer, IScoreExplainer, ITermOverlapScorer
{
    private readonly double _k1;
    private readonly double _b;
    private readonly int _windowSize;
    private readonly double _alpha;
    private readonly QueryTermWeighting _queryTerms;
    private readonly double _saturation;

    /// <param name="k1">Term-frequency saturation applied to the effective frequency.</param>
    /// <param name="b">Document-length normalization, in <c>[0, 1]</c>, exactly as in BM25.</param>
    /// <param name="windowSize">
    /// Width, in tokens, of the non-overlapping windows a spread is counted over. <c>8</c> matches
    /// the context window the library's PMI graph uses, so a spread and an association are counted
    /// over the same span.
    /// </param>
    /// <param name="alpha">
    /// Balance between the raw count (<c>0</c>, which reproduces <see cref="Bm25Scorer"/> exactly)
    /// and the distinct-window spread (<c>1</c>). Values between the two interpolate
    /// geometrically.
    /// </param>
    /// <param name="queryTermWeighting">
    /// How a term repeated in the query is treated. Default
    /// <see cref="QueryTermWeighting.Distinct"/>, as everywhere else in this library.
    /// </param>
    /// <param name="saturationConstant">
    /// Whether the numerator carries <c>k1 + 1</c> (the default) or 1, the same convention switch
    /// <see cref="Bm25Scorer"/> has.
    /// </param>
    public SpreadBm25Scorer(
        double k1 = 1.5,
        double b = 0.75,
        int windowSize = 8,
        double alpha = 1.0,
        QueryTermWeighting queryTermWeighting = QueryTermWeighting.Distinct,
        bool saturationConstant = true)
    {
        if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
            throw new ArgumentOutOfRangeException(nameof(k1), k1, "k1 must be non-negative and finite.");
        if (double.IsNaN(b) || double.IsInfinity(b) || b is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be within [0, 1] and finite.");
        if (windowSize < 1)
            throw new ArgumentOutOfRangeException(nameof(windowSize), windowSize, "windowSize must be at least 1.");
        if (double.IsNaN(alpha) || double.IsInfinity(alpha) || alpha is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(alpha), alpha, "alpha must be within [0, 1] and finite.");

        _k1 = k1;
        _b = b;
        _windowSize = windowSize;
        _alpha = alpha;
        _queryTerms = queryTermWeighting;
        _saturation = saturationConstant ? k1 + 1.0 : 1.0;
    }

    /// <inheritdoc />
    public string Name => "BM25 spread";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;

        if (documentCount == 0 || documentLength == 0 || averageLength <= 0)
            return 0;

        double normalization = 1.0 - _b + (_b * documentLength / averageLength);
        double score = 0;

        var terms = Terms(queryTerms);

        for (int i = 0; i < terms.Count; i++)
        {
            string term = terms[i];
            int tf = index.TermFrequency(documentId, term);

            if (tf == 0)
                continue;

            int df = index.DocumentFrequency(term);
            double idf = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));

            // The raw-count endpoint does not read positions at all: alpha = 0 is the control, and it
            // must cost what BM25 costs, not a positions decode it then multiplies by zero.
            double effective = _alpha <= 0
                ? tf
                : EffectiveFrequency(tf, Spread(index.GetTermPositions(documentId, term)));

            score += idf * effective * _saturation / (effective + (_k1 * normalization));
        }

        return score;
    }

    /// <summary>
    /// The effective frequency at this scorer's <c>α</c>: the raw count at <c>0</c>, the spread at
    /// <c>1</c>, a geometric blend in between.
    /// </summary>
    /// <remarks>
    /// The endpoints are special-cased rather than sent through <see cref="Math.Pow"/> so that
    /// <c>α = 0</c> is <b>exactly</b> <see cref="Bm25Scorer"/> to the bit — the degeneration the
    /// tests pin — and the measured α = 1 path does no exponentiation at all.
    /// </remarks>
    private double EffectiveFrequency(int termFrequency, int spread)
    {
        if (_alpha <= 0)
            return termFrequency;

        if (_alpha >= 1)
            return spread;

        return Math.Pow(termFrequency, 1.0 - _alpha) * Math.Pow(spread, _alpha);
    }

    /// <summary>
    /// How many distinct windows of width <see cref="_windowSize"/> hold at least one occurrence.
    /// </summary>
    /// <remarks>
    /// The positions are sorted ascending by the index contract, so one pass suffices: increment the
    /// count whenever the window index changes. An empty list is zero occurrences, which the caller
    /// has already excluded, but returning zero here keeps the function total.
    /// </remarks>
    private int Spread(IReadOnlyList<int> positions)
    {
        if (positions.Count == 0)
            return 0;

        int windows = 1;
        int last = positions[0] / _windowSize;

        for (int i = 1; i < positions.Count; i++)
        {
            int window = positions[i] / _windowSize;

            if (window != last)
            {
                windows++;
                last = window;
            }
        }

        return windows;
    }

    /// <summary>
    /// The query terms as this scorer reads them: deduplicated, or kept whole so a repeated term is
    /// scored once per occurrence.
    /// </summary>
    private IReadOnlyList<string> Terms(IReadOnlyList<string> queryTerms) =>
        _queryTerms == QueryTermWeighting.QueryFrequency
            ? queryTerms
            : queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="TermContribution.TermFrequency"/> reports the <b>effective</b> frequency the
    /// formula used, and when <c>α &gt; 0</c> each term's spread is in
    /// <see cref="ScoreExplanation.Parameters"/> as <c>spread:&lt;term&gt;</c>. The reported total
    /// always equals <see cref="Score"/>.
    /// </remarks>
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;

        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;
        double normalization = 1.0 - _b + (_b * lengthRatio);

        var contributions = new List<TermContribution>();
        var spreads = new Dictionary<string, double>(StringComparer.Ordinal);
        double total = 0;

        if (documentCount > 0 && documentLength > 0 && averageLength > 0)
        {
            var terms = Terms(queryTerms);

            for (int i = 0; i < terms.Count; i++)
            {
                string term = terms[i];
                int tf = index.TermFrequency(documentId, term);

                if (tf == 0)
                    continue;

                int df = index.DocumentFrequency(term);
                double idf = Math.Log(1.0 + ((documentCount - df + 0.5) / (df + 0.5)));
                double effective;

                if (_alpha <= 0)
                {
                    effective = tf;
                }
                else
                {
                    int spread = Spread(index.GetTermPositions(documentId, term));
                    spreads[term] = spread;
                    effective = EffectiveFrequency(tf, spread);
                }

                double termScore = idf * effective * _saturation / (effective + (_k1 * normalization));
                contributions.Add(new TermContribution(term, effective, df, idf, termScore));
                total += termScore;
            }
        }

        var parameters = new Dictionary<string, double>
        {
            ["k1"] = _k1,
            ["b"] = _b,
            ["saturationConstant"] = _saturation,
            ["windowSize"] = _windowSize,
            ["alpha"] = _alpha,
        };

        foreach (var (term, spread) in spreads)
            parameters[$"spread:{term}"] = spread;

        return new ScoreExplanation(
            documentId,
            Name,
            total,
            documentLength,
            averageLength,
            lengthRatio,
            normalization,
            contributions,
            parameters);
    }
}
