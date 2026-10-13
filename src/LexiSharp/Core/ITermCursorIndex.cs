namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that can resolve a term once and then answer that term's frequency at any
/// document without resolving it again.
/// </summary>
/// <remarks>
/// <para>
/// A scorer that is not a sum of posting-local contributions — query likelihood is the one — asks the
/// index for a term's frequency per candidate. <see cref="IReadOnlyTextIndex.TermFrequency"/> resolves
/// the term on every call, and on a store that resolution is the term table's binary search: measured
/// at 100,000 terms, it is 69% of the call (15.9 µs of 23.1 µs for twenty look-ups), while the seek and
/// the block walk are the other 31%.
/// </para>
/// <para>
/// The plan asks for the same terms on every candidate, so resolving each once per query removes that
/// share without touching the format. The cursor is per term and per query: it holds whatever the
/// index needed to answer without searching, and it is discarded with the plan.
/// </para>
/// </remarks>
internal interface ITermCursorIndex
{
    /// <summary>
    /// Resolves <paramref name="term"/> to a cursor, or returns <c>null</c> when it is out of
    /// vocabulary. The caller owns the cursor and discards it with the query.
    /// </summary>
    ITermCursor? ResolveTerm(string term);
}

/// <summary>One term, resolved once, answering its frequency at any document.</summary>
internal interface ITermCursor
{
    /// <summary>The term's frequency in <paramref name="documentId"/>, or <c>0</c> when it is not held.</summary>
    int Frequency(string documentId);
}
