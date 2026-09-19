# Using SPLADE with LexiSharp — consumer-side guide

LexiSharp never runs an ML model. For learned-sparse retrieval, the library only consumes
the output of your model through one interface:

```csharp
public interface ISparseEmbeddingProvider
{
    Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
        string text, CancellationToken cancellationToken = default);
}
```

The contract, in plain terms:

- **Return term → weight pairs.** Terms are opaque strings; a learned model has its own
  vocabulary, which does not have to match LexiSharp's `Tokenizer` in any way.
- **No duplicate term** in a single mapping.
- **Non-negative weights** (ReLU-like). Non-positive values are treated as "term absent".
- The mapping is typically sparse: SPLADE activates a few dozen to a few hundred terms,
  not the whole vocabulary.

This guide describes how to write such a provider around a SPLADE ONNX model. It is an
**outline, not a shipped or tested reference implementation** — the exact tensor names,
export quirks and package APIs vary per checkpoint and per package version. Verify against
your model.

## What you need

| Piece | Typical choice | Notes |
| --- | --- | --- |
| Model | a SPLADE checkpoint (e.g. `naver/splade_v2` variants) exported to ONNX | The ONNX export must include the **MLM head**, not just the encoder — many BERT-based exports stop at the encoder. |
| Tokenizer | WordPiece (BERT) — `Microsoft.ML.Tokenizers` ships a BERT tokenizer in recent versions; otherwise load `vocab.txt` and implement WordPiece (~100 lines) | Must be the **same tokenizer as the checkpoint**, and the same one at index and query time. |
| Vocab | `vocab.txt` (id → token) | Needed to turn output ids back into the term strings you return. |
| Runtime | `Microsoft.ML.OnnxRuntime` | Consumer dependency; LexiSharp carries none of this. |

## Pipeline sketch

SPLADE-style scoring takes the MLM logits `[seq_len, vocab_size]` and produces one weight
per vocabulary term:

1. Tokenize (with attention mask), run the ONNX model.
2. For each vocabulary id, take the **max logit over token positions** (max pooling).
3. Keep positives: `w(t) = max(0, logit_max(t))` — or `log1p` of it, depending on the
   checkpoint's training scheme.
4. Optionally reweight by IDF (several SPLADE variants do); keep the same IDF table at
   index and query time.
5. Prune to top-k (e.g. 256–512 for documents) — this matters for `pgvector sparsevec`,
   which caps a vector at 1,000 non-zero elements.
6. Map surviving ids to their term strings and return them.

```csharp
// Sketch only — tensor names, input shapes and post-processing depend on your export.
sealed class SpladeProvider : ISparseEmbeddingProvider
{
    private readonly InferenceSession _session;      // Microsoft.ML.OnnxRuntime
    private readonly BertWordPieceTokenizer _tok;    // Microsoft.ML.Tokenizers or your own
    private readonly string[] _vocab;                // id -> token
    private readonly IReadOnlyDictionary<string, float> _idf;
    private readonly int _topK;

    public async Task<IReadOnlyDictionary<string, float>> GetSparseEmbeddingAsync(
        string text, CancellationToken cancellationToken = default)
    {
        // 1. tokenize + run inference (synchronously on a worker thread, or use ORT IOBinding)
        var encoded = _tok.Encode(text);
        var logits = RunMlmHead(_session, encoded);          // [seq, vocab] floats

        // 2-5. max-pool over positions, keep positives, optional IDF, prune to topK
        var sparse = MaxPoolPositive(logits, _idf, _topK);

        // 6. map ids -> terms
        return sparse.ToDictionary(p => _vocab[p.Id], p => p.Weight);
    }
}
```

## Wiring it into a backend

```csharp
ISparseEmbeddingProvider splade = new SpladeProvider(...);
ITextSearchEngine engine = new SparseTextSearchEngine(splade);            // in-memory
// or, with a fixed term -> coordinate vocabulary for the pgvector sparsevec column:
ITextSearchEngine pg = new PostgresSparseSearchEngine(
    connectionString, splade,
    new PostgresSparseOptions { Vocabulary = termToCoordinate });
```

For `PostgresSparseSearchEngine`, the vocabulary mapping is an **index-layout decision**:
it must be fixed once and shared between the index-time and query-time providers. A term
missing from it is silently ignored. Prune document vectors to the same top-k budget on
both sides, and stay under the 1,000 non-zero `sparsevec` cap.

## Failure modes to test on your side

- **Tokenizer/vocabulary desync** between index time and query time: no error is raised,
  results just silently degrade. Worth a parity test (the test suite's `TokenizerParityTests`
  covers the library tokenizer, not your model's).
- **MLM head missing from the export** → logits of the wrong shape or meaningless weights;
  validate against the checkpoint's known activations on a sample query.
- **Negative or NaN weights** after `log1p`/IDF reweighting → treated as "term absent" by
  the engines, which can silently drop the most informative terms if your reweighting is
  wrong.
