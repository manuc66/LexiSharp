using LexiSharp.Core;
using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// PostgreSQL-backed vector search engine (<c>pgvector</c> ANN): stores document embeddings in a
/// shared documents table and answers queries with an approximate nearest-neighbor scan over an
/// HNSW (or IVFFlat) index.
/// </summary>
/// <remarks>
/// <para>
/// <b>Embeddings are computed externally</b>: the engine consumes an <see cref="IEmbeddingProvider"/>
/// supplied by the caller (ONNX model, model server, HTTP API, ...). LexiSharp only orchestrates
/// the storage, the ANN index and the distance math on the database side.
/// </para>
/// <para>
/// The engine shares its table layout with <see cref="PostgresTextSearchEngine"/> (id, content,
/// <c>tsv</c>, plus the <c>embedding vector(n)</c> column). Point both engines at the same
/// table to serve lexico-vector hybrid search; feed the pair to the hybrid package and merge
/// with its <c>ReciprocalRankFusionMerger</c> — the recommended way to combine the incomparable
/// <c>ts_rank_cd</c> and cosine scores.
/// </para>
/// <para>
/// Because <see cref="ITextSearchEngine"/> is synchronous but embedding computation is async
/// (typically remote), the synchronous wrapper methods (<see cref="Add"/>, <see cref="Search"/>, ...)
/// block on the underlying async work. When running without the hybrid, prefer the
/// <see cref="AddAsync"/> and <see cref="SearchAsync"/> overloads.
/// </para>
/// </remarks>
public sealed class PostgresVectorSearchEngine : ITextSearchEngine, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEmbeddingProvider _embeddings;
    private readonly PostgresVectorOptions _options;
    private bool _schemaReady;

    /// <param name="connectionString">A PostgreSQL connection string (Npgsql format). The target database must have the <c>vector</c> extension available.</param>
    /// <param name="embeddings">External embedding producer; never implemented inside LexiSharp.</param>
    /// <param name="options">Naming/behavior options (default: <see cref="PostgresVectorOptions"/>).</param>
    /// <param name="autoCreateSchema">
    /// Shortcut for <see cref="PostgresVectorOptions.AutoCreateSchema"/>; overrides the option when set.
    /// </param>
    public PostgresVectorSearchEngine(
        string connectionString,
        IEmbeddingProvider embeddings,
        PostgresVectorOptions? options = null,
        bool? autoCreateSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(embeddings);

        _options = options ?? new PostgresVectorOptions();

        if (!_options.IsValid)
            throw new ArgumentException("Schema, table names must match [A-Za-z0-9_] and size parameters must be positive.", nameof(options));

        if (autoCreateSchema is not null)
            _options = _options with { AutoCreateSchema = autoCreateSchema.Value };

        _embeddings = embeddings;
        _dataSource = NpgsqlDataSource.Create(connectionString);

        if (_options.AutoCreateSchema)
        {
            EnsureSchema();
        }
        else
        {
            _schemaReady = false;
        }
    }

    /// <summary>
    /// Installs the <c>vector</c> extension, the shared documents table (if needed), the
    /// <c>embedding</c> column and the ANN index. Idempotent.
    /// </summary>
    /// <remarks>
    /// For <see cref="VectorIndexMethod.IvfFlat"/>, the index is only created when the table is
    /// non-empty (IVFFlat needs rows to cluster lists). Call <see cref="EnsureSchema"/> again
    /// after your first batch of inserts if you use IVFFlat.
    /// </remarks>
    public void EnsureSchema() => EnsureSchemaAsync().GetAwaiter().GetResult();

    /// <summary>Async variant of <see cref="EnsureSchema"/>.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var baseOptions = new PostgresIndexOptions { Schema = _options.Schema, Table = _options.Table };

        await PostgresExtensionInstaller.InstallAsync(
            connection, "CREATE EXTENSION IF NOT EXISTS vector;", cancellationToken).ConfigureAwait(false);

        await PostgresSchema.CreateDocumentTableAsync(connection, baseOptions, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ALTER TABLE {_options.QualifiedTableName} ADD COLUMN IF NOT EXISTS embedding {_options.VectorType};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // CREATE INDEX runs on its own command: HNSW/IVFFlat builds must not be part of a
        // multi-statement implicit transaction, and IVFFlat requires rows.
        if (_options.IndexMethod == VectorIndexMethod.IvfFlat)
        {
            long rowCount;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT COUNT(*) FROM {_options.QualifiedTableName}";
                rowCount = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            if (rowCount == 0)
                return;
        }

        await CreateIndexAsync(connection, cancellationToken).ConfigureAwait(false);
        _schemaReady = true;
    }

    private async Task CreateIndexAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var indexName = PostgresIndexOptions.QuoteIdentifier($"{_options.Table}_embedding_{_options.IndexMethod.ToString().ToLowerInvariant()}");

        string build = _options.IndexMethod == VectorIndexMethod.Hnsw
            ? $"USING hnsw (embedding {_options.OpClass}) WITH (m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction})"
            : $"USING ivfflat (embedding {_options.OpClass}) WITH (lists = {_options.IvfLists})";

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {_options.QualifiedTableName} {build}";
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops the documents table (including the ANN and GIN indexes).</summary>
    public void DropSchema()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {_options.QualifiedTableName}";
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"TRUNCATE {_options.QualifiedTableName}";
        command.ExecuteNonQuery();

        foreach (var document in documents)
            Add(document);
    }

    /// <inheritdoc />
    public void Add(SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        AddAsync(document).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public void Remove(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {_options.QualifiedTableName} WHERE id = @id";
        command.Parameters.AddWithValue("id", documentId);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Clear()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"TRUNCATE {_options.QualifiedTableName}";
        command.ExecuteNonQuery();
    }

    /// <summary>Embeds and upserts a single document. Prefer this over the blocking <see cref="Add"/>.</summary>
    public async Task AddAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        float[] embedding = await EmbedAsync(document.Text, cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        string tsvExpr = PostgresSchema.TsvExpression(_options.AsIndexOptions(), "EXCLUDED.content");

        command.CommandText = $"""
            INSERT INTO {_options.QualifiedTableName} (id, content, category, fields, embedding)
            VALUES (@id, @content, @category, @fields, @embedding::vector)
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields,
                    embedding = EXCLUDED.embedding,
                    tsv = {tsvExpr};
            """;

        command.Parameters.AddWithValue("id", document.Id);
        command.Parameters.AddWithValue("content", document.Text);
        command.Parameters.AddWithValue("category", (object?)document.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("embedding", VectorText.Format(embedding));

        if (document.Fields is not null)
        {
            var fieldsParameter = command.Parameters.AddWithValue("fields",
                System.Text.Json.JsonSerializer.Serialize(document.Fields));
            fieldsParameter.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
        }
        else
        {
            command.Parameters.AddWithValue("fields", DBNull.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchAsync(query, options).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Embeds the query and returns the nearest documents by the configured distance, scored as
    /// similarity (higher = better). Prefer this over the blocking <see cref="Search"/>.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.Limit <= 0 || string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        float[] queryVector = await EmbedAsync(query, cancellationToken).ConfigureAwait(false);
        string serialized = VectorText.Format(queryVector);

        string scoreExpression = ScoreExpression();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, content, category, fields, {scoreExpression} AS score
            FROM {_options.QualifiedTableName}
            WHERE embedding IS NOT NULL
            ORDER BY embedding {_options.Operator} @query::vector
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("query", serialized);
        command.Parameters.AddWithValue("limit", options.Limit);

        var results = new List<SearchResult>();

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                double score = reader.GetDouble(4);

                if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                    continue;

                if (score < options.MinimumScore)
                    continue;

                var document = ReadDocument(reader);
                results.Add(new SearchResult(document.Id, score, document));
            }
        }

        return results;
    }

    /// <summary>Releases the underlying Npgsql data source.</summary>
    public void Dispose() => _dataSource.Dispose();

    internal string TestTableName => _options.Table;

    private async Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken)
    {
        var embedding = await _embeddings.GetTextEmbeddingAsync(text, cancellationToken).ConfigureAwait(false);
        var vector = embedding.ToArray();

        if (vector.Length != _options.Dimension)
            throw new InvalidOperationException(
                $"The IEmbeddingProvider returned a {vector.Length}D vector but the engine expects {_options.Dimension}D ({_options.Dimension} = PostgresVectorOptions.Dimension).");

        return vector;
    }

    private string ScoreExpression() => _options.Distance switch
    {
        VectorDistance.L2 => "1 / (1 + (embedding <-> @query::vector))",
        VectorDistance.InnerProduct => "- (embedding <#> @query::vector)",
        _ => "1 - (embedding <=> @query::vector)",
    };

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