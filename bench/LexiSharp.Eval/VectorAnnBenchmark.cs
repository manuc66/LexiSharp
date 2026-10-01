using System.Diagnostics;
using System.Globalization;
using LexiSharp.Core;
using LexiSharp.Postgres;

namespace LexiSharp.Eval;

/// <summary>
/// The approximate-nearest-neighbour cost curve, measured through the real pgvector engine rather
/// than asserted: recall of the ANN ranking against an exact top-k computed in process, and the
/// wall-clock each <c>ef_search</c> costs.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "always route to the dense engine" is a latency decision, and a latency
/// decision needs a latency measurement. The recall column is the one that makes the measurement
/// trustworthy: pgvector's own scoring is compared against an exact search over the same cached
/// vectors, so a drop in recall is the index and not a second, different scoring function.
/// </para>
/// <para>
/// The embeddings are read from the same cache the in-memory dense lane uses, so the two lanes
/// differ only in where the distance is computed. Both are approximate in principle; only one is
/// approximate in this table, and the column says how much.
/// </para>
/// <para>
/// The latency is the fastest pass, and that choice is specific to this host: a pass that loses the
/// CPU is only ever slower, so the minimum is the number the engine owes a query. Three rebuilds of
/// one identical table measured 2.79 / 15.69 / 2.80 ms, which is not a spread a mean can absorb.
/// </para>
/// <para>
/// A step of roughly 15 ms appears at the larger <c>ef_search</c> values, reproducibly, and moves
/// between sizes across runs. Recall is already 0.99 either side of it, so the curve has two
/// regimes and the index is worth having below the step and not above it. <b>Unverified:</b>
/// whether that step is the planner preferring an exact scan for a small LIMIT, the index
/// degrading, or the per-value table rebuild warming differently. It was not isolated.
/// </para>
/// </remarks>
internal static class VectorAnnBenchmark
{
    /// <summary>One measured point on the curve.</summary>
    internal sealed record AnnPoint(int EfSearch, double RecallAtK, double MillisecondsPerQuery)
    {
        public override string ToString() =>
            EfSearch.ToString(CultureInfo.InvariantCulture).PadLeft(8)
            + RecallAtK.ToString("0.0000", CultureInfo.InvariantCulture).PadLeft(12)
            + (MillisecondsPerQuery.ToString("0.00", CultureInfo.InvariantCulture) + " ms").PadLeft(16);
    }

    /// <summary>
    /// Runs the curve and returns one point per requested <c>ef_search</c>.
    /// </summary>
    /// <param name="connectionString">A PostgreSQL connection string (Npgsql format).</param>
    /// <param name="documents">The documents to index, in the order their vectors were cached.</param>
    /// <param name="documentVectors">One vector per document, aligned with <paramref name="documents"/>.</param>
    /// <param name="queries">The queries to score, with their vectors.</param>
    /// <param name="efSearchValues">The candidate-list sizes to measure.</param>
    /// <param name="topK">How deep to compare. Ten, the depth every published figure uses.</param>
    /// <param name="passes">Timed passes; the reported time is the median, and the first is discarded.</param>
    /// <remarks>
    /// A table is dropped and rebuilt per point, so one point's index can never answer another's
    /// query. That is the expensive part of the run and the reason a sweep is a list of sizes
    /// rather than a default.
    /// </remarks>
    public static IReadOnlyList<AnnPoint> Measure(
        string connectionString,
        IReadOnlyList<SearchDocument> documents,
        IReadOnlyList<float[]> documentVectors,
        IReadOnlyList<(string Text, float[] Vector)> queries,
        IReadOnlyList<int> efSearchValues,
        int topK = 10,
        int passes = 7)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(documents);
        ArgumentNullException.ThrowIfNull(documentVectors);
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(efSearchValues);

        if (documents.Count != documentVectors.Count)
        {
            throw new ArgumentException(
                $"{documents.Count} documents but {documentVectors.Count} vectors: the ANN table is built from the " +
                "pairing, and a misalignment would index one document's text under another's embedding.",
                nameof(documentVectors));
        }

        int dimension = documentVectors[0].Length;
        var documentTexts = documents.Select(document => document.Text).ToList();
        var provider = new CachedEmbeddingProvider(
            documentTexts.Zip(documentVectors, (text, vector) => (text, vector)).ToList(),
            queries,
            dimension);

        // The exact top-k, computed here, in process. Cosine over normalized vectors, so the dot
        // product is the similarity and no rescaling is involved.
        var exact = queries
            .Select(query => documents
                .Select((document, index) => (document.Id, similarity: Dot(query.Vector, documentVectors[index])))
                .OrderByDescending(candidate => candidate.similarity)
                .Take(topK)
                .Select(candidate => candidate.Id)
                .ToArray())
            .ToList();

        var points = new List<AnnPoint>(efSearchValues.Count);

