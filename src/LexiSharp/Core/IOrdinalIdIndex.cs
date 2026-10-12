namespace LexiSharp.Core;

/// <summary>
/// Internal capability for an index that can answer a document's <b>id</b> from an ordinal without
/// materializing the document, when the two cost different things.
/// </summary>
/// <remarks>
/// <para>
/// The page window breaks a score tie by comparing document ids, and it materializes a page row only for
/// the entries it keeps. An in-memory index pays a list index either way, so the distinction is invisible
/// there. A segment does not: its <c>DocumentAt</c> decodes the stored id <i>and</i> text, 536 bytes on a
/// 50-token document, where the id alone is a reference to a string the reader already holds from open.
/// The window resolves an id for every comparison it makes on a tied candidate set and a document only
/// for the rows it keeps, so on a corpus whose documents tie the difference is the whole per-query
/// allocation.
/// </para>
/// <para>
/// Internal on purpose, for the same reason as <see cref="IAccumulatingIndex"/>: it is a performance
/// capability, not a promise to library consumers. An index that does not implement it keeps working —
/// the window falls back to resolving the document.
/// </para>
/// </remarks>
internal interface IOrdinalIdIndex
{
    /// <summary>
    /// The id of the document at <paramref name="ordinal"/>, or <c>null</c> when that slot is free. The
    /// same string <c>DocumentAt(ordinal).Id</c> would report, and the same instance when the index keeps
    /// one.
    /// </summary>
    string? IdAt(int ordinal);
}
