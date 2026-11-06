---
title: Dense, sparse and query expansion
nav_order: 7
description: >-
  Dense, learned-sparse and corpus-derived query expansion in .NET, all behind
  consumer-supplied provider seams — no model ships in the package.
---

# Dense, sparse and query expansion

Three ways to reach past exact lexical overlap, and the seam each one leaves open. LexiSharp
never runs a model: `IEmbeddingProvider`, `ISparseEmbeddingProvider` and
`ITokenEmbeddingProvider` describe what a **consumer** would produce, and
`HashingEmbeddingProvider` implements all three deterministically so the whole stack is
testable offline with no model at all.

- **Dense, in memory** — `InMemoryVectorSearchEngine` ranks by cosine over the whole corpus
  (a linear scan, no ANN index), with `Export`/`Import` to reload without re-embedding. It
  is the in-process twin of the PostgreSQL `pgvector` engine.
- **Learned sparse** — `SparseTextSearchEngine` keeps a classic inverted index and replaces
  only the scoring function with a dot product over learned `term → weight` pairs, the shape
  SPLADE and uniCOIL produce. See the
  [consumer-side SPLADE guide](SPLADE.md) for writing the provider.
- **Corpus-derived expansion** — `LexiSharp.Expansion` widens a **document** at index time or
  a **query** at search time with terms learned from your own documents
  (`PmiTermExpander`): pure .NET, no new dependency, no model.

