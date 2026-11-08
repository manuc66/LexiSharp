using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// BM25VA — Okapi BM25 whose length normalization is shared with the document's
/// <b>verboseness</b>, the average number of times a term is repeated in the document
/// (Lipani, Lupu and Hanbury, 2015).
/// </summary>
/// <remarks>
/// <para>
/// <b>The formula.</b> Standard BM25 normalizes a term frequency by the document's length alone.
/// BM25VA replaces that normalization with a two-component one:
/// <c>K_d = k1 · ((1 − b) · v̂_d + b · |d| / avgdl)</c>, where
/// <c>v_d = |d| / |T_d|</c> is the document's verboseness (its length divided by its number of
/// <i>distinct</i> terms) and <c>v̂_d = v_d / E_D[v_d]</c> is that value pivoted by the corpus's
/// elite mean verboseness. The score is the usual sum:
/// <c>Σ_t idf(t) · tf · (k1 + 1) / (tf + K_d)</c>.
/// </para>
/// <para>
/// <b><c>b</c> changes meaning, and that is the point.</b> In this scorer <c>b = 1</c> is exactly
/// <see cref="Bm25Scorer"/> — the verboseness term carries a zero weight — while <c>b = 0</c>
/// normalizes by verboseness alone, so document length drops out of the ranking entirely. The
/// parameter is the same knob, not a second one: it interpolates between the two statistics
/// instead of between “some normalization” and “none”. A caller who reads <c>b = 0</c> as “no
/// normalization” will be surprised, which is why this class spells the meaning out instead of
/// letting the familiar default speak for itself.
/// </para>
/// <para>
/// <b>Why it can help, in one sentence.</b> A document that repeats a handful of words inflates
/// every term frequency it contains, and BM25 rewards that repetition as if it were evidence;
/// verboseness names the inflation directly instead of inferring it from length, and it is the
/// statistic the verboseness hypothesis of Robertson and Zaragoza (2009) is about.
/// </para>
/// <para>
/// <b>The cost is a corpus pass and a slower scoring path, and both are deliberate.</b> The
/// verboseness of a document is not stored by any index this library ships, so it is computed
/// once per index from <see cref="IReadOnlyTextIndex.Documents"/> and
/// <see cref="IReadOnlyTextIndex.GetTerms"/>, then cached against the index's
/// <see cref="IReadOnlyTextIndex.Count"/> and <see cref="IReadOnlyTextIndex.CorpusTokenCount"/>.
/// The cache is rebuilt when either moves, and a document missing from it (added since the pass)
/// is measured on the spot rather than scored with a stale value. The scorer does <b>not</b>
/// implement <see cref="IQueryPlannableScorer"/>: the term-at-a-time and posting-array paths
/// carry a term frequency and a length, not a verboseness, so they are declined and the engine
/// walks candidates document by document — the same trade <see cref="WindowBm25Scorer"/> makes
/// for the same reason.
/// </para>
/// <para>
/// <b>What is not claimed.</b> This is the published BM25VA arithmetic, not a new model, and the
/// literature's gains are corpus-dependent: the pairs that repeat are the ones whose verboseness
/// differs from their length, which is a property of the collection. The opt-in rows of the
/// evaluation harness exist to measure that on the corpus at hand rather than to assume it.
/// </para>
/// </remarks>
public sealed class Bm25VaScorer : ITextScorer, IScoreExplainer, ITermOverlapScorer
{
    private readonly double _k1;
    private readonly double _b;
    private readonly QueryTermWeighting _queryTerms;

    /// <summary>
    /// The saturation constant <c>k1 + 1</c>, or 1 when the caller asked for the formula without
    /// it. It scales every score of a query by the same factor, so it changes no ranking.
    /// </summary>
    private readonly double _saturation;

    /// <summary>
    /// The corpus's verboseness table, published with a volatile write once complete — the same
    /// one-field-under-a-lock pattern <see cref="Bm25Scorer"/> uses for its reciprocal table, and
    /// for the same reason: one scorer is shared by every query an engine runs.
    /// </summary>
    private VerbosenessTable? _verboseness;

    private readonly Lock _verbosenessLock = new();

