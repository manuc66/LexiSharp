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
internal sealed record BackendLane(
    BackendKind Kind,
    string ConnectionString,
    string TextSearchConfig,
    Bm25Parameters? Bm25 = null)
{
    /// <summary>Parses <c>--backend</c>, refusing a name it does not implement by name.</summary>
    public static BackendLane Parse(
        string kind, string connectionString, string textSearchConfig, Bm25Parameters? bm25 = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        BackendKind parsed = kind switch
        {
            "lexical" => BackendKind.Lexical,
            "paradedb" => BackendKind.ParadeDb,
            _ => throw new ArgumentException(
                $"--backend expects 'lexical' or 'paradedb', got '{kind}'. The vector and sparse engines "
                + "are measured by the ANN curve (--ann-ef), which reports recall rather than nDCG."),
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

        return new BackendLane(parsed, connectionString, textSearchConfig, bm25);
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
    public string Description => Kind == BackendKind.ParadeDb
        ? "ParadeDB BM25 (pdb.score, Tantivy)" + Bm25Text
          + " — index over content only, title concatenated into it"
        : $"PostgreSQL lexical (ts_rank_cd, text search config '{TextSearchConfig}' + unaccent)";

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
    public IReadOnlyList<string> Caveats =>
    [
        Kind == BackendKind.ParadeDb
            ? "Scores are Tantivy's BM25" + Bm25Text + ", computed by pg_search; the analysis is "
              + "pdb.simple with ASCII folding, not this library's tokenizer."
            : "ts_rank_cd is a coverage-based rank, not BM25, and it takes no k1/b. Its ORDER BY has no "
              + "tie-break, so the order among equal scores is the executor's and can differ between runs.",
        "The analysis is PostgreSQL's, not the harness tokenizer's: no Porter, no stop list, no "
          + "possessive rule. This row is comparable with the in-memory 'defaults' rows at most, and "
          + "not with an aligned (--analyzer english) row or a published figure.",
    ];
}

/// <summary>One measured backend row: the engine that ran, the index it read, and its metrics.</summary>
internal sealed record BackendMeasurement(
    string Description,
    string Table,
    int Documents,
    ConfigResult Result,
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
    public static BackendMeasurement Measure(
        BackendLane lane,
        BeirCorpus corpus,
        string dataset,
        int topK,
        int? limit,
        NdcgGain ndcgGain,
        bool excludeQueryDocument,
        int jobs)
    {
        ArgumentNullException.ThrowIfNull(lane);
        ArgumentNullException.ThrowIfNull(corpus);

        IReadOnlyList<SearchDocument> documents = Shape(lane.Kind, Evaluation.BuildDocuments(corpus));
        string table = lane.TableFor(dataset);

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

                return new BackendMeasurement(lane.Description, table, documents.Count, result, lane.Caveats);
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
    /// The row as one block of text. A table row would carry a Time column measured through a
    /// connection open per query, and printing it beside the in-memory timings would invite a
    /// comparison that means nothing.
    /// </summary>
    public static string Format(BackendMeasurement measurement, string dataset)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        ConfigResult result = measurement.Result;

        return string.Create(
            CultureInfo.InvariantCulture,
            $"""
             == {dataset}, backend ==
             Engine: {measurement.Description}
             Index:  {measurement.Documents:N0} documents in {measurement.Table}, dropped after the run
             Result: nDCG@10 {result.NdcgAt10:0.0000} | MAP@10 {result.MapAt10:0.0000} | MRR@10 {result.MrrAt10:0.0000} | R@10 {result.RecallAt10:0.0000} | {result.Queries:N0} queries

             """);
    }
}
