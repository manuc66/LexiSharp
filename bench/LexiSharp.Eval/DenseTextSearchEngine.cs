using System.Text.Json;
using LexiSharp.Core;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.Tokenizers;

namespace LexiSharp.Eval;

internal sealed record DenseVectors(
    float[][] DocumentVectors,
    IReadOnlyDictionary<string, float[]> QueryEmbeddings,
    int Dimension);

internal static class DenseModels
{
    public const string ModelId = "Xenova/multilingual-e5-small";
    public const string ModelGitHub = "https://huggingface.co/Xenova/multilingual-e5-small";
    public const string HuggingFaceBase = "https://huggingface.co/" + ModelId + "/resolve/main/";
    public const string OnnxFile = "onnx/model.onnx";
    public const string SpmFile = "sentencepiece.bpe.model";

    public static async Task EnsureFilesAsync(string cacheDir, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(cacheDir, "onnx"));

        await DownloadAsync(Path.Combine(cacheDir, OnnxFile), HuggingFaceBase + OnnxFile, "model.onnx", 256 * 1024 * 1024, ct);
        await DownloadAsync(Path.Combine(cacheDir, SpmFile), HuggingFaceBase + SpmFile, "sentencepiece.bpe.model", 16 * 1024 * 1024, ct);
    }

    private static async Task DownloadAsync(string target, string url, string label, int minSize, CancellationToken ct)
    {
        if (File.Exists(target) && new FileInfo(target).Length >= minSize)
            return;

        Console.WriteLine($"Downloading {ModelId} {label} → {target}");

        using var client = new HttpClient();
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();

        await using FileStream targetStream = new(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await response.Content.CopyToAsync(targetStream, ct);
    }
}

internal static class DenseEmbedder
{
    private const int BatchSize = 8;
    private const long PadTokenId = 1;
    private const int CacheVersion = 3;

    public static async Task<DenseVectors?> TryBuildAsync(
        BeirCorpus corpus, string dataDir, string modelCacheDir, int maxSeqLength, CancellationToken ct)
    {
        int docCount = corpus.Documents.Count;
        int queryCount = corpus.Queries.Count;

        string docCache = Path.Combine(dataDir, "e5-small.docs.bin");
        string queryCache = Path.Combine(dataDir, "e5-small.queries.bin");
        string metaCache = Path.Combine(dataDir, "e5-small.json");

        if (File.Exists(docCache) && File.Exists(queryCache) && File.Exists(metaCache)
            && MetaMatches(metaCache, docCount, queryCount, maxSeqLength))
        {
            Console.WriteLine($"Loading cached multilingual-e5-small embeddings ({docCount}+{queryCount} vectors).");
            return new DenseVectors(
                ReadMatrix(docCache, docCount),
                ReadMatrixAsQueryMap(queryCache, corpus.Queries.Count(), corpus),
                HiddenDim);
        }

        Console.WriteLine(
            "Dense embeddings not cached — encoding corpus and queries with multilingual-e5-small on CPU " +
            $"(threads capped at 4, max {maxSeqLength} tokens/sequence). First run only; this can take a few minutes.");

        await DenseModels.EnsureFilesAsync(modelCacheDir, ct);

        var tokenizer = SentencePieceTokenizer.Create(
            File.OpenRead(Path.Combine(modelCacheDir, DenseModels.SpmFile)),
            addBeginOfSentence: false,
            addEndOfSentence: false);

        var options = new SessionOptions();
        options.AppendExecutionProvider_CPU();
        options.IntraOpNumThreads = 4;
        options.InterOpNumThreads = 1;

        using var session = new InferenceSession(Path.Combine(modelCacheDir, DenseModels.OnnxFile), options);
        using var runOptions = new RunOptions();

        var docTexts = corpus.Documents
            .Select(document => "passage: " + CombineTitleAndText(document))
            .ToList();

        string[] inputNames = session.InputMetadata.Keys.ToArray();
        bool hasTokenTypeIds = inputNames.Any(name => name.Equals("token_type_ids", StringComparison.OrdinalIgnoreCase));

        var sw = System.Diagnostics.Stopwatch.StartNew();

        float[][] docVectors = await EncodeAsync(docTexts, tokenizer, session, runOptions, maxSeqLength, inputNames, hasTokenTypeIds, "documents", ct);
        var queryVectors = await EncodeAsync(corpus.Queries.Select(query => "query: " + query.Text).ToList(), tokenizer, session, runOptions, maxSeqLength, inputNames, hasTokenTypeIds, "queries", ct);

        sw.Stop();

        WriteMatrix(docCache, docVectors);
        WriteMatrix(queryCache, queryVectors);
File.WriteAllText(metaCache, JsonSerializer.Serialize(new
            {
                model = DenseModels.ModelId,
                version = CacheVersion,
                docCount,
                queryCount,
                maxSeqLength,
                dimension = HiddenDim,
                elapsed = sw.Elapsed,
            }));

        Console.WriteLine($"Embeddings written to {Path.GetFileName(docCache)} and {Path.GetFileName(queryCache)} ({sw.Elapsed.TotalSeconds:0}s).");

        return new DenseVectors(docVectors, ToQueryMap(corpus, queryVectors), HiddenDim);
    }

