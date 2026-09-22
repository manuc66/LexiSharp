using LexiSharp.Core;
using Npgsql;
using NpgsqlTypes;

namespace LexiSharp.Postgres;

/// <summary>
/// PostgreSQL-backed <see cref="ITextSearchEngine"/> for approximate, typo-tolerant matching on
/// top of the <c>pg_trgm</c> extension (trigram similarity) and <c>fuzzystrmatch</c>
/// (<c>levenshtein</c> refinement and <c>metaphone</c> phonetics).
/// </summary>
/// <remarks>
/// <para>
/// Two matching modes are available:
/// </para>
/// <list type="bullet">
/// <item>
/// <see cref="TrgmSearchMode.Nearest"/> (default): k-nearest neighbors over
/// <c>content &lt;-&gt; query</c>, glued to a GiST index. Natural fit for autocomplete and
/// "closest label" lookups.
/// </item>
/// <item>
/// <see cref="TrgmSearchMode.Similarity"/>: documents where <c>content % query</c> above
/// <see cref="PostgresFuzzyOptions.SimilarityThreshold"/> (honored via <c>set_limit</c>),
/// ranked by <c>similarity</c>. Natural fit for de-duplication and "did you mean".
/// </item>
/// </list>
/// <para>
/// Scores are trgm similarities (in [0, 1]: 1 identical, 0 no common trigram — the
/// <see cref="LexiSharp.Core"/> convention that score 0 means no match applies). A score of 0
/// (no shared trigram) is always discarded.
/// </para>
/// <para>
/// Optional hardening: an exact <c>levenshtein</c> post-filter
/// (<see cref="PostgresFuzzyOptions.UseLevenshteinRefinement"/>) and a phonetic
/// <c>metaphone</c> column (<see cref="PostgresFuzzyOptions.IncludePhonetic"/>, applied in
/// <see cref="TrgmSearchMode.Similarity"/> mode only).
/// </para>
/// </remarks>
public sealed class PostgresFuzzySearchEngine : ITextSearchEngine, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresFuzzyOptions _options;
    private bool _schemaReady;

    /// <param name="connectionString">A PostgreSQL connection string (Npgsql format). The target database must have the <c>pg_trgm</c> (and, when enabled, <c>fuzzystrmatch</c>) extension available.</param>
    /// <param name="options">Naming/behavior options (default: <see cref="PostgresFuzzyOptions"/>).</param>
    /// <param name="autoCreateSchema">
    /// Shortcut for <see cref="PostgresFuzzyOptions.AutoCreateSchema"/>; overrides the option when set.
    /// </param>
    public PostgresFuzzySearchEngine(
        string connectionString,
        PostgresFuzzyOptions? options = null,
        bool? autoCreateSchema = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        _options = options ?? new PostgresFuzzyOptions();

        if (!_options.IsValid)
            throw new ArgumentException("Schema, table and content field must match [A-Za-z0-9_] and thresholds be in range.", nameof(options));

        if (autoCreateSchema is not null)
            _options = _options with { AutoCreateSchema = autoCreateSchema.Value };

        _dataSource = NpgsqlDataSource.Create(connectionString);

        if (_options.AutoCreateSchema)
            EnsureSchema();
    }

    /// <summary>
    /// Installs the required extensions (<c>pg_trgm</c>, optionally <c>fuzzystrmatch</c>), the
    /// shared documents table and the trigram index structures. Idempotent.
    /// </summary>
    public void EnsureSchema() => EnsureSchemaAsync().GetAwaiter().GetResult();

    /// <summary>Async variant of <see cref="EnsureSchema"/>.</summary>
    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        if (_schemaReady)
            return;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        var baseOptions = new PostgresIndexOptions { Schema = _options.Schema, Table = _options.Table };

        string extension = _options.UseLevenshteinRefinement || _options.IncludePhonetic
            ? "CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS fuzzystrmatch;"
            : "CREATE EXTENSION IF NOT EXISTS pg_trgm;";

        await PostgresExtensionInstaller.InstallAsync(connection, extension, cancellationToken).ConfigureAwait(false);

        await PostgresSchema.CreateDocumentTableAsync(connection, baseOptions, cancellationToken).ConfigureAwait(false);

        if (_options.IncludePhonetic)
        {
            await using (var command = connection.CreateCommand())
            {
                command.CommandText = $"ALTER TABLE {_options.QualifiedTableName} ADD COLUMN IF NOT EXISTS metaphone text;"; // NOSONAR:S2077;
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (_options.IndexKind is TrgmIndexKind.Gist or TrgmIndexKind.Both)
        {
            await CreateIndexAsync(connection, "gist", "USING GIST", cancellationToken).ConfigureAwait(false);
        }

        if (_options.IndexKind is TrgmIndexKind.Gin or TrgmIndexKind.Both)
        {
            await CreateIndexAsync(connection, "gin", "USING GIN", cancellationToken).ConfigureAwait(false);
        }

        if (_options.IncludePhonetic)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE INDEX IF NOT EXISTS {QuoteIdentifier($"{_options.Table}_metaphone_idx")} ON {_options.QualifiedTableName} (metaphone);"; // NOSONAR:S2077;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        _schemaReady = true;
    }

    /// <summary>
    /// Drops the documents table (and with it the trigram indexes). Useful for tests and clean
    /// teardowns. Does not drop the extensions, which are instance-wide.
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

        if (_options.SearchMode == TrgmSearchMode.Similarity)
        {
            using (var limitCommand = connection.CreateCommand())
            {
                limitCommand.CommandText = "SELECT set_limit(@threshold)";
                limitCommand.Parameters.AddWithValue("threshold", (float)_options.SimilarityThreshold);
                limitCommand.ExecuteNonQuery();
            }
        }

        using var command = connection.CreateCommand();
        command.CommandText = BuildSearchSql();
        command.Parameters.AddWithValue("query", query);
        command.Parameters.AddWithValue("limit", options.Limit);

        if (_options.UseLevenshteinRefinement)
            command.Parameters.AddWithValue("maxLevenshtein", _options.MaxLevenshteinDistance);

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

    private static string QuoteIdentifier(string name) => PostgresIndexOptions.QuoteIdentifier(name);

    private async Task CreateIndexAsync(
        NpgsqlConnection connection,
        string kind,
        string accessMethod,
        CancellationToken cancellationToken)
    {
        var indexName = QuoteIdentifier($"{_options.Table}_{_options.ContentField}_trgm_{kind}");
        var opClass = kind == "gist" ? "gist_trgm_ops" : "gin_trgm_ops";

        await using var command = connection.CreateCommand();
command.CommandText = $"CREATE INDEX IF NOT EXISTS {indexName} ON {_options.QualifiedTableName} {accessMethod} ({_options.ContentField} {opClass});"; // NOSONAR:S2077 (identifiers only, validated + quoted)
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private string BuildSearchSql()
    {
        string phonetic = _options.IncludePhonetic && _options.SearchMode == TrgmSearchMode.Similarity
            ? " OR metaphone = metaphone(@query, 4)"
            : string.Empty;

        string levenshtein = _options.UseLevenshteinRefinement
            ? $"levenshtein(unaccent(lower({_options.ContentField})), unaccent(lower(@query))) <= @maxLevenshtein"
            : string.Empty;

        if (_options.SearchMode == TrgmSearchMode.Nearest)
        {
            string where = levenshtein == string.Empty ? string.Empty : $"WHERE {levenshtein}";

            return $"""
                SELECT id, content, category, fields, 1 - ({_options.ContentField} <-> @query) AS score
                FROM {_options.QualifiedTableName}
                {where}
                ORDER BY {_options.ContentField} <-> @query ASC, id ASC
                LIMIT @limit;
                """;
        }

        string conditions = levenshtein == string.Empty
            ? $"({_options.ContentField} % @query{phonetic})"
            : $"({_options.ContentField} % @query{phonetic}) AND {levenshtein}";

        return $"""
            SELECT id, content, category, fields, similarity({_options.ContentField}, @query) AS score
            FROM {_options.QualifiedTableName}
            WHERE {conditions}
            ORDER BY score DESC, id ASC
            LIMIT @limit;
            """;
    }

    private void Insert(NpgsqlConnection connection, NpgsqlTransaction? transaction, SearchDocument document)
    {
        using var command = connection.CreateCommand();

        string columnList = _options.IncludePhonetic ? "id, content, category, fields, metaphone" : "id, content, category, fields";
        string valueList = _options.IncludePhonetic
            ? "@id, @content, @category, @fields, metaphone(@content, 4)"
            : "@id, @content, @category, @fields";

        string upsertSql = $"""
            INSERT INTO {_options.QualifiedTableName} ({columnList})
            VALUES ({valueList})
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields
                    {(_options.IncludePhonetic ? $", metaphone = metaphone(EXCLUDED.content, 4)" : string.Empty)};
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