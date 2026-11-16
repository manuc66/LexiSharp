using LexiSharp.Core;

namespace LexiSharp.Postgres;

/// <summary>One re-ranked match: its document, its term frequencies and its length.</summary>
internal sealed record PostgresBm25Candidate(
    SearchDocument Document,
    IReadOnlyDictionary<string, int> TermFrequencies,
    int Length);

/// <summary>
/// The candidate set of one search, presented as an <see cref="IReadOnlyTextIndex"/> so
/// <see cref="Ranking.Bm25Scorer"/> can score it.
/// </summary>
/// <remarks>
/// <para>
/// <c>DocumentFrequency</c>, <c>N</c> and the average length answer from the corpus snapshot;
/// <c>TermFrequency</c> and <c>DocumentLength</c> answer from the candidate row's own
/// <c>tsvector</c>. That split is what makes the score a corpus statistic rather than a statistic
/// of the page: computing idf over the candidates would give a rare term the frequency of
/// "however many of the top 1,000 happened to contain it".
/// </para>
/// <para>
/// Positions are not kept. Nothing on the BM25 path reads them, and a caller that asks gets
/// <see cref="NotSupportedException"/> rather than an empty list that would read as "the term
/// occurs nowhere".
/// </para>
/// </remarks>
internal sealed class PostgresBm25IndexView : IReadOnlyTextIndex
{
    private readonly PostgresBm25Statistics _statistics;
    private readonly IReadOnlyList<PostgresBm25Candidate> _candidates;
    private readonly Dictionary<string, PostgresBm25Candidate> _byId;

    public PostgresBm25IndexView(PostgresBm25Statistics statistics, IReadOnlyList<PostgresBm25Candidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(statistics);
        ArgumentNullException.ThrowIfNull(candidates);

        _statistics = statistics;
        _candidates = candidates;
        _byId = candidates.ToDictionary(candidate => candidate.Document.Id, StringComparer.Ordinal);
    }

    /// <inheritdoc />
    public IReadOnlyCollection<SearchDocument> Documents =>
        _candidates.Select(candidate => candidate.Document).ToList();

    /// <inheritdoc />
    public int Count => _candidates.Count;

    /// <inheritdoc />
    public double AverageDocumentLength => _statistics.AverageDocumentLength;

    /// <inheritdoc />
    public int StatisticDocumentCount => _statistics.DocumentCount;

    /// <inheritdoc />
    public int VocabularySize => _statistics.VocabularySize;

    /// <inheritdoc />
    public long CorpusTokenCount => _statistics.CorpusTokenCount;

    /// <inheritdoc />
    public bool Contains(string documentId) =>
        documentId is not null && _byId.ContainsKey(documentId);

    /// <inheritdoc />
    public IReadOnlyList<string> GetTerms(string documentId) =>
        Candidate(documentId).TermFrequencies.Keys.OrderBy(term => term, StringComparer.Ordinal).ToList();

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always: frequencies are kept, positions are not.</exception>
    public IReadOnlyList<int> GetTermPositions(string documentId, string term) =>
        throw new NotSupportedException(
            "The BM25 pool keeps per-term frequencies, not positions: a phrase is enforced by the "
            + "tsquery that selected the candidates, not re-checked while scoring.");

    /// <inheritdoc />
    public int DocumentFrequency(string term) =>
        term is null ? 0 : _statistics.DocumentFrequency(term);

    /// <inheritdoc />
    public int CorpusFrequency(string term) =>
        term is null ? 0 : _statistics.CorpusFrequency(term);

    /// <inheritdoc />
    public int TermFrequency(string documentId, string term) =>
        term is not null && Candidate(documentId).TermFrequencies.TryGetValue(term, out int frequency)
            ? frequency
            : 0;

    /// <inheritdoc />
    public int DocumentLength(string documentId) => Candidate(documentId).Length;

    /// <inheritdoc />
    public bool TryGetDocument(string documentId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out SearchDocument? document)
    {
        if (documentId is not null && _byId.TryGetValue(documentId, out PostgresBm25Candidate? candidate))
        {
            document = candidate.Document;
            return true;
        }

        document = null;
        return false;
    }

    private PostgresBm25Candidate Candidate(string documentId)
    {
        ArgumentNullException.ThrowIfNull(documentId);

        return _byId.TryGetValue(documentId, out PostgresBm25Candidate? candidate)
            ? candidate
            : throw new ArgumentException(
                $"Document '{documentId}' is not in this search's candidate pool.", nameof(documentId));
    }
}