    /// <param name="k1">Term-frequency saturation: higher values let frequent terms contribute more.</param>
    /// <param name="b">
    /// Balance between verboseness normalization (<c>0</c>) and document-length normalization
    /// (<c>1</c>). <b>Unlike <see cref="Bm25Scorer"/>, <c>0</c> does not disable normalization</b>:
    /// it normalizes by verboseness alone. <c>1</c> reproduces <see cref="Bm25Scorer"/> exactly.
    /// </param>
    /// <param name="queryTermWeighting">
    /// How a term repeated in the query is treated. Default
    /// <see cref="QueryTermWeighting.Distinct"/>, as everywhere else in this library.
    /// </param>
    /// <param name="saturationConstant">
    /// Whether the numerator carries <c>k1 + 1</c> (the default) or 1. The same convention switch
    /// as <see cref="Bm25Scorer"/>'s, and it scales a query's scores without moving its ranking.
    /// </param>
    public Bm25VaScorer(
        double k1 = 1.5,
        double b = 0.75,
        QueryTermWeighting queryTermWeighting = QueryTermWeighting.Distinct,
        bool saturationConstant = true)
    {
        if (double.IsNaN(k1) || double.IsInfinity(k1) || k1 < 0)
            throw new ArgumentOutOfRangeException(nameof(k1), k1, "k1 must be non-negative and finite.");
        if (double.IsNaN(b) || double.IsInfinity(b) || b is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(b), b, "b must be within [0, 1] and finite.");

        _k1 = k1;
        _b = b;
        _queryTerms = queryTermWeighting;
        _saturation = saturationConstant ? k1 + 1.0 : 1.0;
    }

    /// <inheritdoc />
    public string Name => "BM25VA";

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

        VerbosenessTable table = VerbosenessFor(index);

        if (table.Average <= 0)
            return 0;

        // Loop-invariant: the document is the same for every term.
        double normalization = Normalization(_b, documentLength, table.VerbosenessOf(documentId, index), table.Average, averageLength);
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

