namespace LexiSharp.Core;

/// <summary>
/// Computes a weight for each term of a tokenized query, before the query reaches a scorer.
/// </summary>
/// <remarks>
/// The seam exists because a scorer sums its per-term contributions and, without it, has no way to
/// say that one query term matters less than another. A weighter is a <b>query-level</b> strategy:
/// it sees the whole query and returns one weight per distinct term, and a scorer that accepts
/// weights multiplies its term contribution by them.
/// <para>
/// Weights are relative, not absolute: a weighter should normalize so the most important term has
/// weight <c>1</c>, which keeps the ranking the only thing they change and leaves the score on the
/// scale the corpus statistics already put it on. A term the weighter says nothing about is scored
/// at <c>1</c>, so an incomplete table degrades to plain BM25 rather than to a silent zero.
/// </para>
/// </remarks>
public interface IQueryTermWeighter
{
    /// <summary>
    /// Returns a weight for each term of the query. Terms absent from the returned map are scored
    /// at <c>1</c>; a term present with a weight of <c>0</c> contributes nothing.
    /// </summary>
    IReadOnlyDictionary<string, double> Weigh(IReadOnlyList<string> terms);
}

/// <summary>
/// A scorer that can be rebuilt with per-query term weights, which is what lets a decorator apply
/// an <see cref="IQueryTermWeighter"/> without knowing which scorer it wraps.
/// </summary>
/// <remarks>
/// The returned scorer is a new, independent instance: a scorer is shared by every query an engine
/// runs, and weights are a property of one query, so mutating the prototype would be a data race
/// between concurrent searches. The prototype is never changed.
/// </remarks>
public interface IWeightedQueryScorer : ITextScorer
{
    /// <summary>
    /// Returns a scorer identical to this one except that each query term's contribution is
    /// multiplied by its weight from <paramref name="weights"/>.
    /// </summary>
    ITextScorer WithTermWeights(IReadOnlyDictionary<string, double> weights);
}
