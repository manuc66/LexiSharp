using LexiSharp.Core;
using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// PostgreSQL-backed learned-sparse search engine (<c>pgvector sparsevec</c> + HNSW): stores each
/// document's learned sparse vector (SPLADE-style term weights) in the shared documents table and
/// answers queries with an approximate nearest-neighbor scan over the HNSW index.
/// </summary>
/// <remarks>
/// <para>
/// <b>Vectors are computed externally</b>: the engine consumes an <see cref="ISparseEmbeddingProvider"/>
/// supplied by the caller (ONNX model, model server, HTTP API, ...). LexiSharp only orchestrates
/// the storage, the HNSW index and the distance math on the database side. Because
/// <c>sparsevec</c> is a positional format, the <see cref="PostgresSparseOptions.Vocabulary"/> must
/// be fixed up front and shared between the provider used at index time and the one used at query
/// time.
/// </para>
/// <para>
/// The engine shares its table layout with <see cref="PostgresTextSearchEngine"/> (id, content,
/// <c>tsv</c>, plus the <c>sparse sparsevec(n)</c> column). Point both engines at the same table
/// to serve lexico-sparse hybrid search; feed the pair to the hybrid package and merge with its
/// <c>ReciprocalRankFusionMerger</c>.
/// </para>
/// <para>
/// Because <see cref="ITextSearchEngine"/> is synchronous but embedding computation is async
/// (typically remote), the synchronous wrapper methods (<see cref="Add"/>, <see cref="Search"/>, ...)
/// block on the underlying async work. When running without the hybrid, prefer the
/// <see cref="AddAsync"/> and <see cref="SearchAsync"/> overloads.
/// </para>
/// </remarks>
public sealed class PostgresSparseSearchEngine : ITextSearchEngine, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ISparseEmbeddingProvider _embeddings;
    private readonly PostgresSparseOptions _options;
    private bool _schemaReady;

    /// <param name="connectionString">A PostgreSQL connection string (Npgsql format). The target database must have the <c>vector</c> extension available.</param>
    /// <param name="embeddings">External learned-sparse producer; never implemented inside LexiSharp.</param>
    /// <param name="options">Vocabulary/naming/behavior options.</param>
    /// <param name="autoCreateSchema">
    /// Shortcut for <see cref="PostgresSparseOptions.AutoCreateSchema"/>; overrides the option when set.
    /// </param>
    public PostgresSparseSearchEngine(
        string connectionString,
        ISparseEmbeddingProvider embeddings,
        PostgresSparseOptions? options = null,
        bool? autoCreateSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(embeddings);

        _options = options ?? new PostgresSparseOptions();

        if (!_options.IsValid)
            throw new ArgumentException(
                "Schema and table must match [A-Za-z0-9_]_, the vocabulary must be non-empty with non-negative" +
                " coordinates, and HNSW parameters must be positive.",
                nameof(options));

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
    /// <c>sparse sparsevec(n)</c> column and the HNSW index. Idempotent.
    /// </summary>
    public void EnsureSchema() => EnsureSchemaAsync().GetAwaiter().GetResult();

    /// <summary>Async variant of <see cref="EnsureSchema"/>.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE EXTENSION IF NOT EXISTS vector;";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var baseOptions = new PostgresIndexOptions { Schema = _options.Schema, Table = _options.Table };
        await PostgresSchema.CreateDocumentTableAsync(connection, baseOptions, cancellationToken).ConfigureAwait(false);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ALTER TABLE {_options.QualifiedTableName} ADD COLUMN IF NOT EXISTS sparse {_options.SparseVectorType};";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // CREATE INDEX runs on its own command: the HNSW build must not be part of a
        // multi-statement implicit transaction. HNSW works on empty tables and supports inserts.
        await CreateIndexAsync(connection, cancellationToken).ConfigureAwait(false);
        _schemaReady = true;
    }

    private async Task CreateIndexAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var indexName = PostgresIndexOptions.QuoteIdentifier($"{_options.Table}_sparse_hnsw");

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE INDEX IF NOT EXISTS {indexName}
            ON {_options.QualifiedTableName} USING hnsw (sparse {_options.OpClass})
            WITH (m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction});
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops the documents table (including the sparse HNSW and GIN indexes).</summary>
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

    /// <summary>
    /// Embeds and upserts a single document, storing the term weights whose coordinates the
    /// vocabulary maps. Terms outside the vocabulary contribute nothing. Prefer this over the
    /// blocking <see cref="Add"/>.
    /// </summary>
    public async Task AddAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var weights = await _embeddings.GetSparseEmbeddingAsync(document.Text, cancellationToken).ConfigureAwait(false);
        string literal = SparseVectorText.Format(ToCoordinates(weights), _options.ResolvedDimension);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        string tsvExpr = PostgresSchema.TsvExpression(_options.AsIndexOptions(), "EXCLUDED.content");

        command.CommandText = $"""
            INSERT INTO {_options.QualifiedTableName} (id, content, category, fields, sparse)
            VALUES (@id, @content, @category, @fields, @sparse::sparsevec)
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields,
                    sparse = EXCLUDED.sparse,
                    tsv = {tsvExpr};
            """;

        command.Parameters.AddWithValue("id", document.Id);
        command.Parameters.AddWithValue("content", document.Text);
        command.Parameters.AddWithValue("category", (object?)document.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("sparse", literal);

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
    /// Embeds the query, maps its terms through the vocabulary and returns documents ordered by
    /// the configured distance, scored as similarity (higher = better). Prefer this over the
    /// blocking <see cref="Search"/>.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchAsync(string query, SearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        options ??= SearchOptions.Default;

        if (options.Limit <= 0 || string.IsNullOrWhiteSpace(query))
            return Array.Empty<SearchResult>();

        var weights = await _embeddings.GetSparseEmbeddingAsync(query, cancellationToken).ConfigureAwait(false);
        var queryCoordinates = ToCoordinates(weights);

        if (queryCoordinates.Count == 0)
            return Array.Empty<SearchResult>();

        string queryLiteral = SparseVectorText.Format(queryCoordinates, _options.ResolvedDimension);
        string scoreExpression = ScoreExpression();

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, content, category, fields, {scoreExpression} AS score
            FROM {_options.QualifiedTableName}
            WHERE sparse IS NOT NULL
            ORDER BY sparse {_options.Operator} @query::sparsevec
            LIMIT @limit;
            """;

        command.Parameters.AddWithValue("query", queryLiteral);
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

    private List<(int Coordinate, float Weight)> ToCoordinates(IReadOnlyDictionary<string, float> weights)
    {
        var coordinates = new List<(int, float)>(weights.Count);

        foreach (var (term, weight) in weights)
        {
            if (_options.Vocabulary.TryGetValue(term, out int coordinate))
            {
                if (float.IsFinite(weight) && weight != 0f)
                    coordinates.Add((coordinate + 1, weight));
            }
        }

        return coordinates;
    }

    private string ScoreExpression() => _options.Distance switch
    {
        SparseDistance.Cosine => "1 - (sparse <=> @query::sparsevec)",
        SparseDistance.L2 => "1 / (1 + (sparse <-> @query::sparsevec))",
        SparseDistance.L1 => "1 / (1 + (sparse <+> @query::sparsevec))",
        _ => "- (sparse <#> @query::sparsevec)",
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