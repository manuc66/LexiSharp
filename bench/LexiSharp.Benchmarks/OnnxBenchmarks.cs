using BenchmarkDotNet.Attributes;
using LexiSharp.Core;
using LexiSharp.Onnx;

namespace LexiSharp.Benchmarks;

/// <summary>
/// Cost of the ONNX adapters on a real export. The model is not in the repository, so the suite
/// is opt-in: point <c>LEXISHARP_ONNX_BENCH_MODEL</c> at a <c>.onnx</c> file and
/// <c>LEXISHARP_ONNX_BENCH_TOKENIZER</c> at its <c>vocab.txt</c>, <c>tokenizer.json</c> or
/// SentencePiece model, then run
/// <c>dotnet run -c Release -- --filter '*Onnx*' --job short</c>.
/// </summary>
[MemoryDiagnoser]
public class OnnxEncoderBenchmarks
{
    private OnnxTextEncoder _encoder = null!;
    private string[] _texts = null!;

    /// <summary>Texts per call; also sets the encoder's <c>MaxBatchSize</c>.</summary>
    [Params(1, 8, 32)]
    public int BatchSize { get; set; }

    /// <summary>Loads the export named by the environment variables.</summary>
    [GlobalSetup]
    public void Setup()
    {
        _encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
        {
            ModelPath = Required("LEXISHARP_ONNX_BENCH_MODEL"),
            Tokenizer = Tokenizer(Required("LEXISHARP_ONNX_BENCH_TOKENIZER")),
            Pooling = OnnxPooling.Mean,
            MaxSequenceLength = 256,
            MaxBatchSize = BatchSize,
        });

        _texts = Enumerable.Range(0, 64)
            .Select(i => $"the search engine ranks documents by relevance and explains the score {i}")
            .ToArray();
    }

    /// <summary>One text per call.</summary>
    [Benchmark(Baseline = true)]
    public int Single()
    {
        int count = 0;

        foreach (string text in _texts)
        {
            _encoder.GetTextEmbedding(text);
            count++;
        }

        return count;
    }

    /// <summary>The batch API, chunked at <see cref="BatchSize"/>.</summary>
    [Benchmark]
    public int Batch() => _encoder.GetTextEmbeddings(_texts, EmbeddingUse.Passage).Count;

    /// <summary>Releases the session.</summary>
    [GlobalCleanup]
    public void Cleanup() => _encoder.Dispose();

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"{name} is not set; see the class summary.");

    private static IOnnxTokenizer Tokenizer(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".txt" => new BertWordPieceTokenizer(path),
            ".json" => new BpeOnnxTokenizer(path),
            ".model" => new SentencePieceOnnxTokenizer(path),
            _ => throw new ArgumentException($"Unsupported tokenizer extension: {path}", nameof(path)),
        };
}
