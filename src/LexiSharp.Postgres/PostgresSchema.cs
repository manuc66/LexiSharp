using System.Text;

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
    /// <para>
    /// When <paramref name="textFieldsReference"/> is supplied, the values of the
    /// <c>text_fields</c> jsonb column are flattened into the same tsvector, so a document's
    /// named free-text sections (e.g. <c>title</c>) become searchable by the tsvector engine
    /// instead of being stored but ignored. The two references are SQL fragments naming the
    /// content and the text-fields column (or their <c>EXCLUDED.</c> counterparts on upsert);
    /// passing <c>null</c> keeps the expression content-only, for callers that do not carry
    /// text fields.
    /// </para>
    /// <para>
    /// With <see cref="PostgresIndexOptions.FieldWeights"/> unset, the expression is one
    /// <c>to_tsvector</c> call over the concatenated text: every field lands in the same vector
    /// at the same weight. With it set, the vector is built per section —
    /// <c>setweight(to_tsvector(...), class)</c> for the content, for the fields the map does not
    /// name, and for each named field — and concatenated with <c>||</c>, which offsets each
    /// section's term positions past the previous one's.
    /// </para>
    /// </remarks>
    public static string TsvExpression(PostgresIndexOptions options, string contentReference, string? textFieldsReference = null)
    {
        string ToVector(string text) => $"to_tsvector('{options.TextSearchConfig}', unaccent({text}))";

        if (options.FieldWeights is not { Count: > 0 } weights)
        {
            if (string.IsNullOrEmpty(textFieldsReference))
                return ToVector(contentReference);

            // jsonb_each_text on a null column yields no rows, so the coalesce covers the
            // "no text fields" case without a conditional.
            string flattened = $"coalesce((select string_agg(value, ' ') from jsonb_each_text({textFieldsReference})), '')";
            return ToVector($"{contentReference} || ' ' || {flattened}");
        }

        // Sorted, so the same configuration produces the same SQL bytes on every call (and the
        // same term positions in the vector) regardless of the dictionary's own order.
        var named = weights.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();

        var sections = new List<string>(named.Count + 2)
        {
            Weighted(ToVector(contentReference), options.DefaultFieldWeight),
        };

        if (!string.IsNullOrEmpty(textFieldsReference))
        {
            // Unlisted entries are still indexed: dropping them would make setting a weight for
            // one field silently narrow the index for every other. One aggregating section, so
            // their terms share the default class rather than one class each.
            string unlisted = $"coalesce((select string_agg(value, ' ' order by key) from jsonb_each_text({textFieldsReference})"
                + $" where key not in ({string.Join(", ", named.Select(entry => Literal(entry.Key)))}))"
                + ", '')";
            sections.Add(Weighted(ToVector(unlisted), options.DefaultFieldWeight));

            sections.AddRange(named.Select(entry =>
                Weighted(ToVector($"coalesce({textFieldsReference} ->> {Literal(entry.Key)}, '')"), entry.Value)));
        }

        return string.Join(" || ", sections);
    }

    /// <summary>
    /// The <c>ts_rank_cd</c> weights array matching <see cref="PostgresIndexOptions.FieldWeights"/>,
    /// or <c>null</c> when the option is unset and the function's own default applies.
    /// </summary>
    /// <remarks>
    /// The array maps the stored classes back to numbers, in the order <c>D, C, B, A</c> that
    /// <c>ts_rank_cd</c> expects. The values are PostgreSQL's defaults, so the array states the
    /// mapping the query side relies on rather than changing it: what decides a term's
    /// contribution is the letter <c>setweight</c> wrote onto its lexeme at index time.
    /// </remarks>
    public static string? RankWeightsArray(PostgresIndexOptions options) =>
        options.FieldWeights is { Count: > 0 } ? "'{0.1, 0.2, 0.4, 1.0}'" : null;

    /// <summary>The PostgreSQL weight letter for a class.</summary>
    private static char Letter(PostgresFieldWeight weight) => weight switch
    {
        PostgresFieldWeight.A => 'A',
        PostgresFieldWeight.B => 'B',
        PostgresFieldWeight.C => 'C',
        _ => 'D',
    };

    private static string Weighted(string vector, PostgresFieldWeight weight) =>
        $"setweight({vector}, '{Letter(weight)}')";

    /// <summary>A field name as a quoted SQL literal; names are free-form, so quotes are doubled.</summary>
    /// <remarks>
    /// Doubling the quote is complete under <c>standard_conforming_strings = on</c>, which is
    /// PostgreSQL's default since 9.1 and the only value this library's SQL assumes anywhere else:
    /// with it off, a backslash escapes the closing quote and a field name could end its own literal.
    /// A server configured otherwise is out of contract, and the names arriving here are the
    /// caller's configuration rather than a document's data.
    /// </remarks>
    private static string Literal(string value)
    {
        var literal = new StringBuilder(value.Length + 2).Append('\'');
        literal.Append(value.Replace("'", "''", StringComparison.Ordinal)).Append('\'');
        return literal.ToString();
    }
}
