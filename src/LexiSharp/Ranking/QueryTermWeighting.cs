namespace LexiSharp.Ranking;

/// <summary>
/// How a scorer treats a term that occurs more than once in a query.
/// </summary>
/// <remarks>
/// This is a ranking decision that is easy to mistake for an implementation detail, and it is worth
/// a lot on some corpora.
/// <para>
/// <see cref="Distinct"/> scores a repeated query term once, the way a bag of words is read:
/// « machine learning » is two terms, however many times each is typed. That is the natural reading
/// of a search query, and the default here for that reason — it is also what most published BM25
/// descriptions of the formula assume, because the formula is usually written over distinct terms.
/// </para>
/// <para>
/// <see cref="QueryFrequency"/> scores it once per occurrence, so a term typed three times counts
/// three times, which is how a query is scored as a bag of repeated words rather than a set of
/// them. Reproducing a published BM25 figure therefore needs this setting, and nothing else in this
/// library can substitute for it: a reference implementation issues one scoring clause per
/// query-token occurrence and sums them, so repetition in the query multiplies that term's weight.
/// </para>
/// <para>
/// Measured, with the analysis and metric convention aligned to that reference, on the three BEIR
/// corpora at k1=0.9/b=0.4: ArguAna (each test query is a whole argument) 0.2197 against 0.2902 —
/// and 0.4061 at k1=3.0, against the 0.3970 that implementation publishes. NFCorpus 0.3215 against
/// 0.3215 and SciFact 0.6788 against 0.6788: unchanged to four decimals, because their queries are
/// short and barely repeat. So the setting is free where queries are keywords and decisive where a
/// query is a document.
/// </para>
/// </remarks>
public enum QueryTermWeighting
{
    /// <summary>
    /// Score each distinct query term once, whatever its number of occurrences. The default: a
    /// query is read as a bag of words.
    /// </summary>
    Distinct = 0,

    /// <summary>
    /// Score a query term once per occurrence, so repetition in the query raises that term's
    /// weight. What a reference implementation does, and what reproducing a published figure
    /// requires.
    /// </summary>
    QueryFrequency = 1,
}
