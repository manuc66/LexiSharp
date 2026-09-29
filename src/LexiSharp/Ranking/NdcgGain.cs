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
    /// <c>gain = rel</c>, the convention <c>trec_eval</c> uses and therefore the one behind
    /// <c>pytrec_eval</c>'s <c>ndcg_cut</c> — which is what the BEIR paper's Table 2 numbers were
    /// produced with. Quote this one when comparing against a published <c>trec_eval</c> figure.
    /// </summary>
    Linear = 1,
}
