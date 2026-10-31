# LexiSharp.Onnx

ONNX Runtime plumbing for LexiSharp — **no model ships here**. The package connects an
in-process ONNX export to the seams the core already defines:

| Type | Implements | Use |
|---|---|---|
| `OnnxTextEncoder` | `IEmbeddingProvider` | dense retrieval, MMR, MaxSim, `pgvector` backends |
| `OnnxCrossEncoderScorer` | `ICrossEncoderScorer` | pair scoring, per pair or batched |
| `BatchedCrossEncoderReranker` | `IReranker` | the same rerank rules, one session call per query |
| `BertWordPieceTokenizer` | `IOnnxTokenizer` | BERT-family exports (`vocab.txt`) |
| `SentencePieceOnnxTokenizer` | `IOnnxTokenizer` | E5, multilingual and other SentencePiece exports |
| `BpeOnnxTokenizer` | `IOnnxTokenizer` | GPT-2/RoBERTa-style byte-level BPE from `tokenizer.json` |

The caller supplies the `.onnx` file, its vocabulary and the graph's tensor names. That is
deliberate: tensor names, pooling rules and tokenizers belong to the checkpoint, and a package
that guessed them would be wrong for the next export.

```bash
dotnet add package LexiSharp.Onnx
```

```csharp
using LexiSharp.Core;
using LexiSharp.Hybrid;
using LexiSharp.Indexing;
using LexiSharp.Onnx;

var tokenizer = new BertWordPieceTokenizer("model/vocab.txt");

using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
{
    ModelPath = "model/model.onnx",
    Tokenizer = tokenizer,
    OutputName = "last_hidden_state",   // the export's own name
    Pooling = OnnxPooling.Mean,
    Normalize = true,
    QueryPrefix = "query: ",            // E5-style models
    PassagePrefix = "passage: ",
});

ITextSearchEngine dense = new InMemoryVectorSearchEngine(encoder);
dense.Add(new SearchDocument("doc-1", "oauth access token renewal"));

// One padded session call instead of one per document.
IReadOnlyList<ReadOnlyMemory<float>> vectors =
    encoder.GetTextEmbeddings(["doc-2", "doc-3"]);

using var scorer = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
{
    ModelPath = "model/cross-encoder.onnx",
    Tokenizer = tokenizer,
    OutputName = "logits",
});

var reranker = new BatchedCrossEncoderReranker(scorer);
```

SentencePiece checkpoints use the other tokenizer. The Xenova E5 export shifts content ids by
one and replaces the specials, which the options express without a new package release:

```csharp
int unknown = new SentencePieceOnnxTokenizer("model/sentencepiece.bpe.model").UnknownId;

var e5Tokenizer = new SentencePieceOnnxTokenizer("model/sentencepiece.bpe.model", new()
{
    IdMap = id => id == unknown ? 3 : id + 1,  // the export's own mapping
    PrefixId = 0,                               // <s>
    SuffixId = 2,                               // </s>
});
```

## What is supported

- **Output shapes** `[batch, sequence, hidden]` (CLS or mean pooling) and `[batch, hidden]`
  (already pooled).
- **Element types** float32 and float16 outputs; int64 and int32 token inputs. Anything else is
  rejected at construction with the offending tensor named.
- **Batching** on both the encoder and the scorer, chunked by `MaxBatchSize`, shortest-first
  inside each chunk, and wired into the in-memory and PostgreSQL indexers through
  `IBatchEmbeddingProvider` / `IColumnAwareBatchEmbeddingProvider`. Indexing is atomic: the old
  corpus stays queryable if a provider call fails.
