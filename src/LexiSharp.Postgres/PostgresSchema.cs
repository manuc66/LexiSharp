using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// Shared DDL for the documents table. Used by the lexical (<c>tsvector</c>), vector
/// (<c>pgvector</c> ANN) and ParadeDB (<c>pg_search</c> BM25) engines so they can all point
/// at the same table.
/// </summary>
public static class PostgresSchema
{
    /// <summary>
    /// Idempotently installs <c>unaccent</c> and the documents table with its <c>tsv</c> column
    /// and GIN index.
    /// </summary>
    public static async Task CreateDocumentTableAsync(
        NpgsqlConnection connection,
        PostgresIndexOptions options,
        CancellationToken cancellationToken = default)
    {
        await PostgresExtensionInstaller.InstallAsync(
            connection, "CREATE EXTENSION IF NOT EXISTS unaccent;", cancellationToken).ConfigureAwait(false);

        await using var command = connection.CreateCommand();

        string indexName = SafeIdentifier.QuoteIdentifier($"{options.Table}_tsv_gin");

        string createSql = $"""
            CREATE TABLE IF NOT EXISTS {options.QualifiedTableName} (
                id       text PRIMARY KEY,
                content  text NOT NULL,
                category text,
                fields   jsonb,
                text_fields jsonb,
                tsv      tsvector
            );
            CREATE INDEX IF NOT EXISTS {indexName} ON {options.QualifiedTableName} USING GIN (tsv);
            """;

        // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted).
        command.CommandText = createSql; // NOSONAR:S2077
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The tsvector expression used to derive <c>tsv</c> from a content reference.
    /// </summary>
    /// <remarks>
    /// When <paramref name="textFieldsReference"/> is supplied, the values of the
    /// <c>text_fields</c> jsonb column are flattened into the same tsvector, so a document's
    /// named free-text sections (e.g. <c>title</c>) become searchable by the tsvector engine
    /// instead of being stored but ignored. The two references are SQL fragments naming the
    /// content and the text-fields column (or their <c>EXCLUDED.</c> counterparts on upsert);
    /// passing <c>null</c> keeps the expression content-only, for callers that do not carry
    /// text fields.
    /// </remarks>
    public static string TsvExpression(PostgresIndexOptions options, string contentReference, string? textFieldsReference = null)
    {
        if (string.IsNullOrEmpty(textFieldsReference))
            return $"to_tsvector('{options.TextSearchConfig}', unaccent({contentReference}))";

        // jsonb_each_text on a null column yields no rows, so the coalesce covers the
        // "no text fields" case without a conditional.
        string flattened = $"coalesce((select string_agg(value, ' ') from jsonb_each_text({textFieldsReference})), '')";
        return $"to_tsvector('{options.TextSearchConfig}', unaccent({contentReference} || ' ' || {flattened}))";
    }
}
