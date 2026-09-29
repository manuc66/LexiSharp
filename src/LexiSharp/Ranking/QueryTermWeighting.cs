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
/// <b>Where it is applied, and the trap that made it look broken for a release.</b> The setting is
/// resolved once per search, in the scorer's query plan, so what has to reach the plan is the
/// <i>raw</i> term list with its repetitions intact. The engine deduplicates too, for its own two
/// decisions — how much of the corpus the query can reach, and which candidate documents to
/// enumerate — and those need each term counted once, so the two lists are deliberately different
/// and neither substitutes for the other. Handing the plan the deduplicated list makes this setting
/// inert, and silently so: every scorer's term resolution short-circuits on a list it can prove is
/// already distinct, so the two settings produce bit-identical rankings with nothing to indicate
/// anything is wrong. That is what happened, and it is why
/// <c>QueryTermWeightingTests</c> pins the setting's visibility as well as its meaning.
/// </para>
/// <para>
/// Measured on ArguAna, where every test query is a whole ~200-word argument and its content words
/// repeat; 1,406 queries, <c>--analyzer english</c>, k1=0.9/b=0.4, nDCG@10:
/// <c>Distinct</c> 0.219 and <c>QueryFrequency</c> 0.271, so the setting is worth <b>+0.052</b> there.
/// The linear gain convention gives the same two numbers to three decimals, so the effect is not an
/// artefact of the metric convention. On NFCorpus and SciFact, whose queries are short, it changes
/// nothing to four decimals, because they barely repeat. So the setting is free where queries are
/// keywords and worth five points of nDCG where a query is a document.
/// </para>
/// <para>
/// <b>A larger figure this repository used to publish for it, and has withdrawn.</b> Versions up to
/// and including 0.6.0 claimed <c>+0.0705</c> here, and 0.2902 and 0.4061 in absolute terms. Neither
/// is reproducible, and the reason is structural rather than numerical: the harness flag fed one
/// constructor argument and nothing else, and the engine it reached deduplicated the query before
/// the scorer, so the two settings could not have produced different rankings. The figures could not
/// have come from the code that cited them. The measured values above replace them.
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
