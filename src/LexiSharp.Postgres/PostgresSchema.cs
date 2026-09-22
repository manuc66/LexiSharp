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

        string indexName = PostgresIndexOptions.QuoteIdentifier($"{options.Table}_tsv_gin");

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

    /// <summary>The tsvector expression used to derive <c>tsv</c> from a content reference.</summary>
    public static string TsvExpression(PostgresIndexOptions options, string contentReference) =>
        $"to_tsvector('{options.TextSearchConfig}', unaccent({contentReference}))";
}