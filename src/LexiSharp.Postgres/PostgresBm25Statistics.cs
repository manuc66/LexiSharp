using Npgsql;

namespace LexiSharp.Postgres;

/// <summary>
/// The corpus statistics a <see cref="Ranking.Bm25Scorer"/> needs, read from the table once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a snapshot.</b> BM25 needs three corpus-wide numbers: the document count <c>N</c>, the
/// average document length, and each query term's document frequency. Stock PostgreSQL exposes
/// them through <c>ts_stat</c>, which walks the table — cheap once, unaffordable per query. So one
/// call builds this object and the engine keeps it until a write invalidates it; the numbers are
/// then exact for the corpus as it stood at that moment, and stale until the next rebuild. That is
/// the same trade Lucene makes with per-segment statistics, and it is stated rather than hidden.
/// </para>
/// <para>
/// <b>Why <c>N</c> is not <c>count(*)</c>.</b> A document with no indexed term contributes nothing
/// to any score and cannot be a result; counting it would deflate every idf. Under
/// <c>--backend lexical</c> this repository already measured what a corpus with one empty document
/// costs (1 of 8,674 on ArguAna); the count here is the one <see cref="Core.IReadOnlyTextIndex.StatisticDocumentCount"/>
/// documents, so the two BM25 implementations agree.
/// </para>
/// </remarks>
internal sealed class PostgresBm25Statistics
{
    private readonly Dictionary<string, TermStatistics> _terms;

    private PostgresBm25Statistics(int documentCount, long corpusTokenCount, Dictionary<string, TermStatistics> terms)
    {
        DocumentCount = documentCount;
        CorpusTokenCount = corpusTokenCount;
        _terms = terms;
    }

    /// <summary>Documents holding at least one indexed term: the <c>N</c> of the idf.</summary>
    public int DocumentCount { get; }

    /// <summary>Total indexed tokens across the corpus (the sum of every term's frequency).</summary>
    public long CorpusTokenCount { get; }

    /// <summary>Mean indexed tokens per counted document.</summary>
    public double AverageDocumentLength =>
        DocumentCount == 0 ? 0 : (double)CorpusTokenCount / DocumentCount;

    /// <summary>Terms the snapshot knows.</summary>
    public int VocabularySize => _terms.Count;

    /// <summary>Number of documents containing <paramref name="term"/> (0 when unknown).</summary>
    public int DocumentFrequency(string term) =>
        _terms.TryGetValue(term, out TermStatistics statistics) ? statistics.DocumentFrequency : 0;

    /// <summary>Total occurrences of <paramref name="term"/> (0 when unknown).</summary>
    public int CorpusFrequency(string term) =>
        _terms.TryGetValue(term, out TermStatistics statistics) ? statistics.CorpusFrequency : 0;

    /// <summary>
    /// Reads the statistics of <paramref name="options"/>' table.
    /// </summary>
    /// <remarks>
    /// Two statements: <c>ts_stat</c> answers per term, and the count of non-empty vectors answers
    /// <c>N</c> (a scalar subquery would put both in one round trip, but <c>ts_stat</c> cannot be
    /// correlated, so the two are separate reads of the same connection).
    /// </remarks>
    public static PostgresBm25Statistics Load(NpgsqlConnection connection, PostgresIndexOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(options);

        int documentCount;
        long corpusTokenCount = 0;

        using (var count = connection.CreateCommand())
        {
            // Identifiers only are interpolated (validated [A-Za-z0-9_]+ and quoted).
            count.CommandText =
                $"SELECT count(*) FROM {options.QualifiedTableName} WHERE tsv IS NOT NULL AND tsv::text <> ''"; // NOSONAR:S2077

            documentCount = Convert.ToInt32(count.ExecuteScalar());
        }

        var terms = new Dictionary<string, TermStatistics>(StringComparer.Ordinal);

        using (var command = connection.CreateCommand())
        {
            // The inner query is a literal ts_stat evaluates; the table name is a validated,
            // quoted identifier and carries no quote character of its own.
            command.CommandText =
                $"SELECT word, ndoc, nentry FROM ts_stat('SELECT tsv FROM {options.QualifiedTableName}')"; // NOSONAR:S2077

            using var reader = command.ExecuteReader();

            while (reader.Read())
            {
                string term = reader.GetString(0);
                int ndoc = reader.GetInt32(1);
                int nentry = reader.GetInt32(2);

                terms[term] = new TermStatistics(ndoc, nentry);
                corpusTokenCount += nentry;
            }
        }

        return new PostgresBm25Statistics(documentCount, corpusTokenCount, terms);
    }

    /// <summary>One term's two counts, kept together so they cannot be read apart.</summary>
    private readonly record struct TermStatistics(int DocumentFrequency, int CorpusFrequency);
}
