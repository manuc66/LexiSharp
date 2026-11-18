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
    /// <summary>
    /// Upserts one document, deriving <c>tsv</c> from its text and text fields, and writing
    /// <paramref name="tsvBm25"/> into the client-analysis column when one is given.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="transaction">The transaction the write belongs to, or <c>null</c>.</param>
    /// <param name="options">Naming and vector-expression options.</param>
    /// <param name="document">The document to upsert.</param>
    /// <param name="tsvBm25">
    /// The <c>tsv_bm25</c> literal <see cref="PostgresAnalysis.LexiSharp"/> built with
    /// <see cref="PostgresTsvLiteral"/>, or <c>null</c> to leave the column alone — which is what
    /// <see cref="PostgresAnalysis.Database"/> and every other engine on the shared table do. The
    /// server vector is written either way: the lexical engine reads the same rows.
    /// </param>
    public static void Insert(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        PostgresIndexOptions options,
        SearchDocument document,
        string? tsvBm25 = null)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(document);

        using var command = connection.CreateCommand();

        string tsvExpr = PostgresSchema.TsvExpression(options, "@content", "@text_fields");
        string tsvExprUpsert = PostgresSchema.TsvExpression(options, "EXCLUDED.content", "EXCLUDED.text_fields");

        // The client vector is a parameter cast in SQL rather than a literal interpolated into the
        // statement: the terms are a document's data, and the cast is what makes the server parse
        // them with the tsvector input function instead of trusting the caller's string.
        string bm25Column = tsvBm25 is null ? string.Empty : $", {PostgresSchema.Bm25Column}";
        string bm25Value = tsvBm25 is null ? string.Empty : $", @tsv_bm25::tsvector";
        string bm25Upsert = tsvBm25 is null
            ? string.Empty
            : $",\n                    {PostgresSchema.Bm25Column} = @tsv_bm25::tsvector";

        string insertSql = $"""
            INSERT INTO {options.QualifiedTableName} (id, content, category, fields, text_fields, tsv{bm25Column})
            VALUES (@id, @content, @category, @fields, @text_fields, {tsvExpr}{bm25Value})
            ON CONFLICT (id) DO UPDATE
                SET content = EXCLUDED.content,
                    category = EXCLUDED.category,
                    fields = EXCLUDED.fields,
                    text_fields = EXCLUDED.text_fields,
                    tsv = {tsvExprUpsert}{bm25Upsert};
            """;

        command.CommandText = insertSql; // NOSONAR:S2077

        if (transaction is not null)
            command.Transaction = transaction;

        command.Parameters.AddWithValue("id", document.Id);
        command.Parameters.AddWithValue("content", document.Text);
        command.Parameters.AddWithValue("category", (object?)document.Category ?? DBNull.Value);

        if (tsvBm25 is not null)
            command.Parameters.AddWithValue("tsv_bm25", tsvBm25);

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