    private const int HiddenDim = 384;

    private static async Task<float[][]> EncodeAsync(
        IReadOnlyList<string> texts,
        SentencePieceTokenizer tokenizer,
        InferenceSession session,
        RunOptions runOptions,
        int maxSeqLength,
        string[] inputNames,
        bool hasTokenTypeIds,
        string label,
        CancellationToken ct)
    {
        var result = new float[texts.Count][];
        int[][] allTokens = new int[texts.Count][];

        for (int i = 0; i < texts.Count; i++)
            allTokens[i] = Encode(texts[i], tokenizer, maxSeqLength);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        int done = 0;

        for (int start = 0; start < texts.Count; start += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            int count = Math.Min(BatchSize, texts.Count - start);
            int seq = 1;

            for (int i = start; i < start + count; i++)
                seq = Math.Max(seq, allTokens[i].Length);

            long[] inputIds = new long[count * seq];
            long[] attentionMask = new long[count * seq];

            for (int i = 0; i < count; i++)
            {
                int[] tokens = allTokens[start + i];

                for (int t = 0; t < tokens.Length; t++)
                    inputIds[i * seq + t] = tokens[t];

                for (int t = tokens.Length; t < seq; t++)
                    inputIds[i * seq + t] = PadTokenId;

                for (int t = 0; t < tokens.Length; t++)
                    attentionMask[i * seq + t] = 1;
            }

            using var ids = OrtValue.CreateTensorValueFromMemory<long>(inputIds, new long[] { count, seq });
            using var mask = OrtValue.CreateTensorValueFromMemory<long>(attentionMask, new long[] { count, seq });

            using var typeIds = hasTokenTypeIds
                ? OrtValue.CreateTensorValueFromMemory<long>(new long[inputIds.Length], new long[] { count, seq })
                : null;

            var inputValues = new List<OrtValue>(inputNames.Length);

            foreach (string name in inputNames)
            {
                inputValues.Add(
                    name.Equals("token_type_ids", StringComparison.OrdinalIgnoreCase) ? typeIds!
                    : name.Equals("attention_mask", StringComparison.OrdinalIgnoreCase) ? mask
                    : ids);
            }

            using var run = session.Run(runOptions, inputNames, inputValues, session.OutputNames);

            ReadOnlySpan<float> hidden = run[0].GetTensorDataAsSpan<float>();

            for (int i = 0; i < count; i++)
            {
                var vector = new float[HiddenDim];

                float[] sums = new float[HiddenDim];
                float weight = 0;

                for (int t = 0; t < seq; t++)
                {
                    if (attentionMask[i * seq + t] == 0)
                        continue;

                    weight += 1;

                    for (int h = 0; h < HiddenDim; h++)
                        sums[h] += hidden[(i * seq + t) * HiddenDim + h];
                }

                float scale = weight > 0 ? 1f / weight : 0f;
                double norm = 0;

                for (int h = 0; h < HiddenDim; h++)
                {
                    vector[h] = sums[h] * scale;
                    norm += vector[h] * (double)vector[h];
                }

                if (norm > 0)
                {
                    float inv = (float)(1.0 / Math.Sqrt(norm));

                    for (int h = 0; h < HiddenDim; h++)
                        vector[h] *= inv;
                }

                result[start + i] = vector;
            }

            done += count;

            if (done % 400 < count)
            {
                double rate = done / Math.Max(0.001, sw.Elapsed.TotalSeconds);
                double eta = (texts.Count - done) / Math.Max(0.001, rate);
                Console.WriteLine($"  encoded {done}/{texts.Count} {label} ({rate:0}/s, ETA {eta:0}s)");
            }
        }

        return result;
    }

    private static int[] Encode(string text, SentencePieceTokenizer tokenizer, int maxSeqLength)
    {
        int maxPieces = Math.Max(0, maxSeqLength - 2);

        IReadOnlyList<int> msIds = tokenizer.EncodeToIds(text);

        int[] tokens = new int[Math.Min(msIds.Count, maxPieces) + 2];
        tokens[0] = 0;

        for (int i = 0; i < tokens.Length - 2; i++)
            tokens[i + 1] = ToCrossrefId(msIds[i], tokenizer);

        tokens[tokens.Length - 1] = 2;

        return tokens;
    }

