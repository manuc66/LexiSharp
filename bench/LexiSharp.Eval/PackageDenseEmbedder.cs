using LexiSharp.Core;
using LexiSharp.Onnx;

namespace LexiSharp.Eval;

/// <summary>
/// The dense lane encoded through <c>LexiSharp.Onnx</c> instead of the harness's own session —
/// the path selected by <c>LEXISHARP_EVAL_DENSE_PACKAGE=1</c>. It produces the same
/// <see cref="DenseVectors"/> the rest of the evaluation consumes, so the nDCG rows are read by
/// the same code; only the encoder differs.
/// </summary>
internal static class PackageDenseEmbedder
{
    public static Task<DenseVectors?> TryBuildAsync(
        BeirCorpus corpus,
        string dataDir,
        string modelCacheDir,
        int maxSeqLength,
        CancellationToken cancellationToken)
    {
        string model = Path.Combine(modelCacheDir, DenseModels.OnnxFile);
        string sentencePiece = Path.Combine(modelCacheDir, "sentencepiece.bpe.model");

        if (!File.Exists(model) || !File.Exists(sentencePiece))
        {
            Console.Error.WriteLine(
                $"Dense package lane needs {model} and {sentencePiece}; run the harness's normal --dense once to fetch them, " +
                "or point the model cache at an E5 export with that layout.");
            return Task.FromResult<DenseVectors?>(null);
        }

        int unknown = new SentencePieceOnnxTokenizer(sentencePiece).UnknownId;

        var tokenizer = new SentencePieceOnnxTokenizer(sentencePiece, new SentencePieceOnnxTokenizerOptions
        {
            IdMap = id => id == unknown ? 3 : id + 1,
            PrefixId = 0,
            SuffixId = 2,
        });

        using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = model,
            Tokenizer = tokenizer,
            Pooling = OnnxPooling.Mean,
            MaxSequenceLength = maxSeqLength,
            QueryPrefix = "query: ",
            PassagePrefix = "passage: ",
        });

        Console.WriteLine($"Encoding {corpus.Documents.Count} documents and {corpus.Queries.Count} queries through LexiSharp.Onnx.");

        var documentTexts = corpus.Documents
            .Select(CombineTitleAndText)
            .ToArray();
        var queryTexts = corpus.Queries.Select(query => query.Text).ToArray();

        float[][] documentVectors = encoder.GetTextEmbeddings(documentTexts, EmbeddingUse.Passage, cancellationToken)
            .Select(vector => vector.ToArray())
            .ToArray();
        float[][] queryVectors = encoder.GetTextEmbeddings(queryTexts, EmbeddingUse.Query, cancellationToken)
            .Select(vector => vector.ToArray())
            .ToArray();

        var queryMap = new Dictionary<string, float[]>(StringComparer.Ordinal);

        for (int i = 0; i < corpus.Queries.Count; i++)
            queryMap.TryAdd(corpus.Queries[i].Text, queryVectors[i]);

        return Task.FromResult<DenseVectors?>(new DenseVectors(documentVectors, queryMap, encoder.Dimension));
    }

    private static string CombineTitleAndText(BeirDocument document) =>
        string.IsNullOrEmpty(document.Title) ? document.Text : document.Title + " " + document.Text;
}