- **Tokenizers** BERT WordPiece (`vocab.txt`), SentencePiece (`.model`) and byte-level BPE
  (`tokenizer.json`, including exports that omit the model type, like GPT-2's). The BPE reader
  applies `TemplateProcessing`, `RobertaProcessing` and `BertProcessing` post-processors and
  refuses an unknown one instead of dropping its special tokens.
- **Score selection** for multi-label cross-encoders: `ScoreIndex` picks the positive class and
  `ScoreTransform` applies `Sigmoid` or `Softmax`.
- **Embedding signature** — a SHA-256 over the model file, tokenizer fingerprint and every
  option that changes a vector. `VectorIndexSnapshot` and the PostgreSQL `*_meta` table refuse a
  mismatch instead of comparing vectors from another model.
- **Warm-up and cancellation** — `Warmup()` pays the first-call cost explicitly, and the batch
  APIs map a `CancellationToken` to ONNX Runtime's terminate flag between and during calls.
- **Execution providers** through `ConfigureSession`, which runs on the `SessionOptions` before
  the session is created (CUDA, DirectML, CoreML, graph optimization, arenas).
- **Input caps** — `MaxInputCharacters` refuses a text before the tokenizer allocates a piece per
  character, instead of silently truncating.

## What it does not do

- **No model, no vocabulary, no download.** Nothing here reaches the network or assumes a
  checkpoint.
- **No pre-tokenizer/normalizer synthesis from `tokenizer.json`.** `BpeOnnxTokenizer` reads
  `model.vocab`, `model.merges`, `added_tokens` and the supported post-processors, and applies the
  GPT-2/RoBERTa byte-level pre-tokenizer by default; a checkpoint whose JSON uses a different
  pre-tokenizer or normalizer must pass one explicitly (`BpeOnnxTokenizerOptions.PreTokenizer` /
  `.Normalizer`).
- **No model fingerprint in persisted vectors.** The encoder does not record which checkpoint
  produced a vector, so reloading an exported index with a different model of the same dimension
  is not detected.
- **No SentencePiece pair convention by default.** `EncodePair` throws until
  `PairSeparatorId` is set; concatenating silently would be a guess.
- **No sparse or token-level embeddings.** `ISparseEmbeddingProvider` (SPLADE) and
  `ITokenEmbeddingProvider` (ColBERT/MaxSim) are not implemented here; only whole-text dense
  vectors, whole-text pair scores and reranking.
- **No quantized int8 output.** Quantized exports usually dequantize to float, which works;
  a graph whose *output* is int8 is rejected.
- **No multi-targeting.** `net10.0`, matching the core package.
- **No mobile test matrix.** The runtime ships Android/iOS assets, but this package is tested on
  `linux-x64` only.

## AOT

The package is analyzer-clean under `IsAotCompatible`, and a Native AOT console referencing it
was published and run against the test fixtures (38 MB output, `libonnxruntime.so` included).
The repository's own `AotSmoke` project does not include it yet.

## Size

The package itself is a few tens of KB. The dependencies are the weight:
`Microsoft.ML.OnnxRuntime` is a 153 MB restore and about 28 MB in a `linux-x64` publish;
`Microsoft.ML.Tokenizers` adds roughly a megabyte.

## Testing

The unit suite runs against seeded graphs committed under `tests/LexiSharp.Onnx.Tests/fixtures/`
(about 1.5 KB each), a 32-token word-piece vocabulary and a 429-byte SentencePiece model. They
cover tokenization, int32/int64 binding, float32/float16 output, pooled and token-level shapes,
pooling, normalization, batching and score extraction — offline, with no checkpoint.

Real exports are opt-in, so CI never downloads a model:

```bash
tests/LexiSharp.Onnx.Tests/fixtures/fetch-minilm.sh   # Apache-2.0, ~87 MB
LEXISHARP_ONNX_MODEL_DIR=.model-cache/minilm dotnet test tests/LexiSharp.Onnx.Tests

tests/LexiSharp.Onnx.Tests/fixtures/fetch-e5.sh       # MIT, ~454 MB
LEXISHARP_ONNX_E5_DIR=.model-cache/e5 dotnet test tests/LexiSharp.Onnx.Tests

tests/LexiSharp.Onnx.Tests/fixtures/fetch-gpt2-tokenizer.sh   # MIT, 1.3 MB
LEXISHARP_ONNX_GPT2_TOKENIZER=.model-cache/gpt2/tokenizer.json dotnet test tests/LexiSharp.Onnx.Tests
```
