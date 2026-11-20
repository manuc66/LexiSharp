namespace LexiSharp.Core;

/// <summary>
/// What the corpus says about one query term: how many documents carry it, and how rare the
/// active scorer considers it.
/// </summary>
/// <remarks>
/// <para>
/// This is the query-level counterpart of <see cref="TermContribution"/>, and the difference is
/// the point. A contribution says what a term did to <em>one document's</em> score, so it only
/// exists for a term that document carries. A statistic says what the term is <em>before any
/// document is scored</em>, so it answers for a term no document carries — which is exactly the
/// term a caller needs to look at when a query returns nothing.
/// </para>
/// <para>
/// <b>Why an agent wants it.</b> A retrieval loop decides its next move from how much of the
/// corpus the last query reached. A term with a document frequency of 2 in a corpus of 50,000
/// makes the query restrictive whatever it scored, and a term present in half the corpus cannot
/// discriminate at all; without the figures the caller can only infer both from a hit count that
/// is capped by the page size. None of these numbers require a search to produce — they are read
/// off the index — so exposing them costs one vocabulary walk per query term and no ranking work.
/// </para>
/// </remarks>
/// <param name="Term">The normalized term, as the index stores it.</param>
/// <param name="DocumentFrequency">Number of documents whose text carries the term; <c>0</c> means the term is absent from the corpus.</param>
/// <param name="DocumentCount">
/// The document count the frequency is read against — <see cref="IReadOnlyTextIndex.StatisticDocumentCount"/>,
/// the same <c>N</c> the scorer's inverse document frequency divides by, so
/// <see cref="DocumentShare"/> and <see cref="InverseDocumentFrequency"/> never disagree about
/// which corpus they describe.
/// </param>
/// <param name="InverseDocumentFrequency">
/// The rarity the active scorer assigns the term, or <c>null</c> when that scorer has no such
/// quantity (query-likelihood scores a collection probability, not an idf) or cannot report one
/// without scoring a document.
/// <para>
/// It is the scorer's own value, not a library-wide convention: <c>Bm25Scorer</c> reports
/// <c>ln(1 + (N − df + 0.5) / (df + 0.5))</c> and <c>TfIdfScorer</c> reports its smoothed
/// <c>log((N + 1) / (df + 1)) + 1</c>. Two scorers therefore disagree here exactly as they
/// disagree in <see cref="TermContribution.InverseDocumentFrequency"/> — a caller comparing them
/// is comparing scoring functions, not corpora. For a corpus-fact comparison that does not depend
/// on the scorer, read <see cref="DocumentShare"/>.
/// </para>
/// </param>
public sealed record QueryTermStatistic(
    string Term,
    int DocumentFrequency,
    int DocumentCount,
    double? InverseDocumentFrequency)
{
    /// <summary>
    /// The fraction of the corpus that carries the term — <see cref="DocumentFrequency"/> divided
    /// by <see cref="DocumentCount"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>0</c> for a term no document carries, and <c>1</c> for one every document carries. It is
    /// the scorer-independent reading of rarity: monotone in, and as informative as, any of the
    /// idf conventions, and comparable across scorers and across corpora of different sizes.
    /// </para>
    /// <para>
    /// <c>0</c> also for an empty corpus, where the fraction is undefined — there is no document
    /// that could carry the term, so "none of them do" is the honest answer rather than a
    /// division by zero.
    /// </para>
    /// </remarks>
    public double DocumentShare => DocumentCount <= 0 ? 0 : DocumentFrequency / (double)DocumentCount;

    /// <summary>Whether no document in the corpus carries the term.</summary>
    /// <remarks>
    /// The one statistic that is a verdict rather than a measurement: a query whose every term is
    /// absent cannot match anything, whatever the scorer would assign it.
    /// </remarks>
    public bool IsAbsent => DocumentFrequency == 0;
}

/// <summary>
/// Optional capability of an <see cref="ITextScorer"/> that can describe a query's terms from the
/// corpus statistics alone, without scoring a document.
/// </summary>
/// <remarks>
/// <para>
/// Implementing this interface is opt-in. A scorer that does not implement it still ranks; what it
/// cannot do is say what its inverse document frequency would be, and the caller then sees
/// <see cref="QueryTermStatistic.InverseDocumentFrequency"/> as <c>null</c> instead of a number
/// the scorer did not produce.
/// </para>
/// <para>
/// <b>The contract is the scorer's Explain, one document earlier.</b> For a term the index holds,
/// <see cref="DescribeTerms"/> must report the same idf that <see cref="IScoreExplainer.Explain"/>
/// reports for it as a <see cref="TermContribution"/> — the number a caller sees must not depend on
/// which of the two doors they came through. Implemented by the scorers that already explain
/// themselves: <c>Bm25Scorer</c>, <c>Bm25PlusScorer</c>, <c>Bm25LScorer</c>, <c>Bm25VaScorer</c>,
/// <c>Bm25FScorer</c> and <c>TfIdfScorer</c>.
/// </para>
/// <para>
/// Per-query term weights (<see cref="IWeightedQueryScorer.WithTermWeights"/>) are not applied
/// here: those weights belong to one search, and a prototype scorer is shared by every query an
/// engine runs.
/// </para>
/// </remarks>
public interface IQueryTermStatistics : ITextScorer
{
    /// <summary>
    /// Returns one statistic per distinct term of <paramref name="terms"/>, in the order the terms
    /// first occur.
    /// </summary>
    /// <param name="terms">
    /// The terms a search would score, already tokenized and normalized. Duplicates describe one
    /// term once.
    /// </param>
    /// <param name="index">The corpus the statistics are read from.</param>
    /// <returns>One entry per distinct term; a term absent from the corpus keeps its entry, with a frequency of <c>0</c>.</returns>
    IReadOnlyList<QueryTermStatistic> DescribeTerms(IReadOnlyList<string> terms, IReadOnlyTextIndex index);
}
