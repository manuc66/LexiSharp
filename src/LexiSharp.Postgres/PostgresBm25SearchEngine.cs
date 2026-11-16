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

        // ParseQuerySyntax: false is the caller saying "literal text, do not consult the query
        // parser" — and EnsureSupported is a parser call.
        if (options.ParseQuerySyntax)
            QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(PostgresBm25SearchEngine), options.KnownFields);

        using var connection = _dataSource.OpenConnection();

        // The same analysis on both sides: PostgreSQL's, via the column's configuration.
        string[] queryTerms = ReadQueryLexemes(connection, query);

        if (queryTerms.Length == 0)
            return Array.Empty<SearchResult>();

        // Benign race: two concurrent searches may build the snapshot twice and both publish a
        // complete one. A reference assignment is atomic, so no reader can see a half-built object.
        var statistics = _statistics ??= PostgresBm25Statistics.Load(connection, _indexOptions);
        var match = PostgresMatchExpression.Build(_indexOptions, query);

        using var command = connection.CreateCommand();

        var filters = PostgresMetadataFilterSql.Build(options.Filters);
        filters.Apply(command);
        match.Bind(command);

        string candidatesSql = $"""
            SELECT id, content, category, fields, text_fields, tsv::text
            FROM {_options.QualifiedTableName}
            WHERE ({match.Gate}){filters.Fragment}
            ORDER BY {match.Rank} DESC, {PostgresSchema.OrdinalIdTieBreak}
            LIMIT @pool;
            """;

        // Identifiers/config only are interpolated (validated [A-Za-z0-9_] and quoted); query text is parameterized.
        command.CommandText = candidatesSql; // NOSONAR:S2077
        command.Parameters.AddWithValue("pool", PoolSize(_options, options));

        var candidates = new List<PostgresBm25Candidate>();

        using (var cancelling = PostgresCancellation.Register(command, cancellationToken))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                SearchDocument document = PostgresDocumentReader.Read(reader);
                var (termFrequencies, length) =
                    PostgresTsvText.Parse(reader.IsDBNull(5) ? null : reader.GetString(5));

                candidates.Add(new PostgresBm25Candidate(document, termFrequencies, length));
            }
        }

        if (candidates.Count == 0)
            return Array.Empty<SearchResult>();

        var view = new PostgresBm25IndexView(statistics, candidates);
        var results = new List<SearchResult>(candidates.Count);

        foreach (PostgresBm25Candidate candidate in candidates)
        {
            double score = _scorer.Score(candidate.Document.Id, queryTerms, view);

            if (double.IsNaN(score) || double.IsInfinity(score) || score <= 0)
                continue;

            if (score < options.MinimumScore)
                continue;

            if (!PostgresDocumentExclusion.Passes(options, candidate.Document.Id))
                continue;

            results.Add(new SearchResult(candidate.Document.Id, score, candidate.Document));
        }

        // Score descending, then the ordinal id: the same tie-break the SQL engines write into
        // their ORDER BY, applied here because the ordering is computed in this process.
        results.Sort(static (left, right) =>
        {
            int byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.CompareOrdinal(left.DocumentId, right.DocumentId);
        });

        return results.Skip(options.Offset).Take(options.Limit).ToList();
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

    private string[] ReadQueryLexemes(NpgsqlConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PostgresTsQueryText.QueryLexemes(_indexOptions.TextSearchConfig); // NOSONAR:S2077 (validated config only)
        command.Parameters.AddWithValue("query", query);

        return command.ExecuteScalar() as string[] ?? Array.Empty<string>();
    }
}