            score += idf * tf * _saturation / (tf + (_k1 * normalization));
        }

        return score;
    }

    /// <summary>
    /// The two-component normalization <c>(1 − b) · v̂_d + b · |d| / avgdl</c>, spelled once so
    /// the scoring path, the plan and the explanation cannot disagree.
    /// </summary>
    /// <remarks>
    /// At <c>b = 1</c> the first product is exactly zero and this is
    /// <see cref="Bm25Scorer"/>'s <c>|d| / avgdl</c> to the bit, which is what the degeneration
    /// test pins: the addition of a zero changes nothing, and multiplication by one is exact.
    /// </remarks>
    private static double Normalization(
        double b,
        int documentLength,
        double verboseness,
        double averageVerboseness,
        double averageLength) =>
        ((1.0 - b) * (verboseness / averageVerboseness)) + (b * ((double)documentLength / averageLength));

    /// <summary>
    /// The query terms as this scorer reads them: deduplicated, or kept whole so a repeated term
    /// is scored once per occurrence.
    /// </summary>
    private IReadOnlyList<string> Terms(IReadOnlyList<string> queryTerms) =>
        _queryTerms == QueryTermWeighting.QueryFrequency
            ? queryTerms
            : queryTerms is DistinctTermList ? queryTerms : TermDeduplicator.Distinct(queryTerms);

    /// <summary>
    /// The verboseness table for this index, rebuilt when the index's size or token count moved.
    /// </summary>
    private VerbosenessTable VerbosenessFor(IReadOnlyTextIndex index)
    {
        VerbosenessTable? current = Volatile.Read(ref _verboseness);

        if (current is not null && current.IsFor(index))
            return current;

        lock (_verbosenessLock)
        {
            current = _verboseness;

            if (current is not null && current.IsFor(index))
                return current;

            var built = VerbosenessTable.Build(index);

            // Published only once the table is complete, which is the whole reason this is one
            // field written under a lock rather than several written in sequence.
            Volatile.Write(ref _verboseness, built);

            return built;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Terms absent from the document contribute nothing and are omitted from
    /// <see cref="ScoreExplanation.Terms"/>. The reported
    /// <see cref="ScoreExplanation.TotalScore"/> always equals <see cref="Score"/> for the same
    /// inputs.
    /// </remarks>
    public ScoreExplanation Explain(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(queryTerms);

        int documentCount = index.StatisticDocumentCount;
        int documentLength = index.DocumentLength(documentId);
        double averageLength = index.AverageDocumentLength;

        double lengthRatio = averageLength > 0 ? documentLength / averageLength : 0;

        VerbosenessTable table = VerbosenessFor(index);
        double verboseness = table.VerbosenessOf(documentId, index);
        double normalization = averageLength > 0 && table.Average > 0
            ? Normalization(_b, documentLength, verboseness, table.Average, averageLength)
            : 1.0 - _b;

        var contributions = new List<TermContribution>();
        double total = 0;

        if (documentCount > 0 && documentLength > 0 && averageLength > 0 && table.Average > 0)
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
                double termScore = idf * tf * _saturation / (tf + (_k1 * normalization));

                contributions.Add(new TermContribution(term, tf, df, idf, termScore));
                total += termScore;
            }
        }

        var parameters = new Dictionary<string, double>
        {
            ["k1"] = _k1,
            ["b"] = _b,
            ["saturationConstant"] = _saturation,
            ["verboseness"] = verboseness,
            ["averageVerboseness"] = table.Average,
        };

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

    /// <summary>
    /// The corpus's verboseness table: every document's <c>|d| / |T_d|</c> and the elite mean
    /// <c>E_D[v_d]</c> over the documents that hold at least one term.
    /// </summary>
    /// <remarks>
    /// The mean is the <b>elite</b> average of the normalization literature — the mean of the
    /// per-document ratios, not the ratio of the corpus totals. A document with no term is
    /// skipped rather than divided by zero: it can never score under any term-overlap scorer, and
    /// including it would drag the mean toward zero for a document count that contributes nothing.
    /// </remarks>
    private sealed class VerbosenessTable
    {
        private readonly IReadOnlyTextIndex _index;
        private readonly int _documentCount;
        private readonly long _tokenCount;
        private readonly Dictionary<string, double> _byDocument;

        private VerbosenessTable(
            IReadOnlyTextIndex index,
            int documentCount,
            long tokenCount,
            double average,
            Dictionary<string, double> byDocument)
        {
            _index = index;
            _documentCount = documentCount;
            _tokenCount = tokenCount;
            Average = average;
            _byDocument = byDocument;
        }

        public double Average { get; }

        /// <summary>
        /// Whether this table was built for the index as it currently stands. Count and token
        /// count are both <c>O(1)</c> and move on every add, remove and replacement that changes
        /// what the mean would be; a replacement that changes neither is caught by the per-document
        /// fallback instead.
        /// </summary>
        public bool IsFor(IReadOnlyTextIndex index) =>
            ReferenceEquals(_index, index) &&
            _documentCount == index.Count &&
            _tokenCount == index.CorpusTokenCount;

        public double VerbosenessOf(string documentId, IReadOnlyTextIndex index)
        {
            if (_byDocument.TryGetValue(documentId, out double known))
                return known;

            // A document indexed after the pass. Measure it now rather than serve a stale value;
            // the next rebuild will fold it into the mean.
            int length = index.DocumentLength(documentId);
            if (length == 0)
                return 0;

            int distinct = DistinctTermCount(index.GetTerms(documentId));
            return distinct == 0 ? 1.0 : (double)length / distinct;
        }

        public static VerbosenessTable Build(IReadOnlyTextIndex index)
        {
            var byDocument = new Dictionary<string, double>(index.Count, StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            double total = 0;

            foreach (SearchDocument document in index.Documents)
            {
                int length = index.DocumentLength(document.Id);

                if (length == 0)
                    continue;

                IReadOnlyList<string> terms = index.GetTerms(document.Id);

                seen.Clear();

                for (int i = 0; i < terms.Count; i++)
                    seen.Add(terms[i]);

                if (seen.Count == 0)
                    continue;

                double verboseness = (double)length / seen.Count;

                byDocument[document.Id] = verboseness;
                total += verboseness;
            }

            double average = byDocument.Count == 0 ? 0 : total / byDocument.Count;

            return new VerbosenessTable(index, index.Count, index.CorpusTokenCount, average, byDocument);
        }

        private static int DistinctTermCount(IReadOnlyList<string> terms)
        {
            if (terms.Count <= 1)
                return terms.Count;

            var seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < terms.Count; i++)
                seen.Add(terms[i]);

            return seen.Count;
        }
    }
}
