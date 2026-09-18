using System.Text.RegularExpressions;

namespace LexiSharp.Postgres;

/// <summary>
/// Naming and behavior options for the PostgreSQL-backed index.
/// </summary>
public sealed record PostgresIndexOptions
{
    private static readonly Regex SafeName = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Schema that hosts the documents table (default: <c>public</c>).</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Table that stores documents (default: <c>lexisharp_documents</c>).</summary>
    public string Table { get; init; } = "lexisharp_documents";

    /// <summary>
    /// The PostgreSQL <see href="https://www.postgresql.org/docs/current/textsearch-controls.html">text search configuration</see>
    /// used for queries and the generated tsvector column (default: <c>simple</c>).
    /// Pair it with <c>unaccent</c> to mirror LexiSharp's diacritic-insensitive NFKD normalization.
    /// </summary>
    public string TextSearchConfig { get; init; } = "simple";

    /// <summary>
    /// When true (default), <see cref="PostgresTextSearchEngine.EnsureSchema"/> installs what is
    /// needed: the <c>unaccent</c> extension, the documents table (with a generated
    /// <c>tsv tsvector</c> column) and a GIN index on it.
    /// </summary>
    public bool AutoCreateSchema { get; init; } = true;

    internal bool IsValid => SafeName.IsMatch(Schema) && SafeName.IsMatch(Table) && SafeName.IsMatch(TextSearchConfig);

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => $"{QuoteIdentifier(Schema)}.{QuoteIdentifier(Table)}";

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}