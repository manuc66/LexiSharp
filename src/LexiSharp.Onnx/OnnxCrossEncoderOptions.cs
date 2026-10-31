using Microsoft.ML.OnnxRuntime;

namespace LexiSharp.Onnx;

/// <summary>How the selected cross-encoder logit is turned into a score.</summary>
public enum OnnxScoreTransform
{
    /// <summary>Return the logit as-is (the default).</summary>
    None,

    /// <summary>Map the selected logit through a logistic function, giving a value in <c>(0, 1)</c>.</summary>
    Sigmoid,

    /// <summary>
    /// Apply softmax over the row's labels and return the selected label's probability. Use this
    /// for a multi-class relevance model where the classes compete.
    /// </summary>
    Softmax,
}

/// <summary>
/// Everything the pairwise scorer needs from the caller. As with the dense encoder, no default
/// guesses a checkpoint: the export states its own tensor names, its own sequence cap and what its
/// single output means.
/// </summary>
public sealed class OnnxCrossEncoderOptions
{
    /// <summary>Path to the <c>.onnx</c> file. The file is read when the scorer is constructed.</summary>
    public required string ModelPath { get; init; }

    /// <summary>The checkpoint's tokenizer, used to merge query and document into one sequence.</summary>
    public required IOnnxTokenizer Tokenizer { get; init; }

    /// <summary>Name reported by the scorer; defaults to <c>onnx-cross-encoder</c>.</summary>
    public string Name { get; init; } = "onnx-cross-encoder";

    /// <summary>Name of the token-id graph input. Defaults to <c>input_ids</c>.</summary>
    public string InputIdsName { get; init; } = "input_ids";

    /// <summary>Name of the attention-mask graph input. Defaults to <c>attention_mask</c>.</summary>
    public string AttentionMaskName { get; init; } = "attention_mask";

    /// <summary>
    /// Name of the segment-id graph input, or <see langword="null"/> when the model does not take
    /// one. Defaults to <c>token_type_ids</c>; the tokenizer decides which positions belong to the
    /// document, and the input is dropped when the graph does not declare it.
    /// </summary>
    public string? TokenTypeIdsName { get; init; } = "token_type_ids";

    /// <summary>Name of the score output. Defaults to <c>logits</c>.</summary>
    public string OutputName { get; init; } = "logits";

    /// <summary>
    /// Which label of a multi-label output carries the relevance score. Defaults to <c>0</c>.
    /// A model whose positive class is the second logit (common in NLI-style exports) must set
    /// <c>1</c>; otherwise the returned score is the wrong class's logit, silently.
    /// </summary>
    public int ScoreIndex { get; init; }

    /// <summary>How the selected logit is transformed. Defaults to <see cref="OnnxScoreTransform.None"/>.</summary>
    public OnnxScoreTransform ScoreTransform { get; init; } = OnnxScoreTransform.None;

    /// <summary>Hard cap on the merged sequence, special tokens included. Defaults to 512.</summary>
    public int MaxSequenceLength { get; init; } = 512;

    /// <summary>Cap on the query side of the pair, so a long query cannot crowd out the document. Defaults to 128.</summary>
    public int MaxQueryTokens { get; init; } = 128;

    /// <summary>
    /// Maximum pairs per session call. Bounds the padded token arrays a shortlist allocates at
    /// once. Defaults to 32.
    /// </summary>
    public int MaxBatchSize { get; init; } = 32;

    /// <summary>
    /// Hard cap on input characters, checked before tokenization. Defaults to 1,000,000; set
    /// <c>0</c> to disable. Exceeding it throws rather than silently truncating.
    /// </summary>
    public int MaxInputCharacters { get; init; } = 1_000_000;

    /// <summary>ONNX Runtime intra-op threads; <c>0</c> leaves the runtime's default. Defaults to <c>0</c>.</summary>
    public int IntraOpNumThreads { get; init; }

    /// <summary>ONNX Runtime inter-op threads; <c>0</c> leaves the runtime's default. Defaults to <c>0</c>.</summary>
    public int InterOpNumThreads { get; init; }

    /// <summary>
    /// Optional callback run on the <see cref="SessionOptions"/> before the session is created —
    /// the seam for an execution provider, a graph optimization level or an arena setting. The
    /// options object is disposed right after construction: do not keep it.
    /// </summary>
    public Action<SessionOptions>? ConfigureSession { get; init; }
}
