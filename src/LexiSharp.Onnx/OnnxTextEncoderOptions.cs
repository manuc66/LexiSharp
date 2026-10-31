using LexiSharp.Core;
using Microsoft.ML.OnnxRuntime;

namespace LexiSharp.Onnx;

/// <summary>How a token-level model output is reduced to one vector per text.</summary>
public enum OnnxPooling
{
    /// <summary>Take the first token's vector — the <c>[CLS]</c> position for BERT-style models.</summary>
    Cls,

    /// <summary>Average the vectors of the real tokens, ignoring padding.</summary>
    Mean,
}

/// <summary>
/// Everything the dense encoder needs from the caller: the model file, the tokenizer and the
/// graph's own names. Nothing here has a default that guesses a checkpoint, because the export —
/// not this package — decides what the tensors are called.
/// </summary>
public sealed class OnnxTextEncoderOptions
{
    /// <summary>Path to the <c>.onnx</c> file. The file is read when the encoder is constructed.</summary>
    public required string ModelPath { get; init; }

    /// <summary>The checkpoint's tokenizer. It must be the same one at indexing and at query time.</summary>
    public required IOnnxTokenizer Tokenizer { get; init; }

    /// <summary>Name of the token-id graph input. Defaults to <c>input_ids</c>.</summary>
    public string InputIdsName { get; init; } = "input_ids";

    /// <summary>Name of the attention-mask graph input. Defaults to <c>attention_mask</c>.</summary>
    public string AttentionMaskName { get; init; } = "attention_mask";

    /// <summary>
    /// Name of the segment-id graph input, or <see langword="null"/> when the model does not take
    /// one. Defaults to <c>token_type_ids</c>; the encoder sends zeros if the tokenizer produced
    /// no segment ids, and drops the input when the graph does not declare it.
    /// </summary>
    public string? TokenTypeIdsName { get; init; } = "token_type_ids";

    /// <summary>Name of the token-level output. Defaults to <c>last_hidden_state</c>.</summary>
    public string OutputName { get; init; } = "last_hidden_state";

    /// <summary>Reduction from token vectors to one vector. Defaults to <see cref="OnnxPooling.Cls"/>.</summary>
    public OnnxPooling Pooling { get; init; } = OnnxPooling.Cls;

    /// <summary>Whether to L2-normalize the result, so cosine similarity is a dot product. Defaults to <see langword="true"/>.</summary>
    public bool Normalize { get; init; } = true;

    /// <summary>Hard cap on tokens per text, special tokens included. Defaults to 512.</summary>
    public int MaxSequenceLength { get; init; } = 512;

    /// <summary>
    /// Maximum texts per session call when the batch API is used. Bounds the padded token arrays
    /// a large corpus allocates at once. Defaults to 32.
    /// </summary>
    public int MaxBatchSize { get; init; } = 32;

    /// <summary>
    /// Hard cap on input characters, checked before tokenization so a huge document cannot make
    /// the tokenizer allocate the whole vocabulary-worth of pieces before truncating. Defaults to
    /// 1,000,000; set <c>0</c> to disable. Exceeding it throws rather than silently truncating.
    /// </summary>
    public int MaxInputCharacters { get; init; } = 1_000_000;

    /// <summary>Text prepended to queries before tokenization, e.g. <c>"query: "</c> for E5. Defaults to empty.</summary>
    public string QueryPrefix { get; init; } = string.Empty;

    /// <summary>Text prepended to passages before tokenization, e.g. <c>"passage: "</c> for E5. Defaults to empty.</summary>
    public string PassagePrefix { get; init; } = string.Empty;

    /// <summary>ONNX Runtime intra-op threads; <c>0</c> leaves the runtime's default. Defaults to <c>0</c>.</summary>
    public int IntraOpNumThreads { get; init; }

    /// <summary>ONNX Runtime inter-op threads; <c>0</c> leaves the runtime's default. Defaults to <c>0</c>.</summary>
    public int InterOpNumThreads { get; init; }

    /// <summary>
    /// Optional callback run on the <see cref="SessionOptions"/> before the session is created —
    /// the seam for an execution provider (CUDA, DirectML, CoreML), a graph optimization level or
    /// an arena setting. The options object is disposed right after construction: do not keep it.
    /// </summary>
    public Action<SessionOptions>? ConfigureSession { get; init; }
}

internal static class OnnxSessionFactory
{
    public static InferenceSession Create(
        string modelPath,
        int intraOpNumThreads,
        int interOpNumThreads,
        Action<SessionOptions>? configure)
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = intraOpNumThreads,
            InterOpNumThreads = interOpNumThreads,
        };

        try
        {
            configure?.Invoke(options);
            return new InferenceSession(modelPath, options);
        }
        finally
        {
            options.Dispose();
        }
    }
}

internal static class OnnxPrefixes
{
    public static string For(EmbeddingUse use, string queryPrefix, string passagePrefix) =>
        use == EmbeddingUse.Query ? queryPrefix : passagePrefix;
}
