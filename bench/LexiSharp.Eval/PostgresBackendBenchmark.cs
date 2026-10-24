using System.Globalization;
using LexiSharp.Core;
using LexiSharp.ParadeDB;
using LexiSharp.Postgres;
using LexiSharp.Ranking;

namespace LexiSharp.Eval;

/// <summary>
/// Which SQL backend a backend row is measured through.
/// </summary>
internal enum BackendKind
{
    /// <summary><c>tsvector</c> + <c>ts_rank_cd</c> over stock PostgreSQL: coverage-based, not BM25.</summary>
    Lexical,

    /// <summary><c>pg_search</c> + <c>pdb.score</c>: Okapi BM25 computed by Tantivy.</summary>
    ParadeDb,

    /// <summary><c>pgvector</c> ANN over the harness's cached embeddings, cosine distance.</summary>
    Vector,
}

/// <summary>
/// The connection a backend row is measured on, and which engine it asks for.
/// </summary>
/// <param name="TextSearchConfig">
/// PostgreSQL's own text search configuration for the lexical engine. It is the analysis of the
/// row — <c>simple</c> mirrors the library's default normalization but applies no stemming and no
/// stop list, and <c>english</c> is a Snowball stemmer that is close to, and not identical with,
/// Porter. Either way it is PostgreSQL's analysis, not the harness's tokenizer.
/// </param>
/// <param name="Bm25">
/// The k1/b a ParadeDB row is measured at, or <c>null</c> for Tantivy's defaults. pg_search takes
/// them per field as tokenizer arguments, which is what <see cref="Tokenizer"/> builds; the lexical
/// engine has no such parameter, so asking it for one is refused rather than ignored.
/// </param>
/// <param name="EfSearchValues">
/// The pgvector <c>hnsw.ef_search</c> values a vector row is measured at, one row each. Empty for
/// the other engines, and refused there rather than ignored.
/// </param>
internal sealed record BackendLane(
    BackendKind Kind,
    string ConnectionString,
    string TextSearchConfig,
    Bm25Parameters? Bm25 = null,
    IReadOnlyList<int>? EfSearchValues = null)
{
    /// <summary>The <c>ef_search</c> values in force; pgvector's own default when none was given.</summary>
    public IReadOnlyList<int> EfValues => EfSearchValues is { Count: > 0 } values ? values : [40];

    /// <summary>Parses <c>--backend</c>, refusing a name it does not implement by name.</summary>
    public static BackendLane Parse(
        string kind,
        string connectionString,
        string textSearchConfig,
        Bm25Parameters? bm25 = null,
        IReadOnlyList<int>? efSearchValues = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        BackendKind parsed = kind switch
        {
            "lexical" => BackendKind.Lexical,
            "paradedb" => BackendKind.ParadeDb,
            "vector" => BackendKind.Vector,
            _ => throw new ArgumentException(
                $"--backend expects 'lexical', 'paradedb' or 'vector', got '{kind}'. The sparse and fuzzy "
                + "engines have no row: no sparse model is wired into this harness, and trigram similarity "
                + "is not a ranking of prose."),
        };

        if (parsed == BackendKind.Lexical && bm25 is not null)
        {
            throw new ArgumentException(
                "--backend-bm25 sets k1/b, and ts_rank_cd has neither: the lexical row ranks by coverage "
                + "and takes no parameter. Drop the flag, or use --backend paradedb.");
        }

        if (parsed == BackendKind.ParadeDb && textSearchConfig != "simple")
        {
            throw new ArgumentException(
                $"--backend-config '{textSearchConfig}' selects PostgreSQL's text search configuration, "
                + "which the lexical engine reads and ParadeDB does not. Drop the flag, or use "
                + "--backend lexical.");
        }

        if (parsed == BackendKind.Vector)
        {
            if (bm25 is not null)
            {
                throw new ArgumentException(
                    "--backend-bm25 sets k1/b, which no BM25 parameter reaches in a vector index: the "
                    + "ranking there is cosine distance over embeddings. Drop the flag.");
            }

            if (textSearchConfig != "simple")
            {
                throw new ArgumentException(
                    $"--backend-config '{textSearchConfig}' is PostgreSQL's text search configuration, "
                    + "which the vector engine never reads: its documents are ranked by embedding. Drop "
                    + "the flag, or use --backend lexical.");
            }
        }

        if (parsed != BackendKind.Vector && efSearchValues is { Count: > 0 })
        {
            throw new ArgumentException(
                "--backend-ef sets pgvector's hnsw.ef_search, which only the vector backend reads. "
                + "Drop the flag, or use --backend vector.");
        }

        return new BackendLane(parsed, connectionString, textSearchConfig, bm25, efSearchValues);
    }

    /// <summary>
    /// The table this lane builds. Derived from the dataset rather than defaulted, because the
    /// engines truncate the table before indexing and the default name is the one an application
    /// shares with them: a measurement must not be a reason to empty somebody's corpus.
    /// </summary>
    public string TableFor(string dataset)
    {
        if (dataset.Length == 0 || !dataset.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
        {
            throw new ArgumentException(
                $"The dataset name '{dataset}' is not usable as a table suffix: the backends quote the "
                + "table name and accept [A-Za-z0-9_] only.");
        }

        return "lexisharp_eval_" + dataset;
    }

    /// <summary>Names the engine and its analysis, because a nDCG without them is not a figure.</summary>
    public string Description => Kind switch
    {
        BackendKind.ParadeDb =>
            "ParadeDB BM25 (pdb.score, Tantivy)" + Bm25Text
            + " — index over content only, title concatenated into it",
        BackendKind.Vector =>
            "pgvector HNSW (cosine) over multilingual-e5-small embeddings, ef_search "
            + string.Join(", ", EfValues.Select(value => value.ToString(CultureInfo.InvariantCulture))),
        _ => $"PostgreSQL lexical (ts_rank_cd, text search config '{TextSearchConfig}' + unaccent)",
    };

    /// <summary>
    /// The tokenizer declaration a ParadeDB index is built with. pg_search takes k1/b as per-field
    /// tokenizer arguments, so a matched operating point is an index-time decision and not a query
    /// option: the row's parameters are fixed when the index is created.
    /// </summary>
    public string Tokenizer => Bm25 is { } parameters
        ? string.Create(
            CultureInfo.InvariantCulture,
            $"pdb.simple('ascii_folding=true', 'k1={parameters.K1:0.####}', 'b={parameters.B:0.####}')")
        : "pdb.simple('ascii_folding=true')";

    private string Bm25Text => Bm25 is { } parameters
        ? string.Create(CultureInfo.InvariantCulture, $" at k1={parameters.K1:0.##}, b={parameters.B:0.##}")
        : " at Tantivy's defaults";

    /// <summary>
    /// What a reader has to know before putting this row beside another. Not a disclaimer: each
    /// line is a property of the ranking function that changes what the number is comparable with.
    /// </summary>
    public IReadOnlyList<string> Caveats => Kind switch
    {
        BackendKind.Vector =>
        [
            "The vectors are the harness's cached multilingual-e5-small pass, so this row and the "
            + "in-memory dense row rank the same embeddings: the difference between them is the index. "
            + "hnsw.ef_search trades that difference against latency; the ef_search curve reports how "
            + "many true neighbours the graph missed.",
            "A query with no cached embedding throws rather than being scored as a zero, so the query "
            + "count this row reports is the count it actually measured.",
        ],
        BackendKind.ParadeDb =>
        [
            "Scores are Tantivy's BM25" + Bm25Text + ", computed by pg_search; the analysis is "
            + "pdb.simple with ASCII folding, not this library's tokenizer.",
            AnalysisCaveat,
        ],
        _ =>
        [
            "ts_rank_cd is a coverage-based rank, not BM25, and it takes no k1/b. Its ORDER BY has no "
            + "tie-break, so the order among equal scores is the executor's and can differ between runs.",
            AnalysisCaveat,
        ],
    };

    private const string AnalysisCaveat =
        "The analysis is PostgreSQL's, not the harness tokenizer's: no Porter, no stop list, no "
        + "possessive rule. This row is comparable with the in-memory 'defaults' rows at most, and "
        + "not with an aligned (--analyzer english) row or a published figure.";
}

/// <summary>One metric row: its label (the <c>ef_search</c> for a vector curve) and its figures.</summary>
internal sealed record BackendRow(string Label, ConfigResult Result);

/// <summary>One measured backend: the engine that ran, the index it read, and its rows.</summary>
internal sealed record BackendMeasurement(
    string Description,
    string Table,
    int Documents,
    IReadOnlyList<BackendRow> Rows,
    IReadOnlyList<string> Caveats);

/// <summary>
/// Retrieval quality of a SQL backend over a BEIR corpus, which the in-memory table cannot express:
/// every row there is a different <c>IScorer</c> over one shared <c>ITextIndex</c>, and these engines
/// own their index and their ranking function. So the row is measured and printed on its own, with
/// the analysis and the parameter set it actually ran under.
/// </summary>
/// <remarks>
/// <para>
/// What this measures and what it does not: nDCG/MAP/MRR/R at the harness's metric depth, computed by
/// <see cref="RetrievalMetrics"/> over the qrels, so the number means the same thing as one from the
/// in-memory table. The scores behind the ranking are PostgreSQL-native and are not compared with
/// anything. Latency is deliberately not reported: every search opens a connection, which is the
/// shape of this lane rather than a property of the backend.
/// </para>
/// <para>
/// The table is built and dropped per run, so a measurement leaves the server as it found it. It is
/// also what keeps a row from reading a table an earlier run left behind under the same name.
/// </para>
/// </remarks>
internal static class PostgresBackendBenchmark
{
    /// <summary>
    /// Indexes <paramref name="corpus"/> into a fresh table on <paramref name="lane"/>'s server,
    /// scores it, and drops the table. Throws <see cref="NotSupportedException"/> when the engine
    /// refuses the queries' syntax, restated as what that costs the measurement.
    /// </summary>
    /// <param name="dense">
    /// The cached embeddings, required by <see cref="BackendKind.Vector"/> and unused otherwise. The
    /// vectors are the harness's own dense pass, so the row and the in-memory dense row rank the same
    /// embeddings and differ only in where the distance is computed.
    /// </param>
    public static BackendMeasurement Measure(
        BackendLane lane,
        BeirCorpus corpus,
        string dataset,
        int topK,
        int? limit,
        NdcgGain ndcgGain,
        bool excludeQueryDocument,
        int jobs,
        DenseVectors? dense = null)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(corpus);

        IReadOnlyList<SearchDocument> documents = Shape(lane.Kind, Evaluation.BuildDocuments(corpus));
        string table = lane.TableFor(dataset);

        if (lane.Kind == BackendKind.Vector)
            return MeasureVector(lane, corpus, documents, table, dense, topK, limit, ndcgGain, excludeQueryDocument, jobs);

        if (lane.Kind == BackendKind.ParadeDb)
        {
            using var parade = new ParadeDBTextSearchEngine(
                lane.ConnectionString,
                new ParadeDBOptions { Table = table, ContentTokenizer = lane.Tokenizer });

            return Run(parade, parade.DropSchema);
        }

        using var lexical = new PostgresTextSearchEngine(
            lane.ConnectionString,
            new PostgresIndexOptions { Table = table, TextSearchConfig = lane.TextSearchConfig });

        return Run(lexical, lexical.DropSchema);

        BackendMeasurement Run(ITextSearchEngine engine, Action dropSchema)
        {
            try
            {
                // One transaction, truncate first: both engines' Index does that, so a re-run cannot
                // score a corpus plus whatever a failed run left in the table.
                engine.Index(documents);

                ConfigResult result = Evaluation.RunEngine(
                    corpus, engine, lane.Description, topK, limit, ndcgGain, excludeQueryDocument, jobs);

                return new BackendMeasurement(
                    lane.Description, table, documents.Count, [new BackendRow(string.Empty, result)], lane.Caveats);
            }
            catch (Exception error) when (FindRefusal(error) is { } refusal)
            {
                // The engine refused a query's syntax. Left as it is, that surfaces as a stack trace
                // from whichever parallel worker got there first, with no count and no dataset — and
                // a number computed over the queries that were accepted would be a different query
                // set from every row it is put beside.
                throw Refusal(refusal, engine, Evaluation.BuildQueries(corpus, limit, excludeQueryDocument));
            }
            finally
            {
                // Only the table: the engine is the caller's `using`, which disposes it after this
                // returns — dropping and disposing here as well would be two owners for one handle.
                dropSchema();
            }
        }
    }

    /// <summary>
    /// One vector row per requested <c>ef_search</c>, over one index. The index is the same index at
    /// every value — only the candidate list the search explores changes — so rebuilding it per
    /// point would measure the rebuild, not the parameter.
    /// </summary>
    private static BackendMeasurement MeasureVector(
        BackendLane lane,
        BeirCorpus corpus,
        IReadOnlyList<SearchDocument> documents,
        string table,
        DenseVectors? dense,
        int topK,
        int? limit,
        NdcgGain ndcgGain,
        bool excludeQueryDocument,
        int jobs)
    {
        if (dense is null)
        {
            throw new InvalidOperationException(
                "The vector backend needs the cached embeddings: re-run with --dense, or use a corpus "
                + "the dense lane has already encoded.");
        }

        if (dense.DocumentVectors.Length != documents.Count)
        {
            throw new InvalidOperationException(
                $"{documents.Count} documents but {dense.DocumentVectors.Length} vectors: the vector lane "
                + "pairs them by position, and a mismatch would index one document's text under another's "
                + "embedding.");
        }

        List<EvaluatedQuery> queries = Evaluation.BuildQueries(corpus, limit, excludeQueryDocument);

        // Both roles come from the cache, keyed by text: the dense pass encodes passages and queries
        // with different prefixes, so one lookup per role rather than one vector reused for both.
        var provider = new CachedEmbeddingProvider(
            documents
                .Select(document => document.Text)
                .Zip(dense.DocumentVectors, (text, vector) => (Text: text, Vector: vector))
                .ToList(),
            queries
                .Select(query => (Text: query.Query.Text, Vector: Vector(dense, query.Query.Text)))
                .ToList(),
            dense.Dimension);

        var options = new PostgresVectorOptions
        {
            Table = table,
            Dimension = dense.Dimension,
            IndexMethod = VectorIndexMethod.Hnsw,
            Distance = VectorDistance.Cosine,
        };

        try
        {
            // Built once. HNSW accepts inserts and an index over a populated table alike, so the
            // table is loaded with the engine that does not read it and the reads below share it.
            using (var indexer = new PostgresVectorSearchEngine(lane.ConnectionString, provider, options))
                indexer.Index(documents);

            var rows = new List<BackendRow>(lane.EfValues.Count);

            foreach (int efSearch in lane.EfValues)
            {
                using var engine = new PostgresVectorSearchEngine(
                    lane.ConnectionString, provider, options with { HnswEfSearch = efSearch });

                rows.Add(new BackendRow(
                    efSearch.ToString(CultureInfo.InvariantCulture),
                    Evaluation.RunEngine(
                        corpus, engine, $"pgvector HNSW (ef={efSearch})", topK, limit, ndcgGain,
                        excludeQueryDocument, jobs)));
            }

            return new BackendMeasurement(lane.Description, table, documents.Count, rows, lane.Caveats);
        }
        finally
        {
            using var dropper = new PostgresVectorSearchEngine(lane.ConnectionString, provider, options);
            dropper.DropSchema();
        }

        static float[] Vector(DenseVectors vectors, string text) =>
            vectors.QueryEmbeddings.TryGetValue(text, out float[]? vector)
                ? vector
                : throw new InvalidOperationException(
                    "A query has no cached embedding, so it cannot be scored through the vector backend.");
    }

    /// <summary>
    /// The refusal inside <paramref name="error"/>, unwrapped through the layers a parallel query loop
    /// puts around it, or <c>null</c> when there is none.
    /// </summary>
    public static NotSupportedException? FindRefusal(Exception error)
    {
        var pending = new Stack<Exception>();
        pending.Push(error);

        while (pending.Count > 0)
        {
            Exception current = pending.Pop();

            if (current is NotSupportedException refusal)
                return refusal;

            if (current is AggregateException aggregate)
            {
                foreach (Exception inner in aggregate.InnerExceptions)
                    pending.Push(inner);

                continue;
            }

            if (current.InnerException is { } nested)
                pending.Push(nested);
        }

        return null;
    }

    /// <summary>
    /// States a refusal as what it costs the measurement: how many test queries the engine will not
    /// read, which one, and why the remaining ones are not a substitute.
    /// </summary>
    public static NotSupportedException Refusal(
        NotSupportedException refusal, ITextSearchEngine engine, IReadOnlyList<EvaluatedQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(queries);

        var refused = new List<EvaluatedQuery>();

        if (engine is IQuerySyntaxSupport support)
        {
            foreach (EvaluatedQuery query in queries)
            {
                if ((QuerySyntax.Detect(query.Query.Text) & ~support.SupportedQueryFeatures) != QueryFeatures.None)
                    refused.Add(query);
            }
        }

        if (refused.Count == 0)
        {
            return new NotSupportedException(
                $"{refusal.Message} The engine refused a query whose raw text this harness does not "
                + "read as syntax, so no row is produced for this dataset.", refusal);
        }

        EvaluatedQuery first = refused[0];

        return new NotSupportedException(
            $"{refusal.Message} That hits {refused.Count:N0} of {queries.Count:N0} test queries here "
            + $"(for example '{first.Query.Id}'), so a metric over the other "
            + $"{queries.Count - refused.Count:N0} would be a different query set and no row is produced "
            + "for this dataset.", refusal);
    }

    /// <summary>
    /// The corpus as this backend can index it.
    /// </summary>
    /// <remarks>
    /// The lexical engine's <c>tsv</c> is generated from <c>content || text_fields</c>, so it sees a
    /// BEIR title the way the in-memory flat index does and the documents pass through unchanged.
    /// The ParadeDB index is declared over <c>content</c> alone, so a title left in
    /// <c>text_fields</c> would be stored and never searched — the same corpus indexed with fewer
    /// terms on one side of a comparison, which is a difference nobody would see in the output. It is
    /// concatenated instead, which is exactly the flat view the in-memory BM25 rows score.
    /// </remarks>
    public static IReadOnlyList<SearchDocument> Shape(BackendKind kind, IReadOnlyList<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (kind != BackendKind.ParadeDb)
            return documents;

        return documents
            .Select(document =>
            {
                if (document.TextFields is not { } fields
                    || !fields.TryGetValue("title", out string? title)
                    || string.IsNullOrEmpty(title))
                {
                    return document;
                }

                return new SearchDocument(
                    document.Id,
                    title + "\n" + document.Text,
                    document.Fields,
                    document.Category,
                    TextFields: null);
            })
            .ToList();
    }

    /// <summary>
    /// The rows as one block of text. A table would carry a Time column measured through a connection
    /// open per query, and printing it beside the in-memory timings would invite a comparison that
    /// means nothing.
    /// </summary>
    public static string Format(BackendMeasurement measurement, string dataset)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture, $"== {dataset}, backend =="),
            string.Create(CultureInfo.InvariantCulture, $"Engine: {measurement.Description}"),
            string.Create(
                CultureInfo.InvariantCulture,
                $"Index:  {measurement.Documents:N0} documents in {measurement.Table}, dropped after the run"),
        };

        if (measurement.Rows is [{ Label: "", Result: { } single }])
        {
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Result: nDCG@10 {single.NdcgAt10:0.0000} | MAP@10 {single.MapAt10:0.0000} | "
                + $"MRR@10 {single.MrrAt10:0.0000} | R@10 {single.RecallAt10:0.0000} | {single.Queries:N0} queries"));
        }
        else
        {
            lines.Add(string.Empty);
            lines.Add("  ef_search | nDCG@10 | MAP@10 | MRR@10 |  R@10 | queries");

            foreach (BackendRow row in measurement.Rows)
            {
                ConfigResult result = row.Result;

                lines.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"  {row.Label,9} | {result.NdcgAt10,7:0.0000} | {result.MapAt10,6:0.0000} | "
                    + $"{result.MrrAt10,6:0.0000} | {result.RecallAt10,5:0.0000} | {result.Queries,7:N0}"));
            }
        }

        return string.Join('\n', lines) + "\n";
    }
}
