namespace LexiSharp.Core;

/// <summary>
/// One query term's contribution to a scorer's theoretical upper bound: the corpus figures the bound
/// was read from, and the bound itself.
/// </summary>
/// <remarks>
/// The per-term figures are reported rather than only their sum because a caller checking a bound
/// needs to know which term made it loose. A term whose largest frequency sits in a long document and
/// whose shortest document carries it once gives a much wider interval than one whose occurrences are
/// concentrated, and the two are indistinguishable in a total.
/// </remarks>
/// <param name="Term">The normalized term, as the index stores it.</param>
/// <param name="DocumentFrequency">Number of documents carrying the term; zero when the corpus does not know it.</param>
/// <param name="MaxTermFrequency">Largest term frequency in one document; zero for an absent term.</param>
/// <param name="MinDocumentLength">Smallest token count among the documents carrying the term.</param>
/// <param name="InverseDocumentFrequency">
/// The rarity the scorer assigned the term, including any per-query weight.
/// </param>
/// <param name="MaxContribution">
/// The largest contribution the term can make to one document's score, by the scorer's own formula.
/// </param>
public readonly record struct TermScoreBound(
    string Term,
    int DocumentFrequency,
    int MaxTermFrequency,
    int MinDocumentLength,
    double InverseDocumentFrequency,
    double MaxContribution);

/// <summary>
/// A scorer's theoretical upper bound for one query: no document in the corpus can score above
/// <see cref="UpperBound"/>, and each term's share of that bound is listed.
/// </summary>
/// <remarks>
/// <para>
/// The sum is over query terms, so it bounds a document that achieved every term's individual maximum
/// at once. No such document need exist, which is what makes the bound safe and sometimes loose: the
/// gap is measured, not assumed, before a normalizer is trusted with it.
/// </para>
/// <para>
/// <see cref="UpperBound"/> is not a probability and is not normalized. Dividing a score by it is the
/// job of a score normalizer, and is meaningful only for the scorer and parameters that produced it —
/// which is why <see cref="ScorerName"/> travels with the number.
/// </para>
/// </remarks>
/// <param name="ScorerName">The scorer that produced the bound, e.g. <c>"BM25"</c>.</param>
/// <param name="UpperBound">No document's score can exceed this value for the query.</param>
/// <param name="Terms">
/// One entry per scored query term, in query order. A term the query repeats appears once per
/// occurrence when the scorer counts occurrences, because the bound sums those occurrences too;
/// a scorer that deduplicates reports it once.
/// </param>
public readonly record struct QueryScoreBound(
    string ScorerName,
    double UpperBound,
    IReadOnlyList<TermScoreBound> Terms)
{
    /// <summary>The bound of a scorer that has nothing to score: zero, and no terms.</summary>
    public static readonly QueryScoreBound Empty = new(string.Empty, 0, Array.Empty<TermScoreBound>());
}

/// <summary>
/// Optional capability of an <see cref="ITextScorer"/> that can state a query's theoretical upper
/// bound from corpus statistics alone, without scoring a document.
/// </summary>
/// <remarks>
/// <para>
/// Implementing this interface is opt-in. A scorer that does not implement it still ranks; what it
/// cannot do is answer a normalizer that wants to divide by something other than the largest score the
/// page happened to return.
/// </para>
/// <para>
/// <b>The contract is a bound, not an estimate.</b> For every document the corpus holds, the scorer's
/// own <see cref="ITextScorer.Score"/> must be at or below the reported total — including the
/// arithmetic's rounding. A bound that a single document can exceed is a defect, not a loose
/// approximation, because a normalizer divides by it and the result is presented as a score in
/// <c>[0, 1]</c>.
/// </para>
/// <para>
/// The bound is built from <see cref="IReadOnlyTextIndex.TryGetTermFrequencyBound"/>. An index that
/// cannot answer that question makes the scorer decline, rather than publish a bound computed from a
/// guess about the posting lists.
/// </para>
/// </remarks>
public interface IScoreBoundScorer : ITextScorer
{
    /// <summary>
    /// Computes the query's theoretical upper bound, or returns <c>false</c> when this scorer or this
    /// index cannot answer.
    /// </summary>
    /// <param name="terms">The terms the search would score, already tokenized and normalized.</param>
    /// <param name="index">The corpus the bound is read from.</param>
    /// <param name="bound">The bound on success; <c>default</c> on failure.</param>
    /// <returns><c>true</c> when <paramref name="bound"/> is a valid upper bound for this query.</returns>
    bool TryGetQueryBound(
        IReadOnlyList<string> terms,
        IReadOnlyTextIndex index,
        out QueryScoreBound bound);
}

/// <summary>
/// Optional capability of an <see cref="ITextSearchEngine"/> that can report the theoretical upper
/// bound of the ranking it would produce for a query.
/// </summary>
/// <remarks>
/// <para>
/// A hybrid can use it to normalize a lexical engine's scores by a quantity that does not depend on
/// how many candidates the page happened to contain. The bound is computed from the same tokenizer and
/// scorer the engine searches with, so it describes the scores the engine actually returns.
/// </para>
/// <para>
/// <b>Queries the engine rewrites are declined.</b> A phrase, a prefix or fuzzy expansion, a required
/// or prohibited operator and a field scope all change which terms score; the bound for the literal
/// text would not be the bound for the ranking. Those queries return <c>false</c> and the normalizer
/// falls back to the historical page maximum, which is a looser reading but never a wrong one.
/// </para>
/// </remarks>
public interface IScoreBoundSearchEngine : ITextSearchEngine
{
    /// <summary>
    /// Reports the upper bound of the ranking this engine would return for <paramref name="query"/>.
    /// </summary>
    /// <param name="query">Raw query text.</param>
    /// <param name="options">
    /// The options the search would use, because <see cref="SearchOptions.ParseQuerySyntax"/> and
    /// <see cref="SearchOptions.KnownFields"/> decide whether the query carries syntax at all.
    /// </param>
    /// <param name="bound">The bound on success; <c>default</c> on failure.</param>
    /// <returns>
    /// <c>true</c> when the engine can bound this query; <c>false</c> for an empty corpus, a query
    /// with syntax, or a scorer that does not implement <see cref="IScoreBoundScorer"/>.
    /// </returns>
    bool TryGetQueryBound(
        string query,
        SearchOptions? options,
        out QueryScoreBound bound);
}
