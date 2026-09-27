---
title: Embeddings and expansion
nav_order: 7
---

# Embeddings and expansion

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
dense path has published numbers. No learned-sparse model is wired into that harness, and
corpus-derived expansion is **not** an improvement where it has been measured: on the
reference corpus the `bm25-semantic` lane scores **0.8469** nDCG@5 against plain BM25's
**0.8751** — 2 queries degraded, none improved, reproducible with
`diff --baseline bm25 --candidate bm25-semantic` (see
[Comparing two configurations](evaluation.md#comparing-two-configurations-query-by-query)).
Treat expansion as a tool to measure on your own corpus, not as a default that helps.

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
