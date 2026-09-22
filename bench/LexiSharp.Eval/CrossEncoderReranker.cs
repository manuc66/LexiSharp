using System.Globalization;
using System.Text;
using LexiSharp.Core;
using Microsoft.ML.OnnxRuntime;

namespace LexiSharp.Eval;

internal interface IReranker
{
    IReadOnlyList<(string DocumentId, float Score)> Rerank(string query, IReadOnlyList<SearchDocument> candidates);
}

internal static class CrossEncoderModels
{
    public const string ModelId = "Xenova/ms-marco-MiniLM-L-6-v2";
    public const string HuggingFaceBase = "https://huggingface.co/" + ModelId + "/resolve/main/";
    public const string ModelGitHub = "https://huggingface.co/" + ModelId;

    private static readonly (string Target, string Source, string Label, int MinSize)[] Files =
    [
        ("onnx/model.onnx", "onnx/model.onnx", "model.onnx", 64 * 1024 * 1024),
        ("vocab.txt", "vocab.txt", "vocab.txt", 128 * 1024),
        ("tokenizer.json", "tokenizer.json", "tokenizer.json", 128 * 1024),
        ("config.json", "config.json", "config.json", 1 * 1024),
    ];

    public static async Task EnsureFilesAsync(string cacheDir, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(cacheDir, "onnx"));

        foreach ((string target, string source, string label, int minSize) in Files)
            await DownloadAsync(Path.Combine(cacheDir, target), HuggingFaceBase + source, label, minSize, ct);
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

/// <summary>Minimal BERT (WordPiece) tokenizer over a HunFlair-style vocab.txt, enough for cross-encoder pairs.</summary>
internal sealed class BertTokenizer
{
    private readonly Dictionary<string, int> _vocab;
    private readonly int _pad;
    private readonly int _unk;
    private readonly int _cls;
    private readonly int _sep;

    public BertTokenizer(string vocabPath)
    {
        _vocab = new Dictionary<string, int>(StringComparer.Ordinal);
        string[] lines = File.ReadAllLines(vocabPath);

        for (int i = 0; i < lines.Length; i++)
        {
            string word = lines[i];
            if (word.Length > 0)
                _vocab.TryAdd(word, i);
        }

        _pad = Lookup("[PAD]");
        _unk = Lookup("[UNK]");
        _cls = Lookup("[CLS]");
        _sep = Lookup("[SEP]");

        if (_vocab.Count == 0)
            throw new InvalidOperationException($"{vocabPath} does not look like a BERT vocab file.");
    }

    private int Lookup(string token) =>
        _vocab.TryGetValue(token, out int id) ? id : throw new InvalidOperationException($"Special token {token} missing from vocab.");

    public (int[] Ids, int[] Types) EncodePair(string query, string doc, int maxSeqLength = 512, int maxQueryTokens = 128)
    {
        List<int> queryIds = WordPieceIds(query);
        List<int> docIds = WordPieceIds(doc);

        if (queryIds.Count > maxQueryTokens)
            queryIds.RemoveRange(maxQueryTokens, queryIds.Count - maxQueryTokens);

        int docAllowance = maxSeqLength - queryIds.Count - 3;

        if (docAllowance <= 0)
        {
            int keepQuery = maxSeqLength - 3;
            if (queryIds.Count > keepQuery)
                queryIds.RemoveRange(keepQuery, queryIds.Count - keepQuery);
            docIds.Clear();
            docAllowance = 0;
        }
        else if (docIds.Count > docAllowance)
        {
            docIds.RemoveRange(0, docIds.Count - docAllowance);
        }

        int total = queryIds.Count + docIds.Count + 3;
        int[] ids = new int[total];
        int[] types = new int[total];

        ids[0] = _cls;
        for (int i = 0; i < queryIds.Count; i++)
            ids[i + 1] = queryIds[i];

        int offset = queryIds.Count + 1;
        ids[offset] = _sep;

        for (int i = 0; i < docIds.Count; i++)
            ids[offset + 1 + i] = docIds[i];

        ids[total - 1] = _sep;

        for (int i = offset + 1; i < total; i++)
            types[i] = 1;

        return (ids, types);
    }

    public List<int> WordPieceIds(string text)
    {
        var ids = new List<int>();

        foreach (string word in BasicTokenize(text))
        {
            int start = 0;
            bool first = true;

            while (start < word.Length)
            {
                int end = word.Length;
                int? found = null;

                while (start < end)
                {
                    string candidate = (start == 0 ? "" : "##") + word[start..end];

                    if (_vocab.TryGetValue(candidate, out int id))
                    {
                        found = id;
                        break;
                    }

                    end--;
                }

                if (found is null)
                {
                    ids.Add(_unk);

                    if (first)
                        break;

                    start++;
                    continue;
                }

                ids.Add(found.Value);
                start = end;
                first = false;
            }
        }

        return ids;
    }

