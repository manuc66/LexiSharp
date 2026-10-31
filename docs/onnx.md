---
title: "ONNX Runtime: local embeddings and reranking"
nav_order: 15
description: >-
  Running a local ONNX encoder and cross-encoder behind LexiSharp's provider seams,
  with BERT WordPiece, SentencePiece and byte-level BPE tokenizers.
---

# ONNX Runtime: local embeddings and reranking

`LexiSharp.Onnx` connects an in-process ONNX export to the two seams the core already defines:
`IEmbeddingProvider` for dense retrieval and `ICrossEncoderScorer` for pairwise reranking.
**No model ships in the package.** You supply the `.onnx` file, its vocabulary and the graph's
tensor names; the package supplies the session, the tokenization plumbing, batching and the
adapters.

```bash
dotnet add package LexiSharp.Onnx
```

## Dense retrieval

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;
using LexiSharp.Onnx;

var tokenizer = new BertWordPieceTokenizer("model/vocab.txt");

using var encoder = new OnnxTextEncoder(new OnnxTextEncoderOptions
{
    ModelPath = "model/model.onnx",
    Tokenizer = tokenizer,
    OutputName = "last_hidden_state",
    Pooling = OnnxPooling.Mean,
    Normalize = true,
    QueryPrefix = "query: ",
    PassagePrefix = "passage: ",
});

ITextSearchEngine dense = new InMemoryVectorSearchEngine(encoder);
dense.Index(documents);   // one batched provider call, not one per document
```

`OnnxTextEncoder` implements `IBatchEmbeddingProvider`, so
`InMemoryVectorSearchEngine.Index` and `PostgresVectorSearchEngine.Index` embed the corpus in
chunks (`EmbeddingBatchSize`, default 128) instead of one text at a time. Indexing is atomic:
the old corpus stays queryable if a provider call fails, and the PostgreSQL path runs the
truncate and inserts in one transaction. With `EmbeddingColumns`, a provider that implements
`IColumnAwareBatchEmbeddingProvider` is batched column by column.

## Tokenizers

| Export | Type |
|---|---|
| BERT family (`vocab.txt`) | `BertWordPieceTokenizer` |
| E5, multilingual, SentencePiece (`spiece.model` / `sentencepiece.bpe.model`) | `SentencePieceOnnxTokenizer` |
| GPT-2/RoBERTa byte-level BPE (`tokenizer.json`) | `BpeOnnxTokenizer` |

SentencePiece exports often offset the graph vocabulary from the model ids. That is an option,
not a fork:

```csharp
int unknown = new SentencePieceOnnxTokenizer("model/sentencepiece.bpe.model").UnknownId;

var tokenizer = new SentencePieceOnnxTokenizer("model/sentencepiece.bpe.model", new()
{
    IdMap = id => id == unknown ? 3 : id + 1,
    PrefixId = 0,
    SuffixId = 2,
});
```

`BpeOnnxTokenizer` reads `model.vocab`, `model.merges`, `added_tokens` and the
`TemplateProcessing` / `RobertaProcessing` / `BertProcessing` post-processors from
`tokenizer.json`; an unknown post-processor is refused rather than silently dropping its special
tokens. It applies the GPT-2/RoBERTa byte-level pre-tokenizer by default; a checkpoint with
different normalization must pass `BpeOnnxTokenizerOptions.Normalizer` / `.PreTokenizer`.

## Cross-encoder reranking

```csharp
using var scorer = new OnnxCrossEncoderScorer(new OnnxCrossEncoderOptions
{
    ModelPath = "model/cross-encoder.onnx",
    Tokenizer = tokenizer,
    OutputName = "logits",
});

var reranker = new BatchedCrossEncoderReranker(scorer);  // one session call per shortlist
IReadOnlyList<SearchResult> reranked = reranker.Rerank(query, candidates);
```

The score is the model's selected logit, returned raw by default. A model whose positive class is
not the first logit sets `ScoreIndex`; `ScoreTransform` adds `Sigmoid` or `Softmax`.
`BatchedCrossEncoderReranker` applies the same rules as
`LexiSharp.Hybrid.CrossEncoderReranker` — drop `0`, NaN and infinities, apply the optional
minimum, keep the incoming order on ties.

## What the package validates

At construction it rejects a graph whose token inputs are not int32/int64, whose output is not
float32/float16, or whose output shape is neither `[batch, sequence, hidden]` nor
`[batch, hidden]`. It accepts int32 and int64 token inputs, float32 and float16 outputs, pooled
and token-level outputs, and a `ConfigureSession` callback for execution providers. A
`MaxInputCharacters` cap refuses an oversized text before tokenization.

## Performance

The package is measured with BenchmarkDotNet on a real export. The command, the model and the
machine matter, so the numbers below are a short run, not a stable baseline:

```bash
LEXISHARP_ONNX_BENCH_MODEL=model.onnx \
LEXISHARP_ONNX_BENCH_TOKENIZER=vocab.txt \
dotnet run --project bench/LexiSharp.Benchmarks -c Release -- \
    --filter '*OnnxEncoderBenchmarks*' --job short
