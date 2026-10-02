using System.Collections.Generic;

namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that can hand back the documents containing at least one of the given
/// query terms in whatever order its posting lists happen to produce, instead of paying to
/// restore corpus order.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ICandidateIndex.GetCandidateDocuments"/> documents that its result appears in the
/// same relative order as <see cref="IReadOnlyTextIndex.Documents"/>, and that is a real contract its
/// callers may rely on. Restoring that order means re-walking the whole corpus after the union
/// of the posting lists, which costs O(corpus) no matter how few documents actually matched:
/// measured on a 10,000-document index, 0.001 ms for the union against 0.242 ms for the union
/// plus the ordering pass, for a query whose candidate set was 35 documents.
/// </para>
/// <para>
/// A consumer that does not care about the order should ask for this instead. The search engine
/// qualifies: <c>RankedTextSearchEngine</c> cuts its page with <c>TopRankedWindow</c>, whose
/// order is total — score descending, ties broken by ordinal document id — so the set of
/// documents it keeps, and the page it writes, do not depend on the order candidates were
/// produced in. Its own documentation says so; this interface exists so that fact can be acted
/// on instead of only stated.
/// </para>
/// <para>
/// Internal on purpose, and for the same reason as <c>IQueryPlannableScorer</c>: it is a
/// performance capability, not a promise to library consumers. An index that does not implement
/// it keeps working, through the ordered public path.
/// </para>
/// </remarks>
internal interface IUnorderedCandidateIndex : ICandidateIndex
{
    /// <summary>
    /// Union of the documents containing at least one of <paramref name="terms"/>, each at most
    /// once, in an unspecified order. Same set as
    /// <see cref="ICandidateIndex.GetCandidateDocuments"/>, cheaper to produce.
    /// </summary>
    IEnumerable<SearchDocument> GetCandidatesUnordered(IReadOnlyList<string> terms);
}
