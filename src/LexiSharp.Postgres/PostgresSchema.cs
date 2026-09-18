using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// Shared DDL for the documents table, used by both the lexical and the vector engines so
/// they can safely point at the same table.
/// </summary>
internal static class PostgresSchema
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
        await using var command = connection.CreateCommand();

        string indexName = PostgresIndexOptions.QuoteIdentifier($"{options.Table}_tsv_gin");

        command.CommandText = $"""
            CREATE EXTENSION IF NOT EXISTS unaccent;
            CREATE TABLE IF NOT EXISTS {options.QualifiedTableName} (
                id       text PRIMARY KEY,
                content  text NOT NULL,
                category text,
                fields   jsonb,
                tsv      tsvector
            );
            CREATE INDEX IF NOT EXISTS {indexName} ON {options.QualifiedTableName} USING GIN (tsv);
            """;

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The tsvector expression used to derive <c>tsv</c> from a content reference.</summary>
    public static string TsvExpression(PostgresIndexOptions options, string contentReference) =>
        $"to_tsvector('{options.TextSearchConfig}', unaccent({contentReference}))";
}