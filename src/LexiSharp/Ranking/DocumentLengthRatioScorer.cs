using LexiSharp.Core;

namespace LexiSharp.Ranking;

/// <summary>
/// A document's length relative to the corpus average: <c>|D| / avgdl</c>, where both figures come
/// from the index. One for an average document, above one for a long one, below for a short one.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is a component, not a relevance scorer.</b> Used alone it ranks documents by length and
/// by nothing else — it reads no query term and matches nothing. What it is for is being weighted
/// inside a score that does the matching: a <b>negative</b> weight makes it a length prior (long
/// documents lose), a <b>positive</b> one a length preference (they win), and <c>0</c> removes it.
/// The weight belongs to whoever combines the components, not here.
/// </para>
/// <para>
/// <b>The unit is always the document.</b> The two figures are the index's
/// <see cref="IReadOnlyTextIndex.DocumentLength"/> and
/// <see cref="IReadOnlyTextIndex.AverageDocumentLength"/>, so the ratio means the same thing in
/// every configuration that reads the same index. That is what makes it usable as a fixed term
/// across an A/B: an index built over windows is a different index with its own average length,
/// and a length prior measured against it answers a different question than the same prior measured
/// against whole documents — which is the question, not a caveat about it.
/// </para>
/// <para>
/// <b>How this differs from BM25's <c>b</c>.</b> A length term inside the saturation factor damps
/// the contribution of a query term <i>in a document that matched</i>, and the damping scales with
/// how much that term actually occurred. This term is a property of the document alone: it is added
/// to every document's score, whether the document matched or not, and it does not scale with any
/// term. A corpus can therefore rank two documents that matched identically differently under this
/// term, which is the effect a length prior has and a length normalization does not.
/// </para>
/// <para>
/// <b>No centred variant, deliberately.</b> Subtracting a constant from every document's score
/// cannot change the order they come back in, so a centred form would rank identically to the
/// uncentred one for any weight and would be surface with no behaviour behind it. The raw ratio is
/// the whole of it.
/// </para>
/// <para>
/// <b>Deliberately not an <see cref="ITermOverlapScorer"/>.</b> A document matching no query term
/// still has a length, and this scorer returns a score for it — which is the property the component
/// exists for, and also why the engine cannot skip non-matching candidates on its behalf.
/// </para>
/// <para>
/// <b>No <see cref="IScoreExplainer"/>.</b> The score depends on no query term, so there is no
/// per-term contribution to report, and every other explainer keeps
/// <see cref="ScoreExplanation.TotalScore"/> equal to the sum of its
/// <see cref="TermContribution"/>s. An explanation whose total came from nowhere and whose terms
/// were all absent would break that invariant to describe nothing, so this scorer simply does not
/// explain itself. Reach for the explanation of the scorer that did the matching.
/// </para>
/// </remarks>
public sealed class DocumentLengthRatioScorer : ITextScorer
{
    /// <inheritdoc />
    public string Name => "LengthRatio";

    /// <inheritdoc />
    public double Score(string documentId, IReadOnlyList<string> queryTerms, IReadOnlyTextIndex index)
    {
        ArgumentNullException.ThrowIfNull(index);

        // The query is not read, and the check is here anyway: the contract says a scorer is handed
        // one, and a null means the caller broke it. Silently tolerating a null in the one scorer
        // that ignores the argument would be the kind of exception that hides the next defect.
        ArgumentNullException.ThrowIfNull(queryTerms);

        double averageLength = index.AverageDocumentLength;

        // A corpus whose documents hold no tokens has no length to be relative to. Dividing would
        // produce NaN or infinity, and an engine rejects both as not-a-score — so returning 0 leaves
        // the component neutral instead of taking the whole search down with it.
        if (averageLength <= 0)
            return 0;

        return index.DocumentLength(documentId) / averageLength;
    }
}