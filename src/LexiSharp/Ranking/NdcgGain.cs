namespace LexiSharp.Ranking;

/// <summary>
/// How a relevance level is turned into a gain in <see cref="RetrievalMetrics.NdcgAtK(IReadOnlyCollection{string}, IReadOnlyDictionary{string, double}, int, NdcgGain)"/>.
/// </summary>
/// <remarks>
/// The two conventions agree on binary relevance — the only level present is 1, and
/// <c>2¹ − 1 = 1</c> — and disagree as soon as a corpus is graded. Measured over the NFCorpus
/// corpus (levels 1 and 2), the same BM25 run scores 0.3080 under <see cref="Exponential"/> and
/// 0.3071 under <see cref="Linear"/>: small, but a real difference between two numbers that are
/// otherwise quoted side by side.
/// </remarks>
public enum NdcgGain
{
    /// <summary>
    /// <c>gain = 2^rel − 1</c>, the convention of Järvelin and Kekäläinen's paper. This is
    /// <see cref="RetrievalMetrics"/>'s default.
    /// </summary>
    Exponential = 0,

    /// <summary>
    /// <c>gain = rel</c>: the gain is the relevance level itself. This is the convention the
    /// standard IR evaluation tool uses, and therefore the one behind the published BM25 figures a
    /// comparison here is measured against. Use it when quoting a number next to a published one.
    /// </summary>
    Linear = 1,
}
