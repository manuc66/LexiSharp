using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using LexiSharp.Core;
using LexiSharp.Postgres;
using Npgsql;

namespace LexiSharp.Eval;

/// <summary>
/// The approximate-nearest-neighbour cost curve, measured through the real pgvector engine rather
/// than asserted: recall of the ANN ranking against an exact top-k computed in process, the
/// wall-clock each <c>ef_search</c> costs, and the scan node that answered.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "always route to the dense engine" is a latency decision, and a latency
/// decision needs a latency measurement. The recall column is the one that makes the measurement
/// trustworthy: pgvector's own scoring is compared against an exact search over the same cached
/// vectors, so a drop in recall is the index and not a second, different scoring function.
/// </para>
/// <para>
/// <b>The plan column is what makes the curve readable.</b> pgvector's planner hook grows the ANN
/// index's estimated <i>startup</i> cost with <c>ef_search</c>, so past a threshold that depends on
/// the corpus, PostgreSQL answers the search with a sequential scan and an exact top-k. A
/// recall/latency pair measured there describes a different algorithm from the one this run is
/// about, and nothing in a recall of 1.0 says so — so the node is read from the plan and printed,
/// and the curve is only a curve over the index where every point on it names the index.
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
/// Every point's table is <c>ANALYZE</c>d after it is loaded, because the planner's choice at these
/// corpus sizes is a function of the table's statistics, and PostgreSQL only collects them on
/// <c>ANALYZE</c> or on the autovacuum daemon's schedule. The threshold above lands wherever the
/// daemon happened to fire otherwise: measured between <c>ef_search</c> 60 and 80 on a table
/// analyzed on purpose, and around 160 on one the daemon analyzed part-way through its load.
/// </para>
/// </remarks>
internal static class VectorAnnBenchmark
{
    /// <summary>One measured point on the curve, with the node that answered it.</summary>
    internal sealed record AnnPoint(int EfSearch, double RecallAtK, double MillisecondsPerQuery, string Plan)
    {
        public override string ToString() =>
            EfSearch.ToString(CultureInfo.InvariantCulture).PadLeft(8)
            + Plan.PadRight(11)
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
    public static async Task<IReadOnlyList<AnnPoint>> Measure(
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

                // The plan is read once, on the statistics every point shares. It does not change
                // during the passes: nothing here writes to the table, and the ef_search the search
                // runs with is the one this read used.
                await AnalyzeAsync(connectionString, options.QualifiedTableName);

                string plan = await ReadPlanAsync(
                    connectionString, options, efSearch, VectorLiteral(queries[0].Vector), topK);

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
                    times.Min(),
                    plan));
            }
            finally
            {
                engine.DropSchema();
            }
        }

        return points;
    }

    /// <summary>
    /// Collects the table's statistics, so every point of the curve is planned against the same
    /// numbers. A table that was only just loaded has none, and which plan PostgreSQL then picks
    /// depends on whether the autovacuum daemon has been by yet — a threshold that moves between
    /// runs, which is the opposite of what a sweep of one parameter is for.
    /// </summary>
    private static async Task AnalyzeAsync(string connectionString, string qualifiedTableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"ANALYZE {qualifiedTableName};"; // NOSONAR:S2077 (a validated, quoted identifier)
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The scan node that answers <see cref="SearchStatement"/> at this <c>ef_search</c>, read from
    /// PostgreSQL's own plan rather than assumed from the shape of the query. An
    /// <c>Index Scan</c> over the <c>hnsw</c> index is an approximate top-k; a <c>Seq Scan</c> under
    /// a top-N sort is an exact one, and the two are not interchangeable in a recall column.
    /// </summary>
    private static async Task<string> ReadPlanAsync(
        string connectionString, PostgresVectorOptions options, int efSearch, string vector, int limit)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var set = connection.CreateCommand())
        {
            set.Transaction = transaction;
            set.CommandText = $"SET LOCAL hnsw.ef_search = {efSearch};"; // NOSONAR:S2077 (an int option value)
            await set.ExecuteNonQueryAsync();
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "EXPLAIN (FORMAT JSON) " + SearchStatement(options);
        command.Parameters.AddWithValue("query", vector);
        command.Parameters.AddWithValue("limit", limit);

        var plan = new StringBuilder();

        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
                plan.Append(reader.GetString(0));

        await transaction.CommitAsync();
        return PlanNode(plan.ToString());
    }

    /// <summary>
    /// The scan node a plan names, in the words the table prints: the ANN index, an exact scan, or
    /// <c>?</c> for a node this harness does not recognise — printed as unknown rather than guessed
    /// at, because the whole column exists to keep an unrecognised answer from reading as the index.
    /// </summary>
    internal static string PlanNode(string explainJson)
    {
        using var document = JsonDocument.Parse(explainJson);

        string node = "?";
        Walk(document.RootElement[0].GetProperty("Plan"), ref node);
        return node;

        static void Walk(JsonElement plan, ref string node)
        {
            string type = plan.TryGetProperty("Node Type", out JsonElement nodeType)
                ? nodeType.GetString() ?? ""
                : "";

            if (type.Contains("Index Scan", StringComparison.Ordinal)
                && plan.TryGetProperty("Index Name", out JsonElement indexName)
                && (indexName.GetString() ?? "").Contains("hnsw", StringComparison.Ordinal))
            {
                node = "index";
            }
            else if (type.Contains("Seq Scan", StringComparison.Ordinal))
            {
                node = "seq scan";
            }

            if (plan.TryGetProperty("Plans", out JsonElement children))
                foreach (JsonElement child in children.EnumerateArray())
                    Walk(child, ref node);
        }
    }

    /// <summary>
    /// The search statement this harness explains: a single-column cosine engine, the two parameters
    /// <see cref="PostgresVectorSearchEngine"/> binds, and the metadata filter's fragment spliced
    /// where the engine splices it. It is written out here rather than taken from the engine so that
    /// the harness does not need the engine's internals, and <c>VectorAnnPlanTests</c> asserts the
    /// two strings are equal — which is what makes the node reported beside a point the node a
    /// search actually gets.
    /// </summary>
    internal static string SearchStatement(PostgresVectorOptions options, string filterFragment = "") => $"""
        SELECT id, content, category, fields, text_fields, 1 - ("embedding" <=> @query::vector) AS score
        FROM {options.QualifiedTableName}
        WHERE "embedding" IS NOT NULL{filterFragment}
        ORDER BY "embedding" <=> @query::vector
        LIMIT @limit;
        """;

    /// <summary>
    /// A vector as a pgvector literal. The engine has its own formatter behind an internal door; the
    /// same rounding rule is asserted against it in <c>VectorAnnPlanTests</c>, since a parameter
    /// written differently is a different statement text to the planner.
    /// </summary>
    internal static string VectorLiteral(float[] vector)
    {
        var builder = new StringBuilder(vector.Length * 10 + 2);
        builder.Append('[');

        for (int i = 0; i < vector.Length; i++)
        {
            if (i > 0)
                builder.Append(',');

            builder.Append(vector[i].ToString("R", CultureInfo.InvariantCulture));
        }

        builder.Append(']');
        return builder.ToString();
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