    private static IEnumerable<string> BasicTokenize(string text)
    {
        List<string> tokens = [];

        foreach (string raw in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string folded = FoldAccents(raw.ToLowerInvariant());

            if (folded.Length == 0)
                continue;

            var buffer = new StringBuilder();

            void Flush()
            {
                if (buffer.Length > 0)
                {
                    tokens.Add(buffer.ToString());
                    buffer.Clear();
                }
            }

            foreach (char c in folded)
            {
                if (char.IsWhiteSpace(c))
                    Flush();
                else if (char.IsLetterOrDigit(c) || c == '#' || c == '\'')
                    buffer.Append(c);
                else
                {
                    Flush();
                    tokens.Add(c.ToString());
                }
            }

            Flush();
        }

        return tokens;
    }

    private static string FoldAccents(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);

        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }

        return sb.ToString();
    }
}

/// <summary>Second-stage re-ranker: cross-encoder (ms-marco MiniLM-L-6) re-scores top-N candidates per query.</summary>
internal sealed class CrossEncoderReranker : IReranker
{
    private const int MaxSeqLength = 512;
    private const int MaxQueryTokens = 128;
    private const int BatchSize = 16;

    private readonly BertTokenizer _tokenizer;
    private readonly InferenceSession _session;

    public CrossEncoderReranker(BertTokenizer tokenizer, InferenceSession session)
    {
        _tokenizer = tokenizer;
        _session = session;
    }

    public IReadOnlyList<(string DocumentId, float Score)> Rerank(string query, IReadOnlyList<SearchDocument> candidates)
    {
        var scored = new List<(string, float)>(candidates.Count);
        int offset = 0;

        while (offset < candidates.Count)
        {
            int count = Math.Min(BatchSize, candidates.Count - offset);
            var pairs = new (int[] Ids, int[] Types)[count];

            for (int i = 0; i < count; i++)
                pairs[i] = _tokenizer.EncodePair(query, candidates[offset + i].Text, MaxSeqLength, MaxQueryTokens);

            int maxLength = pairs.Max(pair => pair.Ids.Length);
            int seq = maxLength == 0 ? 1 : maxLength;
            var inputIds = new long[count * seq];
            var attentionMask = new long[inputIds.Length];
            var tokenTypes = new long[inputIds.Length];

            for (int i = 0; i < count; i++)
            {
                for (int j = 0; j < pairs[i].Ids.Length; j++)
                {
                    int at = i * seq + j;
                    inputIds[at] = pairs[i].Ids[j];
                    attentionMask[at] = 1;
                    tokenTypes[at] = pairs[i].Types[j];
                }
            }

            using var runOptions = new RunOptions();
            using var idsValue = OrtValue.CreateTensorValueFromMemory<long>(inputIds, new long[] { count, seq });
            using var maskValue = OrtValue.CreateTensorValueFromMemory<long>(attentionMask, new long[] { count, seq });
            using var typesValue = OrtValue.CreateTensorValueFromMemory<long>(tokenTypes, new long[] { count, seq });

            using (var run = _session.Run(
                runOptions,
                new[] { "input_ids", "attention_mask", "token_type_ids" },
                new OrtValue[] { idsValue, maskValue, typesValue },
                new[] { "logits" }))
            {
                float[] logits = run[0].GetTensorDataAsSpan<float>().ToArray();

                for (int i = 0; i < count; i++)
                    scored.Add((candidates[offset + i].Id, logits[i]));
            }

            offset += count;
        }

        return scored
            .OrderByDescending(pair => pair.Item2)
            .ToArray();
    }
}

internal static class RerankEngines
{
    public static ITextSearchEngine Reranked(ITextSearchEngine baseEngine, int candidates, IReranker reranker) =>
        new RerankTextSearchEngine(baseEngine, candidates, reranker);
}

internal sealed class RerankTextSearchEngine(
    ITextSearchEngine baseEngine,
    int candidates,
    IReranker reranker) : ITextSearchEngine
{
    public void Index(IEnumerable<SearchDocument> documents) =>
        throw new NotSupportedException("RerankTextSearchEngine delegates to its base engine.");

    public void Add(SearchDocument document) =>
        throw new NotSupportedException("RerankTextSearchEngine delegates to its base engine.");

    public void Remove(string documentId) =>
        throw new NotSupportedException("RerankTextSearchEngine delegates to its base engine.");

    public void Clear() =>
        throw new NotSupportedException("RerankTextSearchEngine delegates to its base engine.");

    public IReadOnlyList<SearchResult> Search(string query, SearchOptions? options = null)
    {
        options ??= SearchOptions.Default;

        var candidateResults = baseEngine.Search(query, new SearchOptions(candidates));
        var byId = candidateResults.ToDictionary(candidate => candidate.DocumentId, StringComparer.Ordinal);

        var reranked = reranker.Rerank(query, candidateResults.Select(candidate => candidate.Document).ToList());

        return reranked
            .Take(options.Limit)
            .Select(result => new SearchResult(result.DocumentId, result.Score, byId[result.DocumentId].Document))
            .ToList();
    }
}