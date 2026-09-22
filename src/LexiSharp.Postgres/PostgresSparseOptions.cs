using System.Text.RegularExpressions;

namespace LexiSharp.Postgres;

/// <summary>Distance operator driving the sparse ANN index and the query ordering.</summary>
public enum SparseDistance
{
    /// <summary>
    /// Inner product (<c>&lt;#&gt;</c>, <c>sparsevec_ip_ops</c>) — the native score of learned
    /// sparse (SPLADE-style) retrieval, where a higher dot product means more relevant.
    /// </summary>
    InnerProduct,

    /// <summary>Cosine distance (<c>&lt;=&gt;</c>, <c>sparsevec_cosine_ops</c>).</summary>
    Cosine,

    /// <summary>Euclidean L2 distance (<c>&lt;-&gt;</c>, <c>sparsevec_l2_ops</c>).</summary>
    L2,

    /// <summary>Taxicab L1 distance (<c>&lt;+&gt;</c>, <c>sparsevec_l1_ops</c>).</summary>
    L1,
}

/// <summary>
/// Naming, vocabulary and behavior options for the pgvector <c>sparsevec</c> index. The table
/// layout is shared with the lexical engine so both engines can serve a single corpus table.
/// </summary>
/// <remarks>
/// <para>
/// <b>The vocabulary must be supplied by the caller.</b> <c>sparsevec</c> is a positional format
/// (term weights live at explicit <c>1..Dimension</c> coordinates), so the term → coordinate
/// mapping is an index-layout decision: it must be fixed up front, shared between the engine that
/// stored the documents and the provider that embeds <i>queries</i>, and never resized afterwards.
/// </para>
/// <para>
/// pgvector only supports <b>HNSW</b> indexing for <c>sparsevec</c> (IVFFlat is unavailable), so
/// this engine always builds an HNSW index — inserts and empty tables are fine (contrary to IVFFlat).
/// </para>
/// </remarks>
public sealed partial record PostgresSparseOptions
{
    // Trivial linear pattern; NonBacktracking keeps the engine immune to ReDoS (S6444). Compiled
    // once at startup by the source generator (SYSLIB1045).
    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SafeNameRegex();

    /// <summary>Schema that hosts the documents table (default: <c>public</c>).</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Table that stores documents (default: <c>lexisharp_documents</c>).</summary>
    public string Table { get; init; } = "lexisharp_documents";

    /// <summary>
    /// Term → coordinate mapping shared between indexing and querying. Coordinates are
    /// <c>0..Dimension-1</c>; the engine writes them 1-based to the <c>sparsevec</c> literal.
    /// </summary>
    public IReadOnlyDictionary<string, int> Vocabulary { get; init; } =
        new Dictionary<string, int>();

    /// <summary>
    /// Number of sparsevec coordinates. When unset, derived from the vocabulary as
    /// <c>max(coordinates) + 1</c> (at least 1).
    /// </summary>
    public int? Dimension { get; init; }

    /// <summary>Distance operator used for ranking and the index opclass (default: inner product).</summary>
    public SparseDistance Distance { get; init; } = SparseDistance.InnerProduct;

    /// <summary>HNSW <c>m</c> (max connections per node).</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW <c>ef_construction</c>.</summary>
    public int HnswEfConstruction { get; init; } = 64;

    /// <summary>
    /// When true (default), the constructor installs the <c>vector</c> extension, the documents
    /// table (if needed) and the HNSW index.
    /// </summary>
    public bool AutoCreateSchema { get; init; } = true;

    internal int ResolvedDimension
    {
        get
        {
            if (Dimension is not null)
                return Dimension.Value;

            int maxCoordinate = Vocabulary.Values.DefaultIfEmpty().Max();
            int derived = maxCoordinate + 1;
            return derived < 1 ? 1 : derived;
        }
    }

    internal bool IsValid =>
        SafeNameRegex().IsMatch(Schema) && SafeNameRegex().IsMatch(Table)
        && Vocabulary.Count > 0
        && Vocabulary.Values.All(index => index >= 0)
        && HnswM > 0 && HnswEfConstruction > 0;

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => $"{PostgresIndexOptions.QuoteIdentifier(Schema)}.{PostgresIndexOptions.QuoteIdentifier(Table)}";

    /// <summary>Base table options (schema/table only) shared with the lexical engine.</summary>
    internal PostgresIndexOptions AsIndexOptions() =>
        new() { Schema = Schema, Table = Table, AutoCreateSchema = AutoCreateSchema };

    /// <summary>pgvector type declaration for the sparse column, e.g. <c>sparsevec(8)</c>.</summary>
    internal string SparseVectorType => $"sparsevec({ResolvedDimension})";

    /// <summary>The distance operator, e.g. <c>&lt;#&gt;</c>.</summary>
    internal string Operator => Distance switch
    {
        SparseDistance.Cosine => "<=>",
        SparseDistance.L2 => "<->",
        SparseDistance.L1 => "<+>",
        _ => "<#>",
    };

    /// <summary>The HNSW opclass matching <see cref="Distance"/>.</summary>
    internal string OpClass => Distance switch
    {
        SparseDistance.Cosine => "sparsevec_cosine_ops",
        SparseDistance.L2 => "sparsevec_l2_ops",
        SparseDistance.L1 => "sparsevec_l1_ops",
        _ => "sparsevec_ip_ops",
    };
}