using System.Collections.Generic;

namespace LexiSharp.Expansion;

/// <summary>
/// A single additional term injected into the index for a document, alongside its source text
/// tokenization. The term is indexed as a regular inverted-list member (one occurrence at a
/// synthetic position), so the stock scorers — BM25, TF-IDF, query likelihood — can match
/// documents that never mention the term literally.
/// </summary>
/// <remarks>
/// <see cref="Weight"/> is informational: it reflects how strongly the source corpus associated
/// the term with the document's own terms. The index itself is not impact-weighted, so the
/// weight does not multiply the score; it is exposed so callers can inspect or threshold the
/// expansion set.
/// </remarks>
/// <param name="Term">The additional term to index.</param>
/// <param name="Weight">Association strength in <c>[0, 1)</c> (higher = more related).</param>
public readonly record struct ExpandedTerm(string Term, double Weight);