namespace LexiSharp.Core;

/// <summary>
/// Capability for an index that can enumerate the terms starting with a prefix without reading the
/// terms before or after it.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IVocabularyIndex.Vocabulary"/> is every term, and a prefix expansion that walks it reads
/// the whole dictionary to find its matches. On a store that walk decodes each term from the segment —
/// measured at 100,000 terms, a prefix query allocated 6.9 MB against the in-memory index's 4.5 KB.
/// </para>
/// <para>
/// The dictionary is sorted by code point, which is what makes the matches a contiguous range: an index
/// that keeps it sorted can find the prefix's first term by binary search and stop at the first term
/// that no longer starts with it. This is the same technique the term look-up uses, applied to a range
/// instead of a point.
/// </para>
/// </remarks>
internal interface IVocabularyPrefixIndex
{
    /// <summary>
    /// The terms starting with <paramref name="prefix"/>, in code-point order, each once.
    /// </summary>
    IEnumerable<string> TermsWithPrefix(string prefix);
}
