using LexiSharp.Core;
using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// PostgreSQL-backed <see cref="ITextSearchEngine"/>: lexical full-text search over a
/// <c>tsvector</c> column with a GIN index, queried with <c>websearch_to_tsquery</c> and ranked
/// with <c>ts_rank_cd</c>. Combined with <c>unaccent()</c> and the <c>simple</c> configuration it
/// mirrors LexiSharp's diacritic-insensitive normalization.
/// </summary>
/// <remarks>
/// <para>
/// Schema (created by <see cref="EnsureSchema"/> unless <see cref="PostgresIndexOptions.AutoCreateSchema"/>
/// is disabled):
/// </para>
/// <code>
/// CREATE EXTENSION IF NOT EXISTS unaccent;
/// CREATE TABLE IF NOT EXISTS {schema}.{table} (
///   id       text PRIMARY KEY,
///   content  text NOT NULL,
///   category text,
///   fields   jsonb,
///   text_fields jsonb,
///   tsv      tsvector
/// );
/// CREATE INDEX IF NOT EXISTS {table}_tsv_gin ON {schema}.{table} USING GIN (tsv);
/// </code>
/// <para>
/// <c>tsv</c> is an ordinary column the engine writes on every insert and upsert (from
/// <c>content</c> and <c>text_fields</c>, see <see cref="PostgresSchema.TsvExpression"/>), not a
/// generated column: the weighting that derives it lives in the options, and rows written by the
/// vector and sparse engines into the same table are written with the same expression.
/// </para>
/// <para>
/// Ranking uses the database's native <c>ts_rank_cd</c>, so scores are <b>not</b> numerically
/// comparable to LexiSharp's BM25/TF-IDF. When identical scoring matters, wrap this engine in
/// the hybrid package's merger strategy (re-rank on the union), recompute scores in C#, or use
/// <see cref="PostgresBm25SearchEngine"/>, which ranks the same rows with BM25 and no extension.
/// </para>
/// <para>
/// <see cref="PostgresIndexOptions.MatchMode"/> decides how a multi-term query becomes a
/// <c>tsquery</c>: <see cref="PostgresMatchMode.AllTerms"/> (the default, and what this engine has
/// always issued) requires every term, <see cref="PostgresMatchMode.AnyTerm"/> ORs the query's
/// lexemes. The mode changes the candidate set, not the ranking — <c>ts_rank_cd</c> is coverage
/// based either way.
/// </para>
/// <para>
/// Embeddings/ANN (<c>pgvector</c>) are intentionally not created here: add a
/// <c>embedding vector(n)</c> column and an HNSW/IVFFlat index when you opt into vector search.
/// </para>
/// </remarks>
public sealed class PostgresTextSearchEngine : ITextSearchEngine, IDisposable, IQuerySyntaxSupport
{
    /// <inheritdoc />
    public QueryFeatures SupportedQueryFeatures => QueryFeatures.Phrases;

    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresIndexOptions _options;
    private bool _schemaReady;

    /// <param name="connectionString">A PostgreSQL connection string (Npgsql format).</param>
    /// <param name="options">Naming/behavior options (default: <see cref="PostgresIndexOptions"/>).</param>
    /// <param name="autoCreateSchema">
    /// Shortcut for <see cref="PostgresIndexOptions.AutoCreateSchema"/>; overrides the option when set.
    /// </param>
    public PostgresTextSearchEngine(
        string connectionString,
        PostgresIndexOptions? options = null,
        bool? autoCreateSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _options = options ?? new PostgresIndexOptions();

        if (!_options.IsValid)
            throw new ArgumentException(
                "Schema, table and text search config must match [A-Za-z0-9_], and a field weight must name " +
                "a non-empty field with a defined class.",
                nameof(options));

        if (autoCreateSchema is not null)
            _options = _options with { AutoCreateSchema = autoCreateSchema.Value };

        _dataSource = NpgsqlDataSource.Create(connectionString);

        if (_options.AutoCreateSchema)
        {
            EnsureSchema();
            _schemaReady = true;
        }
        else
        {
            _schemaReady = false;
        }
    }

    /// <summary>Installs the extension, table and GIN index if they do not exist yet. Idempotent.</summary>
    /// <remarks>
    /// This path is synchronous on purpose: it must not await <see cref="EnsureSchemaAsync"/> and
    /// block a thread-pool thread on a continuation the same pool has to schedule. Under load that
    /// wait reaches the connection timeout. Opening the connection and running the DDL
    /// synchronously removes the dependency.
    /// </remarks>
    public void EnsureSchema()
    {
        if (_schemaReady)
            return;

        using var connection = _dataSource.OpenConnection();
        PostgresSchema.CreateDocumentTable(connection, _options);
        _schemaReady = true;
    }

