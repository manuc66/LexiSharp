using LexiSharp.Core;
using LexiSharp.Onnx;
using Xunit;

namespace LexiSharp.Tests;

/// <summary>
/// Opt-in parity check between this package and the evaluation harness's own dense encoder —
/// the code that produced the published dense rows. It reuses the harness's <c>DenseEmbedder</c>
/// rather than reimplementing it, so a divergence in tokenization, id mapping or pooling shows up
/// here instead of as a quality regression.
/// </summary>
public sealed class HarnessParityTests
{
    private static double Cosine(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;

        for (int i = 0; i < left.Length; i++)
        {
            dot += (double)left[i] * right[i];
            leftNorm += (double)left[i] * left[i];
            rightNorm += (double)right[i] * right[i];
        }

        return dot / Math.Sqrt(leftNorm * rightNorm);
    }

    [SkippableFact]
    public async Task PackageEmbeddingsMatchTheHarnessDenseEncoder()
    {
        string? directory = Environment.GetEnvironmentVariable("LEXISHARP_ONNX_E5_HARNESS_DIR");
        Skip.If(string.IsNullOrWhiteSpace(directory), "LEXISHARP_ONNX_E5_HARNESS_DIR is not set.");
        Skip.If(!File.Exists(Path.Combine(directory!, "onnx", "model.onnx")), "The harness model layout is missing.");
        Skip.If(!File.Exists(Path.Combine(directory!, "sentencepiece.bpe.model")), "The harness SentencePiece model is missing.");

        var corpus = new LexiSharp.Eval.BeirCorpus
        {
            Name = "parity",
            Documents = [new LexiSharp.Eval.BeirDocument("d1", string.Empty, "hello world")],
            Queries = [new LexiSharp.Eval.BeirQuery("q1", "hello world")],
            TestRelevance = new Dictionary<string, IReadOnlyDictionary<string, double>>(),
        };

        string dataDirectory = Path.Combine(Path.GetTempPath(), "lexisharp-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataDirectory);

        try
        {
            LexiSharp.Eval.DenseVectors? harness = await LexiSharp.Eval.DenseEmbedder.TryBuildAsync(
                corpus, dataDirectory, directory!, maxSeqLength: 512, CancellationToken.None);

            Skip.If(harness is null, "The harness produced no dense vectors.");

            int unknown = new SentencePieceOnnxTokenizer(Path.Combine(directory!, "sentencepiece.bpe.model")).UnknownId;

            var tokenizer = new SentencePieceOnnxTokenizer(
                Path.Combine(directory!, "sentencepiece.bpe.model"),
                new SentencePieceOnnxTokenizerOptions
                {
                    IdMap = id => id == unknown ? 3 : id + 1,
                    PrefixId = 0,
                    SuffixId = 2,
                });

            using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
            {
                ModelPath = Path.Combine(directory!, "onnx", "model.onnx"),
                Tokenizer = tokenizer,
                Pooling = OnnxPooling.Mean,
                MaxSequenceLength = 512,
                QueryPrefix = "query: ",
                PassagePrefix = "passage: ",
            });

            float[] packageDocument = encoder.GetTextEmbedding("hello world", EmbeddingUse.Passage).ToArray();
            float[] packageQuery = encoder.GetTextEmbedding("hello world", EmbeddingUse.Query).ToArray();

            Assert.True(Cosine(harness!.DocumentVectors[0], packageDocument) > 0.9999);
            Assert.True(Cosine(harness.QueryEmbeddings["hello world"], packageQuery) > 0.9999);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
        }
    }
}
