namespace LexiSharp.Postgres;

/// <summary>
/// The four weight classes a <c>tsvector</c> lexeme can carry, which <c>setweight</c> writes and
/// <c>ts_rank_cd</c> reads back through its weights array.
/// </summary>
/// <remarks>
/// These are PostgreSQL's own classes, not a scoring model: <c>ts_rank_cd</c> maps them to
/// <c>A = 1.0</c>, <c>B = 0.4</c>, <c>C = 0.2</c> and <c>D = 0.1</c> by default. There is no
/// per-field length normalisation to go with them, so a long field and a short one contribute
/// the same weight per matched term.
/// </remarks>
public enum PostgresFieldWeight
{
    /// <summary>Class <c>A</c>, the highest (counted at 1.0 by <c>ts_rank_cd</c>).</summary>
    A,

    /// <summary>Class <c>B</c> (0.4).</summary>
    B,

    /// <summary>Class <c>C</c> (0.2).</summary>
    C,

    /// <summary>Class <c>D</c>, the lowest (0.1) — the class of a lexeme written without <c>setweight</c>.</summary>
    D,
}

/// <summary>
/// How the lexical engine turns a multi-term query into a match.
/// </summary>
public enum PostgresMatchMode
{
    /// <summary>
    /// Every term must appear (<c>websearch_to_tsquery</c>). The default, and the rule a search box
    /// that promises "all words" implies.
    /// </summary>
    /// <remarks>
    /// Long queries pay for it: SciFact's test queries average 13.1 terms, and requiring all of
    /// them on a ~500-word abstract leaves almost nothing to rank. On this repository's BEIR run
    /// the rule reads 0.0188 nDCG@10 there.
    /// </remarks>
    AllTerms,

    /// <summary>
    /// Any term may appear: the query's lexemes are OR-ed into one <c>tsquery</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The lexemes come from <c>to_tsvector</c> over the configured text search configuration, so
    /// they are the same strings the <c>tsv</c> column holds. It is a bag of words: the websearch
    /// operators (<c>or</c>, <c>-term</c>, a trailing <c>*</c>) are not honoured. Quoted phrases
    /// still are, because the engines split them out before they build the expression.
    /// </para>
    /// <para>
    /// This changes which documents are candidates, not how they are ranked: the ordering is still
    /// <c>ts_rank_cd</c>'s coverage score, which is not BM25. Use
    /// <see cref="PostgresBm25SearchEngine"/> when the ranking itself has to be BM25.
    /// </para>
    /// </remarks>
    AnyTerm,
}

/// <summary>
/// Naming and behavior options for the PostgreSQL-backed index.
/// </summary>
public sealed record PostgresIndexOptions
{
    /// <summary>Schema that hosts the documents table (default: <c>public</c>).</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Table that stores documents (default: <c>lexisharp_documents</c>).</summary>
    public string Table { get; init; } = "lexisharp_documents";

    /// <summary>
    /// The PostgreSQL <see href="https://www.postgresql.org/docs/current/textsearch-controls.html">text search configuration</see>
    /// used for queries and the <c>tsv</c> column (default: <c>simple</c>).
    /// Pair it with <c>unaccent</c> to mirror LexiSharp's diacritic-insensitive NFKD normalization.
    /// </summary>
    public string TextSearchConfig { get; init; } = "simple";

    /// <summary>
    /// When true (default), <see cref="PostgresTextSearchEngine.EnsureSchema"/> installs what is
    /// needed: the <c>unaccent</c> extension, the documents table (with a <c>tsv tsvector</c> column)
    /// and a GIN index on it.
    /// </summary>
    public bool AutoCreateSchema { get; init; } = true;

    /// <summary>
    /// Weight class per <see cref="Core.SearchDocument.TextFields"/> entry, applied with
    /// <c>setweight</c> when the <c>tsv</c> column is written (default: <c>null</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// When null or empty, every field is written into one plain <c>tsvector</c> and
    /// <c>ts_rank_cd</c> counts every match the same — the expression a table has always been
    /// written with, unchanged.
    /// </para>
    /// <para>
    /// When set, the row's <c>tsv</c> is the concatenation of one weighted vector per section:
    /// <see cref="Core.SearchDocument.Text"/> and every entry not named here take
    /// <see cref="DefaultFieldWeight"/>, and each named entry takes its class. The text engine's
    /// query passes <c>ts_rank_cd</c> the matching class-to-value array
    /// (<c>{0.1, 0.2, 0.4, 1.0}</c> for <c>D, C, B, A</c>, PostgreSQL's own defaults), so a
    /// <c>title</c> at <c>A</c> against a body at <c>D</c> counts a title match ten times a body
    /// match. A field named in neither place is still indexed: it falls into the unlisted section
    /// at <see cref="DefaultFieldWeight"/>.
    /// </para>
    /// <para>
    /// The class is stored on the row, not in the table's DDL, so an existing table needs no
    /// schema change — only a rewrite of the rows written before the option was set. See
    /// <c>docs/backends.md</c> for the statement. Every engine that writes the shared table
    /// (lexical, vector, sparse) has to be given the same value, or a document written by one
    /// scores differently from the same document written by another; the expressions are built
    /// from this option by <see cref="PostgresSchema.TsvExpression"/> for all three.
    /// </para>
    /// </remarks>
    public IReadOnlyDictionary<string, PostgresFieldWeight>? FieldWeights { get; init; }

    /// <summary>
    /// Class given to <see cref="Core.SearchDocument.Text"/> and to every
    /// <see cref="Core.SearchDocument.TextFields"/> entry not named in
    /// <see cref="FieldWeights"/> when weighting is on (default: <see cref="PostgresFieldWeight.D"/>).
    /// </summary>
    public PostgresFieldWeight DefaultFieldWeight { get; init; } = PostgresFieldWeight.D;

    /// <summary>
    /// How a multi-term query is matched: every term (<see cref="PostgresMatchMode.AllTerms"/>, the
    /// default) or any term (<see cref="PostgresMatchMode.AnyTerm"/>).
    /// </summary>
    /// <remarks>
    /// The mode decides the candidate set, not the ranking: results are ordered by
    /// <c>ts_rank_cd</c> either way. Row storage does not change, so the same table serves both —
    /// only the query's <c>tsquery</c> moves.
    /// </remarks>
    public PostgresMatchMode MatchMode { get; init; } = PostgresMatchMode.AllTerms;

    internal bool IsValid => SafeIdentifier.IsValid(Schema) && SafeIdentifier.IsValid(Table) && SafeIdentifier.IsValid(TextSearchConfig)
        && IsValidWeighting && Enum.IsDefined(MatchMode);

    /// <summary>Field names reach SQL as quoted literals, so only empty or NUL-bearing keys are refused.</summary>
    internal bool IsValidWeighting => IsWeightingValid(FieldWeights, DefaultFieldWeight);

    internal static bool IsWeightingValid(
        IReadOnlyDictionary<string, PostgresFieldWeight>? weights, PostgresFieldWeight defaultWeight) =>
        Enum.IsDefined(defaultWeight)
        && (weights is null || weights.All(entry =>
            entry.Key.Length > 0
            && entry.Key.IndexOf('\0') < 0
            && Enum.IsDefined(entry.Value)));

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => $"{SafeIdentifier.QuoteIdentifier(Schema)}.{SafeIdentifier.QuoteIdentifier(Table)}";

}