    /// <summary>For tests: the physical table name in use.</summary>
    internal string TestTableName => _options.Table;

    /// <summary>
    /// Drops the documents table (and its GIN index). Useful for tests and clean teardowns.
    /// Does not drop the <c>unaccent</c> extension, which is instance-wide.
    /// </summary>
    public void DropSchema()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {_options.QualifiedTableName}"; // NOSONAR:S2077 (identifier only, validated + quoted)
        command.ExecuteNonQuery();
    }

    /// <summary>Async variant of <see cref="EnsureSchema"/>.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresSchema.CreateDocumentTableAsync(connection, _options, cancellationToken).ConfigureAwait(false);

        _schemaReady = true;
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
                Insert(connection, transaction, document);

            transaction.Commit();
        }
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var connection = _dataSource.OpenConnection();
        Insert(connection, null, document);
    }

    /// <inheritdoc />
    public bool Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_options.QualifiedTableName} WHERE id = @id"; // NOSONAR:S2077 (identifiers only; id is parameterized)
        command.Parameters.AddWithValue("id", documentId);
        return command.ExecuteNonQuery() > 0;
    }

    /// <inheritdoc />
    public void Clear()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"TRUNCATE {_options.QualifiedTableName}"; // NOSONAR:S2077 (identifier only, validated + quoted)
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();

        options ??= SearchOptions.Default;

        if (options.IsEmpty || string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        // ParseQuerySyntax: false is the caller saying "literal text, do not consult the query
        // parser" — and EnsureSupported is a parser call.
        if (options.ParseQuerySyntax)
            QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(PostgresTextSearchEngine), options.KnownFields);

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();

        var filters = PostgresMetadataFilterSql.Build(options.Filters);
        filters.Apply(command);

        // Mixed queries honor the same contract as the stock engine: quoted segments are a hard
        // corpus gate (each phrase must appear, AND-ed) while the free terms only contribute to
        // scoring. A query without quotes is matched by PostgresIndexOptions.MatchMode.
        var match = PostgresMatchExpression.Build(_options, query);
        match.Bind(command);

        string searchSql = $"""
            SELECT id, content, category, fields, text_fields, {match.Rank} AS score
            FROM {_options.QualifiedTableName}
            WHERE ({match.Gate}){filters.Fragment}
            ORDER BY score DESC, {PostgresSchema.OrdinalIdTieBreak}
            LIMIT @limit;
            """;

        // Identifiers/config only are interpolated (validated [A-Za-z0-9_]+ and quoted); query text is parameterized.
        command.CommandText = searchSql; // NOSONAR:S2077

        // Fetch the whole window (Offset + Limit): the C# side drops rows below MinimumScore
        // afterwards, and score DESC makes those drops a suffix of the fetched prefix — so the
        // Skip/Take below cuts the same page the in-memory engines would.
        // The window plus one row per excluded id: the exclusion is applied below, so a window of
        // exactly Window would come back one short for every excluded document inside it.
        command.Parameters.AddWithValue("limit", PostgresDocumentExclusion.FetchLimit(options));

        var results = new List<SearchResult>();

        // Npgsql's blocking API takes no token; the registration is what makes this command
        // cancellable while it runs. See PostgresCancellation.
        using var cancelling = PostgresCancellation.Register(command, cancellationToken);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            double score = reader.GetDouble(5);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                continue;

            if (score < options.MinimumScore)
                continue;

            var document = ReadDocument(reader);

            if (!PostgresDocumentExclusion.Passes(options, document.Id))
                continue;

            results.Add(new SearchResult(document.Id, score, document));
        }

        var page = results.Skip(options.Offset).Take(options.Limit).ToList();
        options.Trace?.RecordScoreStage("Postgres lexical", page);
        return page;
    }

    /// <summary>Releases the underlying Npgsql data source.</summary>
    public void Dispose() => _dataSource.Dispose();

    private void Insert(NpgsqlConnection connection, NpgsqlTransaction? transaction, SearchDocument document) =>
        PostgresDocumentWriter.Insert(connection, transaction, _options, document);

    private static SearchDocument ReadDocument(NpgsqlDataReader reader) => PostgresDocumentReader.Read(reader);
}