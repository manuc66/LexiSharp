using System.Globalization;

using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// Shared DDL for the documents table. Used by the lexical (<c>tsvector</c>), BM25
/// (<c>PostgresBm25SearchEngine</c>), vector (<c>pgvector</c> ANN) and ParadeDB
/// (<c>pg_search</c> BM25) engines so they can all point at the same table.
/// </summary>
public static class PostgresSchema
{
    /// <summary>
    /// The tie-break every SQL engine appends to its <c>ORDER BY</c>: the document id, ordinally.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>COLLATE "C"</c> is load-bearing. Ordering a <c>text</c> column under the database's default
    /// collation is locale-sensitive — <c>en_US.utf8</c> folds case and ignores some punctuation, so
    /// <c>a</c> sorts before <c>A</c> — while the in-memory engines break ties with
    /// <c>string.CompareOrdinal</c>. The C collation compares bytes, which is that ordinal order.
    /// </para>
    /// <para>
    /// Without a secondary key the order among exactly equal scores is whatever the executor produced,
    /// and a page can differ between two plans of the same query. With it, the page is the one
    /// <see cref="Ranking.TieBreak.DocumentId"/> documents, and the SQL engines agree with the
    /// in-memory ones. On the three BEIR corpora this repository evaluates, the C and
    /// <c>en_US.utf8</c> orders of the ids are identical, so no published row moves.
    /// </para>
    /// </remarks>
    internal const string OrdinalIdTieBreak = "id COLLATE \"C\" ASC";

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

        // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted).
        command.CommandText = CreateDocumentTableSql(options); // NOSONAR:S2077
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous <see cref="CreateDocumentTableAsync"/>, for callers whose own API is synchronous.
    /// </summary>
    /// <param name="connection">An open connection.</param>
    /// <param name="options">Naming and text-search options.</param>
    internal static void CreateDocumentTable(NpgsqlConnection connection, PostgresIndexOptions options)
    {
        PostgresExtensionInstaller.Install(connection, "CREATE EXTENSION IF NOT EXISTS unaccent;");

        using var command = connection.CreateCommand();

        // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted).
        command.CommandText = CreateDocumentTableSql(options); // NOSONAR:S2077
        command.ExecuteNonQuery();
    }

    private static string CreateDocumentTableSql(PostgresIndexOptions options)
    {
        string indexName = SafeIdentifier.QuoteIdentifier($"{options.Table}_tsv_gin");

        return $"""
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
    /// <para>
    /// <b>Field names are values, not syntax.</b> Each one is a parameter, named from
    /// <paramref name="parameterPrefix"/> and sorted so the same configuration produces the same
    /// SQL on every call. Bind them with <see cref="TsvExpressionSql.Bind"/>. A literal would have
    /// doubled its quotes and still depended on <c>standard_conforming_strings</c> being on: a
    /// backslash escapes the closing quote when it is off, and field names are free-form caller
    /// text.
    /// </para>
    /// </remarks>
    /// <param name="options">Naming and weighting options.</param>
    /// <param name="contentReference">SQL naming the content (e.g. <c>@content</c>).</param>
    /// <param name="textFieldsReference">SQL naming the text-fields jsonb column, or <c>null</c>.</param>
    /// <param name="parameterPrefix">
    /// Prefix for the field-name parameters; must match <c>[A-Za-z0-9_]</c>. Two expressions spliced
    /// into one statement need different prefixes.
    /// </param>
    public static TsvExpressionSql TsvExpression(
        PostgresIndexOptions options,
        string contentReference,
        string? textFieldsReference = null,
        string parameterPrefix = "tsv_field")
    {
        ArgumentNullException.ThrowIfNull(options);

        string ToVector(string text) => $"to_tsvector('{options.TextSearchConfig}', unaccent({text}))";

        if (options.FieldWeights is not { Count: > 0 } weights)
        {
            if (string.IsNullOrEmpty(textFieldsReference))
                return new TsvExpressionSql(ToVector(contentReference), Array.Empty<KeyValuePair<string, string>>());

            // jsonb_each_text on a null column yields no rows, so the coalesce covers the
            // "no text fields" case without a conditional.
            string flattened = $"coalesce((select string_agg(value, ' ') from jsonb_each_text({textFieldsReference})), '')";

            return new TsvExpressionSql(
                ToVector($"{contentReference} || ' ' || {flattened}"),
                Array.Empty<KeyValuePair<string, string>>());
        }

        if (!SafeIdentifier.IsValid(parameterPrefix))
            throw new ArgumentException("A parameter prefix must match [A-Za-z0-9_].", nameof(parameterPrefix));

        // Sorted, so the same configuration produces the same SQL bytes on every call (and the
        // same term positions in the vector) regardless of the dictionary's own order.
        var named = weights.OrderBy(entry => entry.Key, StringComparer.Ordinal).ToList();

        var names = new string[named.Count];
        var parameters = new List<KeyValuePair<string, string>>(named.Count);

        for (int i = 0; i < named.Count; i++)
        {
            string name = parameterPrefix + "_" + i.ToString(CultureInfo.InvariantCulture);
            names[i] = "@" + name;
            parameters.Add(new KeyValuePair<string, string>(name, named[i].Key));
        }

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
                + $" where key not in ({string.Join(", ", names)})), '')";
            sections.Add(Weighted(ToVector(unlisted), options.DefaultFieldWeight));

            for (int i = 0; i < named.Count; i++)
            {
                sections.Add(Weighted(
                    ToVector($"coalesce({textFieldsReference} ->> {names[i]}, '')"),
                    named[i].Value));
            }
        }

        return new TsvExpressionSql(string.Join(" || ", sections), parameters);
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
}
