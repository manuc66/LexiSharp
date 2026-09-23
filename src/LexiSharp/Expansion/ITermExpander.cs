using System.Collections.Generic;

namespace LexiSharp.Expansion;

/// <summary>
/// Derives the additional terms to index for a document from its own tokenization.
/// </summary>
/// <remarks>
/// This is the seam behind the « semantic lexical index »: a document is indexed under its
/// literal terms <em>plus</em> a handful of conceptually associated terms carrying a weak
/// weight — the same idea as learned sparse representations (SPLADE and its descendants),
/// without requiring a neural model. Anything can be plugged in: a corpus-statistics model
/// (see <see cref="PmiTermExpander"/>), a synonym/thesaurus-backed generator, a consumer-side
/// embedding model, or a later integration of a real SPLADE model.
/// </remarks>
public interface ITermExpander
{
    /// <summary>
    /// Returns the expansion terms for the given document terms. Implementations must yield
    /// distinct terms, never echo an input term, and should keep the set small (a few terms)
    /// so the injected tokens stay a weak complement to the literal text.
    /// </summary>
    /// <param name="terms">The tokenization of a document's text, in document order.</param>
    IReadOnlyCollection<ExpandedTerm> Expand(IReadOnlyList<string> terms);
}