**What is measured here, and what is not.** The BEIR evaluation runs a *real* ONNX model for
its dense lane (`multilingual-e5-small`) and reports it next to the lexical engines, so the
dense path has published numbers. The sparse path is measured from vectors a SPLADE export
produced offline: the harness loads term weights and runs no model itself, and its rows are
that producer's. Corpus-derived expansion is **not** an improvement where it has been measured: on the
reference corpus the `bm25-semantic` lane scores **0.8469** nDCG@5 against plain BM25's
**0.8751** — 2 queries degraded, none improved, reproducible with
`diff --baseline bm25 --candidate bm25-semantic` (see
[Comparing two configurations](evaluation.md#comparing-two-configurations-query-by-query)).
Treat expansion as a tool to measure on your own corpus, not as a default that helps.

**On SciFact, a corpus-derived neighbour set is worse than no neighbour set.** Query-side
expansion over the BEIR harness scores **0.580 to 0.628** nDCG@10 against plain BM25's
**0.662**, across five window-density cuts and both neighbour orderings, with recall
falling in step. Tightening the density cut makes it worse rather than better, and ranking
neighbours by mutual information instead of co-occurrence count is a better neighbourhood
and a worse ranking — the specific associates it surfaces (`sp600125` and `p85alpha` for
*inhibitor kinase*, `t2wmh` and `arteriography` for *cerebral white matter*) retrieve
nothing the lexical terms did not. Reproduce with

```bash
dotnet run --project bench/LexiSharp.Eval -c Release -- --dataset scifact --no-tuned \
    --top-k 10 --expand-density 0.5,0.2,0.1,0.05,0.02 --expand-ranking both
```

Default analysis, 300 queries, unweighted expansion at the default budget. A co-occurrence
window measures which terms share a topic, and on a corpus whose vocabulary the query and its
answer already share, that association is what the lexical terms already carried.

## Empty vectors mean "no embedding"

An `IEmbeddingProvider` may return `ReadOnlyMemory<float>.Empty` for a text it cannot embed. The
semantic consumers treat that as a declined text, not a zero vector: the centroid classifier skips
it in training, `Learn` and `Unlearn`, and returns nothing for it at prediction; the dense
nearest-neighbour path does the same. A provider therefore does not need a sentinel vector, and it
does not have to make `Dimension` depend on the last vector it produced.

## In-memory dense retrieval (`InMemoryVectorSearchEngine`, `HashingEmbeddingProvider`)

The dense counterpart of the sparse engine, and the in-process twin of the PostgreSQL
`pgvector` engine: documents are embedded with an `IEmbeddingProvider` and queries are ranked
by **cosine similarity** over the whole corpus — a linear scan, no ANN index, so it targets
small/medium corpora (reach for `LexiSharp.Postgres` beyond that). Negative and orthogonal
cosines are clamped to `0` ("not a match"), keeping the standard LexiSharp score convention:

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;

IEmbeddingProvider model = myOnnxMiniLm;               // consumer-provided
ITextSearchEngine dense = new InMemoryVectorSearchEngine(model);

dense.Add(new SearchDocument("doc-1", "oauth access token renewal"));

var results = dense.Search("refresh credentials");
```

Like the sparse engine it implements `ITextSearchEngine`, so it fuses with BM25 through
`HybridTextSearchEngine` + `ReciprocalRankFusionMerger` (lexical + dense hybrid, in memory),
and `Export()` / `Import()` reload a corpus from its stored vectors without calling the model.
`InMemoryVectorSearchEngine` also implements `IQueryCostProbe` (a scan touches every document),
so `RoutedSearchEngine` can weigh it against the lexical engines.

Vectors are float32 by default. `InMemoryVectorSearchOptions` can store and scan them as int8
instead — signed bytes with one scale per vector, a quarter of the payload, and a squared norm
recomputed from the stored components so a cosine still divides by the length of the vector the
scan reads. It is **opt-in**: the recall cost depends on the embedding distribution, so the measured
recall, memory per vector and scan latency, with the command that produces them and the limits of
the measurement, are on [the benchmarks page](benchmarks.md#dense-quantisation-lane-quantize).

```csharp
ITextSearchEngine dense = new InMemoryVectorSearchEngine(
    myOnnxMiniLm,
    new InMemoryVectorSearchOptions { Quantization = VectorQuantization.Int8 });
```

To make the seam usable with **no model at all**, the core ships
`HashingEmbeddingProvider`: a deterministic feature-hashing embedding (FNV-1a, sign hashing,
L2-normalized) implementing both `IEmbeddingProvider` and `ITokenEmbeddingProvider`. It is
**not semantic** — it captures lexical overlap, not meaning — but it exercises and tests the
whole embedding stack (dense search, MMR, MaxSim, Postgres vector) offline, and is a drop-in
baseline to swap for a real model behind the same interface.

## Sparse learned embeddings (`ISparseEmbeddingProvider`, `SparseTextSearchEngine`)

SPLADE-style models (SPLADE, uniCOIL, ...) produce **sparse** learned vectors: a handful of
`term → weight` pairs where the weights are learned instead of tf-idf/BM25 frequencies.
The key insight of this family is that it does **not** replace the inverted-index infrastructure,
only the scoring function. `SparseTextSearchEngine` applies exactly that: it keeps a classic
`term → document → weight` inverted index, and a query scores each document by sparse
**dot product** `Σ_t w_q(t)·w_d(t,d)` — only the terms the learned model activated are visited:

```csharp
using LexiSharp.Core;
using LexiSharp.Indexing;

ISparseEmbeddingProvider splade = myOnnxSplade; // consumer-provided, incl. vocab mapping
ITextSearchEngine engine = new SparseTextSearchEngine(splade);

engine.Add(new SearchDocument("doc-1", "sparse retrievers beat dense on exact terms"));

var results = engine.Search("learned sparse retrieval");
```

Like `IEmbeddingProvider`, `ISparseEmbeddingProvider` is a pure seam in the core: the ONNX model,
tokenizer and vocabulary live in the consumer — and the `EmbeddingUse` role is threaded through
uniformly across the dense/sparse/token seams (`Passage` at index time, `Query` per search),
so asymmetric sparse variants keep a hook even though most SPLADE models are symmetric.
A walkthrough of writing a SPLADE provider
(ONNX + HuggingFace tokenizer + vocabulary mapping) is in
[docs/SPLADE.md](SPLADE.md) — an outline, not a tested reference implementation. Weights
are expected non-negative (ReLU-like); non-positive values are treated as "term absent". The
engine implements `ITextSearchEngine`, so
it drops straight into `HybridTextSearchEngine` where it merges with BM25 and dense engines via
`ReciprocalRankFusionMerger` — RRF keeps sparse-only hits (matching terms the lexical scorer and
the dense cosine disagree on) that a BM25 re-scoring merge would drop.

The engine never re-embeds on reload: `Export()` / `Import()` decouple inference from
persistence, `MessagePackSparseIndexPersistence` serializes the stored weights directly, and
`PostgresSparseSearchEngine` is the same model over `pgvector sparsevec`. In every backend, only
**queries** keep needing the provider after the corpus is loaded.

## Semantic lexical expansion (`LexiSharp.Expansion`)

A query (or a document) rarely uses the *exact* vocabulary of what it is about. The
`ITermExpander` seam lets the index enrich itself: each document is indexed as usual, then
gains a handful of **expansion terms** — related words learned from the corpus — stored at
synthetic positions after its literal tokens (a phrase query can never bridge the boundary).
`PmiTermExpander` learns those associations statistically from your own documents (windowed
co-occurrence, PMI-filtered, density-capped so function words don't leak); plug your own
implementation (e.g. a neural SPLADE model) behind the same interface:

```csharp
using LexiSharp;
using LexiSharp.Expansion;

// Learn the associations from the documents you are about to index (say, loaders output).
var expansion = PmiTermExpander.LearnFrom(corpus, options: new PmiTermExpanderOptions
{
    ContextWindowSize = 8,     // ±7 neighbours; 8 is the standard (LSA/PMI) default
    MaxWindowDensity = 0.5,    // drop words present in > half the windows (function words)
    MaxTotalTerms = 8,         // expansion budget per document
});

var index = new LexiSharpIndex<SearchDocument>(options => options.TermExpander = expansion);
index.AddRange(documents); // every document is expanded lazily at its own Add

var hits = index.Search("refresh"); // also finds "token", "expiry", "session" documents
```

The rediscovered terms enter the very same inverted index — BM25, phrase, highlighting and
every engine work on the enriched vocabulary untouched. The `bm25-semantic` preset of the
benchmark CLI measures the impact against plain BM25 on your own corpus.

The same expander can widen the **query** instead of the documents, with no reindexing, through
the `ExpandingTextSearchEngine` decorator (original terms stay first; syntax-bearing queries are
passed through untouched):

```csharp
using LexiSharp.Expansion;

ITextSearchEngine engine = new ExpandingTextSearchEngine(bm25Engine, expansion);
var hits = engine.Search("refresh"); // query becomes "refresh token access oauth session"
```

## Adapting a Microsoft.Extensions.AI generator

If your embeddings come from a Microsoft.Extensions.AI `IEmbeddingGenerator<string, Embedding<float>>`
(OpenAI, Azure OpenAI, Ollama, …), the adapter is short enough to live in your own code rather than
in a package: copy the class below and adjust the prefixes for your model. The core stays free of
any MEAI dependency, and you keep full control over the backend.

```csharp
using System;
using LexiSharp.Core;
using Microsoft.Extensions.AI;

public sealed class MeaiEmbeddingProvider : IEmbeddingProvider, IBatchEmbeddingProvider
{
    private readonly IEmbeddingGenerator<string, Embedding<float>> _generator;
    private readonly string _queryPrefix;
    private readonly string _passagePrefix;
    private int _dimension;
    private readonly object _lock = new();

    public MeaiEmbeddingProvider(
        IEmbeddingGenerator<string, Embedding<float>> generator,
        string queryPrefix = "",
        string passagePrefix = "",
        int dimension = 0)
    {
        _generator = generator;
        _queryPrefix = queryPrefix;
        _passagePrefix = passagePrefix;
        _dimension = dimension;
    }

    public int Dimension
    {
        get
        {
            if (_dimension > 0) return _dimension;
            lock (_lock)
            {
                if (_dimension > 0) return _dimension;
                var probe = GetTextEmbeddingAsync("", EmbeddingUse.Passage).GetAwaiter().GetResult();
                if (probe.IsEmpty)
                    throw new InvalidOperationException("supply the dimension explicitly");
                _dimension = probe.Length;
                return _dimension;
            }
        }
    }

    public async Task<ReadOnlyMemory<float>> GetTextEmbeddingAsync(
        string text, EmbeddingUse use, CancellationToken ct = default)
    {
        string input = (use == EmbeddingUse.Query ? _queryPrefix : _passagePrefix) + text;
        var result = await _generator.GenerateAsync(new[] { input }, null, ct).ConfigureAwait(false);
        if (result.Count == 0) return ReadOnlyMemory<float>.Empty;
        var vector = result[0].Vector;
        if (_dimension == 0 && !vector.IsEmpty) { lock (_lock) { if (_dimension == 0) _dimension = vector.Length; } }
        return vector;
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> GetTextEmbeddingsAsync(
        IReadOnlyList<string> texts, EmbeddingUse use, CancellationToken ct = default)
    {
        if (texts.Count == 0) return Array.Empty<ReadOnlyMemory<float>>();
        var inputs = new string[texts.Count];
        for (int i = 0; i < texts.Count; i++)
            inputs[i] = (use == EmbeddingUse.Query ? _queryPrefix : _passagePrefix) + texts[i];
        var result = await _generator.GenerateAsync(inputs, null, ct).ConfigureAwait(false);
        if (result.Count != texts.Count)
            throw new InvalidOperationException(
                $"generator returned {result.Count} vector(s) for {texts.Count} input(s)");
        var vectors = new ReadOnlyMemory<float>[texts.Count];
        for (int i = 0; i < texts.Count; i++) vectors[i] = result[i].Vector;
        return vectors;
    }
}
```

Register it normally:

```csharp
services.AddEmbeddingGenerator<string, Embedding<float>>(sp =>
    new OpenAIClient(apiKey).AsEmbeddingGenerator("text-embedding-3-small"));

services.AddSingleton<IEmbeddingProvider>(sp =>
    new MeaiEmbeddingProvider(
        sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
        queryPrefix: "query: ",
        passagePrefix: "passage: "));
```
