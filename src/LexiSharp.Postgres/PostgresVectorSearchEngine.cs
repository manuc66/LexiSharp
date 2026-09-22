using System.Runtime.CompilerServices;
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
public sealed class PostgresVectorSearchEngine : ITextSearchEngine, IDetailedSearchEngine, IListableSearchEngine, IDisposable, IQuerySyntaxSupport
{
    /// <inheritdoc />
    public QueryFeature SupportedQueryFeatures => QueryFeature.None;

    private const string LegacyEmbeddingColumn = "embedding";

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

        foreach (string column in GetEmbeddingColumnNames())
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"ALTER TABLE {_options.QualifiedTableName} ADD COLUMN IF NOT EXISTS {Quote(column)} {_options.VectorType};"; // NOSONAR:S2077
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = $"ALTER TABLE {_options.QualifiedTableName} ADD COLUMN IF NOT EXISTS text_fields jsonb;"; // NOSONAR:S2077
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // CREATE INDEX runs on its own command: HNSW/IVFFlat builds must not be part of a
        // multi-statement implicit transaction, and IVFFlat requires rows.
        if (_options.IndexMethod == VectorIndexMethod.IvfFlat)
        {
            long rowCount;

            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"SELECT COUNT(*) FROM {_options.QualifiedTableName}"; // NOSONAR:S2077
                rowCount = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

            if (rowCount == 0)
                return;
        }

        foreach (string column in GetEmbeddingColumnNames())
        {
            // CTAS/index name derived from the column: embedding -> {table}_embedding_hnsw
            // (legacy single column keeps its historical name), title_embedding -> {table}_title_embedding_hnsw.
            string indexBase = column == LegacyEmbeddingColumn
                ? $"{_options.Table}_embedding"
                : $"{_options.Table}_{column}";
            await CreateIndexAsync(connection, column, indexBase, cancellationToken).ConfigureAwait(false);
        }

