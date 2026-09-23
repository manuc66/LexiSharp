using System.Text.RegularExpressions;

namespace LexiSharp.ParadeDB;

/// <summary>
/// Naming and behavior options for the ParadeDB-backed engine.
/// </summary>
/// <remarks>
/// The engine works on the same documents table as <c>LexiSharp.Postgres</c> (see
/// <c>PostgresSchema.CreateDocumentTableAsync</c>): the <c>pg_search</c> extension adds a
/// Tantivy (BM25) index <c>ON</c> that table, so the lexical <c>tsvector</c> engine, the
/// vector engine and this one can coexist on a single table.
/// </remarks>
public sealed partial record ParadeDBOptions
{
    // Trivial linear pattern; NonBacktracking keeps the engine immune to ReDoS (S6444). Compiled
    // once at startup by the source generator (SYSLIB1045).
    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SafeNameRegex();

    /// <summary>Schema that hosts the documents table (default: <c>public</c>).</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Table that stores documents (default: <c>lexisharp_documents</c>).</summary>
    public string Table { get; init; } = "lexisharp_documents";

    /// <summary>Name of the index created with <c>USING paradedb</c> (default: <c>{Table}_paradedb_idx</c>).</summary>
    public string IndexName { get; init; } = string.Empty;

    /// <summary>
    /// Column holding searchable text (default: <c>content</c>). Must match the column that
    /// stores <see cref="LexiSharp.Core.SearchDocument.Text"/>.
    /// </summary>
    public string ContentField { get; init; } = "content";

    /// <summary>
    /// Tantivy tokenizer applied to the content field, with per-token options. The engine
    /// appends <c>alias=&lt;ContentField&gt;</c> so the operator predicates keep working on the
    /// plain column expression. The default (<c>pdb.simple</c> with ASCII folding) mirrors
    /// LexiSharp's diacritic-insensitive, lowercase normalization.
    /// </summary>
    public string ContentTokenizer { get; init; } = "pdb.simple('ascii_folding=true')";

    /// <summary>
    /// When true (default), <see cref="ParadeDBTextSearchEngine.EnsureSchema"/> installs what
    /// is needed: the <c>pg_search</c> extension, the documents table and the ParadeDB index.
    /// </summary>
    public bool AutoCreateSchema { get; init; } = true;

    internal bool IsValid => SafeNameRegex().IsMatch(Schema) && SafeNameRegex().IsMatch(Table) && SafeNameRegex().IsMatch(ContentField);

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => $"{QuoteIdentifier(Schema)}.{QuoteIdentifier(Table)}";

    /// <summary>The quoted ParadeDB (BM25) index name.</summary>
    public string QualifiedIndexName => QuoteIdentifier(IndexName.Length == 0 ? $"{Table}_paradedb_idx" : IndexName);

    /// <summary>
    /// The expression to index for full-text search, e.g.
    /// <c>(content::pdb.simple('ascii_folding=true', 'alias=content'))</c>.
    /// </summary>
    public string ContentExpression => $"({ContentField}::{WithAlias(ContentTokenizer)})";

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    private string WithAlias(string tokenizer)
    {
        if (tokenizer.Contains("alias=", System.StringComparison.Ordinal))
            return tokenizer;

        return tokenizer.EndsWith(')')
            ? tokenizer[..^1] + $", 'alias={ContentField}')"
            : tokenizer;
    }
}