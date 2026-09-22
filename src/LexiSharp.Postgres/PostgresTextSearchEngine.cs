using LexiSharp.Core;
using Npgsql;
using NpgsqlTypes;

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
///   tsv      tsvector GENERATED ALWAYS AS (to_tsvector('simple', unaccent(content))) STORED
/// );
/// CREATE INDEX IF NOT EXISTS {table}_tsv_gin ON {schema}.{table} USING GIN (tsv);
/// </code>
/// <para>
/// Ranking uses the database's native <c>ts_rank_cd</c>, so scores are <b>not</b> numerically
/// comparable to LexiSharp's BM25/TF-IDF. When identical scoring matters, wrap this engine in
/// the hybrid package's merger strategy (re-rank on the union) or recompute scores in C#.
/// </para>
/// <para>
/// Embeddings/ANN (<c>pgvector</c>) are intentionally not created here: add a
/// <c>embedding vector(n)</c> column and an HNSW/IVFFlat index when you opt into vector search.
/// </para>
/// </remarks>
public sealed class PostgresTextSearchEngine : ITextSearchEngine, IDisposable, IQuerySyntaxSupport
{
    /// <inheritdoc />
    public QueryFeature SupportedQueryFeatures => QueryFeature.Phrases;

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
            throw new ArgumentException("Schema, table and text search config must match [A-Za-z0-9_].", nameof(options));

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
    public void EnsureSchema()
    {
        EnsureSchemaAsync().GetAwaiter().GetResult();
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

        if (options.IsEmpty || string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(PostgresTextSearchEngine));

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();

        var filters = PostgresMetadataFilterSql.Build(options.Filters);
        filters.Apply(command);

        // Mixed queries honor the same contract as the stock engine: quoted segments are a hard
        // corpus gate (each phrase must appear, AND-ed) while the free terms only contribute to
        // scoring. Gating on the whole query (websearch_to_tsquery) would wrongly require the
        // free terms too. A query without quotes keeps its exact previous behavior.
        var segments = QueryParser.SplitRaw(query);

        string gateCondition;
        string rankExpression;

        if (segments.Phrases.Count == 0)
        {
            gateCondition = $"tsv @@ websearch_to_tsquery('{_options.TextSearchConfig}', unaccent(@query))";
            rankExpression = $"websearch_to_tsquery('{_options.TextSearchConfig}', unaccent(@query))";
            command.Parameters.AddWithValue("query", query);
        }
        else
        {
            var gates = new List<string>(segments.Phrases.Count);
            var ranks = new List<string>(segments.Phrases.Count + 1);

            if (!string.IsNullOrWhiteSpace(segments.FreeText))
            {
                ranks.Add($"websearch_to_tsquery('{_options.TextSearchConfig}', unaccent(@freeText))");
                command.Parameters.AddWithValue("freeText", segments.FreeText);
            }

            for (int i = 0; i < segments.Phrases.Count; i++)
            {
                gates.Add($"tsv @@ phraseto_tsquery('{_options.TextSearchConfig}', unaccent(@phrase{i}))");
                ranks.Add($"phraseto_tsquery('{_options.TextSearchConfig}', unaccent(@phrase{i}))");
                command.Parameters.AddWithValue($"phrase{i}", segments.Phrases[i]);
            }

            gateCondition = string.Join(" AND ", gates);
            // OR the free terms with each phrase so a phrase-only match still scores above zero
            // (the ts_rank_cd of the full AND query is 0 when a free term is missing).
            rankExpression = string.Join(" || ", ranks);
        }

        string searchSql = $"""
            SELECT id, content, category, fields, ts_rank_cd(tsv, {rankExpression}) AS score
            FROM {_options.QualifiedTableName}
            WHERE ({gateCondition}){filters.Fragment}
            ORDER BY score DESC
            LIMIT @limit;
            """;

        // Identifiers/config only are interpolated (validated [A-Za-z0-9_]+ and quoted); query text is parameterized.
        command.CommandText = searchSql; // NOSONAR:S2077

        // Fetch the whole window (Offset + Limit): the C# side drops rows below MinimumScore
        // afterwards, and score DESC makes those drops a suffix of the fetched prefix — so the
        // Skip/Take below cuts the same page the in-memory engines would.
        command.Parameters.AddWithValue("limit", options.Window);

        var results = new List<SearchResult>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            double score = reader.GetDouble(4);

            if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                continue;

            if (score < options.MinimumScore)
                continue;

            var document = ReadDocument(reader);
            results.Add(new SearchResult(document.Id, score, document));
        }

        return results.Skip(options.Offset).Take(options.Limit).ToList();
    }

    /// <summary>Releases the underlying Npgsql data source.</summary>
    public void Dispose() => _dataSource.Dispose();

    private void Insert(NpgsqlConnection connection, NpgsqlTransaction? transaction, SearchDocument document)
    {
        using var command = connection.CreateCommand();

        string tsvExpr = PostgresSchema.TsvExpression(_options, "@content");

        string insertSql = $"""
            INSERT INTO {_options.QualifiedTableName} (id, content, category, fields, tsv)
            VALUES (@id, @content, @category, @fields, {tsvExpr})
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields,
                    tsv = {tsvExpr.Replace("@content", "EXCLUDED.content")};
            """;

        // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted); values are parameters.
        command.CommandText = insertSql; // NOSONAR:S2077

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