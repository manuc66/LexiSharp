using System.Globalization;

using LexiSharp.Core;
using LexiSharp.Ranking;
using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// Okapi BM25 on stock PostgreSQL: the <c>tsvector</c> column and GIN index do the matching, and
/// <see cref="Bm25Scorer"/> does the ranking over the term frequencies the vector already holds.
/// No extension beyond <c>unaccent</c>, so it runs on RDS, Cloud SQL and Azure Database for
/// PostgreSQL — where the ParadeDB <c>pg_search</c> extension cannot be installed — with only the
/// usual privileged role needed to create that one extension.
/// </summary>
/// <remarks>
/// <para>
/// <b>Where each number comes from.</b> The document count, the average length and every term's
/// document frequency are read once by <see cref="PostgresBm25Statistics"/> through
/// <c>ts_stat</c> and cached until a write invalidates them; the per-document term frequencies and
/// length come from the candidate row's own <c>tsvector</c>. The query passes through the same
/// text search configuration as the column, so the terms the scorer sums over are the terms the
/// index matched — not a second tokenizer's opinion of them.
/// </para>
/// <para>
/// <b>The candidate gate.</b> A GIN index returns posting lists, not rows in score order, so the
/// only way to cut the cost of a query whose common terms match most of the corpus is to rank a
/// smaller set. <see cref="PostgresBm25Options.RareTermGate"/> generates candidates from the
/// query's terms whose document frequency is at most
/// <see cref="PostgresBm25Options.RareTermGateBudget"/> — the terms BM25's idf says
/// are the discriminative ones — and scores those with the full query. It is a recall bound:
/// a document holding none of the rare terms is not returned. See the backend guide for what it
/// measured.
/// </para>
/// <para>
/// <b>What the pool costs.</b> PostgreSQL selects the <see cref="PostgresBm25Options.CandidatePool"/>
/// best matches by <c>ts_rank_cd</c> and the engine re-ranks that set; scoring the whole match set
/// would make a query proportional to the corpus and mean shipping every <c>tsvector</c> to the
/// client. A document outside the pool cannot be returned. The pool is a recall bound, not a
/// ranking approximation: inside it the ordering is exact BM25.
/// </para>
/// <para>
/// <b>Scores.</b> Unlike the <c>ts_rank_cd</c> engine, these are <see cref="Bm25Scorer"/> scores on
/// the in-memory scorer's scale, with the same conventions — so a document's score is comparable
/// with the in-memory BM25 row, and <see cref="Bm25Scorer.Explain"/>'s decomposition applies to the
/// same formula. What is <i>not</i> identical is the analysis: PostgreSQL's configuration, not this
/// library's tokenizer, produced the terms on both sides.
/// </para>
/// <para>
/// <b>Statistics lag.</b> The cached snapshot is invalidated by <see cref="Add"/>,
/// <see cref="Remove"/>, <see cref="Clear"/> and <see cref="Index"/>, so the first search after a
/// write pays one <c>ts_stat</c> walk of the table. An application that writes and searches in a
/// tight loop should batch its writes through <see cref="Index"/>, or accept that cost.
/// </para>
/// </remarks>
public sealed class PostgresBm25SearchEngine : ITextSearchEngine, IDisposable, IQuerySyntaxSupport
{
    /// <inheritdoc />
    public QueryFeatures SupportedQueryFeatures => QueryFeatures.Phrases;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresBm25Options _options;
    private readonly PostgresIndexOptions _indexOptions;
    private readonly Bm25Scorer _scorer;
    private PostgresBm25Statistics? _statistics;
    private bool _schemaReady;

