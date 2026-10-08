namespace LexiSharp.Core;

/// <summary>
/// The query-bound half of a term's contribution, for a term whose postings are already resolved.
/// </summary>
/// <remarks>
/// <see cref="IPostingWeight"/> names the term it wants folded; this names nothing, because the
/// caller has resolved the term and holds the postings. That is the whole difference: a weight over
/// resolved postings carries no string, which is what lets a term that is its own normalized form go
/// from tokenizer to accumulator without becoming one.
/// </remarks>
internal interface IResolvedWeight
{
    /// <summary>The contribution of one posting entry, from the term's frequency in the document and the document's length.</summary>
    double Weight(int termFrequency, int documentLength);
}
