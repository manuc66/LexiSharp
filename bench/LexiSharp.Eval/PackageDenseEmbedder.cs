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
    private const int PackageCacheVersion = 1;

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

        string documentCache = Path.Combine(dataDir, "e5-small.package.docs.bin");
        string queryCache = Path.Combine(dataDir, "e5-small.package.queries.bin");
        string metaCache = Path.Combine(dataDir, "e5-small.package.json");

        if (File.Exists(documentCache) && File.Exists(queryCache) && File.Exists(metaCache)
            && CacheMatches(metaCache, corpus.Documents.Count, corpus.Queries.Count, maxSeqLength))
        {
            Console.WriteLine($"Loading cached package embeddings ({corpus.Documents.Count}+{corpus.Queries.Count} vectors).");
            return Task.FromResult<DenseVectors?>(new DenseVectors(
                DenseEmbedder.ReadMatrix(documentCache, corpus.Documents.Count),
                DenseEmbedder.ReadMatrixAsQueryMap(queryCache, corpus.Queries.Count, corpus),
                DenseEmbedder.HiddenDim));
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

        DenseEmbedder.WriteMatrix(documentCache, documentVectors);
        DenseEmbedder.WriteMatrix(queryCache, queryVectors);
        File.WriteAllText(metaCache, System.Text.Json.JsonSerializer.Serialize(new
        {
            model = DenseModels.ModelId,
            packageVersion = PackageCacheVersion,
            docCount = corpus.Documents.Count,
            queryCount = corpus.Queries.Count,
            maxSeqLength,
            dimension = encoder.Dimension,
        }));

        return Task.FromResult<DenseVectors?>(new DenseVectors(documentVectors, queryMap, encoder.Dimension));
    }

    internal static bool CacheMatches(string metaPath, int documentCount, int queryCount, int maxSeqLength)
    {
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(metaPath));
            System.Text.Json.JsonElement root = document.RootElement;

            return root.GetProperty("model").GetString() == DenseModels.ModelId
                && root.GetProperty("packageVersion").GetInt32() == PackageCacheVersion
                && root.GetProperty("docCount").GetInt32() == documentCount
                && root.GetProperty("queryCount").GetInt32() == queryCount
                && root.GetProperty("maxSeqLength").GetInt32() == maxSeqLength;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string CombineTitleAndText(BeirDocument document) =>
        string.IsNullOrEmpty(document.Title) ? document.Text : document.Title + " " + document.Text;
}
