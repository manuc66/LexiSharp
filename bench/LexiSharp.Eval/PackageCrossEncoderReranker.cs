using LexiSharp.Core;
using LexiSharp.Onnx;

namespace LexiSharp.Eval;

/// <summary>
/// The harness's rerank stage through <c>LexiSharp.Onnx</c>, selected by
/// <c>LEXISHARP_EVAL_RERANK_PACKAGE=1</c>. It returns the same score shape the harness's own
/// cross-encoder does, so the retrieval pipeline and the nDCG rows are unchanged; only the model
/// adapter differs.
/// </summary>
internal sealed class PackageCrossEncoderReranker : IReranker, IDisposable
{
    private readonly OnnxCrossEncoderScorer _scorer;

    public PackageCrossEncoderReranker(string modelPath, string vocabularyPath)
    {
        _scorer = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
        {
            ModelPath = modelPath,
            Tokenizer = new BertWordPieceTokenizer(vocabularyPath),
            OutputName = "logits",
        });
    }

    public IReadOnlyList<(string DocumentId, float Score)> Rerank(
        string query,
        IReadOnlyList<SearchDocument> candidates)
    {
        IReadOnlyList<double> scores = _scorer.ScoreBatch(query, candidates);

        return candidates
            .Select((document, index) => (document.Id, Score: (float)scores[index]))
            .OrderByDescending(pair => pair.Score)
            .ToArray();
    }

    public void Dispose() => _scorer.Dispose();
}