        _schemaReady = true;
    }

    private async Task CreateIndexAsync(NpgsqlConnection connection, string column, string indexBase, CancellationToken cancellationToken)
    {
        var indexName = PostgresIndexOptions.QuoteIdentifier($"{indexBase}_{_options.IndexMethod.ToString().ToLowerInvariant()}");

        string build = _options.IndexMethod == VectorIndexMethod.Hnsw
            ? $"USING hnsw ({Quote(column)} {_options.OpClass}) WITH (m = {_options.HnswM}, ef_construction = {_options.HnswEfConstruction})"
            : $"USING ivfflat ({Quote(column)} {_options.OpClass}) WITH (lists = {_options.IvfLists})";

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {_options.QualifiedTableName} {build}"; // NOSONAR:S2077
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Drops the documents table (including the ANN and GIN indexes).</summary>
    public void DropSchema()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"DROP TABLE IF EXISTS {_options.QualifiedTableName}"; // NOSONAR:S2077
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Index(IEnumerable<SearchDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"TRUNCATE {_options.QualifiedTableName}"; // NOSONAR:S2077
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
        command.CommandText = $"DELETE FROM {_options.QualifiedTableName} WHERE id = @id"; // NOSONAR:S2077 (identifiers only; id is parameterized)
        command.Parameters.AddWithValue("id", documentId);
        command.ExecuteNonQuery();
    }

    /// <inheritdoc />
    public void Clear()
    {
        using var connection = _dataSource.OpenConnection();

        using var command = connection.CreateCommand();
        command.CommandText = $"TRUNCATE {_options.QualifiedTableName}"; // NOSONAR:S2077
        command.ExecuteNonQuery();
    }

    /// <summary>Embeds and upserts a single document. Prefer this over the blocking <see cref="Add"/>.</summary>
    public async Task AddAsync(SearchDocument document, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);

        var sources = _options.EmbeddingColumns;

        if (sources is null)
        {
            float[] embedding = await EmbedAsync(
                EmbeddingSource(document, _options.EmbeddingTextField),
                EmbeddingUse.Passage, cancellationToken).ConfigureAwait(false);

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            string tsvExpr = PostgresSchema.TsvExpression(_options.AsIndexOptions(), "EXCLUDED.content");

            string insertSql = $"""
                INSERT INTO {_options.QualifiedTableName} (id, content, category, fields, text_fields, embedding)
                VALUES (@id, @content, @category, @fields, @text_fields, @embedding::vector)
                ON CONFLICT (id) DO UPDATE
                    SET content = EXCLUDED.content,
                        category = EXCLUDED.category,
                        fields = EXCLUDED.fields,
                        text_fields = EXCLUDED.text_fields,
                        embedding = EXCLUDED.embedding,
                        tsv = {tsvExpr};
                """;

            // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted); values are parameters.
            command.CommandText = insertSql; // NOSONAR:S2077
            AddCommonParameters(command, document);
            command.Parameters.AddWithValue("embedding", VectorText.Format(embedding));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // One embedding per configured column, each sourced from its own TextFields entry.
        var columns = sources.Keys.ToList();
        var embeddings = new float[sources.Count][];

        for (int i = 0; i < sources.Count; i++)
        {
            embeddings[i] = await EmbedAsync(
                EmbeddingSource(document, sources[columns[i]]),
                EmbeddingUse.Passage, cancellationToken).ConfigureAwait(false);
        }

        await using var multiConnection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var multiCommand = multiConnection.CreateCommand();

        string columnList = string.Join(", ", columns.Select(c => Quote($"{c}_embedding")));
        string parameterList = string.Join(", ", columns.Select((_, i) => $"@e{i}::vector"));
        string updateList = string.Join(", ",
            columns.Select(c => $"{Quote($"{c}_embedding")} = EXCLUDED.{Quote($"{c}_embedding")}"));

        string multiTsvExpr = PostgresSchema.TsvExpression(_options.AsIndexOptions(), "EXCLUDED.content");

        string insertMultiSql = $"""
            INSERT INTO {_options.QualifiedTableName} (id, content, category, fields, text_fields, {columnList})
            VALUES (@id, @content, @category, @fields, @text_fields, {parameterList})
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields,
                    text_fields = EXCLUDED.text_fields,
                    {updateList},
                    tsv = {multiTsvExpr};
            """;

        // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted); values are parameters.
        multiCommand.CommandText = insertMultiSql; // NOSONAR:S2077
        AddCommonParameters(multiCommand, document);

        for (int i = 0; i < columns.Count; i++)
            multiCommand.Parameters.AddWithValue($"e{i}", VectorText.Format(embeddings[i]));

        await multiCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddCommonParameters(NpgsqlCommand command, SearchDocument document)
    {
        command.Parameters.AddWithValue("id", document.Id);
        command.Parameters.AddWithValue("content", document.Text);
        command.Parameters.AddWithValue("category", (object?)document.Category ?? DBNull.Value);

        if (document.TextFields is not null)
        {
            var textFieldsParameter = command.Parameters.AddWithValue("text_fields",
                System.Text.Json.JsonSerializer.Serialize(document.TextFields));
            textFieldsParameter.NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Jsonb;
        }
        else
        {
            command.Parameters.AddWithValue("text_fields", DBNull.Value);
        }

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
        return (await SearchCoreAsync(query, GetDefaultColumns(), options, cancellationToken).ConfigureAwait(false))
            .Results;
    }

    /// <summary>
    /// Searches only the given embedding columns (labels from
    /// <see cref="PostgresVectorOptions.EmbeddingColumns"/>, or <c>"embedding"</c> on the legacy
    /// single-column engine). Documents are ranked by their best similarity across the selected
    /// columns — the same OR-fusion a caller feeding candidate sets to a local rerank expects.
    /// </summary>
    public async Task<IReadOnlyList<SearchResult>> SearchWithColumnsAsync(
        string query,
        IReadOnlyList<string> columns,
        SearchOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(columns);

        return (await SearchCoreAsync(query, ResolveColumns(columns), options, cancellationToken).ConfigureAwait(false))
            .Results;
    }

    /// <inheritdoc cref="SearchWithColumnsAsync"/>
    public IReadOnlyList<SearchResult> SearchWithColumns(
        string query,
        IReadOnlyList<string> columns,
        SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        return SearchWithColumnsAsync(query, columns, options).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Same search as <see cref="SearchAsync"/>, but every result also carries the per-embedding-column
    /// similarity it achieved (<see cref="DetailedSearchResult.Contributions"/>, keyed by the column
    /// label) alongside the merged best-column score. Part of the opt-in
    /// <see cref="IDetailedSearchEngine"/> capability; on the legacy single-column engine the single
    /// contribution is keyed <c>"embedding"</c>. Search and details always agree on the ordering.
    /// </summary>
    public IReadOnlyList<DetailedSearchResult> SearchWithDetails(string query, SearchOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(query);

        var outcome = SearchCoreAsync(query, GetDefaultColumns(), options, CancellationToken.None).GetAwaiter().GetResult();

        return outcome.Results
            .Select(x => new DetailedSearchResult(
                x.DocumentId,
                x.Score,
                x.Document,
                outcome.Contributions.GetValueOrDefault(x.DocumentId)
                    ?? new Dictionary<string, double>()))
            .ToList();
    }

    private async Task<SearchOutcome> SearchCoreAsync(
        string query,
        IReadOnlyList<(string Label, string Column)> columns,
        SearchOptions? options,
        CancellationToken cancellationToken)
    {
        options ??= SearchOptions.Default;

        if (options.IsEmpty || string.IsNullOrWhiteSpace(query) || columns.Count == 0)
            return SearchOutcome.Empty;

        QuerySyntax.EnsureSupported(query, SupportedQueryFeatures, nameof(PostgresVectorSearchEngine));

        float[] queryVector = await EmbedAsync(query, EmbeddingUse.Query, cancellationToken).ConfigureAwait(false);
        string serialized = VectorText.Format(queryVector);

        // Multi-column searches need enough ANN candidates per column for the OR-fusion to be
        // meaningful: ramp the per-column LIMIT up so a column's noise does not starve the merge,
        // and keep it below/at the HNSW ef_search when one is configured (an ANN scan only ever
        // yields ef_search rows). The window (Offset + Limit) is always covered so the final
        // Skip/Take can cut any page of the merged ranking.
        int candidateLimit = columns.Count == 1
            ? options.Window
            : Math.Max(options.Window, Math.Max(40, _options.HnswEfSearch ?? 0));

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        NpgsqlTransaction? transaction = null;

        if (_options.HnswEfSearch is int hnswEfSearch)
        {
            transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var setCommand = connection.CreateCommand())
            {
                setCommand.Transaction = transaction;
                setCommand.CommandText = $"SET LOCAL hnsw.ef_search = {hnswEfSearch};"; // NOSONAR:S2077 (int option value, not user text)
                await setCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        var contributions = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        var bestScores = new Dictionary<string, double>(StringComparer.Ordinal);
        var documents = new Dictionary<string, SearchDocument>(StringComparer.Ordinal);
        var filters = PostgresMetadataFilterSql.Build(options.Filters);

        foreach ((string label, string column) in columns)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            filters.Apply(command);

            string searchSql = $"""
                SELECT id, content, category, fields, text_fields, {ScoreExpression(column)} AS score
                FROM {_options.QualifiedTableName}
                WHERE {Quote(column)} IS NOT NULL{filters.Fragment}
                ORDER BY {Quote(column)} {_options.Operator} @query::vector
                LIMIT @limit;
                """;

            // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted); query text is parameterized.
            command.CommandText = searchSql; // NOSONAR:S2077

            command.Parameters.AddWithValue("query", serialized);
            command.Parameters.AddWithValue("limit", candidateLimit);

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    double score = reader.GetDouble(5);

                    if (double.IsNaN(score) || double.IsInfinity(score) || score == 0)
                        continue;

                    var document = ReadDocument(reader);

                    if (!contributions.TryGetValue(document.Id, out var sources))
                    {
                        sources = new Dictionary<string, double>(StringComparer.Ordinal);
                        contributions[document.Id] = sources;
                    }

                    sources[label] = score;

                    if (!bestScores.TryGetValue(document.Id, out double best) || score > best)
                    {
                        bestScores[document.Id] = score;
                        documents[document.Id] = document;
                    }
                }
            }
        }

        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        var results = bestScores
            .Where(x => x.Value >= options.MinimumScore)
            .OrderByDescending(x => x.Value)
            .Skip(options.Offset)
            .Take(options.Limit)
            .Select(x => new SearchResult(x.Key, x.Value, documents[x.Key]))
            .ToList();

        return new SearchOutcome(results, contributions);
    }

    /// <summary>Releases the underlying Npgsql data source.</summary>
    public void Dispose() => _dataSource.Dispose();

    internal string TestTableName => _options.Table;

    /// <summary>
    /// SQL embedding column names: configured <see cref="PostgresVectorOptions.EmbeddingColumns"/>
    /// suffixes suffixed with <c>_embedding</c>, or the legacy <c>embedding</c> column. Rule
    /// S2365 asks for a method because the value copies; the allocation is unavoidable (the list
    /// is a fresh projection) and happens once at schema time.
    /// </summary>
    private IReadOnlyList<string> GetEmbeddingColumnNames() =>
        _options.EmbeddingColumns is not null
            ? _options.EmbeddingColumns.Keys.Select(k => $"{k}_embedding").ToList()
            : new[] { LegacyEmbeddingColumn };

    /// <summary>The (label, SQL column) pairs searched by default: every configured column.</summary>
    private IReadOnlyList<(string Label, string Column)> GetDefaultColumns() =>
        _options.EmbeddingColumns is not null
            ? _options.EmbeddingColumns.Keys.Select(k => (Label: k, Column: $"{k}_embedding")).ToList()
            : new[] { (Label: LegacyEmbeddingColumn, Column: LegacyEmbeddingColumn) };

    /// <summary>Validates column labels against the configured schema and maps them to SQL column names.</summary>
    private IReadOnlyList<(string Label, string Column)> ResolveColumns(IReadOnlyList<string> labels)
    {
        if (labels.Count == 0)
            throw new ArgumentException("At least one embedding column must be selected.", nameof(labels));

        var resolved = new List<(string Label, string Column)>(labels.Count);

        foreach (string label in labels)
        {
            if (_options.EmbeddingColumns is not null)
            {
                if (!_options.EmbeddingColumns.ContainsKey(label))
                    throw new ArgumentException(
                        $"Unknown embedding column '{label}'. Configured columns: {string.Join(", ", _options.EmbeddingColumns.Keys)}.",
                        nameof(labels));

                resolved.Add((label, $"{label}_embedding"));
            }
            else
            {
                if (label != LegacyEmbeddingColumn)
                    throw new ArgumentException("A single-column engine exposes only the 'embedding' column.", nameof(labels));

                resolved.Add((label, LegacyEmbeddingColumn));
            }
        }

        return resolved;
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> ListDocumentIdsAsync(
        int batchSize = 1000,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        string? lastId = null;

        while (true)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();

            command.CommandText = lastId is null
                ? $"SELECT id FROM {_options.QualifiedTableName} ORDER BY id LIMIT @limit;"
                : $"SELECT id FROM {_options.QualifiedTableName} WHERE id > @last_id ORDER BY id LIMIT @limit;";

            if (lastId is not null)
                command.Parameters.AddWithValue("last_id", lastId);

            command.Parameters.AddWithValue("limit", batchSize + 1);

            var page = new List<string>(batchSize + 1);

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    page.Add(reader.GetString(0));
            }

            if (page.Count == 0)
                yield break;

            bool hasMore = page.Count > batchSize;
            int taken = hasMore ? batchSize : page.Count;

            for (int i = 0; i < taken; i++)
                yield return page[i];

            if (!hasMore)
                yield break;

            lastId = page[taken - 1];
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> ListDocumentIds(int batchSize = 1000)
    {
        var results = new List<string>();

        var enumerator = ListDocumentIdsAsync(batchSize).GetAsyncEnumerator();

        try
        {
            bool hasNext = true;

            while (hasNext)
            {
                hasNext = AwaitMoveNext(enumerator);

                if (hasNext)
                    results.Add(enumerator.Current);
            }
        }
        finally
        {
            AwaitDispose(enumerator);
        }

        return results;
    }

    // Every ValueTask produced by the async enumerator is converted and awaited exactly once
    // (S5034): the helper isolates the construction from the sync blocker.
    private static bool AwaitMoveNext(IAsyncEnumerator<string> enumerator) =>
        enumerator.MoveNextAsync().AsTask().GetAwaiter().GetResult();

    private static void AwaitDispose(IAsyncEnumerator<string> enumerator) =>
        enumerator.DisposeAsync().AsTask().GetAwaiter().GetResult();

    private async Task<float[]> EmbedAsync(string text, EmbeddingUse use, CancellationToken cancellationToken)
    {
        var embedding = await _embeddings.GetTextEmbeddingAsync(text, use, cancellationToken).ConfigureAwait(false);
        var vector = embedding.ToArray();

        if (vector.Length != _options.Dimension)
            throw new InvalidOperationException(
                $"The IEmbeddingProvider returned a {vector.Length}D vector but the engine expects {_options.Dimension}D ({_options.Dimension} = PostgresVectorOptions.Dimension).");

        return vector;
    }

    /// <summary>
    /// The text that gets embedded for a field: the named <see cref="SearchDocument.TextFields"/>
    /// entry when it exists, otherwise the plain <see cref="SearchDocument.Text"/>. For the legacy
    /// single column, <paramref name="textFieldKey"/> is <see cref="PostgresVectorOptions.EmbeddingTextField"/>.
    /// <see cref="SearchDocument.Text"/> is always what lands in the <c>content</c> column (and
    /// thus the lexical <c>tsv</c>), so both engines stay consistent whatever the embedding source.
    /// </summary>
    private static string EmbeddingSource(SearchDocument document, string? textFieldKey)
    {
        if (textFieldKey is not null
            && document.TextFields is not null
            && document.TextFields.TryGetValue(textFieldKey, out string? fieldValue))
        {
            return fieldValue;
        }

        return document.Text;
    }

    private string ScoreExpression(string column)
    {
        string quoted = Quote(column);

        return _options.Distance switch
        {
            VectorDistance.L2 => $"1 / (1 + ({quoted} <-> @query::vector))",
            VectorDistance.InnerProduct => $"- ({quoted} <#> @query::vector)",
            _ => $"1 - ({quoted} <=> @query::vector)",
        };
    }

    private static string Quote(string identifier) => PostgresIndexOptions.QuoteIdentifier(identifier);

    private sealed record SearchOutcome(
        IReadOnlyList<SearchResult> Results,
        IReadOnlyDictionary<string, Dictionary<string, double>> Contributions)
    {
        public static readonly SearchOutcome Empty = new(
            Array.Empty<SearchResult>(),
            new Dictionary<string, Dictionary<string, double>>());
    }

    private static SearchDocument ReadDocument(NpgsqlDataReader reader)
    {
        string id = reader.GetString(0);
        string content = reader.GetString(1);
        string? category = reader.IsDBNull(2) ? null : reader.GetString(2);
        var fields = reader.IsDBNull(3) ? null : DeserializeFields(reader.GetString(3));
        var textFields = reader.IsDBNull(4) ? null : DeserializeFields(reader.GetString(4));

        return new SearchDocument(id, content, fields, category, textFields);
    }

    private static IReadOnlyDictionary<string, string>? DeserializeFields(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
    }
}