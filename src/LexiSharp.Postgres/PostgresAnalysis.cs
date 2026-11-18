namespace LexiSharp.Postgres;

/// <summary>
/// Who analyses the text a <see cref="PostgresBm25SearchEngine"/> indexes and searches: PostgreSQL,
/// or this library's own tokenizer.
/// </summary>
/// <remarks>
/// <para>
/// The BM25 engine reads term frequencies out of a <c>tsvector</c>, so whatever produced that vector
/// is what the ranking weighs. <see cref="Database"/> is the historical behaviour and the default:
/// the <c>tsv</c> column and the query lexemes both come from <see cref="PostgresBm25Options.TextSearchConfig"/>,
/// which keeps every PostgreSQL dictionary and configuration — a thesaurus, ispell, a Snowball
/// stemmer for another language — available at no cost to the caller.
/// </para>
/// <para>
/// <see cref="LexiSharp"/> takes the analysis out of PostgreSQL's hands: the engine tokenizes each
/// document with <see cref="Linguistics.Tokenizer"/> and writes the resulting lexemes and positions
/// into a separate <c>tsv_bm25</c> column. The difference is measured rather than theoretical — on
/// FiQA-2018 the stock configuration costs about 0.007 nDCG@10 against the reference analysis,
/// because a Snowball stemmer, a 127-word stop list and PostgreSQL's parser are not Lucene's Porter,
/// 33-word list and scanner.
/// </para>
/// <para>
/// This is opt-in and per engine. The server <c>tsv</c> column is still written in this mode, so
/// readers of the shared table keep working, and PostgreSQL dictionaries remain available as
/// query-time expanders through <see cref="PostgresBm25Options.QueryDictionaries"/> in either mode.
/// </para>
/// </remarks>
public enum PostgresAnalysis
{
    /// <summary>
    /// PostgreSQL analyses the text, through <see cref="PostgresBm25Options.TextSearchConfig"/>
    /// (the default). Every dictionary and parser the server has is therefore in play.
    /// </summary>
    Database,

    /// <summary>
    /// The library analyses the text in the client: <c>uax29</c> word boundaries, Porter stemming
    /// and the 33-word English function-word list by default, into a separate <c>tsv_bm25</c>
    /// column with its own GIN index.
    /// </summary>
    /// <remarks>
    /// A row written by another engine on the shared table carries no <c>tsv_bm25</c> and is absent
    /// from this engine's index until this engine rewrites it: the mode assumes this engine is the
    /// writer of the table. Pass <see cref="PostgresBm25Options.AnalysisOptions"/> to analyse another
    /// language or another way.
    /// </remarks>
    LexiSharp,
}
