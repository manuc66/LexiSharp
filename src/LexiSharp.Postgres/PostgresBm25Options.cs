namespace LexiSharp.Postgres;

/// <summary>
/// Naming, analysis and ranking options for the stock-PostgreSQL BM25 engine.
/// </summary>
/// <remarks>
/// <para>
/// The table layout is the lexical engine's: <see cref="PostgresBm25SearchEngine"/> reads the same
/// <c>documents</c> table, the same <c>tsv</c> column and the same GIN index, so one corpus serves
/// <c>ts_rank_cd</c>, BM25, vector and fuzzy search without a second copy.
/// </para>
/// <para>
/// <b>What makes this BM25 and not <c>ts_rank_cd</c>.</b> The matching rule is still PostgreSQL's —
/// the same <c>tsquery</c>, the same GIN scan — but the score is
/// <c>Σ idf(t)·tf(t,d)·(k1+1)/(tf(t,d)+k1(1−b+b·|d|/avgdl))</c> with
/// <c>idf(t) = ln(1+(N−df(t)+0.5)/(df(t)+0.5))</c>, computed by
/// <see cref="Ranking.Bm25Scorer"/> over the term frequencies the <c>tsvector</c> already stores.
/// It takes <c>k1</c> and <c>b</c>, which <c>ts_rank_cd</c> does not have, and it returns scores on
/// the in-memory scorer's scale rather than PostgreSQL's.
/// </para>
/// </remarks>
public sealed record PostgresBm25Options
{
    /// <summary>Schema that hosts the documents table (default: <c>public</c>).</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Table that stores documents (default: <c>lexisharp_documents</c>).</summary>
    public string Table { get; init; } = "lexisharp_documents";

    /// <summary>
    /// The PostgreSQL text search configuration that analysis, matching and scoring all read
    /// (default: <c>simple</c>). The <c>tsv</c> column, the query lexemes and the term frequencies
    /// come from this one setting, which is why the three cannot disagree.
    /// </summary>
    public string TextSearchConfig { get; init; } = "simple";

    /// <summary>
    /// When true (default), the constructor installs <c>unaccent</c>, the documents table and its
    /// GIN index — the lexical engine's schema, unchanged.
    /// </summary>
    public bool AutoCreateSchema { get; init; } = true;

    /// <summary>
    /// Weight class per <see cref="Core.SearchDocument.TextFields"/> entry, applied when the
    /// <c>tsv</c> column is written (default: <c>null</c>).
    /// </summary>
    /// <remarks>
    /// <c>setweight</c> classes are a <c>ts_rank_cd</c> concept: this engine reads term frequencies
    /// out of the vector and scores them with BM25, which has no place for a class. Setting weights
    /// here only matters if another engine reads the same rows — pass the same value to every
    /// engine writing the table so the <c>tsv</c> column is written one way.
    /// </remarks>
    public IReadOnlyDictionary<string, PostgresFieldWeight>? FieldWeights { get; init; }

    /// <summary>Class given to <see cref="Core.SearchDocument.Text"/> when weighting is on.</summary>
    public PostgresFieldWeight DefaultFieldWeight { get; init; } = PostgresFieldWeight.D;

    /// <summary>Term-frequency saturation (default <c>1.5</c>, the usual Okapi value).</summary>
    public const double DefaultK1 = 1.5;

    /// <summary>Length normalisation in <c>[0, 1]</c> (default <c>0.75</c>); <c>0</c> disables it.</summary>
    public const double DefaultB = 0.75;

    /// <summary>How many matches are re-ranked with BM25 when <see cref="CandidatePool"/> is unset.</summary>
    public const int DefaultCandidatePool = 1000;

    /// <summary>
    /// The most candidates one query may ask for, shared with every SQL engine's per-query fetch
    /// ceiling. <see cref="CandidatePool"/> = <c>0</c> means this, not an unbounded scan.
    /// </summary>
    public const int MaximumCandidatePool = PostgresDocumentExclusion.MaxRowsPerQuery;

    /// <summary>Term-frequency saturation (default <c>1.5</c>, the usual Okapi value).</summary>
    public double K1 { get; init; } = DefaultK1;

    /// <summary>Length normalisation in <c>[0, 1]</c> (default <c>0.75</c>); <c>0</c> disables it.</summary>
    public double B { get; init; } = DefaultB;

    /// <summary>
    /// How a multi-term query is matched: any term (<see cref="PostgresMatchMode.AnyTerm"/>, the
    /// default) or every term (<see cref="PostgresMatchMode.AllTerms"/>).
    /// </summary>
    /// <remarks>
    /// AnyTerm is the default because BM25 ranks a candidate set: an AND rule on a long query
    /// leaves almost nothing to rank (0.0188 nDCG@10 on SciFact in this repository's own run), and
    /// the rare-term idf that makes BM25 useful never gets to see the documents that hold only part
    /// of the query. <see cref="PostgresMatchMode.AllTerms"/> stays available for a corpus where a
    /// result must contain every word.
    /// </remarks>
    public PostgresMatchMode MatchMode { get; init; } = PostgresMatchMode.AnyTerm;

    /// <summary>
    /// How many matches are re-ranked with BM25 (default <c>1000</c>).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The database chooses candidates with <c>ts_rank_cd</c> and the engine scores only the top
    /// <see cref="CandidatePool"/> of them: transferring and parsing the whole match set would make
    /// a search proportional to the corpus. The pool is therefore a recall bound — a document
    /// outside it cannot be returned, however well it would have scored — and it is widened
    /// automatically when the requested page is larger.
    /// </para>
    /// <para>
    /// <c>0</c> removes the bound, up to the engine's own ceiling of 100,000 rows per query. That is
    /// the exact ranking and a cost that grows with the match set; use it to check what the pool
    /// costs on a corpus, not in a request path.
    /// </para>
    /// </remarks>
    public int CandidatePool { get; init; } = DefaultCandidatePool;

    internal bool IsValid =>
        AsIndexOptions().IsValid
        && !double.IsNaN(K1) && !double.IsInfinity(K1) && K1 >= 0
        && !double.IsNaN(B) && !double.IsInfinity(B) && B is >= 0 and <= 1
        && CandidatePool >= 0;

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => AsIndexOptions().QualifiedTableName;

    internal PostgresIndexOptions AsIndexOptions() =>
        new()
        {
            Schema = Schema,
            Table = Table,
            TextSearchConfig = TextSearchConfig,
            AutoCreateSchema = AutoCreateSchema,
            FieldWeights = FieldWeights,
            DefaultFieldWeight = DefaultFieldWeight,
            MatchMode = MatchMode,
        };
}
