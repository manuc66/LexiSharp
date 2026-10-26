using System.Globalization;
using LexiSharp.Core;
using LexiSharp.ParadeDB;
using LexiSharp.Postgres;
using LexiSharp.Ranking;
using Npgsql;

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

    /// <summary>
    /// <c>pg_trgm</c> trigram similarity: documents whose content passes the threshold, ranked by
    /// <c>similarity(content, query)</c>.
    /// </summary>
    Fuzzy,

    /// <summary>
    /// <c>pgvector sparsevec</c> + HNSW over precomputed learned-sparse vectors: inner product
    /// between the query's term weights and each document's.
    /// </summary>
    Sparse,
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
/// <param name="FieldWeights">
/// The <c>tsvector</c> weight class per text field for a lexical row, or <c>null</c> for the
/// unweighted vector every field has always shared. Weights are classes rather than multipliers:
/// <c>ts_rank_cd</c> maps <c>A</c> to 1.0 and <c>D</c> to 0.1, and has no per-field length
/// normalisation to go with them. Without a weighted <c>title</c>, a document's body counts as
/// much as its title and the row measures nothing new.
/// </param>
/// <param name="SimilarityThresholds">
/// The <c>pg_trgm</c> similarity thresholds a fuzzy row is measured at, one row each. The threshold
/// is the <c>%</c> operator's match rule (<c>set_limit</c>), not a ranking parameter: a document
/// either clears it and is ranked by <c>similarity</c>, or it is absent. Empty for the other
/// engines, and refused there rather than ignored.
/// </param>
/// <param name="SparseVectorsDirectory">
/// The directory of precomputed sparse vectors a sparse row indexes, in the producer's format
/// (<c>&lt;dataset&gt;.docs.tsv</c>, <c>.queries.tsv</c>, <c>.meta.json</c>). Required by
/// <see cref="BackendKind.Sparse"/> and ignored by every other kind — not refused there, because
/// <c>--sparse</c> is also its own in-memory row, so <c>--backend lexical --sparse &lt;dir&gt;</c> is
/// a legitimate pair of rows rather than a flag that reached the wrong lane.
/// </param>
internal sealed record BackendLane(
    BackendKind Kind,
    string ConnectionString,
    string TextSearchConfig,
    Bm25Parameters? Bm25 = null,
    IReadOnlyList<int>? EfSearchValues = null,
    IReadOnlyDictionary<string, PostgresFieldWeight>? FieldWeights = null,
    IReadOnlyList<double>? SimilarityThresholds = null,
    string? SparseVectorsDirectory = null)
{
    /// <summary>The <c>ef_search</c> values in force; pgvector's own default when none was given.</summary>
    public IReadOnlyList<int> EfValues => EfSearchValues is { Count: > 0 } values ? values : [40];

    /// <summary>The similarity thresholds in force; <c>pg_trgm</c>'s own default when none was given.</summary>
    public IReadOnlyList<double> Thresholds =>
        SimilarityThresholds is { Count: > 0 } values ? values : [0.3];

    /// <summary>Parses <c>--backend</c>, refusing a name it does not implement by name.</summary>
    public static BackendLane Parse(
        string kind,
        string connectionString,
        string textSearchConfig,
        Bm25Parameters? bm25 = null,
        IReadOnlyList<int>? efSearchValues = null,
        IReadOnlyDictionary<string, PostgresFieldWeight>? fieldWeights = null,
        IReadOnlyList<double>? similarityThresholds = null,
        string? sparseVectorsDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        BackendKind parsed = kind switch
        {
            "lexical" => BackendKind.Lexical,
            "paradedb" => BackendKind.ParadeDb,
            "vector" => BackendKind.Vector,
            "fuzzy" => BackendKind.Fuzzy,
            "sparse" => BackendKind.Sparse,
            _ => throw new ArgumentException(
                $"--backend expects 'lexical', 'paradedb', 'vector', 'fuzzy' or 'sparse', got '{kind}'."),
        };

        if (parsed == BackendKind.Sparse)
        {
            if (string.IsNullOrWhiteSpace(sparseVectorsDirectory))
            {
                throw new ArgumentException(
                    "--backend sparse measures PostgresSparseSearchEngine over precomputed learned-sparse "
                    + "vectors, and the engine has no model of its own: give them with --sparse <dir>, the "
                    + "same directory the in-memory sparse row reads. --dense is not a substitute — those "
                    + "are the dense lane's embeddings, not sparse term weights.");
            }

            if (bm25 is not null)
            {
                throw new ArgumentException(
                    "--backend-bm25 sets k1/b, and the sparse row ranks by the inner product of two "
                    + "learned sparse vectors: no BM25 parameter reaches it. Drop the flag.");
            }

            if (textSearchConfig != "simple")
            {
                throw new ArgumentException(
                    $"--backend-config '{textSearchConfig}' is PostgreSQL's text search configuration, "
                    + "which the sparse engine never reads: the shared table's tsv column is written but "
                    + "the sparse ranking is a dot product over term coordinates. Drop the flag, or use "
                    + "--backend lexical.");
            }

            if (efSearchValues is { Count: > 0 })
            {
                throw new ArgumentException(
                    "--backend-ef sets pgvector's hnsw.ef_search, and PostgresSparseSearchEngine has no "
                    + "option that reaches it: the sparse row is measured at pgvector's default 40. Set "
                    + "it on the server (a database-level setting applies, because the engine never "
                    + "overrides it) and say so in the row beside the number, or drop the flag.");
            }
        }

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

        if (parsed == BackendKind.Fuzzy)
        {
            if (bm25 is not null)
            {
                throw new ArgumentException(
                    "--backend-bm25 sets k1/b, which no BM25 parameter reaches in a trigram index: the "
                    + "ranking there is similarity over character trigrams. Drop the flag.");
            }

            if (textSearchConfig != "simple")
            {
                throw new ArgumentException(
                    $"--backend-config '{textSearchConfig}' is PostgreSQL's text search configuration, "
                    + "which the fuzzy engine never reads: its documents are matched by trigram "
                    + "similarity. Drop the flag, or use --backend lexical.");
            }
        }

        if (parsed != BackendKind.Vector && efSearchValues is { Count: > 0 })
        {
            throw new ArgumentException(
                "--backend-ef sets pgvector's hnsw.ef_search, which only the vector backend reads. "
                + "Drop the flag, or use --backend vector.");
        }

        if (parsed != BackendKind.Fuzzy && similarityThresholds is { Count: > 0 })
        {
            throw new ArgumentException(
                "--backend-threshold sets pg_trgm's similarity threshold, which only the fuzzy backend "
                + "reads: the other engines match by term or by embedding and have no threshold. Drop "
                + "the flag, or use --backend fuzzy.");
        }

        if (parsed != BackendKind.Lexical && fieldWeights is { Count: > 0 })
        {
            throw new ArgumentException(
                "--backend-field-weights configures setweight on the tsv column, which only the lexical "
                + "backend ranks with: the vector, sparse, ParadeDB and fuzzy rows are ranked by an "
                + "embedding, by a sparse dot product, by pdb.score or by trigram similarity. Drop the "
                + "flag, or use --backend lexical.");
        }

        return new BackendLane(
            parsed, connectionString, textSearchConfig, bm25, efSearchValues, fieldWeights,
            similarityThresholds,
            sparseVectorsDirectory is null ? null : Path.GetFullPath(sparseVectorsDirectory));
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
        BackendKind.Fuzzy =>
            "pg_trgm similarity (content % query, ranked by similarity) at similarity threshold "
            + string.Join(", ", Thresholds.Select(FormatThreshold))
            + " — index over content only, title concatenated into it",
        BackendKind.Sparse =>
            "pgvector sparsevec (inner product) + HNSW (m = 16, ef_construction = 64), pgvector's "
            + "default hnsw.ef_search 40, and the same table after DROP INDEX",
        _ => $"PostgreSQL lexical (ts_rank_cd, text search config '{TextSearchConfig}' + unaccent"
            + FieldWeightsText + ")",
    };

    private static string FormatThreshold(double threshold) =>
        threshold.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>
    /// The weighting the row ran under, spelled into its name: a lexical nDCG with and without
    /// <c>setweight</c> is two configurations, and a table that labelled both the same way would be
    /// two rows a reader could not tell apart.
    /// </summary>
    private string FieldWeightsText => FieldWeights is { Count: > 0 } weights
        ? ", field weights " + string.Join(
            ", ",
            weights.OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => $"{entry.Key}={entry.Value}"))
            + ", other text at D"
        : string.Empty;

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
        BackendKind.Fuzzy =>
        [
            "The match rule is content % query above the stated pg_trgm similarity threshold, ranked "
            + "by similarity(content, query). Similarity is shared character trigrams over the sum of "
            + "both trigram sets: a short query against a whole document divides a handful of shared "
            + "trigrams by thousands, so a relevant document scores near zero and the threshold decides "
            + "whether a row has any documents to rank at all. The threshold is a match rule, not a "
            + "relevance weight.",
            "Each row is one point of an in-sample threshold sweep, scored on the same qrels it is "
            + "reported against. The best row of the sweep is an upper bound on what this engine "
            + "scores on this corpus, not a tuned configuration, and not comparable with a tuned row.",
            "The index covers content alone and the BEIR title is concatenated into it; the analysis "
            + "is character trigrams, with no stemming, no stop list and no term weighting.",
        ],
        BackendKind.Sparse =>
        [
            "The vectors are the ones bench/LexiSharp.Eval/sparse/encode_splade.py wrote, read through "
            + "the same loader and the same directory as the in-memory sparse row: this row and that "
            + "one rank the same vectors, so the difference between them is where the dot product "
            + "runs, not the model. The metrics contain no encode cost.",
            "The vocabulary (term → coordinate) is the sorted union of the terms in this run's "
            + "document and query vectors, so no term in the set is dropped. It is this run's layout, "
            + "not a fixed vocabulary an application would keep across corpora.",
            "Documents are encoded as title + newline + body, exactly as the producer hashed them: "
            + "the sparse engine has no field concept, so no title weight exists in this row.",
            "Query text reaches the encoder verbatim: the engine declares no query-syntax features, "
            + "and this harness runs with query syntax off, so a quote or a `*` is a character in the "
            + "text rather than an operator. Every judged test query is scored — including the ones "
            + "the lexical and vector rows refuse — which is the query set the in-memory sparse row "
            + "is scored on.",
        ],
        _ =>
        [
            "ts_rank_cd is a coverage-based rank, not BM25, and it takes no k1/b. Its ORDER BY has no "
            + "tie-break, so the order among equal scores is the executor's and can differ between runs.",
            FieldWeights is { Count: > 0 }
                ? "The weighting is a tsvector class per field, so what it changes is the ratio between "
                + "classes (A = 1.0 against D = 0.1 in ts_rank_cd's default mapping). It has no per-field "
                + "length normalisation: a match in a long field contributes the same as a match in a "
                + "short one, and only the class separates them."
                : "Every text field is written into one tsvector at the same weight, so a match in the "
                + "title and a match in the body contribute the same.",
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
    IReadOnlyList<string> Caveats,
    string RowAxis = "ef_search");

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

        if (lane.Kind == BackendKind.Fuzzy)
            return MeasureFuzzy(lane, corpus, documents, table, topK, limit, ndcgGain, excludeQueryDocument, jobs);

        if (lane.Kind == BackendKind.Sparse)
        {
            return MeasureSparse(
                lane, corpus, dataset, documents, table, topK, limit, ndcgGain, excludeQueryDocument, jobs);
        }

        if (lane.Kind == BackendKind.ParadeDb)
        {
            using var parade = new ParadeDBTextSearchEngine(
                lane.ConnectionString,
                new ParadeDBOptions { Table = table, ContentTokenizer = lane.Tokenizer });

            return Run(parade, parade.DropSchema);
        }

        using var lexical = new PostgresTextSearchEngine(
            lane.ConnectionString,
            new PostgresIndexOptions
            {
                Table = table,
                TextSearchConfig = lane.TextSearchConfig,
                FieldWeights = lane.FieldWeights,
            });

        return Run(lexical, lexical.DropSchema);

        BackendMeasurement Run(ITextSearchEngine engine, Action dropSchema)
        {
            try
            {
                // Before the index is built: a query the engine will refuse is knowable from its
                // declared syntax, and indexing 8,674 documents to then throw on the first query is
                // work nobody gets back.
                RefuseUnsupported(engine, Evaluation.BuildQueries(corpus, limit, excludeQueryDocument));

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
            {
                // Same rule as the text lane: a query this engine will refuse is knowable from what
                // it declares, and ArguAna is the corpus where that matters — 148 of its 1,406 test
                // queries are a phrase to a parser and plain text to an encoder.
                RefuseUnsupported(indexer, queries);
                indexer.Index(documents);
            }

            var rows = new List<BackendRow>(lane.EfValues.Count);

            foreach (int efSearch in lane.EfValues)
            {
                using var engine = new PostgresVectorSearchEngine(
                    lane.ConnectionString, provider, options with { HnswEfSearch = efSearch });

                try
                {
                    rows.Add(new BackendRow(
                        efSearch.ToString(CultureInfo.InvariantCulture),
                        Evaluation.RunEngine(
                            corpus, engine, $"pgvector HNSW (ef={efSearch})", topK, limit, ndcgGain,
                            excludeQueryDocument, jobs)));
                }
                catch (Exception error) when (FindRefusal(error) is { } refusal)
                {
                    throw Refusal(refusal, engine, queries);
                }
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
    /// One fuzzy row per requested <c>pg_trgm</c> similarity threshold, over one index. The threshold
    /// is query-side session state (<c>set_limit</c>), so every point reads the same table and the
    /// sweep measures the match rule rather than a rebuild.
    /// </summary>
    private static BackendMeasurement MeasureFuzzy(
        BackendLane lane,
        BeirCorpus corpus,
        IReadOnlyList<SearchDocument> documents,
        string table,
        int topK,
        int? limit,
        NdcgGain ndcgGain,
        bool excludeQueryDocument,
        int jobs)
    {
        List<EvaluatedQuery> queries = Evaluation.BuildQueries(corpus, limit, excludeQueryDocument);

        var options = new PostgresFuzzyOptions
        {
            Table = table,
            SearchMode = TrgmSearchMode.Similarity,
            SimilarityThreshold = lane.Thresholds[0],
            IndexKind = TrgmIndexKind.Both,
        };

        try
        {
            // Built once. Every threshold reads this table, and the engine truncates it before
            // indexing, so one builder is also what makes a re-run independent of the last one.
            using (var indexer = new PostgresFuzzySearchEngine(lane.ConnectionString, options))
                indexer.Index(documents);

            var rows = new List<BackendRow>(lane.Thresholds.Count);

            foreach (double threshold in lane.Thresholds)
            {
                using var engine = new PostgresFuzzySearchEngine(
                    lane.ConnectionString,
                    options with { SimilarityThreshold = threshold, AutoCreateSchema = false });

                try
                {
                    rows.Add(new BackendRow(
                        threshold.ToString("0.####", CultureInfo.InvariantCulture),
                        Evaluation.RunEngine(
                            corpus, engine,
                            $"pg_trgm similarity (threshold="
                            + threshold.ToString("0.####", CultureInfo.InvariantCulture) + ")",
                            topK, limit, ndcgGain, excludeQueryDocument, jobs)));
                }
                catch (Exception error) when (FindRefusal(error) is { } refusal)
                {
                    throw Refusal(refusal, engine, queries);
                }
            }

            return new BackendMeasurement(
                lane.Description, table, documents.Count, rows, lane.Caveats, "threshold");
        }
        finally
        {
            using var dropper = new PostgresFuzzySearchEngine(
                lane.ConnectionString, options with { AutoCreateSchema = false });
            dropper.DropSchema();
        }
    }

    /// <summary>
    /// The sparse rows: the engine's own query with its HNSW index in place, then the same table with
    /// the index dropped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two rows, because the engine has both a <c>sparsevec</c> column and an HNSW index over it, and
    /// which one answers a search is the planner's decision rather than the engine's: the SQL is
    /// <c>ORDER BY sparse &lt;#&gt; @query LIMIT k</c>, which pgvector can serve from the graph or from
    /// an exact scan. The plan PostgreSQL chose is read with <c>EXPLAIN</c> and printed with the rows,
    /// and the first row's label follows it — so a table cannot say "hnsw" about a sequential scan.
    /// </para>
    /// <para>
    /// The second row is the same table after <c>DROP INDEX</c>, so the distance between the two is
    /// what the index costs in metric points at pgvector's default <c>hnsw.ef_search</c> of 40 — the
    /// value the engine never overrides and exposes no option for. Without that row, a gap against the
    /// in-memory sparse row could not be attributed between the index and the float conversion.
    /// </para>
    /// <para>
    /// No syntax refusal runs here, unlike the lexical and vector lanes. The engine declares no query
    /// features, but this harness calls it with <c>ParseQuerySyntax: false</c> and the text reaches the
    /// encoder verbatim, exactly as it does for the in-memory sparse row — refusing would score a
    /// different query set from the row this one is put beside.
    /// </para>
    /// </remarks>
    private static BackendMeasurement MeasureSparse(
        BackendLane lane,
        BeirCorpus corpus,
        string dataset,
        IReadOnlyList<SearchDocument> documents,
        string table,
        int topK,
        int? limit,
        NdcgGain ndcgGain,
        bool excludeQueryDocument,
        int jobs)
    {
        string directory = lane.SparseVectorsDirectory
            ?? throw new InvalidOperationException(
                "A sparse lane has no vectors directory. BackendLane.Parse refuses that combination, so "
                + "this is a lane built by hand rather than from --backend/--sparse.");

        SparseVectorSet vectors = SparseVectorSet.Load(directory, dataset);

        vectors.EnsureCoversDocuments(documents, dataset);

        List<EvaluatedQuery> queries = Evaluation.BuildQueries(corpus, limit, excludeQueryDocument);

        vectors.EnsureCoversQueries(queries, dataset);

        IReadOnlyDictionary<string, int> vocabulary = Vocabulary(vectors);

        var provider = new SparseVectorProvider(vectors);

        var options = new PostgresSparseOptions
        {
            Table = table,
            Vocabulary = vocabulary,
            Dimension = vocabulary.Count,
            Distance = SparseDistance.InnerProduct,
            // Spelled out rather than inherited: the row's name states these values, and a changed
            // default would otherwise move the measurement without the label moving with it.
            HnswM = 16,
            HnswEfConstruction = 64,
        };

        try
        {
            // The engine truncates before indexing, so a re-run cannot score a corpus plus whatever a
            // failed run left in the table. The HNSW index is built by the constructor and accepts
            // inserts, which is why the load can happen after it exists.
            using (var indexer = new PostgresSparseSearchEngine(lane.ConnectionString, provider, options))
                indexer.Index(documents);

            if (!SparseIndexExists(lane.ConnectionString, table))
            {
                throw new InvalidOperationException(
                    $"PostgresSparseSearchEngine did not create '{SparseIndexName(table)}', so both rows "
                    + "would be exact scans and the measurement would not contain the index it names.");
            }

            (string node, bool usesIndex) =
                SparsePlan(lane.ConnectionString, table, vocabulary.Count, topK + 1);

            var rows = new List<BackendRow>(2);

            using (var engine = new PostgresSparseSearchEngine(
                       lane.ConnectionString, provider, options with { AutoCreateSchema = false }))
            {
                rows.Add(new BackendRow(
                    usesIndex ? "hnsw" : "sequential",
                    Evaluation.RunEngine(
                        corpus, engine, lane.Description, topK, limit, ndcgGain,
                        excludeQueryDocument, jobs)));
            }

            DropSparseIndex(lane.ConnectionString, table);

            using (var engine = new PostgresSparseSearchEngine(
                       lane.ConnectionString, provider, options with { AutoCreateSchema = false }))
            {
                rows.Add(new BackendRow(
                    "exact",
                    Evaluation.RunEngine(
                        corpus, engine, lane.Description, topK, limit, ndcgGain,
                        excludeQueryDocument, jobs)));
            }

            var caveats = new List<string>(lane.Caveats)
            {
                $"The column is sparsevec({vocabulary.Count.ToString("N0", CultureInfo.InvariantCulture)}) "
                + "— one coordinate per term the vector set carries — and the producer counted "
                + $"{vectors.Manifest.TermsPerDocument:0.0} non-zero terms per document on average.",
                $"The plan PostgreSQL chose for the search shape on this table: {node}. The first row "
                + "was measured with the engine's HNSW index in place and the second after DROP INDEX, "
                + "so the difference between them is what the index costs in metric points at pgvector's "
                + "default hnsw.ef_search of 40.",
            };

            if (!usesIndex)
            {
                caveats.Add(
                    "The plan above does not name the index, so the first row is an exact scan too and "
                    + "the two rows differ only in the table's index state.");
            }

            return new BackendMeasurement(
                lane.Description, table, documents.Count, rows, caveats, "scan");
        }
        finally
        {
            using var dropper = new PostgresSparseSearchEngine(
                lane.ConnectionString, provider, options with { AutoCreateSchema = false });
            dropper.DropSchema();
        }
    }

    /// <summary>
    /// The term → coordinate layout the <c>sparsevec</c> column is written with: every term any vector
    /// in the set carries, sorted so the mapping does not depend on dictionary iteration order.
    /// </summary>
    /// <remarks>
    /// A term absent from this mapping is dropped by the engine, so the union of the document and query
    /// vectors is what makes the SQL dot product the same sum the in-memory engine computes. It is this
    /// run's layout: an application fixes one vocabulary and keeps it across writes.
    /// </remarks>
    private static IReadOnlyDictionary<string, int> Vocabulary(SparseVectorSet vectors)
    {
        var terms = new SortedSet<string>(StringComparer.Ordinal);

        foreach (IReadOnlyDictionary<string, float> vector in vectors.Documents.Values)
            terms.UnionWith(vector.Keys);

        foreach (IReadOnlyDictionary<string, float> vector in vectors.Queries.Values)
            terms.UnionWith(vector.Keys);

        var vocabulary = new Dictionary<string, int>(terms.Count, StringComparer.Ordinal);
        int coordinate = 0;

        foreach (string term in terms)
            vocabulary[term] = coordinate++;

        return vocabulary;
    }

    /// <summary>The name the engine gives its index: <c>&lt;table&gt;_sparse_hnsw</c>.</summary>
    private static string SparseIndexName(string table) => table + "_sparse_hnsw";

    /// <summary>A quoted identifier; callers pass names validated as <c>[A-Za-z0-9_]</c>.</summary>
    private static string Quote(string identifier) => "\"" + identifier + "\"";

    private static bool SparseIndexExists(string connectionString, string table)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT to_regclass(@name) IS NOT NULL";
        command.Parameters.AddWithValue("name", "public." + Quote(SparseIndexName(table)));

        return (bool)command.ExecuteScalar()!;
    }

    private static void DropSparseIndex(string connectionString, string table)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "DROP INDEX IF EXISTS public." + Quote(SparseIndexName(table));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// The plan node PostgreSQL picks for the engine's search shape, and whether it names the HNSW
    /// index. The literal is a one-coordinate vector of the column's dimension: a plan depends on the
    /// operator, the type and the LIMIT, not on the weights, and one shape serves every query.
    /// </summary>
    private static (string Node, bool UsesIndex) SparsePlan(
        string connectionString, string table, int dimension, int fetchLimit)
    {
        using var connection = new NpgsqlConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText =
            "EXPLAIN (COSTS OFF) SELECT id FROM public." + Quote(table)
            + " WHERE sparse IS NOT NULL ORDER BY sparse <#> '{1:1}/"
            + dimension.ToString(CultureInfo.InvariantCulture) + "'::sparsevec LIMIT "
            + fetchLimit.ToString(CultureInfo.InvariantCulture);

        var lines = new List<string>();

        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                lines.Add(reader.GetString(0).Trim());
        }

        string node = lines.FirstOrDefault(line => line.Contains("Scan", StringComparison.Ordinal))
            ?? lines.FirstOrDefault()
            ?? "no plan";

        return (node.TrimStart('-', '>', ' '), node.Contains(SparseIndexName(table), StringComparison.Ordinal));
    }

    /// <summary>
    /// Refuses, before any indexing, a query whose syntax the engine declares it cannot read.
    /// </summary>
    /// <remarks>
    /// The engine's own rule, asked ahead of time rather than discovered by running: an engine that
    /// declares phrases rejects a prefix operator, and one that declares nothing rejects a quote,
    /// because a phrase is syntax it will not read.
    /// </remarks>
    public static void RefuseUnsupported(ITextSearchEngine engine, IReadOnlyList<EvaluatedQuery> queries)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(queries);

        if (engine is not IQuerySyntaxSupport support)
            return;

        try
        {
            foreach (EvaluatedQuery query in queries)
            {
                QuerySyntax.EnsureSupported(
                    query.Query.Text, support.SupportedQueryFeatures, engine.GetType().Name);
            }
        }
        catch (NotSupportedException refusal)
        {
            throw Refusal(refusal, engine, queries);
        }
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
    /// concatenated instead, which is exactly the flat view the in-memory BM25 rows score. The fuzzy
    /// engine reads <c>content</c> alone too, so it gets the same shape. The sparse engine has no
    /// field concept and its vectors were computed from <c>title + "\n" + text</c> by the producer, so
    /// the same concatenation is the only text whose SHA-256 is in the vector file.
    /// </remarks>
    public static IReadOnlyList<SearchDocument> Shape(BackendKind kind, IReadOnlyList<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (kind is not (BackendKind.ParadeDb or BackendKind.Fuzzy or BackendKind.Sparse))
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
            lines.Add($"  {measurement.RowAxis,9} | nDCG@10 | MAP@10 | MRR@10 |  R@10 | queries");

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
