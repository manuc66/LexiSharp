using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// BM25F ranking: BM25 over a document split into weighted fields, so a term in a title can count
/// for more than the same term in a body.
/// </summary>
/// <remarks>
/// <para>
/// The formula is BM25F as given in Robertson, Zaragoza &amp; Taylor (<i>New formal models of
/// BM25</i>, 2004). For each query term <c>t</c>:
/// </para>
/// <code>
/// t&#771;(t,d)  = &#8721;_f  w_f &#183; tf(t,d,f) / (1 &#8722; b + b &#183; |d_f| / avgf)
/// |D(t,d)|   = &#8721;_f  w_f &#183; |d_f|       over the fields where tf(t,d,f) &gt; 0
/// W         = &#8721;_f  w_f &#183; avgf
///
/// score(q,d) = &#8721;_t  idf(t) &#183; t&#771;(t,d) &#183; (k1 + 1)
///                  / (k1 &#183; (1 &#8722; b + b &#183; |D(t,d)| / W) + t&#771;(t,d))
/// </code>
/// <para>
/// Two deliberate departures from the paper, so the behaviour is unambiguous:
/// </para>
/// <list type="bullet">
/// <item>the paper carries one <c>b_f</c> per field; here a single <c>b</c> applies to all of them;</item>
/// <item>
/// the paper rescales <c>k1</c> into <c>k&#770;1</c> to account for the number of fields; here
/// <c>k&#770;1 = k1</c>, which keeps the saturation comparable with <see cref="Bm25Scorer"/>'s.
/// </item>
/// </list>
/// <para>
/// <c>idf</c> is the document-level inverse document frequency, the same one
/// <see cref="Bm25Scorer"/> uses, so a term present in a title and a body of the same document
/// counts once toward it.
/// </para>
/// <para>
/// The length entering the denominator is <b>the length of the fields that contain the term</b>,
/// not the whole document: a term appearing only in a short title is not penalized for the length
/// of a long body it never appears in. That is the substantive difference from running
/// <see cref="Bm25Scorer"/> over the flattened text.
/// </para>
/// <para>
/// <b>Nothing here claims that weighting retrieves better.</b> A field weighting is a hypothesis
/// about your corpus; measure it with <see cref="LexiSharp.Benchmarking.CorpusBenchmark"/> on your
/// own data. With every weight at <c>1</c> and a single field this is a length-normalized BM25
/// variant, not <see cref="Bm25Scorer"/> itself.
/// </para>
/// <para>
/// Requires an index that tracks per-field statistics (<see cref="ITextIndex.HasFieldStatistics"/>).
/// Scoring against one that does not throws <see cref="NotSupportedException"/> naming the index,
/// rather than quietly ranking on zeros.
/// </para>
/// </remarks>
public sealed class Bm25FScorer : IScoreExplainer, ITermOverlapScorer, IQueryPlannableScorer
{
    private static readonly Dictionary<string, double> EmptyWeights = new(StringComparer.Ordinal);

    private readonly double _k1;
    private readonly double _b;
    private readonly IReadOnlyDictionary<string, double> _weights;

