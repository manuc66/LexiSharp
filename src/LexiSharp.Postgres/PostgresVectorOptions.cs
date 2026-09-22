using System.Text.RegularExpressions;

namespace LexiSharp.Postgres;

/// <summary>Index access method used for the ANN index over the embedding column.</summary>
public enum VectorIndexMethod
{
    /// <summary>HNSW: exact-ish graph index, supports inserts and empty tables. Recommended default.</summary>
    Hnsw,

    /// <summary>
    /// IVFFlat: inverted-file index, faster to build but approximate and slower to insert.
    /// Requires the table to be non-empty when the index is first created.
    /// </summary>
    IvfFlat,
}

/// <summary>Distance operator driving the ANN index and the query ordering.</summary>
public enum VectorDistance
{
    /// <summary>Cosine distance (<c>&lt;=&gt;</c>, <c>vector_cosine_ops</c>). Best for text embeddings.</summary>
    Cosine,

    /// <summary>Euclidean L2 distance (<c>&lt;-&gt;</c>, <c>vector_l2_ops</c>).</summary>
    L2,

    /// <summary>Inner product (<c>&lt;#&gt;</c>, <c>vector_ip_ops</c>). Higher dot product = more relevant.</summary>
    InnerProduct,
}

/// <summary>
/// Naming and behavior options for the embeddings-backed (pgvector) index. The table layout is
/// shared with the lexical engine so both engines can serve a single corpus table.
/// </summary>
public sealed record PostgresVectorOptions
{
    private static readonly Regex SafeName = new("^[A-Za-z0-9_]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Schema that hosts the documents table (default: <c>public</c>).</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Table that stores documents (default: <c>lexisharp_documents</c>).</summary>
    public string Table { get; init; } = "lexisharp_documents";

    /// <summary>Embedding length, controlled by the <see cref="Core.IEmbeddingProvider"/> (default: <c>384</c>).</summary>
    public int Dimension { get; init; } = 384;

    /// <summary>ANN access method (default: <see cref="VectorIndexMethod.Hnsw"/>).</summary>
    public VectorIndexMethod IndexMethod { get; init; } = VectorIndexMethod.Hnsw;

    /// <summary>Distance operator used for ranking and the index opclass (default: cosine).</summary>
    public VectorDistance Distance { get; init; } = VectorDistance.Cosine;

    /// <summary>HNSW <c>m</c> (max connections per node), when <see cref="IndexMethod"/> is HNSW.</summary>
    public int HnswM { get; init; } = 16;

    /// <summary>HNSW <c>ef_construction</c>, when <see cref="IndexMethod"/> is HNSW.</summary>
    public int HnswEfConstruction { get; init; } = 64;

    /// <summary>
    /// HNSW <c>ef_search</c>, the size of the dynamic candidate list during a search (higher = more
    /// recall, more IO). When set, the engine issues <c>SET LOCAL hnsw.ef_search = @v</c> inside a
    /// dedicated transaction around each search; when null (default), pgvector's built-in default
    /// (<c>40</c>) applies and no GUC is touched.
    /// </summary>
    public int? HnswEfSearch { get; init; }

    /// <summary>IVFFlat <c>lists</c>, when <see cref="IndexMethod"/> is IVFFlat (roughly <c>sqrt(rows)</c>).</summary>
    public int IvfLists { get; init; } = 100;

    /// <summary>
    /// Name of the <see cref="Core.SearchDocument.TextFields"/> entry that should be embedded in
    /// place of <see cref="Core.SearchDocument.Text"/>. When null (default), the whole
    /// <see cref="Core.SearchDocument.Text"/> is embedded.
    /// </summary>
    public string? EmbeddingTextField { get; init; }

    /// <summary>
    /// When true (default), the constructor installs the <c>vector</c> extension, the documents
    /// table (if needed) and the ANN index.
    /// </summary>
    public bool AutoCreateSchema { get; init; } = true;

    internal bool IsValid =>
        SafeName.IsMatch(Schema) && SafeName.IsMatch(Table)
        && Dimension > 0 && HnswM > 0 && HnswEfConstruction > 0 && IvfLists > 0
        && (HnswEfSearch is null || HnswEfSearch > 0);

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => $"{PostgresIndexOptions.QuoteIdentifier(Schema)}.{PostgresIndexOptions.QuoteIdentifier(Table)}";

    /// <summary>Base table options (schema/table only) shared with the lexical engine.</summary>
    internal PostgresIndexOptions AsIndexOptions() =>
        new() { Schema = Schema, Table = Table, AutoCreateSchema = AutoCreateSchema };

    /// <summary>pgvector type declaration for the embedding column, e.g. <c>vector(384)</c>.</summary>
    internal string VectorType => $"vector({Dimension})";

    /// <summary>The distance operator, e.g. <c>&lt;=&gt;</c>.</summary>
    internal string Operator => Distance switch
    {
        VectorDistance.L2 => "<->",
        VectorDistance.InnerProduct => "<#>",
        _ => "<=>",
    };

    /// <summary>The pgvector index opclass matching <see cref="Distance"/>.</summary>
    internal string OpClass => Distance switch
    {
        VectorDistance.L2 => "vector_l2_ops",
        VectorDistance.InnerProduct => "vector_ip_ops",
        _ => "vector_cosine_ops",
    };
}