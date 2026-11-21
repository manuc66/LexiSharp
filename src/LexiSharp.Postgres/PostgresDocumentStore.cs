using LexiSharp.Core;
using Npgsql;
using NpgsqlTypes;

namespace LexiSharp.Postgres;

/// <summary>
/// The upsert the two <c>tsvector</c> text engines issue into the shared documents table.
/// </summary>
/// <remarks>
/// <para>
/// Extracted for the same reason <see cref="PostgresDocumentExclusion"/> was: a second engine now
/// writes the same table, and the two must write the same row. The <c>tsv</c> column in particular
/// is derived here from <see cref="PostgresSchema.TsvExpression"/>, from the same options, so a
/// document written by the lexical engine and the same document written by the BM25 engine score
/// through one analysis.
/// </para>
/// <para>
/// Identifiers only are interpolated (validated <c>[A-Za-z0-9_]+</c> and quoted); every value is a
/// parameter.
/// </para>
/// </remarks>
internal static class PostgresDocumentWriter
{
    /// <summary>Upserts one document, deriving <c>tsv</c> from its text and text fields.</summary>
    public static void Insert(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PostgresIndexOptions options,
        SearchDocument document)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(document);

        using var command = connection.CreateCommand();

        var tsvExpr = PostgresSchema.TsvExpression(options, "@content", "@text_fields", "tsv");
        var tsvExprUpsert = PostgresSchema.TsvExpression(options, "EXCLUDED.content", "EXCLUDED.text_fields", "tsv_up");

        string insertSql = $"""
            INSERT INTO {options.QualifiedTableName} (id, content, category, fields, text_fields, tsv)
            VALUES (@id, @content, @category, @fields, @text_fields, {tsvExpr.Sql})
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields,
                    text_fields = EXCLUDED.text_fields,
                    tsv = {tsvExprUpsert.Sql};
            """;

        command.CommandText = insertSql; // NOSONAR:S2077

        tsvExpr.Bind(command);
        tsvExprUpsert.Bind(command);

        if (transaction is not null)
            command.Transaction = transaction;

        command.Parameters.AddWithValue("id", document.Id);
        command.Parameters.AddWithValue("content", document.Text);
        command.Parameters.AddWithValue("category", (object?)document.Category ?? DBNull.Value);

        var fieldsParameter = command.Parameters.AddWithValue(
            "fields",
            document.Fields is null ? DBNull.Value : System.Text.Json.JsonSerializer.Serialize(document.Fields, PostgresJsonContext.Default.IReadOnlyDictionaryStringString));
        fieldsParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;

        NpgsqlParameter textFieldsParameter;
        if (document.TextFields is not null)
        {
            textFieldsParameter = command.Parameters.AddWithValue(
                "text_fields",
                System.Text.Json.JsonSerializer.Serialize(document.TextFields, PostgresJsonContext.Default.IReadOnlyDictionaryStringString));
            textFieldsParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
        }
        else
        {
            textFieldsParameter = command.Parameters.AddWithValue("text_fields", DBNull.Value);
            textFieldsParameter.NpgsqlDbType = NpgsqlDbType.Jsonb;
        }

        command.ExecuteNonQuery();
    }
}

/// <summary>
/// Reads the five document columns the engines select, in the order they select them.
/// </summary>
internal static class PostgresDocumentReader
{
    /// <summary>
    /// Reads one row whose first five columns are <c>id, content, category, fields, text_fields</c>.
    /// </summary>
    public static SearchDocument Read(NpgsqlDataReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        string id = reader.GetString(0);
        string content = reader.GetString(1);
        string? category = reader.IsDBNull(2) ? null : reader.GetString(2);
        var fields = reader.IsDBNull(3) ? null : Deserialize(reader.GetString(3));
        var textFields = reader.IsDBNull(4) ? null : Deserialize(reader.GetString(4));

        return new SearchDocument(id, content, fields, category, textFields);
    }

    private static IReadOnlyDictionary<string, string>? Deserialize(string json)
    {
        if (string.IsNullOrEmpty(json))
            return null;

        return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json, PostgresJsonContext.Default.DictionaryStringString);
    }
}
