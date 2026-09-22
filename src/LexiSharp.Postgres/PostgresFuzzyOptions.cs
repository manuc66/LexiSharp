using System.Text.RegularExpressions;

namespace LexiSharp.Postgres;

/// <summary>The way approximate (trigram) search matches documents.</summary>
public enum TrgmSearchMode
{
    /// <summary>
    /// K-nearest neighbors: order by <c>content &lt;-&gt; query</c> (GiST kNN). Best fit for
    /// autocomplete and "give me the closest labels" lookups.
    /// </summary>
    Nearest,

    /// <summary>
    /// Threshold similarity: return documents where <c>content % query</c> above the configured
    /// <see cref="PostgresFuzzyOptions.SimilarityThreshold"/>. Best fit for de-duplication and
    /// "did you mean" filtering.
    /// </summary>
    Similarity,
}

/// <summary>Which trigram index structures are created on the content field.</summary>
public enum TrgmIndexKind
{
    /// <summary>GiST index only; supports both <c>%</c> and kNN distance ordering.</summary>
    Gist,

    /// <summary>GIN index only; supports <c>%</c> (and <c>LIKE</c>) but not distance ordering.</summary>
    Gin,

    /// <summary>Both a GiST and a GIN index (default): every search mode is index-accelerated.</summary>
    Both,
}

/// <summary>
/// Naming and behavior options for the <see cref="PostgresFuzzySearchEngine"/>.
/// </summary>
/// <remarks>
/// The engine works on the same shared documents table as the other <c>LexiSharp.Postgres</c>
/// engines; it relies on the <c>pg_trgm</c> extension (trigram similarity) and optionally on
/// <c>fuzzystrmatch</c> (<c>levenshtein</c> refinement and <c>metaphone</c> phonetic field).
/// </remarks>
public sealed partial record PostgresFuzzyOptions
{
    // Trivial linear pattern; NonBacktracking keeps the engine immune to ReDoS (S6444). Compiled
    // once at startup by the source generator (SYSLIB1045).
    [GeneratedRegex("^[A-Za-z0-9_]+$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex SafeNameRegex();

    /// <summary>Schema that hosts the documents table (default: <c>public</c>).</summary>
    public string Schema { get; init; } = "public";

    /// <summary>Table that stores documents (default: <c>lexisharp_documents</c>).</summary>
    public string Table { get; init; } = "lexisharp_documents";

    /// <summary>Column holding the text compared with trigrams (default: <c>content</c>).</summary>
    public string ContentField { get; init; } = "content";

    /// <summary>
    /// Trigram index structures to create (default: <see cref="TrgmIndexKind.Both"/>).
    /// </summary>
    public TrgmIndexKind IndexKind { get; init; } = TrgmIndexKind.Both;

    /// <summary>How approximate search matches documents (default: <see cref="TrgmSearchMode.Nearest"/>).</summary>
    public TrgmSearchMode SearchMode { get; init; } = TrgmSearchMode.Nearest;

    /// <summary>
    /// Similarity threshold used by the <c>%</c> operator in
    /// <see cref="TrgmSearchMode.Similarity"/> mode (default: <c>0.3</c>, pg_trgm's own default).
    /// </summary>
    public double SimilarityThreshold { get; init; } = 0.3;

    /// <summary>
    /// When true, candidates are post-filtered with <c>levenshtein</c> (via
    /// <c>unaccent(lower())</c> to stay case- and accent-insensitive) so only matches within
    /// <see cref="MaxLevenshteinDistance"/> edits survive. Requires <c>fuzzystrmatch</c>.
    /// </summary>
    public bool UseLevenshteinRefinement { get; init; } = false;

    /// <summary>Maximum edit distance allowed by <see cref="UseLevenshteinRefinement"/> (default: <c>3</c>).</summary>
    public int MaxLevenshteinDistance { get; init; } = 3;

    /// <summary>
    /// When true, a <c>metaphone</c> column is added and phonetic matches are included in
    /// <see cref="TrgmSearchMode.Similarity"/> mode (documents whose <c>metaphone</c> equals the
    /// query's). Useful for proper nouns that sound alike but spell differently. Requires
    /// <c>fuzzystrmatch</c>.
    /// </summary>
    public bool IncludePhonetic { get; init; } = false;

    /// <summary>
    /// When true (default), <see cref="PostgresFuzzySearchEngine.EnsureSchema"/> installs what is
    /// needed: the extensions, the shared documents table and the trigram index structures.
    /// </summary>
    public bool AutoCreateSchema { get; init; } = true;

    internal bool IsValid =>
        SafeNameRegex().IsMatch(Schema) && SafeNameRegex().IsMatch(Table) && SafeNameRegex().IsMatch(ContentField)
        && SimilarityThreshold is > 0 and <= 1
        && MaxLevenshteinDistance >= 0;

    /// <summary>Fully qualified table name, e.g. <c>public.lexisharp_documents</c>.</summary>
    public string QualifiedTableName => $"{QuoteIdentifier(Schema)}.{QuoteIdentifier(Table)}";

    internal static string QuoteIdentifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}