    /// <param name="connectionString">A PostgreSQL connection string (Npgsql format).</param>
    /// <param name="options">Naming, analysis and ranking options (default: <see cref="PostgresBm25Options"/>).</param>
    /// <param name="autoCreateSchema">
    /// Shortcut for <see cref="PostgresBm25Options.AutoCreateSchema"/>; overrides the option when set.
    /// </param>
    public PostgresBm25SearchEngine(
        string connectionString,
        PostgresBm25Options? options = null,
        bool? autoCreateSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _options = options ?? new PostgresBm25Options();

        if (!_options.IsValid)
            throw new ArgumentException(
                "Schema, table and text search config must match [A-Za-z0-9_], a field weight must name "
                + "a non-empty field with a defined class, k1 must be a finite non-negative number, b a "
                + "finite number in [0, 1], and CandidatePool a non-negative count.",
                nameof(options));

        if (autoCreateSchema is not null)
            _options = _options with { AutoCreateSchema = autoCreateSchema.Value };

        _indexOptions = _options.AsIndexOptions();
        _scorer = new Bm25Scorer(_options.K1, _options.B);

        _dataSource = NpgsqlDataSource.Create(connectionString);

        if (_options.AutoCreateSchema)
            EnsureSchema();
    }

    /// <summary>
    /// Installs the <c>unaccent</c> extension, the shared documents table and its GIN index.
    /// Idempotent, and the same schema the lexical engine creates.
    /// </summary>
    public void EnsureSchema()
    {
        if (_schemaReady)
            return;

        using var connection = _dataSource.OpenConnection();
        PostgresSchema.CreateDocumentTable(connection, _indexOptions);
        _schemaReady = true;
    }

