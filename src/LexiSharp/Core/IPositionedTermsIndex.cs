namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that can report a document's tokens and the position each was indexed at
/// in one call, rather than one term's positions at a time.
/// </summary>
/// <remarks>
/// <para>
/// The phrase gate needs the positions of every term of every phrase it is checking. An index that
/// <i>stores</i> positions answers one term at a time in a dictionary read, which is cheap and is what
/// <see cref="IReadOnlyTextIndex.GetTermPositions"/> is for. A store keeps no positions and recomputes
/// them by tokenizing the document, so a call per term pays a full tokenization per term: the gate's
/// whole cost, and measured at 494 MB for one two-term phrase query over a 20,000-document store.
/// </para>
/// <para>
/// An index in that position implements this instead, and the engine tokenizes the document once and
/// evaluates every phrase of the query against the result. The sequence is the same one
/// <see cref="IReadOnlyTextIndex.GetTermPositions"/> reports from, in document order and with each text
/// field's tokens after the body at the field gap, so a phrase matches in exactly the same places.
/// </para>
/// </remarks>
internal interface IPositionedTermsIndex
{
    /// <summary>
    /// The document's tokens paired with the position each was indexed at, in document order, or an
    /// empty list when the document is not held.
    /// </summary>
    IReadOnlyList<(string Term, int Position)> GetPositionedTerms(string documentId);
}
