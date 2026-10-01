namespace LexiSharp.Ranking;

/// <summary>
/// The precision BM25's arithmetic is carried out in.
/// </summary>
/// <remarks>
/// <see cref="Double"/> is what this library computes in and what it should compute in: the per-term
/// contributions and their sum both carry far more significant figures than a score is ever read at, so
/// the extra precision is free and the results are the same to every digit a reader can see.
/// <para>
/// <see cref="SinglePrecision"/> is narrower on purpose. One implementation of BM25 computes each
/// term's contribution in single precision, sums those into a double, and rounds the total once; its
/// length normalisation is a 256-entry table of single-precision reciprocals indexed by the stored
/// length byte, rather than an expression evaluated per document. That is the same mathematics and a
/// different arithmetic, and the two disagree in the sixth significant figure — which is invisible in a
/// ranking and visible in a score.
/// </para>
/// <para>
/// This is here to reproduce a recorded score, not because single precision is better. Turning it on
/// costs accuracy in exchange for agreement, and the only reason to pay that is a comparison against
/// numbers produced that way.
/// </para>
/// </remarks>
public enum Bm25Arithmetic
{
    /// <summary>Per-term contributions and their sum in double precision. The default.</summary>
    Double = 0,

    /// <summary>
    /// Each term's contribution in single precision, the sum in double, and the total rounded to single
    /// precision once — plus the single-precision reciprocal table for the length normalisation.
    /// </summary>
    SinglePrecision = 1,
}