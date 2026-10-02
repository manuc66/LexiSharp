using System.Text.RegularExpressions;

namespace LexiSharp.Postgres;

/// <summary>
/// Naming and behavior options for the PostgreSQL-backed index.
/// </summary>
public sealed record PostgresIndexOptions
{
    // One instance, one parse of the pattern, for a check that runs on every configuration.
    //
    // NonBacktracking selects the runtime's linear-time engine, which cannot backtrack and so
    // cannot be walked into the exponential blowup a nested quantifier would allow. That is the
    // whole reason this check exists: Schema, Table and TextSearchConfig are interpolated into DDL,
    // and "^[A-Za-z0-9_]+$" is all of what stands between them and a caller.
    //
    // Written out rather than declared with [GeneratedRegex] (SYSLIB1045): the source generator
    // cannot emit a specialized matcher for NonBacktracking and falls back to precisely this
    // constructor call, which is the SYSLIB1044 the build reports. The comment it replaced said
    // "compiled once at startup by the source generator"; the generated file said "a custom
    // Regex-derived type could not be generated because RegexOptions.NonBacktracking isn't
    // supported". The second sentence was true whichever way it went.
    private static readonly Regex SafeNameRegex =
        new("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

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

    internal bool IsValid => SafeNameRegex.IsMatch(Schema) && SafeNameRegex.IsMatch(Table) && SafeNameRegex.IsMatch(TextSearchConfig);

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => $"{QuoteIdentifier(Schema)}.{QuoteIdentifier(Table)}";

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}