    /// <param name="k1">Term-frequency saturation; higher values let frequent terms contribute more.</param>
    /// <param name="b">Document-length normalization in <c>[0, 1]</c>, applied to every field.</param>
    /// <param name="fieldWeights">
    /// Weight per field name. A field left out keeps the neutral weight of <c>1</c>, so an unset map
    /// means « treat every field equally », not « use the main text only ».
    /// </param>
    public Bm25FScorer(
        double k1 = 1.2,
        double b = 0.75,
        IReadOnlyDictionary<string, double>? fieldWeights = null)
    {
        if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(k1), k1, "k1 must be non-negative and finite.");
        }

        if (double.IsNaN(b) || double.IsInfinity(b) || b is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(b), b, "b must be within [0, 1] and finite.");
        }

        _k1 = k1;
        _b = b;
        _weights = ValidateWeights(fieldWeights);
    }

    /// <summary>Builds a scorer from a preset or hand-tuned <see cref="Bm25FParameters"/> profile.</summary>
    public Bm25FScorer(Bm25FParameters parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);

        if (double.IsNaN(parameters.K1) || double.IsInfinity(parameters.K1) || parameters.K1 < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters), parameters, "k1 must be non-negative and finite.");
        }

        if (double.IsNaN(parameters.B) || double.IsInfinity(parameters.B) ||
            parameters.B is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(parameters), parameters, "b must be within [0, 1] and finite.");
        }

        _k1 = parameters.K1;
        _b = parameters.B;
        _weights = ValidateWeights(parameters.FieldWeights);
    }

    private static IReadOnlyDictionary<string, double> ValidateWeights(
        IReadOnlyDictionary<string, double>? weights)
    {
        if (weights is null)
            return EmptyWeights;

        foreach (var (field, weight) in weights)
        {
            TextFields.Validate(field, nameof(weights));

            if (double.IsNaN(weight) || double.IsInfinity(weight) || weight < 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(weights), weight,
                    $"The weight of field '{field}' must be non-negative and finite.");
            }
        }

        return weights;
    }

    /// <inheritdoc />
    public string Name => "BM25F";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        if (index.Count == 0)
            return 0;

        var fields = ResolveFields(index);
        var terms = queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

        double score = 0;

        for (int i = 0; i < terms.Count; i++)
        {
            var geometry = Geometry(documentId, terms[i], fields, index, _b);
            score += Combine(geometry, InverseDocumentFrequency(index.StatisticDocumentCount, geometry.DocumentFrequency), _k1, _b);
        }

        return score;
    }

    ISearchQueryPlan IQueryPlannableScorer.CreatePlan(IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(queryTerms);
        ArgumentNullException.ThrowIfNull(index);

        return new Bm25FQueryPlan(queryTerms, index, _k1, _b, _weights);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <see cref="TermContribution.TermFrequency"/> is the field-summed, length-normalized
    /// t&#771; of the formula, not a raw count: it is fractional, and it is the number the term's
    /// contribution was actually built from. The reported
    /// <see cref="ScoreExplanation.LengthNormalization"/> is the document-wide weighted length
    /// ratio, whereas each term's own denominator uses the length of the fields that contain
    /// <i>that</i> term — so it is not the exact divisor of every term. The reported
    /// <see cref="ScoreExplanation.TotalScore"/> always equals <see cref="Score"/>.
    /// </remarks>
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, ITextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;
        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;

        var contributions = new List<TermContribution>();
        double total = 0;

        if (documentCount > 0 && documentLength > 0)
        {
            var fields = ResolveFields(index);
            var terms = queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

            for (int i = 0; i < terms.Count; i++)
            {
                var geometry = Geometry(documentId, terms[i], fields, index, _b);

                // A term in no field of this document contributes nothing and is omitted, as in the
                // other explainers.
                if (!geometry.Found)
                    continue;

                double idf = InverseDocumentFrequency(documentCount, geometry.DocumentFrequency);
                double termScore = Combine(geometry, idf, _k1, _b);

                contributions.Add(new TermContribution(
                    terms[i], geometry.NormalizedFrequency, geometry.DocumentFrequency, idf, termScore));

                total += termScore;
            }
        }

        var parameters = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["k1"] = _k1,
            ["b"] = _b,
        };

        foreach (var (field, weight) in _weights)
            parameters[$"weight.{field}"] = weight;

        return new ScoreExplanation(
            documentId,
            Name,
            total,
            documentLength,
            averageLength,
            lengthRatio,
            lengthRatio,
            contributions,
            parameters);
    }

    /// <summary>
    /// Resolves the fields the scorer reads, in the index's own stable order, each carrying its
    /// weight and corpus average length. Done once per call so the per-term loop is pure lookups.
    /// </summary>
    private List<FieldStats> ResolveFields(ITextIndex index)
    {
        RequireFieldStatistics(index);

        var fields = new List<FieldStats>();

        foreach (string field in index.Fields)
            fields.Add(new FieldStats(field, WeightOf(field), index.AverageFieldLength(field)));

        return fields;
    }

    /// <summary>
    /// A field's weight, defaulting to <c>1</c> when unconfigured. The default matters: reading it
    /// out of the map with <c>TryGetValue</c> would yield <c>0</c> for every unmentioned field, and
    /// a zero weight means « ignore this field » — so an unconfigured scorer would silently score
    /// nothing at all.
    /// </summary>
    private double WeightOf(string field) =>
        _weights.TryGetValue(field, out double weight) ? weight : 1.0;

    /// <summary>One field's identity, its weight, and its corpus average length.</summary>
    private readonly record struct FieldStats(string Name, double Weight, double AverageLength)
    {
        /// <summary>Weight times average length: this field's share of the corpus baseline W.</summary>
        public double WeightedAverageLength => Weight * AverageLength;
    }

    /// <summary>
    /// The per-field sums one term needs, before the scoring function is applied. Shared by
    /// <see cref="Score"/>, <see cref="Explain"/> and the query plan so all three agree bit for bit.
    /// </summary>
    private readonly record struct TermGeometry(
        double NormalizedFrequency,
        double WeightedLength,
        double CorpusBaseline,
        int DocumentFrequency)
    {
        /// <summary>
        /// Whether the term occurs in at least one field of the document. A false
        /// <see cref="NormalizedFrequency"/> is what lets this scorer satisfy
        /// <see cref="ITermOverlapScorer"/>.
        /// </summary>
        public bool Found => NormalizedFrequency > 0;
    }

    private static TermGeometry Geometry(
        string documentId,
        string term,
        List<FieldStats> fields,
        ITextIndex index,
        double b)
    {
        double normalizedFrequency = 0;
        double weightedLength = 0;
        double corpusBaseline = 0;

        for (int f = 0; f < fields.Count; f++) // NOSONAR:S3267
        {
            var field = fields[f];
            corpusBaseline += field.WeightedAverageLength;

            if (field.Weight <= 0)
                continue;

            int tf = index.FieldTermFrequency(documentId, field.Name, term);

            if (tf == 0)
                continue;

            int fieldLength = index.FieldLength(documentId, field.Name);

            // A field nobody filled averages 0; normalizing by it would explode, so the length
            // correction degenerates to 1 for such a field.
            double correction = field.AverageLength > 0
                ? 1.0 - b + b * fieldLength / field.AverageLength
                : 1.0;

            normalizedFrequency += field.Weight * tf / correction;
            weightedLength += field.Weight * fieldLength;
        }

        return new TermGeometry(
            normalizedFrequency,
            weightedLength,
            corpusBaseline,
            index.DocumentFrequency(term));
    }

    private static double Combine(TermGeometry geometry, double idf, double k1, double b)
    {
        double normalizedFrequency = geometry.NormalizedFrequency;

        if (normalizedFrequency <= 0)
            return 0;

        // A corpus where every field is empty or weightless has no baseline to normalize against,
        // so the length correction is dropped rather than divide by zero.
        double lengthCorrection = geometry.CorpusBaseline > 0
            ? 1.0 - b + b * geometry.WeightedLength / geometry.CorpusBaseline
            : 1.0;

        return idf * normalizedFrequency * (k1 + 1.0)
            / (k1 * lengthCorrection + normalizedFrequency);
    }

    private static double InverseDocumentFrequency(int documentCount, int documentFrequency) =>
        Math.Log(1.0 + (documentCount - documentFrequency + 0.5) / (documentFrequency + 0.5));

    private static void RequireFieldStatistics(ITextIndex index)
    {
        if (index.HasFieldStatistics)
            return;

        throw new NotSupportedException(
            $"BM25F needs per-field statistics, and {index.GetType().Name} has none, so no field " +
            "but the default one can be read. Build the index over SearchDocument.TextFields " +
            $"(e.g. {nameof(LexiSharp.Indexing.InMemoryTextIndex)}), or use {nameof(Bm25Scorer)}.");
    }

    /// <summary>
    /// Pre-resolves the field list and the per-term idf once per query, so scoring a document is
    /// only index lookups. The arithmetic is the same shared code, which is what keeps a planned
    /// search bit-identical to an unplanned one.
    /// </summary>
    private sealed class Bm25FQueryPlan : ISearchQueryPlan
    {
        private readonly ITextIndex _index;
        private readonly string[] _terms;
        private readonly double[] _idf;
        private readonly List<FieldStats> _fields;
        private readonly double _k1;
        private readonly double _b;

        public Bm25FQueryPlan(
            IReadOnlyList<string> queryTerms,
            ITextIndex index,
            double k1,
            double b,
            IReadOnlyDictionary<string, double> weights)
        {
            _index = index;
            _k1 = k1;
            _b = b;

            var terms = queryTerms is DistinctTermList
                ? queryTerms
                : TermDeduplicator.Distinct(queryTerms);

            _terms = new string[terms.Count];
            _idf = new double[terms.Count];

            var fields = new List<FieldStats>();

            if (index.Count > 0)
            {
                RequireFieldStatistics(index);

                foreach (string field in index.Fields)
                {
                    double weight = weights.TryGetValue(field, out double configured) ? configured : 1.0;
                    fields.Add(new FieldStats(field, weight, index.AverageFieldLength(field)));
                }
            }

            _fields = fields;

            for (int i = 0; i < terms.Count; i++)
            {
                _terms[i] = terms[i];
                _idf[i] = index.StatisticDocumentCount == 0
                    ? 0
                    : InverseDocumentFrequency(index.StatisticDocumentCount, index.DocumentFrequency(terms[i]));
            }
        }

        /// <inheritdoc />
        public double Score(string documentId)
        {
            if (_index.DocumentLength(documentId) == 0)
                return 0;

            double score = 0;

            for (int i = 0; i < _terms.Length; i++)
            {
                score += Combine(
                    Geometry(documentId, _terms[i], _fields, _index, _b),
                    _idf[i],
                    _k1,
                    _b);
            }

            return score;
        }
    }
}