```

On MiniLM (384d, 64 texts, 256-token cap, one CPU host), a **medium** BenchmarkDotNet run
(15 iterations, 2 launches) reads:

| MaxBatchSize | Single | Batch | Ratio |
|---:|---:|---:|---:|
| 1 | 240.6 ms | 251.9 ms | 1.05× (slower) |
| 8 | 270.8 ms | 123.7 ms | **0.46×** |
| 32 | 249.0 ms | 122.5 ms median | **~0.49×** (bimodal mean 201 ms) |

Allocation is about **1.7 MB per 64 texts**, nearly independent of batch size. The `Single` row is
the query-side path; `Batch` is an indexing pass. Batch 1 is slower because the batch machinery
has nothing to amortize.

## BEIR quality through the package

The evaluation harness can run its dense lane through this package instead of its own session:

```bash
LEXISHARP_EVAL_DENSE_PACKAGE=1 dotnet run --project bench/LexiSharp.Eval -c Release -- \
    --dataset nfcorpus --no-tuned --dense --jobs 4 --top-k 10
```

Measured on the three public corpora, with multilingual-e5-small:

| Corpus | Dense nDCG@10 | Hybrid weighted | Hybrid RRF |
|---|---:|---:|---:|
| NFCorpus | 0.304 | 0.323 | 0.333 |
| SciFact | 0.655 | 0.681 | 0.686 |
| ArguAna | 0.313 | 0.349 | 0.350 |

These are the same rows the harness publishes with its own encoder — the vector-parity test and
the nDCG campaign agree.

## Signature, warm-up and cancellation

`OnnxTextEncoder` implements `IEmbeddingSignatureProvider`: the signature is a SHA-256 over the
model file, the tokenizer fingerprint and every option that changes a vector (pooling,
normalization, prefixes, truncation). `InMemoryVectorSearchEngine.ExportSnapshot` carries it and
`ImportSnapshot` refuses a different one unless `allowSignatureMismatch` is set;
`PostgresVectorSearchEngine` stores it in a `*_meta` table and refuses a mismatched table unless
`AllowSignatureMismatch` is set.

`Warmup()` pays the first-call cost explicitly. `GetTextEmbedding(s)`/`ScoreBatch` accept a
`CancellationToken`; the token is checked between chunks and mapped to ONNX Runtime's
`RunOptions.Terminate`, so a long inference can be stopped.

## What it does not do

- No model download, no vocabulary, no network.
- No sparse (`ISparseEmbeddingProvider`) or token-level (`ITokenEmbeddingProvider`) embeddings.
- No pre-tokenizer/normalizer synthesis from `tokenizer.json`.
- No multi-targeting: `net10.0`.
- Mobile assets are not tested; the test matrix is `linux-x64`.

## AOT and size

The package is analyzer-clean under `IsAotCompatible`; a Native AOT console referencing it was
published and run. ONNX Runtime is a per-RID native dependency: 153 MB at restore and about
28 MB in a `linux-x64` publish. The package itself is a few tens of KB.

## Compatibility tests

The unit suite runs offline against seeded graphs and a 429-byte SentencePiece model. Real
exports are opt-in:

```bash
tests/LexiSharp.Onnx.Tests/fixtures/fetch-minilm.sh
LEXISHARP_ONNX_MODEL_DIR=.model-cache/minilm dotnet test tests/LexiSharp.Onnx.Tests

tests/LexiSharp.Onnx.Tests/fixtures/fetch-e5.sh
LEXISHARP_ONNX_E5_DIR=.model-cache/e5 dotnet test tests/LexiSharp.Onnx.Tests

tests/LexiSharp.Onnx.Tests/fixtures/fetch-gpt2-tokenizer.sh
LEXISHARP_ONNX_GPT2_TOKENIZER=.model-cache/gpt2/tokenizer.json dotnet test tests/LexiSharp.Onnx.Tests
```
