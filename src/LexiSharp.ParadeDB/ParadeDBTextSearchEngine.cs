using LexiSharp.Core;
using LexiSharp.Postgres;
using Npgsql;
using NpgsqlTypes;

namespace LexiSharp.ParadeDB;

/// <summary>
/// ParadeDB/PG_search-backed <see cref="ITextSearchEngine"/>: true BM25 ranking built on the
/// Tantivy index of the <c>pg_search</c> extension. The search field is matched with the
/// match-disjunction operator (<c>|||</c>) and ranked with <c>pdb.score(key_field)</c> —
/// real Okapi BM25, unlike <c>ts_rank_cd</c>.
/// </summary>
/// <remarks>
/// <para>
/// Depends on the <c>pg_search</c> extension (AGPL-3, see
/// <see href="https://paradedb.com">ParadeDB</see>); the ParadeDB Docker image preloads it.
/// </para>
/// <para>
/// The documents table is the shared <see cref="PostgresSchema"/> table: the lexical
/// <c>tsvector</c> engine, the vector engine and this one can coexist on a single table and
/// be merged by the hybrid engine. Writes are plain inserts; <c>pg_search</c> maintains its
/// own inverted index in the same transaction.
/// </para>
/// <para>
/// BM25 scores (Tantivy variant) are PostgreSQL-native: ordering is meaningful, but numeric
/// values are not comparable to <see cref="LexiSharp.Ranking.Bm25Scorer"/>. Wrap this engine
/// in the hybrid package's <c>ReciprocalRankFusionMerger</c> (or re-rank the union) when a
/// single cross-engine ordering is required.
/// </para>
/// </remarks>
public sealed class ParadeDBTextSearchEngine : ITextSearchEngine, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ParadeDBOptions _options;
    private bool _schemaReady;

    /// <param name="connectionString">A PostgreSQL connection string (Npgsql format). The target database must have the <c>pg_search</c> extension available.</param>
    /// <param name="options">Naming/behavior options (default: <see cref="ParadeDBOptions"/>).</param>
    /// <param name="autoCreateSchema">
    /// Shortcut for <see cref="ParadeDBOptions.AutoCreateSchema"/>; overrides the option when set.
    /// </param>
    public ParadeDBTextSearchEngine(
        string connectionString,
        ParadeDBOptions? options = null,
        bool? autoCreateSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _options = options ?? new ParadeDBOptions();

        if (!_options.IsValid)
            throw new ArgumentException("Schema, table and content field must match [A-Za-z0-9_].", nameof(options));

        if (autoCreateSchema is not null)
            _options = _options with { AutoCreateSchema = autoCreateSchema.Value };

        _dataSource = NpgsqlDataSource.Create(connectionString);

        if (_options.AutoCreateSchema)
            EnsureSchema();
    }

    /// <summary>
    /// Installs the <c>pg_search</c> extension, the shared documents table and the ParadeDB
    /// (BM25) index. Idempotent.
    /// </summary>
    public void EnsureSchema() => EnsureSchemaAsync().GetAwaiter().GetResult();

    /// <summary>Async variant of <see cref="EnsureSchema"/>.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await PostgresExtensionInstaller.InstallAsync(
            connection, "CREATE EXTENSION IF NOT EXISTS pg_search;", cancellationToken).ConfigureAwait(false);

        var baseOptions = new PostgresIndexOptions { Schema = _options.Schema, Table = _options.Table };
        await PostgresSchema.CreateDocumentTableAsync(connection, baseOptions, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            string createIndexSql = $"""
                CREATE INDEX IF NOT EXISTS {_options.QualifiedIndexName}
                ON {_options.QualifiedTableName}
                USING paradedb (id, {_options.ContentExpression}, category)
                WITH (key_field = 'id');
                """;

            // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted).
            command.CommandText = createIndexSql; // NOSONAR:S2077
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _schemaReady = true;
    }

    /// <summary>
    /// Drops the documents table (and with it the ParadeDB index). Useful for tests and clean
    /// teardowns. Does not drop the <c>pg_search</c> extension, which is instance-wide.
    /// </summary>
    public void DropSchema()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {_options.QualifiedTableName}"; // NOSONAR:S2077 (identifier only, validated + quoted)
        command.ExecuteNonQuery();
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
    public void Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_options.QualifiedTableName} WHERE id = @id"; // NOSONAR:S2077 (identifiers only; id is parameterized)
        command.Parameters.AddWithValue("id", documentId);
        command.ExecuteNonQuery();
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
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.Limit <= 0 || string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        string searchSql = $"""
            SELECT id, content, category, fields, pdb.score(id) AS score
            FROM {_options.QualifiedTableName}
            WHERE {_options.ContentField} ||| @query
            ORDER BY pdb.score(id) DESC, id ASC
            LIMIT @limit;
            """;

        // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted); query text is parameterized.
        command.CommandText = searchSql; // NOSONAR:S2077

        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("limit", options.Limit);

        var results = new List<SearchResult>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            double score = reader.GetFloat(4);

            if (double.IsNaN(score) || double.IsInfinity(score) || score <= 0)
                continue;

            if (score < options.MinimumScore)
                continue;

            var document = ReadDocument(reader);
            results.Add(new SearchResult(document.Id, score, document));
        }

        return results;
    }

    /// <summary>Releases the underlying Npgsql data source.</summary>
    public void Dispose() => _dataSource.Dispose();

    internal string TestTableName => _options.Table;

    private void Insert(NpgsqlConnection connection, NpgsqlTransaction? transaction, SearchDocument document)
    {
        using var command = connection.CreateCommand();

        string upsertSql = $"""
            INSERT INTO {_options.QualifiedTableName} (id, content, category, fields)
            VALUES (@id, @content, @category, @fields)
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields;
            """;

        // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted); values are parameters.
        command.CommandText = upsertSql; // NOSONAR:S2077

        if (transaction is not null)
            command.Transaction = transaction;

        command.Parameters.AddWithValue("id", document.Id);
        command.Parameters.AddWithValue("content", document.Text);
        command.Parameters.AddWithValue("category", (object?)document.Category ?? DBNull.Value);

        var fieldsParameter = command.Parameters.AddWithValue(
            "fields",
            document.Fields is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(document.Fields));
        fieldsParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;

        command.ExecuteNonQuery();
    }

    private static SearchDocument ReadDocument(NpgsqlDataReader reader)
    {
        string id = reader.GetString(0);
        string content = reader.GetString(1);
        string? category = reader.IsDBNull(2) ? null : reader.GetString(2);
        var fields = reader.IsDBNull(3) ? null : DeserializeFields(reader.GetString(3));

        return new SearchDocument(id, content, fields, category);
    }

    private static IReadOnlyDictionary<string, string>? DeserializeFields(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }
}