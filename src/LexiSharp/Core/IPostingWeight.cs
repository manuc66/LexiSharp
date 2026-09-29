namespace LexiSharp.Core;

/// <summary>
/// A query plan's per-term contribution, evaluated once per posting entry during a
/// term-at-a-time accumulation pass.
/// </summary>
/// <remarks>
/// <para>
/// The contract that makes a scorer eligible for the term-at-a-time path is that a term's
/// contribution to a document depends on nothing but that document's term frequency and length —
/// the document-wide figures and the query-wide constants are already folded in. BM25, BM25+,
/// BM25L and TF-IDF all have this shape, which is why one walk over the inverted lists can replace
/// the document-at-a-time loop that resolved a document id per (document, term) pair.
/// </para>
/// <para>
/// <b>Bit-exactness is the whole point.</b> The contribution a weight returns for a given
/// (term, tf, length) must be the identical <see cref="double"/> the scorer's own per-document
/// loop would have added for that term. Summing the terms in query order — which the accumulation
/// pass does, one posting list at a time — then reproduces the document-at-a-time sum
/// operation-for-operation, and therefore bit for bit. That is what keeps the accumulated ranking
/// equal to the one the engine used to compute, ties included, and ties are what the result order
/// is decided by.
/// </para>
/// <para>
/// Implemented by a <c>struct</c> so the accumulation loop can be generic over it: the JIT
/// specializes the call and inlines <see cref="Weight"/>, which is the difference between a few
/// nanoseconds per posting entry and a virtual call.
/// </para>
/// </remarks>
internal interface IPostingWeight
{
    /// <summary>The query term this weight applies to.</summary>
    string Term { get; }

    /// <summary>
    /// The term's contribution to a document that contains it, from that document's term
    /// frequency and token count.
    /// </summary>
    double Weight(int termFrequency, int documentLength);
}
