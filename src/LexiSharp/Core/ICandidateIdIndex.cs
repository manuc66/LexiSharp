namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that can enumerate the <b>ids</b> of the documents sharing at least one of
/// the given terms, rather than the documents themselves.
/// </summary>
/// <remarks>
/// <para>
/// The engine's candidate loop needs a candidate's id to score it and its ordinal to place it in the
/// window. It needs the <i>document</i> only when a metadata filter or a facet counter reads one, and
/// only the page's documents are ever returned. An index that materializes a candidate's document
/// therefore decodes its text for every candidate to hand the engine something it reads one field of.
/// </para>
/// <para>
/// For a store that is the difference between decoding the candidate set and decoding the page:
/// measured on a 20,000-document store, a query-likelihood search — which takes the candidate path
/// because its score is not a sum of posting-local contributions — decoded every candidate, and its
/// latency was dominated by that rather than by scoring. An index in that position implements this,
/// and the engine pairs it with <see cref="IOrdinalIdIndex"/> to resolve ids from ordinals and defers
/// the document to the page cut, which <c>TopRankedWindow</c> already supports.
/// </para>
/// </remarks>
internal interface ICandidateIdIndex
{
    /// <summary>
    /// The ids of the documents containing at least one of <paramref name="terms"/>, each once, in
    /// ascending ordinal order — which is corpus order, the order <see cref="IReadOnlyTextIndex.Documents"/>
    /// enumerates in.
    /// </summary>
    IEnumerable<string> GetCandidateIds(IReadOnlyList<string> terms);
}