    /// <summary>Async variant of <see cref="EnsureSchema"/>.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await PostgresSchema.CreateDocumentTableAsync(connection, _indexOptions, cancellationToken).ConfigureAwait(false);
        _schemaReady = true;
    }

    /// <summary>For tests: the physical table name in use.</summary>
    internal string TestTableName => _options.Table;

    /// <summary>Drops the documents table and its indexes. Useful for tests and clean teardowns.</summary>
    public void DropSchema()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {_options.QualifiedTableName}"; // NOSONAR:S2077 (identifier only, validated + quoted)
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// Rebuilds the corpus statistics now, instead of waiting for the next write to invalidate them.
    /// </summary>
    /// <remarks>
    /// The snapshot is exact for the corpus at the moment it was read and unchanged until a write
    /// through this engine invalidates it. A writer that changes the table by other means — another
    /// engine on the same table, a bulk load, a restore — invalidates nothing, and this is how that
    /// caller says so. Returns the number of terms in the snapshot.
    /// </remarks>
    public int RefreshStatistics()
    {
        using var connection = _dataSource.OpenConnection();
        _statistics = PostgresBm25Statistics.Load(connection, _indexOptions);
        return _statistics.VocabularySize;
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        using var connection = _dataSource.OpenConnection();

        using (var command = connection.CreateCommand())
        using (var transaction = connection.BeginTransaction())
        {
            command.CommandText = $"TRUNCATE {_options.QualifiedTableName}"; // NOSONAR:S2077 (identifier only, validated + quoted)
            command.Transaction = transaction;
            command.ExecuteNonQuery();

            foreach (var document in documents)
                PostgresDocumentWriter.Insert(connection, transaction, _indexOptions, document);

            transaction.Commit();
        }

        _statistics = null;
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var connection = _dataSource.OpenConnection();
        PostgresDocumentWriter.Insert(connection, null, _indexOptions, document);

        _statistics = null;
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_options.QualifiedTableName} WHERE id = @id"; // NOSONAR:S2077 (identifiers only; id is parameterized)
        command.Parameters.AddWithValue("id", documentId);
        bool removed = command.ExecuteNonQuery() > 0;

        if (removed)
            _statistics = null;

        return removed;
    }

    /// <inheritdoc />
    public void Clear()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"TRUNCATE {_options.QualifiedTableName}"; // NOSONAR:S2077 (identifier only, validated + quoted)
        command.ExecuteNonQuery();

        _statistics = null;
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(
        string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= SearchOptions.Default;

        if (options.IsEmpty || string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        PostgresTieBreak.EnsureSupported(options);

        // ParseQuerySyntax: false is the caller saying "literal text, do not consult the query
        // parser" — and EnsureSupported is a parser call.
        if (options.ParseQuerySyntax)
            QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(PostgresBm25SearchEngine), options.KnownFields);

        using var connection = _dataSource.OpenConnection();

        // The same analysis on both sides: PostgreSQL's, via the column's configuration.
        string[] queryTerms = ReadQueryLexemes(connection, query);

        if (queryTerms.Length == 0)
            return Array.Empty<SearchResult>();

        // The query's terms, as the set the per-candidate reader keeps: the length needs every
        // position, the frequencies only need these.
        var keptTerms = new HashSet<string>(queryTerms, StringComparer.Ordinal);

        // Benign race: two concurrent searches may build the snapshot twice and both publish a
        // complete one. A reference assignment is atomic, so no reader can see a half-built object.
        var statistics = _statistics ??= PostgresBm25Statistics.Load(connection, _indexOptions);
        var match = PostgresMatchExpression.Build(_indexOptions, query);

        string[] gateTerms = GateTermsFor(match, statistics, queryTerms);

        using var command = CreateCandidatesCommand(connection, match, gateTerms, options);

        List<PostgresBm25Candidate> candidates = ReadCandidates(command, keptTerms, cancellationToken);

        // The gate is a query-level decision, so it is one step with no document id: which rule was
        // applied, and how many candidates it produced. Recorded even when the count is zero,
        // because "the gate matched nothing" is the answer a reader is looking for.
        if (options.Trace is not null)
        {
            options.Trace.Record(new TraceStep(
                TraceStage.Gate,
                string.Empty,
                double.NaN,
                double.NaN,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"{GateDescription(match, gateTerms)}; {candidates.Count:N0} candidates")));
        }

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        List<(string DocumentId, double Score)> scored =
            ScoreCandidates(candidates, queryTerms, statistics, options);

        List<(string DocumentId, double Score)> page = scored
            .Skip(options.Offset)
            .Take(options.Limit)
            .ToList();

        if (page.Count == 0)
            return Array.Empty<SearchResult>();

        Dictionary<string, SearchDocument> documents = ReadDocuments(connection, page, cancellationToken);

        List<SearchResult> results = BuildResults(page, documents);

        options.Trace?.RecordScoreStage("Postgres BM25", results);

        return results;
    }

    /// <summary>
    /// Builds the candidate query: the match expression, the metadata filters, the rare-term gate
    /// and the pool, in one statement.
    /// </summary>
    /// <remarks>
    /// Two shapes in one statement: the inner query ranks the match set and keeps the top
    /// <c>@pool</c>, carrying only <c>(id, score)</c> so the sort stays in memory; the outer join
    /// then reads one <c>tsv</c> per candidate. Selecting content and jsonb here as well would put
    /// about a kilobyte of payload per row into the sort, which PostgreSQL answered by spilling it
    /// to disk.
    /// </remarks>
    private NpgsqlCommand CreateCandidatesCommand(
        NpgsqlConnection connection,
        PostgresMatchExpression match,
        string[] gateTerms,
        SearchOptions options)
    {
        var command = connection.CreateCommand();

        var filters = PostgresMetadataFilterSql.Build(options.Filters);
        filters.Apply(command);
        match.Bind(command);

        // The candidate gate: the query's rare terms when it has any, the whole query otherwise.
        // A GIN index cannot rank, so this is what keeps the scan proportional to the
        // discriminative part of the query instead of to every document its common part matches.
        string gate = match.Gate;

        if (gateTerms.Length > 0)
        {
            gate = "tsv @@ (SELECT string_agg(quote_literal(term), ' | ') FROM unnest(@gateTerms::text[]) AS term)::tsquery";
            command.Parameters.AddWithValue("gateTerms", gateTerms);
        }

        command.CommandText = $"""
            SELECT t.id, t.tsv::text
            FROM {_options.QualifiedTableName} t
            JOIN (
                SELECT id, {match.Rank} AS score
                FROM {_options.QualifiedTableName}
                WHERE ({gate}){filters.Fragment}
                ORDER BY score DESC, {PostgresSchema.OrdinalIdTieBreak}
                LIMIT @pool
            ) s ON t.id = s.id;
            """; // NOSONAR:S2077 (identifiers validated and quoted; query text parameterized)

        command.Parameters.AddWithValue("pool", PoolSize(_options, options));

        return command;
    }

    /// <summary>Reads the candidate rows: an id and the term frequencies the gate and the scorer need.</summary>
    private static List<PostgresBm25Candidate> ReadCandidates(
        NpgsqlCommand command, HashSet<string> keptTerms, CancellationToken cancellationToken)
    {
        var candidates = new List<PostgresBm25Candidate>();

        using (var cancelling = PostgresCancellation.Register(command, cancellationToken))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var (termFrequencies, length) =
                    PostgresTsvText.Parse(reader.IsDBNull(1) ? null : reader.GetString(1), keptTerms);

                candidates.Add(new PostgresBm25Candidate(reader.GetString(0), termFrequencies, length));
            }
        }

        return candidates;
    }

    /// <summary>Scores the candidates with BM25 and returns the survivors, best first.</summary>
    private List<(string DocumentId, double Score)> ScoreCandidates(
        List<PostgresBm25Candidate> candidates,
        string[] queryTerms,
        PostgresBm25Statistics statistics,
        SearchOptions options)
    {
        var view = new PostgresBm25IndexView(statistics, candidates);
        var scored = new List<(string DocumentId, double Score)>(candidates.Count);

        foreach (PostgresBm25Candidate candidate in candidates)
        {
            double score = _scorer.Score(candidate.DocumentId, queryTerms, view);

            if (double.IsNaN(score) || double.IsInfinity(score) || score <= 0)
                continue;

            if (score < options.MinimumScore)
                continue;

            if (!PostgresDocumentExclusion.Passes(options, candidate.DocumentId))
                continue;

            scored.Add((candidate.DocumentId, score));
        }

        // Score descending, then the ordinal id: the same tie-break the SQL engines write into
        // their ORDER BY, applied here because the ordering is computed in this process.
        scored.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.DocumentId, right.DocumentId);
        });

        return scored;
    }

    /// <summary>
    /// Pairs the page's scores with the documents read for it, skipping a row another writer
    /// deleted between the two reads.
    /// </summary>
    private static List<SearchResult> BuildResults(
        List<(string DocumentId, double Score)> page,
        Dictionary<string, SearchDocument> documents)
    {
        var results = new List<SearchResult>(page.Count);

        foreach (var (documentId, score) in page)
        {
            if (documents.TryGetValue(documentId, out SearchDocument? document))
                results.Add(new SearchResult(documentId, score, document));
        }

        return results;
    }

    /// <summary>
    /// Reads the documents of the page, by id — the second half of the two-phase read.
    /// </summary>
    /// <remarks>
    /// The candidate query fetched ids and term frequencies for the whole pool; only the page's
    /// rows need their content, category and fields. One statement with <c>= ANY(@ids)</c> rather
    /// than one per id: it is the same round trip for a page of ten.
    /// </remarks>
    private Dictionary<string, SearchDocument> ReadDocuments(
        NpgsqlConnection connection,
        IReadOnlyList<(string DocumentId, double Score)> page,
        CancellationToken cancellationToken)
    {
        var ids = new string[page.Count];

        for (int i = 0; i < page.Count; i++)
            ids[i] = page[i].DocumentId;

        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT id, content, category, fields, text_fields
            FROM {_options.QualifiedTableName}
            WHERE id = ANY(@ids);
            """; // NOSONAR:S2077 (identifier only, validated + quoted)

        command.Parameters.AddWithValue("ids", ids);

        var documents = new Dictionary<string, SearchDocument>(page.Count, StringComparer.Ordinal);

        using (var cancelling = PostgresCancellation.Register(command, cancellationToken))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                SearchDocument document = PostgresDocumentReader.Read(reader);
                documents[document.Id] = document;
            }
        }

        return documents;
    }

    /// <summary>Releases the underlying Npgsql data source.</summary>
    public void Dispose() => _dataSource.Dispose();

    /// <summary>
    /// How many matches the database hands over: the configured pool, widened to at least the
    /// requested page, and capped by the engine's per-query ceiling.
    /// </summary>
    /// <remarks>
    /// The widening is what keeps pagination honest: a page beyond the pool would otherwise come
    /// back empty even though more matches exist. The pool decides which candidates are scored, and
    /// it can never be smaller than the window the caller asked for.
    /// </remarks>
    internal static int PoolSize(PostgresBm25Options options, SearchOptions searchOptions)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(searchOptions);

        long wanted = (long)searchOptions.Window + (searchOptions.ExcludedDocumentIds?.Count ?? 0);

        if (options.CandidatePool == 0)
            return PostgresBm25Options.MaximumCandidatePool;

        return (int)Math.Min(
            Math.Max(options.CandidatePool, wanted),
            PostgresBm25Options.MaximumCandidatePool);
    }

    /// <summary>
    /// The terms the gate applies to this query, or an empty array when it does not apply.
    /// </summary>
    /// <remarks>
    /// Only for a free-text query. A mixed query's gate is the phrase conjunction, and the free
    /// terms beside it are scoring-only by contract: substituting the rare-term disjunction for the
    /// phrases would let a document without the phrase in.
    /// </remarks>
    private string[] GateTermsFor(
        PostgresMatchExpression match, PostgresBm25Statistics statistics, string[] queryTerms) =>
        _options.RareTermGate && !match.HasPhrases
            ? GateTerms(queryTerms, statistics.DocumentFrequency, _options.RareTermGateBudget)
            : [];

    /// <summary>The gate's rule in words, for the trace.</summary>
    private string GateDescription(PostgresMatchExpression match, string[] gateTerms)
    {
        if (gateTerms.Length > 0)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"rare-term gate [{string.Join(", ", gateTerms)}] under a {_options.RareTermGateBudget:N0}-row budget");
        }

        if (match.HasPhrases)
            return "phrase gate";

        if (!_options.RareTermGate)
            return "all terms (gate disabled)";

        return string.Create(
            CultureInfo.InvariantCulture,
            $"all terms (no term within a {_options.RareTermGateBudget:N0}-row budget)");
    }

    /// <summary>
    /// The query's terms the candidate gate matches on: the rarest ones, by ascending document
    /// frequency, while their summed document frequency stays inside <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sum of the selected terms' document frequencies is an upper bound on the gate's match
    /// set, so the budget is a ceiling on rows read rather than a frequency threshold whose meaning
    /// would change with the corpus. Terms are added while they fit; when even the rarest one does
    /// not, the caller falls back to the whole query.
    /// </para>
    /// <para>
    /// Absent terms (<c>df = 0</c>) are skipped: gating on one would match nothing, and it cannot
    /// contribute to a score either. The order is frequency then ordinal, so the selection does not
    /// depend on the order a dictionary happened to produce.
    /// </para>
    /// </remarks>
    internal static string[] GateTerms(
        IReadOnlyList<string> terms,
        Func<string, int> documentFrequency,
        int budget)
    {
        ArgumentNullException.ThrowIfNull(terms);
        ArgumentNullException.ThrowIfNull(documentFrequency);

        if (budget <= 0)
            return [];

        var candidates = new List<(string Term, int DocumentFrequency)>(terms.Count);

        foreach (string term in terms)
        {
            int frequency = documentFrequency(term);

            if (frequency > 0)
                candidates.Add((term, frequency));
        }

        candidates.Sort(static (left, right) =>
        {
            int byFrequency = left.DocumentFrequency.CompareTo(right.DocumentFrequency);
            return byFrequency != 0 ? byFrequency : string.CompareOrdinal(left.Term, right.Term);
        });

        var selected = new List<string>(candidates.Count);
        long bound = 0;

        foreach (var candidate in candidates)
        {
            if (bound + candidate.DocumentFrequency > budget)
                break;

            selected.Add(candidate.Term);
            bound += candidate.DocumentFrequency;
        }

        return [.. selected];
    }

    private string[] ReadQueryLexemes(NpgsqlConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PostgresTsQueryText.QueryLexemes(_indexOptions.TextSearchConfig); // NOSONAR:S2077 (validated config only)
        command.Parameters.AddWithValue("query", query);

        return command.ExecuteScalar() as string[] ?? Array.Empty<string>();
    }
}