        foreach (int efSearch in efSearchValues)
        {
            // A table per point. Sharing one across sizes would let a larger ef_search's index
            // answer a smaller one's query, and the curve would be a property of the last build.
            var options = new PostgresVectorOptions
            {
                Table = "lexisharp_ann_" + efSearch.ToString(CultureInfo.InvariantCulture),
                Dimension = dimension,
                IndexMethod = VectorIndexMethod.Hnsw,
                Distance = VectorDistance.Cosine,
                HnswEfSearch = efSearch,
            };

            var engine = new PostgresVectorSearchEngine(connectionString, provider, options);

            try
            {
                engine.Index(documents);

                // Recall accumulates over every pass; the reported figure is the mean, so a
                // one-off tie-break difference between two equal-score documents does not move it.
                var recalls = new List<double>(passes);
                var times = new List<double>(passes);

                for (int pass = 0; pass < passes; pass++)
                {
                    int hits = 0;
                    var stopwatch = Stopwatch.StartNew();

                    for (int q = 0; q < queries.Count; q++)
                    {
                        var returned = engine.Search(queries[q].Text, new SearchOptions(topK))
                            .Select(result => result.DocumentId)
                            .ToList();

                        foreach (string id in returned)
                        {
                            if (exact[q].Contains(id))
                                hits++;
                        }
                    }

                    stopwatch.Stop();

                    // Recall@K is hits over queries*K, not over queries: hits is summed across every
                    // query's top-K, so dividing by the query count yields a mean of K values that
                    // can exceed 1 whenever exact top-Ks overlap. The denominator is the number of
                    // returned slots, which is also what a top-K recall is defined against.
                    recalls.Add((double)hits / (queries.Count * topK));

                    // The first pass builds the connection and warms the index, and its time is the
                    // cost of a cold process rather than of a query.
                    if (pass > 0)
                        times.Add(stopwatch.Elapsed.TotalMilliseconds / queries.Count);
                }

                // The minimum, not the median or the mean. This host's timings are one-sided: a
                // pass that loses the CPU to another process is only ever slower, so the fastest
                // pass is the one that ran without contention and is the number the engine owes a
                // query. Three rebuilds of one identical table measured 2.79 / 15.69 / 2.80 ms, so
                // the noise is not a small error to be averaged away — it is the dominant term.
                points.Add(new AnnPoint(
                    efSearch,
                    recalls.Average(),
                    times.Min()));
            }
            finally
            {
                engine.DropSchema();
            }
        }

        return points;
    }

    private static double Dot(float[] a, float[] b)
    {
        double sum = 0;
        for (int i = 0; i < a.Length; i++)
            sum += (double)a[i] * b[i];

        return sum;
    }

    /// <summary>
    /// Serves the already-computed vectors, so the measurement is of the index and the network
    /// and never of an ONNX session.
    /// </summary>
    /// <remarks>
    /// Documents and queries are told apart by a caller-supplied lookup rather than by the
    /// <see cref="EmbeddingUse"/> role, because the dense lane encodes passages and queries with
    /// different prefixes and the cache holds both. A miss throws rather than returning zeros: a
    /// zero vector would rank last and quietly read as "the ANN missed it".
    /// </remarks>
    private sealed class CachedEmbeddingProvider : IEmbeddingProvider
    {
        private readonly Dictionary<string, float[]> _byText;
        private readonly int _dimension;

        public CachedEmbeddingProvider(
            IReadOnlyList<(string Text, float[] Vector)> documents,
            IReadOnlyList<(string Text, float[] Vector)> queries,
            int dimension)
        {
            ArgumentNullException.ThrowIfNull(documents);
            ArgumentNullException.ThrowIfNull(queries);

            _dimension = dimension;
            _byText = new Dictionary<string, float[]>(StringComparer.Ordinal);

            for (int i = 0; i < documents.Count; i++)
            {
                // A duplicate document text would collapse two documents onto one vector here. The
                // dense lane's cache has the same property, so the pairing is already the cache's.
                _byText[documents[i].Text] = documents[i].Vector;
            }

            for (int i = 0; i < queries.Count; i++)
            {
                _byText[queries[i].Text] = queries[i].Vector;
            }
        }

        public int Dimension => _dimension;

        public Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
            string text, EmbeddingUse use, CancellationToken cancellationToken = default)
        {
            if (_byText.TryGetValue(text, out float[]? vector))
                return Task.FromResult<ReadOnlyMemory<float>>(vector);

            // A miss is thrown rather than answered with zeros: a zero vector ranks last, and a
            // last-ranked placeholder reads exactly like an ANN that failed to retrieve.
            throw new InvalidOperationException(
                $"No cached embedding for {(use == EmbeddingUse.Query ? "a query" : "a passage")}. " +
                "The dense lane's cache and the texts being indexed must come from the same corpus " +
                "in the same order.");
        }
    }
}