    private static int ToCrossrefId(int msId, SentencePieceTokenizer tokenizer) =>
        msId == tokenizer.UnknownId ? 3 : msId + 1;

    private static bool MetaMatches(string metaCache, int docCount, int queryCount, int maxSeqLength)
    {
        try
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(metaCache));

            return json.RootElement.GetProperty("model").GetString() == DenseModels.ModelId
                && json.RootElement.GetProperty("version").GetInt32() == CacheVersion
                && json.RootElement.GetProperty("docCount").GetInt32() == docCount
                && json.RootElement.GetProperty("queryCount").GetInt32() == queryCount
                && json.RootElement.GetProperty("maxSeqLength").GetInt32() == maxSeqLength;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void WriteMatrix(string path, float[][] vectors)
    {
        using var writer = new BinaryWriter(File.Create(path));

        foreach (float[] vector in vectors)
            foreach (float value in vector)
                writer.Write(value);
    }

    private static float[][] ReadMatrix(string path, int count)
    {
        using BinaryReader reader = new(File.OpenRead(path));

        var vectors = new float[count][];

        for (int i = 0; i < count; i++)
        {
            var vector = new float[HiddenDim];

            for (int h = 0; h < HiddenDim; h++)
                vector[h] = reader.ReadSingle();

            vectors[i] = vector;
        }

        return vectors;
    }

    private static IReadOnlyDictionary<string, float[]> ReadMatrixAsQueryMap(string path, int count, BeirCorpus corpus)
    {
        float[][] vectors = ReadMatrix(path, count);
        return ToQueryMap(corpus, vectors);
    }

    private static IReadOnlyDictionary<string, float[]> ToQueryMap(BeirCorpus corpus, float[][] queryVectors)
    {
        var map = new Dictionary<string, float[]>(StringComparer.Ordinal);

        for (int i = 0; i < corpus.Queries.Count; i++)
        {
            string text = corpus.Queries[i].Text;

            if (!map.TryGetValue(text, out _))
                map[text] = queryVectors[i];
        }

        return map;
    }

    private static string CombineTitleAndText(BeirDocument document) =>
        string.IsNullOrEmpty(document.Title) ? document.Text : document.Title + " " + document.Text;
}

internal sealed class DenseTextSearchEngine : ITextSearchEngine
{
    private readonly float[][] _documentVectors;
    private readonly IReadOnlyDictionary<string, float[]> _queryEmbeddings;
    private readonly IReadOnlyList<BeirDocument> _documents;
    private readonly Dictionary<string, int> _documentIndex;

    public DenseTextSearchEngine(
        BeirCorpus corpus, DenseVectors vectors)
    {
        _documentVectors = vectors.DocumentVectors;
        _queryEmbeddings = vectors.QueryEmbeddings;
        _documents = corpus.Documents;
        _documentIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        for (int i = 0; i < corpus.Documents.Count; i++)
            _documentIndex[corpus.Documents[i].Id] = i;
    }

    public void Index(IEnumerable<SearchDocument> documents)
    {
        throw new NotSupportedException("DenseTextSearchEngine is read-only (precomputed embeddings).");
    }

    public void Add(SearchDocument document)
    {
        throw new NotSupportedException("DenseTextSearchEngine is read-only (precomputed embeddings).");
    }

    public void Remove(string documentId)
    {
        throw new NotSupportedException("DenseTextSearchEngine is read-only (precomputed embeddings).");
    }

    public void Clear()
    {
        throw new NotSupportedException("DenseTextSearchEngine is read-only (precomputed embeddings).");
    }

    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        if (!_queryEmbeddings.TryGetValue(query, out float[]? queryVector))
            return Array.Empty<SearchResult>();

        options ??= SearchOptions.Default;

        int limit = options.Limit <= 0 ? 1 : options.Limit;

        double[] scores = new double[_documentVectors.Length];
        List<int> order = new();

        for (int i = 0; i < _documentVectors.Length; i++)
        {
            double score = 0;
            float[] doc = _documentVectors[i];

            for (int h = 0; h < doc.Length; h++)
                score += queryVector[h] * (double)doc[h];

            scores[i] = score;
            order.Add(i);
        }

        order.Sort((a, b) => scores[b].CompareTo(scores[a]));

        var results = new List<SearchResult>(Math.Min(limit, order.Count));

        for (int i = 0; i < order.Count && results.Count < limit; i++)
        {
            int index = order[i];
            results.Add(new SearchResult(
                _documents[index].Id,
                scores[index],
                new SearchDocument(_documents[index].Id, _documents[index].Text)));
        }

        return results;
    }
}